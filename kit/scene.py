"""Сцена поиска как одна картина (FLUX.2 [klein] 4B, правка по образцу).

Порядок (так же делают игры «поиск предметов»: предметы рисуются внутри сцены, а не наклеиваются сверху):
  sketch  — эскиз: фон и мебель из отобранных картинок (results\\_Нужные) раскладываются по scenes\\<сцена>.json;
  scene   — эскиз перерисовывается в цельную картину: общий свет, линия и тени (варианты -> results\\90_scenes);
            лучший вариант выбирается в Otbor.exe;
  items   — в выбранную картину по одному вписывается каждый открыватель и предмет списка: рядом с ним вырезается
            кусок картины, предмет кладётся в своё место (или серая форма, если отобранной картинки нет), и модель
            перерисовывает кусок. Слой предмета = то, что изменилось, вместе с тенью; фон под ним — сама картина.
            Варианты -> results\\91_<сцена>\\<предмет>, выбираются в Otbor.exe.
В каждом варианте предмета: <имя>.png — перерисованный кусок, <имя>_alpha.png — слой предмета того же размера;
положение куска в картине — crop в meta.json (и вычисляется заново из scenes\\<сцена>.json).

  python scene.py --kit K --runtime R --scene p0_reception --stage sketch|scene|items [--variants N] [--only id,id]
  python scene.py --kit K --scene p0_reception --stage sketch     # без видеокарты: results\\90_scenes\\..._sketch.png
"""
import argparse
import json
import os
import sys
import time

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate as g  # noqa: E402

SCENE_GROUP = '90_scenes'
MIN_CROP = 512
DIFF = 14          # порог отличия (0..255), выше — пиксель относится к предмету
PLACEHOLDER = (128, 128, 128, 255)


# ---------------------------------------------------------------- данные

def load_spec(kit, scene):
    with open(os.path.join(kit, 'scenes', scene + '.json'), encoding='utf-8') as f:
        return json.load(f)


def items_group(spec):
    return '91_' + spec['id']


def scene_job(spec):
    return {'group': SCENE_GROUP, 'id': 'scene_' + spec['id']}


def picked(results, groups, job_id):
    """Отобранная картинка задания: вырезка (*_alpha.png), если есть, иначе исходник. None — не отобрано."""
    d = os.path.join(results, '_Нужные', groups.get(job_id, ''), job_id)
    if not os.path.isdir(d):
        return None
    names = sorted(os.listdir(d))
    alpha = [n for n in names if n.endswith('_alpha.png')]
    raw = [n for n in names if n.endswith('.png') and not n.endswith('_alpha.png')]
    pick = alpha[0] if alpha else (raw[0] if raw else None)
    return os.path.join(d, pick) if pick else None


def trim_alpha(img, pad=4):
    """Как Tools/prepare_sourceart.py в игре: обрезка по непрозрачному (+pad), чтобы прямоугольники совпадали."""
    img = img.convert('RGBA')
    box = img.getchannel('A').point(lambda v: 255 if v >= 8 else 0).getbbox()
    if not box:
        return img
    x0, y0, x1, y1 = box
    return img.crop((max(0, x0 - pad), max(0, y0 - pad), min(img.width, x1 + pad), min(img.height, y1 + pad)))


def fit(img, rect):
    x, y, w, h = rect
    return img.resize((max(1, round(w)), max(1, round(h))), Image.LANCZOS), (round(x), round(y))


# ---------------------------------------------------------------- эскиз

def sketch(kit, spec, groups):
    results = os.path.join(kit, 'results')
    bg_path = picked(results, groups, spec['background'])
    if not bg_path:
        sys.exit(f'Нет отобранного фона {spec["background"]} в results\\_Нужные — сначала отберите его в Otbor.exe.')
    canvas = Image.open(bg_path).convert('RGBA').resize(tuple(spec['size']), Image.LANCZOS)
    missing = []
    for e in spec['sketch']:
        p = picked(results, groups, e['job'])
        if not p:
            missing.append(e['job'])
            continue
        spr, pos = fit(trim_alpha(Image.open(p)), e['rect'])
        canvas.alpha_composite(spr, pos)
    for o in spec.get('openers', []):
        p = picked(results, groups, o['closed'])
        if not p:
            missing.append(o['closed'])
            continue
        spr, pos = fit(trim_alpha(Image.open(p)), o['rect'])
        canvas.alpha_composite(spr, pos)
    if missing:
        print('Нет отобранных картинок (в эскиз не вошли, модель дорисует по тексту):', ', '.join(missing))
    return canvas.convert('RGB')


