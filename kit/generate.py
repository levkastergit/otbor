"""FluxKit generator: FLUX.2 [klein] 4B (distilled, 4 steps) + the LoRAs trained by ai-toolkit.

For every job in jobs.json and every mode it makes N variants:
  t2i  - text + LoRA;
  ref  - text + LoRA + reference images from the dataset (keeps the face and clothes closer).
Each variant is saved raw (white background) and as a trimmed PNG with alpha (background removed).
Backgrounds (lora "bg" or asset T_BG_* / T_Story_*) are saved raw only: nothing is cut out.
results/index.html shows everything; results/compare/*.png compares LoRA checkpoints.

  python generate.py --kit K --runtime R [--compare] [--only id,id] [--variants 4]
                     [--char-step 1500] [--prop-step 2000] [--bg-step 1000] [--force]
  python generate.py --kit K --rebg [--tol 26]      # redo background removal only (no GPU)
  python generate.py --kit K --runtime R --regen    # only jobs marked for regeneration in Otbor.exe
  python generate.py --kit K --runtime R --compare-char NAME   # the current character LoRA against a newly trained one

ПРОПУСКИ.txt in the kit root lists image-name patterns that are never generated (see load_skips).
Jobs marked ready in Otbor.exe are never generated again (see load_status).
"""
import argparse
import fnmatch
import glob
import html
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import zlib

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

MODEL = 'black-forest-labs/FLUX.2-klein-4B'
STEPS = 4         # distilled klein: 4 steps (model card)
GUIDANCE = 1.0    # ignored by the step-distilled model
REPO_API = 'https://api.github.com/repos/levkastergit/otbor/contents/'


# ---------------------------------------------------------------- background removal

def remove_bg(img, tol=26):
    """Background = pixels close to the border colour and connected to the border.
    Edge pixels get partial alpha by distance from that colour and are un-blended from it."""
    rgb = np.asarray(img.convert('RGB')).astype(np.float32)
    border = np.concatenate([rgb[0], rgb[-1], rgb[:, 0], rgb[:, -1]])
    bgc = np.median(border, axis=0)
    dist = np.abs(rgb - bgc).max(axis=2)
    lab, _ = ndimage.label(dist <= tol)
    ids = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
    bg = np.isin(lab, ids[ids > 0])
    alpha = np.where(bg, 0.0, 1.0)
    ring = ndimage.binary_dilation(bg, iterations=2) & ~bg
    alpha[ring] = np.clip(dist[ring] / 120.0, 0.0, 1.0)
    # Soft grey shadows on the floor (the original sprites have them as semi-transparent black):
    # light neutral areas touching the background become black with alpha = how much darker than the bg they are.
    val = rgb.max(axis=2)
    neutral = (rgb.max(axis=2) - rgb.min(axis=2) < 12) & (val > 140) & ~bg
    slab, _ = ndimage.label(neutral)
    touch = np.unique(slab[ndimage.binary_dilation(bg, iterations=1) & neutral])
    shadow = np.isin(slab, touch[touch > 0])
    alpha[shadow] = np.clip((bgc.mean() - val[shadow]) / bgc.mean(), 0.0, 1.0)
    a = alpha[..., None]
    fg = np.where(a > 0.02, (rgb - bgc * (1.0 - a)) / np.maximum(a, 0.02), rgb)
    fg[shadow] = 0.0
    out = Image.fromarray(np.dstack([np.clip(fg, 0, 255), alpha * 255.0]).astype(np.uint8), 'RGBA')
    share = float(bg.mean())
    ok = bool(bgc.min() >= 200 and 0.05 < share < 0.98)
    return out, ok, share


def trim(img, pad=4):
    box = img.getchannel('A').point(lambda v: 255 if v > 8 else 0).getbbox()
    if not box:
        return img
    l, t, r, b = box
    return img.crop((max(0, l - pad), max(0, t - pad), min(img.width, r + pad), min(img.height, b + pad)))


