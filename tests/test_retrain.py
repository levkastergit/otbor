"""Проверка retrain.py: какие отобранные картинки попадают в набор для дообучения. Видеокарта не нужна."""
import importlib.util
import json
import os
import shutil
import sys
import tempfile

spec = importlib.util.spec_from_file_location('retrain', sys.argv[1])
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)

passed = failed = 0


def check(ok, name):
    global passed, failed
    if ok:
        passed += 1
    else:
        failed += 1
    print(('ok    ' if ok else 'FAIL  ') + name)


root = tempfile.mkdtemp()
kit = os.path.join(root, 'kit')
source = os.path.join(kit, 'datasets', 'char')
results = os.path.join(kit, 'results')
os.makedirs(source)
os.makedirs(results)


def original(name, who):
    with open(os.path.join(source, name + '.png'), 'wb') as f:
        f.write(b'png')
    with open(os.path.join(source, name + '.txt'), 'w', encoding='utf-8') as f:
        f.write(f'[trigger], {who}, a character, standing, plain white background')


for i in range(6):
    original(f'{i:03}_masha', 'Masha')
original('010_babushka', 'Grandma')
os.makedirs(os.path.join(source, '_latent_cache'))


def job(i, who, action='standing, full body', lora='char', group='80_chars2'):
    return {'id': i, 'group': group, 'lora': lora, 'desc': f'{who}, a person', 'action': action}


def pick(j, *names):
    folder = os.path.join(results, '_Нужные', j['group'], j['id'])
    os.makedirs(folder, exist_ok=True)
    for name in names:
        with open(os.path.join(folder, name + '.png'), 'wb') as f:
            f.write(b'png')
    return {'id': j['id'], 'group': j['group'], 'state': 'ready', 'picked': list(names)}


jobs = [job(f'masha_{i}', 'Masha') for i in range(5)] + [
    job('sleep', 'Masha', 'lying on an invisible bed'), job('gran_1', 'Grandma'), job('gran_2', 'Grandma'), job('gran_3', 'Grandma'),
    job('gran_4', 'Grandma'), job('gran_5', 'Grandma'), job('cup', 'Cup', lora='prop'), job('new_one', 'Stranger'), job('by_v2', 'Grandma'),
    job('rejected', 'Masha'), job('moved', 'Masha')]
by_id = {j['id']: j for j in jobs}
rows = [pick(by_id[f'masha_{i}'], *[f'masha_{i}__ref_{n}' for n in range(1, 5)]) for i in range(5)]
rows += [pick(by_id['sleep'], 'sleep__ref_1'), pick(by_id['cup'], 'cup__ref_1'), pick(by_id['new_one'], 'new_one__ref_1')]
rows += [pick(by_id[f'gran_{i}'], f'gran_{i}__ref_1') for i in range(1, 6)]
rows += [pick(by_id['by_v2'], 'by_v2__ref_1')]
rows += [{'id': 'rejected', 'group': '80_chars2', 'state': 'regen', 'picked': [], 'note': 'нет'}]
rows += [{'id': 'moved', 'group': '80_chars2', 'state': 'ready', 'picked': ['moved__ref_1']}]
meta = os.path.join(results, '80_chars2', 'by_v2')
os.makedirs(meta)
with open(os.path.join(meta, 'meta.json'), 'w', encoding='utf-8') as f:
    json.dump({'by_v2__ref_1': {'lora': r'C:\FluxKit\output\pd_char_v2\pd_char_v2.safetensors'}}, f)
with open(os.path.join(kit, 'jobs.json'), 'w', encoding='utf-8') as f:
    json.dump({'jobs': jobs}, f)
with open(os.path.join(results, '_статус.json'), 'w', encoding='utf-8') as f:
    json.dump({'jobs': rows}, f, ensure_ascii=False)

added = r.build(kit)
target = os.path.join(kit, 'datasets', 'char_v2')
names = sorted(os.listdir(target))
pngs = [n[:-4] for n in names if n.endswith('.png')]
extra = [n for n in pngs if not n[0].isdigit()]
print(extra)

check(sum(n[0].isdigit() for n in pngs) == 7, 'все исходные образцы на месте')
check('_latent_cache' not in names, 'служебная папка не копируется')
check(all(n + '.txt' in names for n in pngs), 'у каждой картинки есть подпись')
masha = [n for n in extra if n.startswith('masha_')]
check(len(masha) == 6, f'у Маши не больше, чем исходных образцов: {len(masha)}')
check(all(any(n.startswith(f'masha_{i}__') for n in masha) for i in range(5)), 'сначала по варианту на каждое задание')
check(max(sum(n.startswith(f'masha_{i}__') for n in masha) for i in range(5)) <= r.MAX_PER_JOB, 'не больше двух вариантов на задание')
gran = [n for n in extra if n.startswith('gran_')]
check(len(gran) == 4, f'при одном образце лимит равен минимуму: {len(gran)}')
check('sleep__ref_1' not in extra, 'мебель, которую дорисовала модель, не берётся')
check('cup__ref_1' not in extra, 'предметы не берутся')
check('new_one__ref_1' in extra, 'новый персонаж без исходных образцов берётся')
check('by_v2__ref_1' not in extra, 'картинки самой новой модели не берутся')
check(added == len(extra), 'итог совпадает с числом файлов')
with open(os.path.join(target, 'masha_0__ref_1.txt'), encoding='utf-8') as f:
    text = f.read()
check(text == '[trigger], Masha, a person, standing, full body, plain white background', 'подпись как у исходных образцов: ' + text)

with open(os.path.join(target, 'лишний.png'), 'wb') as f:
    f.write(b'x')
r.build(kit)
check(not os.path.exists(os.path.join(target, 'лишний.png')), 'повторная сборка начинается с чистой папки')
check(sorted(os.listdir(os.path.join(kit, 'datasets', 'char'))) == sorted(os.listdir(source)), 'исходная папка не тронута')

with open(os.path.join(results, '_статус.json'), 'w', encoding='utf-8') as f:
    json.dump({'jobs': []}, f)
stopped = False
try:
    r.build(kit)
except SystemExit as e:
    stopped = bool(e.code)
check(stopped, 'без отобранных картинок сборка останавливается')
check(os.path.isdir(target) and len(os.listdir(target)) > 0, 'прошлый набор при этом не стёрт')

shutil.rmtree(root)
print(f'\nИТОГО: прошло {passed}, упало {failed}')
sys.exit(1 if failed else 0)