# ---------------------------------------------------------------- вписывание предмета

def crop_box(rect, size):
    """Кусок картины вокруг предмета: квадрат не меньше MIN_CROP и в 3 раза больше предмета, кратный 16, внутри картины."""
    x, y, w, h = rect
    side = max(MIN_CROP, int(3 * max(w, h)))
    side = min(side - side % 16, size[0] - size[0] % 16, size[1] - size[1] % 16)
    cx, cy = x + w / 2, y + h / 2
    x0 = int(min(max(0, cx - side / 2), size[0] - side))
    y0 = int(min(max(0, cy - side / 2), size[1] - side))
    return x0, y0, x0 + side, y0 + side


def open_rect(closed_img, open_img, rect):
    """Открытое состояние в том же масштабе, что закрытое, с тем же нижним центром (как пары открывателей в игре)."""
    k = rect[2] / closed_img.width
    w, h = open_img.width * k, open_img.height * k
    bx, by = rect[0] + rect[2] / 2, rect[1] + rect[3]
    return [bx - w / 2, by - h, w, h]


def guide(base_crop, origin, rect, sprite):
    """Кусок с положенным в своё место предметом и маска этого места (координаты куска)."""
    x, y, w, h = rect
    lx, ly = x - origin[0], y - origin[1]
    img = base_crop.convert('RGBA')
    mask = Image.new('L', img.size, 0)
    if sprite is not None:
        spr, _ = fit(sprite, rect)
        img.alpha_composite(spr, (round(lx), round(ly)))
        mask.paste(spr.getchannel('A').point(lambda v: 255 if v >= 128 else 0), (round(lx), round(ly)))
    else:
        r = max(2, int(min(w, h) * 0.2))
        ImageDraw.Draw(img).rounded_rectangle((lx, ly, lx + w, ly + h), r, fill=PLACEHOLDER)
        ImageDraw.Draw(mask).rounded_rectangle((lx, ly, lx + w, ly + h), r, fill=255)
    return img.convert('RGB'), np.asarray(mask) > 0


def match_tones(result, base, keep):
    """Модель могла чуть сдвинуть цвет всего куска: подгоняем по каналам на пикселях, где предмета точно нет."""
    out = result.copy()
    for c in range(3):
        r, b = result[..., c][keep], base[..., c][keep]
        if r.size < 100 or r.std() < 1:
            continue
        a = b.std() / r.std()
        out[..., c] = (result[..., c] - r.mean()) * a + b.mean()
    return np.clip(out, 0, 255)


def extract_layer(base_crop, result, place_mask, size):
    """Слой предмета: отличия перерисованного куска от исходного рядом с местом предмета, вместе с тенью."""
    b = np.asarray(base_crop.convert('RGB')).astype(np.float32)
    r = np.asarray(result.convert('RGB').resize(base_crop.size, Image.LANCZOS)).astype(np.float32)
    grow = max(8, int(0.3 * size))   # тень и правки модели могут выходить за силуэт на треть размера предмета
    region = ndimage.binary_dilation(place_mask, iterations=grow)
    keep = ~ndimage.binary_dilation(region, iterations=8)
    r = match_tones(r, b, keep)
    d = np.abs(ndimage.gaussian_filter(r, (1, 1, 0)) - ndimage.gaussian_filter(b, (1, 1, 0))).max(axis=2)
    m = (d > DIFF) & region
    m = ndimage.binary_opening(m, iterations=1)
    m = ndimage.binary_closing(m, iterations=3)
    m = ndimage.binary_fill_holes(m)
    lab, _ = ndimage.label(m)
    touch = np.unique(lab[m & ndimage.binary_dilation(place_mask, iterations=3)])
    m = np.isin(lab, touch[touch > 0]) | ndimage.binary_erosion(place_mask, iterations=2)
    alpha = np.clip(ndimage.gaussian_filter(m.astype(np.float32), 1.0) * 1.25, 0, 1)
    layer = np.dstack([r, alpha * 255.0]).astype(np.uint8)
    return Image.fromarray(layer, 'RGBA'), Image.fromarray(r.astype(np.uint8), 'RGB')


def item_prompt(spec, e, has_sprite, trigger):
    style = (f'{trigger}, ' if trigger else '') + 'a crop of a hand-drawn cartoon illustration of a room. '
    tail = (' Draw it with the same line thickness, colours and shading as the room around it, lit by the warm '
            'evening light of the ceiling lamp, with a soft contact shadow where it touches the surface. '
            'Do not change anything else in the picture. No text, no letters.')
    if has_sprite:
        return (style + f'In the middle, {e["where"]}, there is {e["desc"]} pasted on top of the picture. '
                'Redraw only this object so that it belongs to the illustration. Keep its shape, size, colours and '
                'position exactly.' + tail)
    return (style + f'Replace the plain grey shape in the middle with {e["desc"]}, {e["where"]}, exactly the same '
            'size and position as the grey shape.' + tail)