def save_alpha(raw_path, tol):
    cut, ok, share = remove_bg(Image.open(raw_path), tol)
    cut = trim(cut)
    p = raw_path[:-4] + '_alpha.png'
    cut.save(p)
    return p, ok


def keep_bg(job):
    """Фоны и сюжетные картинки: фон не убирать (remove_bg вырезал бы стены и пол)."""
    return job.get('lora') == 'bg' or job.get('asset', '').startswith(('T_BG_', 'T_Story_'))


# ---------------------------------------------------------------- jobs and prompts

def load_jobs(kit):
    with open(os.path.join(kit, 'jobs.json'), encoding='utf-8') as f:
        return json.load(f)


def load_skips(kit):
    """ПРОПУСКИ.txt в корне кита: по строке на шаблон имени картинки, которую НЕ генерировать.
    Строка — начало имени (port_dasha__t2i) или шаблон с * (*__t2i). Пустые строки и # — комментарии."""
    p = os.path.join(kit, 'ПРОПУСКИ.txt')
    if not os.path.isfile(p):
        return []
    pats = []
    with open(p, encoding='utf-8-sig') as f:
        for line in f:
            s = line.split('#', 1)[0].strip().lower()
            if s:
                pats.append(s)
    return pats


def is_skipped(name, pats):
    n = name.lower()
    return any(fnmatch.fnmatch(n, p) or fnmatch.fnmatch(n, p + '*') for p in pats)


def fetch_repo_json(path):
    """Файл из публичного репозитория отбора. None — файла там нет или GitHub недоступен."""
    req = urllib.request.Request(REPO_API + path, headers={'Accept': 'application/vnd.github.raw+json',
                                                           'User-Agent': 'FluxKit'})
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return json.loads(r.read().decode('utf-8-sig'))
    except urllib.error.HTTPError as e:
        if e.code != 404:
            print(f'!!! GitHub, {path}: HTTP {e.code}')
    except (OSError, ValueError) as e:
        print(f'!!! GitHub, {path}: {e}')
    return None


def load_status(results):
    """Решения Otbor.exe: {(группа, задание): запись}. Они лежат в results/_статус.json и в репозитории
    (там же решения других ПК); если записи расходятся, верна более свежая."""
    docs = [fetch_repo_json('status/status.json')]
    local = os.path.join(results, '_статус.json')
    if os.path.isfile(local):
        with open(local, encoding='utf-8-sig') as f:
            docs.append(json.load(f))
    rows = {}
    for doc in docs:
        for row in (doc or {}).get('jobs', []):
            key = (row['group'], row['id'])
            if key not in rows or (row.get('at') or '') > (rows[key].get('at') or ''):
                rows[key] = row
    return rows


def has_state(status, job, state):
    return status.get((job['group'], job['id']), {}).get('state') == state


def waits_for_review(results, job):
    """Варианты прошлой перегенерации ещё не разобраны в Otbor.exe."""
    return bool(glob.glob(os.path.join(glob.escape(job_dir(results, job)), '*.png')))


def select_jobs(jobs, status, results, regen, force):
    if regen:
        return [j for j in jobs if has_state(status, j, 'regen') and not waits_for_review(results, j)]
    return [j for j in jobs if force or not has_state(status, j, 'ready')]


def variants_made(results, job, mode, row):
    """Наибольший номер варианта этого режима. Новые варианты идут дальше: тот же номер дал бы тот же seed
    и ту же картинку. Отвергнутые файлы могут лежать на другом ПК, поэтому учитывается и запись отбора."""
    rx = re.compile(re.escape(f"{job['id']}__{mode}_") + r'(\d+)(?:_alpha)?(?:\.png)?$')
    names = list(row.get('tried') or [])
    for sub in ('', '_Нужные', '_Ненужные'):
        d = os.path.join(results, sub, job['group'], job['id'])
        names += [os.path.basename(p) for p in glob.glob(os.path.join(glob.escape(d), '*.png'))]
    return max((int(m.group(1)) for m in map(rx.match, names) if m), default=0)


