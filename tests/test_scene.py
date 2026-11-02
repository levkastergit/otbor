"""Проверка kit/scene.py без видеокарты: модель заменена заглушкой, отбор в Otbor.exe — переносом файлов.

python -I tests/test_scene.py <папка _Нужные с группами> <рабочая папка> <jobs.json кита>
Заглушка «перерисовывает» картинку так, как это могла бы сделать модель: сдвигает тон всего куска,
затемняет тень под местом предмета и чуть меняет сам предмет. Проверяется, что слой предмета:
захватывает предмет и его тень, не захватывает остальной кусок, а картина + слои совпадают с перерисованными кусками.
"""
import json
import os
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(REPO, 'kit'))
import generate as g  # noqa: E402
import scene  # noqa: E402

FAILS = []


def check(cond, msg):
    print(('ok   ' if cond else 'FAIL ') + msg)
    if not cond:
        FAILS.append(msg)


LAST = {}
_guide = scene.guide


def recording_guide(base_crop, origin, rect, sprite):
    ref, place = _guide(base_crop, origin, rect, sprite)
    LAST['place'] = place
    return ref, place


scene.guide = recording_guide


def fake_shadow(place):
    """Тень, как её рисует модель: полоса под предметом чуть шире него, высотой в четверть предмета."""
    ys, xs = np.nonzero(place)
    x0, x1, y1 = xs.min(), xs.max(), ys.max()
    hgt = max(4, (ys.max() - ys.min()) // 4)
    h, w = place.shape
    yy, xx = np.mgrid[0:h, 0:w]
    cx, rx = (x0 + x1) / 2, (x1 - x0) / 2 * 1.15
    return (((xx - cx) / rx) ** 2 + ((yy - y1) / hgt) ** 2 < 1) & ~place


class FakeGen:
    calls = []

    def __init__(self):
        self.lora = None

    def use_lora(self, path):
        self.lora = path

    def run(self, prompt, size, seed, refs=None):
        FakeGen.calls.append((prompt, size, seed))
        img = refs[0].convert('RGB').resize(size, Image.LANCZOS)
        a = np.asarray(img).astype(np.float32)
        a = a * 1.03 + 4                                   # общий сдвиг тона всего куска, как у модели
        if size[0] == size[1] and 'place' in LAST:         # кусок с предметом: тень под ним и правка самого предмета
            place = LAST['place']
            a[fake_shadow(place)] *= 0.7
            a[place] = a[place] * 0.85 + 30
        return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def pick(kit, group, job, name):
    """Как Otbor.exe: пара name.png + name_alpha.png переносится в results/_Нужные/<группа>/<задание>."""
    src = os.path.join(kit, 'results', group, job)
    dst = os.path.join(kit, 'results', '_Нужные', group, job)
    os.makedirs(dst, exist_ok=True)
    for n in (name + '.png', name + '_alpha.png'):
        if os.path.isfile(os.path.join(src, n)):
            shutil.move(os.path.join(src, n), os.path.join(dst, n))


def main():
    picks, work = sys.argv[1], sys.argv[2]
    if os.path.isdir(work):
        shutil.rmtree(work)
    kit = os.path.join(work, 'kit')
    shutil.copytree(picks, os.path.join(kit, 'results', '_Нужные'))
    shutil.copy(sys.argv[3], os.path.join(kit, 'jobs.json'))
    os.makedirs(os.path.join(kit, 'scenes'))
    shutil.copy(os.path.join(REPO, 'kit', 'scenes', 'p0_reception.json'), os.path.join(kit, 'scenes'))
    spec = scene.load_spec(kit, 'p0_reception')
    g.Gen = FakeGen

    def run(*args):
        sys.argv = ['scene.py', '--kit', kit, '--runtime', os.path.join(work, 'runtime'), '--scene', 'p0_reception'] + list(args)
        scene.main()

    # 1. эскиз без модели
    run('--stage', 'sketch')
    sk = os.path.join(kit, 'results', '90_scenes', 'scene_p0_reception', 'scene_p0_reception__sketch.png')
    check(os.path.isfile(sk) and Image.open(sk).size == (1472, 1024), 'эскиз 1472x1024')
    # персонаж в эскиз не рисуется: над столом, где была бы голова Тамары, эскиз совпадает с фоном
    groups = {j['id']: j['group'] for j in g.load_jobs(kit)['jobs']}
    bg = Image.open(scene.picked(os.path.join(kit, 'results'), groups, spec['background'])).convert('RGB')
    for c in spec['characters']:
        x, y, w, h = c['rect']
        head = (int(x + w * 0.3), int(y + 5), int(x + w * 0.7), int(y + h * 0.35))
        diff = np.abs(np.asarray(Image.open(sk).crop(head)).astype(int) - np.asarray(bg.crop(head)).astype(int)).max()
        check(diff == 0, f'персонаж {c["job"]} не нарисован в эскизе (отличие от фона {diff})')
    check(all('dec_rec_desk' in c['behind'] for c in spec['characters']), 'стол отмечен как мебель перед Тамарой')

    # 2. items до отбора сцены — понятный отказ
    try:
        run('--stage', 'items', '--variants', '1')
        check(False, 'items без отобранной сцены должен остановиться')
    except SystemExit as e:
        check('отберите' in str(e), 'items без отобранной сцены: понятное сообщение')

    # 3. сцена: LoRA фонов нет -> только вариант без неё
    run('--stage', 'scene', '--variants', '2')
    d = os.path.join(kit, 'results', '90_scenes', 'scene_p0_reception')
    made = sorted(n for n in os.listdir(d) if n.endswith('.png') and 'sketch' not in n)
    check(made == ['scene_p0_reception__none_1.png', 'scene_p0_reception__none_2.png'], f'варианты сцены: {made}')
    check(all(c[2] for c in FakeGen.calls), 'у каждого запуска свой seed')
    n_before = len(FakeGen.calls)
    run('--stage', 'scene', '--variants', '2')
    check(len(FakeGen.calls) == n_before, 'повторный запуск не перегенерирует готовое')
    pick(kit, '90_scenes', 'scene_p0_reception', 'scene_p0_reception__none_1')

    # 4. предметы (места и тени запоминаются по ходу)
    places = {}
    _run_insert = scene.run_insert

    def recording_insert(gen, base, rect, sprite, prompt, seed, job, name, d, meta, size_hint):
        out = _run_insert(gen, base, rect, sprite, prompt, seed, job, name, d, meta, size_hint)
        places[job['id']] = LAST['place']
        return out

    scene.run_insert = recording_insert
    run('--stage', 'items', '--variants', '1')
    group = scene.items_group(spec)
    ids = [o['id'] for o in spec['openers']] + [e['id'] for e in spec['items']]
    for i in ids:
        dd = os.path.join(kit, 'results', group, i)
        ok = os.path.isfile(os.path.join(dd, i + '__edit_1.png')) and os.path.isfile(os.path.join(dd, i + '__edit_1_alpha.png'))
        if not ok:
            check(False, f'нет варианта {i}')
    check(True, f'варианты предметов: {len(ids)} заданий')

    scene_img = Image.open(os.path.join(kit, 'results', '_Нужные', '90_scenes', 'scene_p0_reception',
                                        'scene_p0_reception__none_1.png')).convert('RGBA')
    comp = scene_img.copy()
    worst_out, worst_shadow, cover = 0.0, 1.0, 1.0
    for i in ids:
        dd = os.path.join(kit, 'results', group, i)
        meta = json.load(open(os.path.join(dd, 'meta.json'), encoding='utf-8'))[i + '__edit_1']
        layer = Image.open(os.path.join(dd, i + '__edit_1_alpha.png')).convert('RGBA')
        x0, y0, x1, y1 = meta['crop']
        check(layer.size == (x1 - x0, y1 - y0), f'{i}: слой размером с кусок {layer.size}') if i == ids[0] else None
        al = np.asarray(layer.getchannel('A')).astype(np.float32) / 255
        place = places[i]
        shadow = fake_shadow(place)
        from scipy import ndimage
        far = ~ndimage.binary_dilation(place | shadow, iterations=40)
        worst_out = max(worst_out, float(al[far].mean()))
        worst_shadow = min(worst_shadow, float(al[shadow].mean()))
        cover = min(cover, float(al[place].mean()))
        comp.alpha_composite(layer, (x0, y0))
    check(worst_out < 0.01, f'слой не захватывает край куска (худшая доля {worst_out:.3f})')
    check(cover > 0.6, f'слой покрывает место предмета (худшая доля {cover:.2f})')
    check(worst_shadow > 0.5, f'тень рядом с предметом входит в слой (худшая доля {worst_shadow:.2f})')
    comp.convert('RGB').save(os.path.join(work, 'composite.jpg'), quality=88)

    # 5. предметы в шкафу берут картину с открытым шкафом
    inside = [e for e in spec['items'] if e.get('inside')]
    prompts = [c[0] for c in FakeGen.calls]
    check(any('open' in p for p in prompts), 'открыватель перерисован')
    check(len(inside) == 2, 'в шкафу два предмета')
    # 6. без отобранного спрайта — серая форма
    shape = [e['id'] for e in spec['items'] if not scene.picked(os.path.join(kit, 'results'),
                                                             {j['id']: j['group'] for j in g.load_jobs(kit)['jobs']}, e['job'])]
    check(len(shape) > 0 and any('grey shape' in p for p in prompts), f'предметы без картинки идут по серой форме: {len(shape)}')
    print('\nИТОГ:', 'всё прошло' if not FAILS else f'{len(FAILS)} провалов')
    sys.exit(1 if FAILS else 0)


if __name__ == '__main__':
    main()