def opener_prompt(spec, o, trigger):
    return ((f'{trigger}, ' if trigger else '') + 'a crop of a hand-drawn cartoon illustration of a room. '
            f'In the middle there is {o["desc_open"]} pasted over the closed one. Redraw it so that it belongs to the '
            'illustration: the same line thickness, colours and shading as the room, lit by the warm lamp light, '
            'with a soft contact shadow on the floor. Keep its shape, size and position exactly. '
            'Do not change anything else in the picture. No text, no letters.')


# ---------------------------------------------------------------- шаги

def save_variant(d, name, raw_img, layer, meta, info):
    raw_img.save(os.path.join(d, name + '.png'))
    if layer is not None:
        layer.save(os.path.join(d, name + '_alpha.png'))
    meta[name] = info
    with open(os.path.join(d, 'meta.json'), 'w', encoding='utf-8') as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)


def load_meta(d):
    p = os.path.join(d, 'meta.json')
    return json.load(open(p, encoding='utf-8')) if os.path.isfile(p) else {}


def stage_scene(kit, gen, spec, groups, loras, triggers, variants, force):
    results = os.path.join(kit, 'results')
    job = scene_job(spec)
    d = g.job_dir(results, job)
    os.makedirs(d, exist_ok=True)
    ref = sketch(kit, spec, groups)
    ref.save(os.path.join(d, job['id'] + '__sketch.png'))
    meta = load_meta(d)
    size = tuple(spec['size'])
    for mode in ('bg', 'none'):
        if mode == 'bg' and not loras.get('bg'):
            print('LoRA фонов не обучена — только вариант без неё')
            continue
        gen.use_lora(loras.get(mode))
        prompt = (f"{triggers['bg']}, " if mode == 'bg' else '') + spec['scene_prompt']
        for k in range(variants):
            name = f"{job['id']}__{mode}_{k + 1}"
            if (os.path.isfile(os.path.join(d, name + '.png')) or g.is_sorted(results, job, name)) and not force:
                continue
            t0 = time.time()
            img = gen.run(prompt, size, g.job_seed(job['id']) + k, [ref])
            save_variant(d, name, img, None, meta, {'prompt': prompt, 'seed': g.job_seed(job['id']) + k,
                                                    'mode': mode, 'size': size})
            print(f'{name}  {time.time() - t0:.1f} с')


def picked_scene(results, spec):
    job = scene_job(spec)
    d = os.path.join(results, '_Нужные', job['group'], job['id'])
    names = sorted(n for n in os.listdir(d) if n.endswith('.png') and '_alpha' not in n) if os.path.isdir(d) else []
    if len(names) != 1:
        sys.exit(f'Нужна ровно одна отобранная картина сцены в {d} (сейчас {len(names)}): '
                 'отберите вариант в Otbor.exe после шага scene.')
    return Image.open(os.path.join(d, names[0])).convert('RGB').resize(tuple(spec['size']), Image.LANCZOS)


def run_insert(gen, base, rect, sprite, prompt, seed, job, name, d, meta, size_hint):
    x0, y0, x1, y1 = crop_box(rect, base.size)
    crop = base.crop((x0, y0, x1, y1))
    ref, place = guide(crop, (x0, y0), rect, sprite)
    t0 = time.time()
    result = gen.run(prompt, crop.size, seed, [ref])
    layer, fixed = extract_layer(crop, result, place, size_hint)
    save_variant(d, name, fixed, layer, meta, {'prompt': prompt, 'seed': seed, 'crop': [x0, y0, x1, y1],
                                               'rect': rect, 'guide': 'sprite' if sprite is not None else 'shape'})
    print(f'{name}  {time.time() - t0:.1f} с')
    return layer, (x0, y0)