def build_prompt(job, mode, triggers):
    lora = job.get('lora', 'none')
    if lora == 'none':
        return job['prompt']
    t = triggers[lora]
    refs = job.get('refs', [])
    ref_word = 'reference image' if len(refs) == 1 else 'reference images'
    if lora == 'bg':
        if mode == 'ref':
            return (f"{t}, {job['desc']}. Same drawing style, line art, colours and camera height "
                    f"as the {ref_word}, but a different room.")
        return f"{t}, {job['desc']}"
    if lora == 'prop':
        if mode == 'ref':
            return (f"{t}, {job['desc']}. Same drawing style, outline and shading as the {ref_word}. "
                    f"Single object, plain white background.")
        return f"{t}, {job['desc']}, single object, plain white background"
    if mode == 'ref':
        return (f"{t}, the same character as in the {ref_word}: {job['desc']}. New pose: {job['action']}. "
                f"Keep exactly the same face, hairstyle, clothes, colours and line art as in the {ref_word}. "
                f"Plain white background.")
    return f"{t}, {job['desc']}, {job['action']}, plain white background"


def job_seed(job_id):
    return zlib.crc32(job_id.encode('utf-8')) % 1_000_000


def lora_files(runtime, name):
    d = os.path.join(runtime, 'output', name)
    steps = sorted(glob.glob(os.path.join(d, f'{name}_*.safetensors')))
    final = os.path.join(d, f'{name}.safetensors')
    return steps, (final if os.path.isfile(final) else None)


def step_of(path, name):
    """Step number of a checkpoint <name>_000000250.safetensors, None for other files."""
    tail = os.path.basename(path)[len(name) + 1:-len('.safetensors')]
    return int(tail) if tail.isdigit() else None


def pick_lora(runtime, name, step):
    steps, final = lora_files(runtime, name)
    if step:
        p = os.path.join(runtime, 'output', name, f'{name}_{int(step):09d}.safetensors')
        if os.path.isfile(p):
            return p
        # ai-toolkit saves the last step without a number (<name>.safetensors): the bottom row of the comparison
        # sheet. A step past the last numbered checkpoint means that final file.
        last = max([n for n in (step_of(s, name) for s in steps) if n is not None], default=0)
        if final and int(step) > last:
            print(f'{name}: шаг {step} — это конец обучения, его файл без номера: беру {os.path.basename(final)}')
            return final
        have = [os.path.basename(s) for s in steps] + ([os.path.basename(final)] if final else [])
        sys.exit(f'Нет чекпойнта {p}. Есть: {have}')
    if final:
        return final
    if steps:
        print(f'!!! {name}: финального файла нет (обучение прервано?), беру последний чекпойнт {steps[-1]}')
        return steps[-1]
    return None


# ---------------------------------------------------------------- pipeline

class Gen:
    def __init__(self):
        import torch
        from diffusers import Flux2KleinPipeline
        self.torch = torch
        t0 = time.time()
        self.pipe = Flux2KleinPipeline.from_pretrained(MODEL, torch_dtype=torch.bfloat16)
        vram = torch.cuda.get_device_properties(0).total_memory / 2 ** 30
        if vram >= 24:
            self.pipe.to('cuda')
        else:
            self.pipe.enable_model_cpu_offload()  # model card: offload saves VRAM
        print(f'Модель загружена за {time.time() - t0:.0f} с, видеопамять {vram:.1f} ГБ')
        self.lora = None

    def use_lora(self, path):
        if path == self.lora:
            return
        if self.lora:
            self.pipe.unload_lora_weights()
        if path:
            self.pipe.load_lora_weights(path)
            print(f'LoRA: {path}')
        self.lora = path

    def run(self, prompt, size, seed, refs=None):
        g = self.torch.Generator(device='cuda').manual_seed(seed)
        kw = dict(prompt=prompt, width=size[0], height=size[1], num_inference_steps=STEPS,
                  guidance_scale=GUIDANCE, generator=g)
        if refs:
            kw['image'] = refs
        return self.pipe(**kw).images[0]