def stage_items(kit, gen, spec, groups, loras, triggers, variants, force, only):
    results = os.path.join(kit, 'results')
    scene = picked_scene(results, spec)
    group = items_group(spec)
    gen.use_lora(loras.get('none'))
    opened = {}
    for o in spec.get('openers', []):
        job = {'group': group, 'id': o['id']}
        d = g.job_dir(results, job)
        os.makedirs(d, exist_ok=True)
        meta = load_meta(d)
        cp, op = picked(results, groups, o['closed']), picked(results, groups, o['open'])
        if not (cp and op):
            print(f'!!! {o["id"]}: нет отобранных картинок {o["closed"]} / {o["open"]} — открыватель пропущен')
            continue
        closed, open_img = trim_alpha(Image.open(cp)), trim_alpha(Image.open(op))
        rect = open_rect(closed, open_img, o['rect'])
        if not only or any(o['id'].startswith(s) for s in only):
            for k in range(variants):
                name = f"{o['id']}__edit_{k + 1}"
                if (os.path.isfile(os.path.join(d, name + '.png')) or g.is_sorted(results, job, name)) and not force:
                    continue
                run_insert(gen, scene, rect, open_img, opener_prompt(spec, o, ''), g.job_seed(o['id']) + k,
                           job, name, d, meta, max(rect[2], rect[3]))
        opened[o['id']] = opened_scene(results, job, scene, d)
    for e in spec['items']:
        if only and not any(e['id'].startswith(s) for s in only):
            continue
        job = {'group': group, 'id': e['id']}
        d = g.job_dir(results, job)
        os.makedirs(d, exist_ok=True)
        meta = load_meta(d)
        base = opened.get(e.get('inside'), scene) if e.get('inside') else scene
        if e.get('inside') and e['inside'] not in opened:
            print(f'!!! {e["id"]}: открыватель {e["inside"]} не готов — предмет пропущен')
            continue
        sp = picked(results, groups, e['job'])
        sprite = trim_alpha(Image.open(sp)) if sp else None
        prompt = item_prompt(spec, e, sprite is not None, '')
        for k in range(variants):
            name = f"{e['id']}__edit_{k + 1}"
            if (os.path.isfile(os.path.join(d, name + '.png')) or g.is_sorted(results, job, name)) and not force:
                continue
            run_insert(gen, base, e['rect'], sprite, prompt, g.job_seed(e['id']) + k, job, name, d, meta,
                       max(e['rect'][2], e['rect'][3]))


def opened_scene(results, job, scene, d):
    """Картина с открытым открывателем: отобранный вариант, а пока не отобран — первый сделанный."""
    sel = os.path.join(results, '_Нужные', job['group'], job['id'])
    for folder in (sel, d):
        if not os.path.isdir(folder):
            continue
        layers = sorted(n for n in os.listdir(folder) if n.endswith('_alpha.png'))
        if layers:
            name = layers[0][:-len('_alpha.png')]
            meta = load_meta(d)
            if name not in meta:
                continue
            x0, y0 = meta[name]['crop'][:2]
            out = scene.convert('RGBA')
            out.alpha_composite(Image.open(os.path.join(folder, layers[0])).convert('RGBA'), (x0, y0))
            return out.convert('RGB')
    return scene


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--kit', required=True)
    ap.add_argument('--runtime', default='')
    ap.add_argument('--scene', required=True)
    ap.add_argument('--stage', required=True, choices=['sketch', 'scene', 'items'])
    ap.add_argument('--variants', type=int, default=4)
    ap.add_argument('--bg-step', default='')
    ap.add_argument('--only', default='')
    ap.add_argument('--force', action='store_true')
    a = ap.parse_args()

    data = g.load_jobs(a.kit)
    groups = {j['id']: j['group'] for j in data['jobs']}
    spec = load_spec(a.kit, a.scene)
    results = os.path.join(a.kit, 'results')
    if a.stage == 'sketch':
        d = g.job_dir(results, scene_job(spec))
        os.makedirs(d, exist_ok=True)
        p = os.path.join(d, scene_job(spec)['id'] + '__sketch.png')
        sketch(a.kit, spec, groups).save(p)
        print('Эскиз:', p)
        return
    loras = {'bg': g.pick_lora(a.runtime, data['loras']['bg'], a.bg_step) if 'bg' in data['loras'] else None,
             'none': None}
    gen = g.Gen()
    t0 = time.time()
    if a.stage == 'scene':
        stage_scene(a.kit, gen, spec, groups, loras, data['triggers'], a.variants, a.force)
        print('Дальше: отберите одну картину в Otbor.exe (раздел «Без раздела», 90_scenes), затем 7_SCENE_ITEMS.bat')
    else:
        only = [s.strip() for s in a.only.split(',') if s.strip()]
        stage_items(a.kit, gen, spec, groups, loras, data['triggers'], a.variants, a.force, only)
        print(f'Дальше: отберите по одному варианту каждого предмета в Otbor.exe ({items_group(spec)})')
    print(f'Готово за {(time.time() - t0) / 60:.0f} мин.')


if __name__ == '__main__':
    main()