def load_refs(kit, job):
    paths = [os.path.join(kit, r) for r in job.get('refs', [])]
    missing = [p for p in paths if not os.path.isfile(p)]
    if missing:
        print(f'!!! {job["id"]}: нет образцов {missing} — задание пропущено')
        return None
    return [Image.open(p).convert('RGB') for p in paths]


# ---------------------------------------------------------------- outputs

def job_dir(results, job):
    return os.path.join(results, job['group'], job['id'])


def is_sorted(results, job, name):
    """Otbor.exe переносит разобранные варианты из папки задания в _Нужные и _Ненужные."""
    return any(os.path.isfile(os.path.join(results, s, job['group'], job['id'], name + '.png'))
               for s in ('_Нужные', '_Ненужные'))


def write_index(kit, data):
    results = os.path.join(kit, 'results')
    parts = ['<!doctype html><meta charset="utf-8"><title>FluxKit results</title><style>',
             'body{font-family:sans-serif;margin:16px;background:#f4f4f4}',
             'h2{margin-top:32px}.job{background:#fff;padding:12px;margin:12px 0;border-radius:8px}',
             '.imgs{display:flex;flex-wrap:wrap;gap:8px}',
             '.imgs figure{margin:0;font-size:11px;text-align:center}',
             '.imgs img{height:260px;background:repeating-conic-gradient(#ddd 0 25%,#fff 0 50%) 0 0/16px 16px}',
             '.bad{color:#b00}code{font-size:11px;color:#555}</style>',
             '<h1>FluxKit — результаты</h1>']
    comp = sorted(glob.glob(os.path.join(results, 'compare', '*.png')))
    if comp:
        parts.append('<h2>Сравнение чекпойнтов LoRA</h2>')
        for c in comp:
            rel = os.path.relpath(c, results).replace(os.sep, '/')
            parts.append(f'<p><a href="{rel}"><img src="{rel}" style="max-width:100%"></a></p>')
    group = None
    for job in data['jobs']:
        d = job_dir(results, job)
        files = sorted(glob.glob(os.path.join(d, '*_alpha.png')))
        if keep_bg(job):
            files = sorted(f for f in glob.glob(os.path.join(d, '*.png')) if not f.endswith('_alpha.png'))
        if group != job['group']:
            group = job['group']
            parts.append(f'<h2>{html.escape(group)}</h2>')
        parts.append(f'<div class="job"><b>{html.escape(job["id"])}</b> — {html.escape(job.get("ru", ""))}'
                     f' <code>{html.escape(job.get("asset", ""))}</code><div class="imgs">')
        meta = {}
        mp = os.path.join(d, 'meta.json')
        if os.path.isfile(mp):
            meta = json.load(open(mp, encoding='utf-8'))
        for f in files:
            rel = os.path.relpath(f, results).replace(os.sep, '/')
            cut = '_alpha.png' if f.endswith('_alpha.png') else '.png'
            raw = rel[:-len(cut)] + '.png'
            name = os.path.basename(f)[:-len(cut)]
            bad = '' if meta.get(name, {}).get('bg_ok', True) else ' <span class="bad">фон не убран</span>'
            parts.append(f'<figure><a href="{raw}"><img src="{rel}"></a><figcaption>{name}{bad}</figcaption></figure>')
        if not files:
            parts.append('<i>ещё нет</i>')
        parts.append('</div></div>')
    with open(os.path.join(results, 'index.html'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(parts))


def contact_sheet(gen, ckpts, prompts, trigger, size, path):
    tiles = []
    for ck in ckpts:
        gen.use_lora(ck)
        label = os.path.basename(ck)[:-len('.safetensors')]
        row = [gen.run(p.replace('[trigger]', trigger), size, 777) for p in prompts]
        tiles.append((label, row))
        print(f'сравнение {label}: готово')
    w, h = size
    sheet = Image.new('RGB', (w * len(prompts) + 220, h * len(tiles)), 'white')
    d = ImageDraw.Draw(sheet)
    for r, (label, row) in enumerate(tiles):
        d.text((8, r * h + 8), label, fill=(0, 0, 0))
        for c, im in enumerate(row):
            sheet.paste(im.resize((w, h)), (220 + c * w, r * h))
    sheet.save(path)


def compare(kit, runtime, gen, data):
    out = os.path.join(kit, 'results', 'compare')
    os.makedirs(out, exist_ok=True)
    for key, name in data['loras'].items():
        steps, final = lora_files(runtime, name)
        ckpts = steps + ([final] if final else [])
        if not ckpts:
            print(f'!!! {name}: нет обученных файлов, сравнение пропущено')
            continue
        size = {'char': (512, 768), 'bg': (704, 512)}.get(key, (512, 512))
        contact_sheet(gen, ckpts, data['compare_prompts'][key], data['triggers'][key], size, os.path.join(out, f'{name}.png'))
    gen.use_lora(None)


def compare_new(kit, runtime, gen, data, name):
    """Прежняя модель персонажей первой строкой, ниже последние чекпойнты новой: те же промпты и тот же seed."""
    out = os.path.join(kit, 'results', 'compare')
    os.makedirs(out, exist_ok=True)
    old = lora_files(runtime, data['loras']['char'])[1]
    steps, final = lora_files(runtime, name)
    if not final:
        sys.exit(f'Нет обученной модели {name}.')
    ckpts = ([old] if old else []) + steps[-3:] + [final]
    contact_sheet(gen, ckpts, data['compare_prompts']['char'], data['triggers']['char'], (512, 768), os.path.join(out, f'{name}.png'))
    gen.use_lora(None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--kit', required=True)
    ap.add_argument('--runtime', default='')
    ap.add_argument('--only', default='', help='ids через запятую (или их начало)')
    ap.add_argument('--variants', type=int, default=4)
    ap.add_argument('--char-step', default='')
    ap.add_argument('--prop-step', default='')
    ap.add_argument('--bg-step', default='')
    ap.add_argument('--compare', action='store_true', help='только сравнение чекпойнтов')
    ap.add_argument('--compare-char', default='', help='только сравнение модели персонажей с новой моделью')
    ap.add_argument('--char-lora', default='', help='какой моделью делать персонажей вместо указанной в jobs.json')
    ap.add_argument('--force', action='store_true', help='перегенерировать существующие')
    ap.add_argument('--regen', action='store_true', help='только задания, отмеченные в Otbor.exe на перегенерацию')
    ap.add_argument('--rebg', action='store_true', help='только заново убрать фон')
    ap.add_argument('--tol', type=int, default=26)
    a = ap.parse_args()

    data = load_jobs(a.kit)
    if a.char_lora:
        data['loras']['char'] = a.char_lora
    results = os.path.join(a.kit, 'results')
    os.makedirs(results, exist_ok=True)

    if a.rebg:
        keep = {os.path.normcase(job_dir(results, j)) for j in data['jobs'] if keep_bg(j)}
        for raw in glob.glob(os.path.join(results, '*', '*', '*.png')):
            if raw.endswith('_alpha.png') or os.path.basename(os.path.dirname(os.path.dirname(raw))) == 'compare':
                continue
            if os.path.normcase(os.path.dirname(raw)) in keep:
                continue
            save_alpha(raw, a.tol)
        write_index(a.kit, data)
        print('Фон убран заново, см. results/index.html')
        return

    status, overrides, jobs = {}, {}, []
    if not a.compare and not a.compare_char:
        status = load_status(results)
        only = [s.strip() for s in a.only.split(',') if s.strip()]
        jobs = [j for j in data['jobs'] if not only or any(j['id'].startswith(o) for o in only)]
        found = len(jobs)
        jobs = select_jobs(jobs, status, results, a.regen, a.force)
        if a.regen:
            print(f'На перегенерацию: {len(jobs)} заданий')
            if not jobs:
                print('Перегенерировать нечего.')
                return
            overrides = fetch_repo_json('regen/overrides.json') or {}
            jobs = [{**j, **overrides.get(j['id'], {})} for j in jobs]
        elif found != len(jobs):
            print(f'Отбор: {found - len(jobs)} готовых заданий пропущено')

    gen = Gen()
    if a.compare:
        compare(a.kit, a.runtime, gen, data)
        write_index(a.kit, data)
        return
    if a.compare_char:
        compare_new(a.kit, a.runtime, gen, data, a.compare_char)
        write_index(a.kit, data)
        return

    loras = {
        'char': pick_lora(a.runtime, data['loras']['char'], a.char_step),
        'prop': pick_lora(a.runtime, data['loras']['prop'], a.prop_step),
        'bg': pick_lora(a.runtime, data['loras']['bg'], a.bg_step) if 'bg' in data['loras'] else None,
        'none': None,
    }
    skips = load_skips(a.kit)
    if skips:
        print(f'ПРОПУСКИ.txt: {len(skips)} шаблонов, подходящие картинки не генерируются')
    # group by LoRA so each LoRA is loaded once
    jobs.sort(key=lambda j: ['char', 'prop', 'bg', 'none'].index(j.get('lora', 'none')))
    total = sum(len(j.get('modes', ['t2i'])) * a.variants for j in jobs)
    done = 0
    t_start = time.time()
    for job in jobs:
        lora = job.get('lora', 'none')
        if lora != 'none' and not loras[lora]:
            print(f'!!! {job["id"]}: LoRA {lora} не обучена — пропуск')
            continue
        gen.use_lora(loras[lora])
        d = job_dir(results, job)
        os.makedirs(d, exist_ok=True)
        meta_path = os.path.join(d, 'meta.json')
        meta = json.load(open(meta_path, encoding='utf-8')) if os.path.isfile(meta_path) else {}
        refs = load_refs(a.kit, job)
        if refs is None:
            continue
        size = tuple(job.get('size', {'char': [832, 1216], 'bg': [1472, 1024]}.get(lora, [1024, 1024])))
        base = job_seed(job['id'])
        for mode in job.get('modes', ['t2i']):
            prompt = build_prompt(job, mode, data['triggers'])
            row = status.get((job['group'], job['id']), {})
            first = variants_made(results, job, mode, row) if a.regen else 0
            for k in range(first, first + a.variants):
                done += 1
                name = f"{job['id']}__{mode}_{k + 1}"
                if is_skipped(name, skips):
                    continue
                raw = os.path.join(d, name + '.png')
                if (os.path.isfile(raw) or is_sorted(results, job, name)) and not a.force:
                    continue
                t0 = time.time()
                img = gen.run(prompt, size, base + k, refs if mode == 'ref' else None)
                img.save(raw)
                ok = True if keep_bg(job) else save_alpha(raw, a.tol)[1]
                meta[name] = {'prompt': prompt, 'seed': base + k, 'mode': mode, 'size': size,
                              'lora': loras[lora], 'bg_ok': ok}
                with open(meta_path, 'w', encoding='utf-8') as f:
                    json.dump(meta, f, ensure_ascii=False, indent=1)
                print(f'[{done}/{total}] {name}  {time.time() - t0:.1f} с' + ('' if ok else '  (фон не убран)'))
        write_index(a.kit, data)
    print(f'Готово за {(time.time() - t_start) / 60:.0f} мин. Откройте results/index.html')


if __name__ == '__main__':
    main()
