"""Проверка логики generate.py без видеокарты: Gen подменён, настоящая генерация заменена записью имён и seed."""
import importlib.util
import json
import os
import shutil
import sys
import tempfile

spec = importlib.util.spec_from_file_location('generate', sys.argv[1])
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)

passed = failed = 0


def check(ok, name):
    global passed, failed
    if ok:
        passed += 1
    else:
        failed += 1
    print(('ok    ' if ok else 'FAIL  ') + name)


calls = []


class FakeGen:
    def use_lora(self, path):
        pass

    def run(self, prompt, size, seed, refs=None):
        calls.append((prompt, seed))
        from PIL import Image
        return Image.new('RGB', (32, 32), 'white')


g.Gen = FakeGen
g.save_alpha = lambda raw, tol: (raw[:-4] + '_alpha.png', True)
g.write_index = lambda kit, data: None
g.pick_lora = lambda runtime, name, step: 'lora.safetensors'
remote = {}
g.fetch_repo_json = lambda path: remote.get(path)

root = tempfile.mkdtemp()
kit = os.path.join(root, 'kit')
results = os.path.join(kit, 'results')
os.makedirs(os.path.join(kit, 'refs'))
open(os.path.join(kit, 'refs', 'r.png'), 'wb').close()
from PIL import Image
Image.new('RGB', (8, 8)).save(os.path.join(kit, 'refs', 'r.png'))


def job(i, group='30_a', **extra):
    return {'id': i, 'group': group, 'lora': 'char', 'desc': 'd', 'action': 'a', 'modes': ['ref'], 'refs': ['refs/r.png'], **extra}


data = {'loras': {'char': 'c', 'prop': 'p', 'bg': 'b'}, 'triggers': {'char': 't', 'prop': 't', 'bg': 't'},
        'jobs': [job('good'), job('bad'), job('fresh'), job('bad2'), job('other', group='31_b'), job('good_long')]}
with open(os.path.join(kit, 'jobs.json'), 'w', encoding='utf-8') as f:
    json.dump(data, f)
with open(os.path.join(kit, 'ПРОПУСКИ.txt'), 'w', encoding='utf-8') as f:
    f.write('bad2__ref_6\n')


def run(*args):
    calls.clear()
    sys.argv = ['generate.py', '--kit', kit, '--runtime', 'x'] + list(args)
    g.main()
    return list(calls)


def touch(*parts):
    p = os.path.join(results, *parts)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    Image.new('RGB', (8, 8)).save(p)


status = {'jobs': [
    {'id': 'good', 'group': '30_a', 'state': 'ready', 'picked': ['good__ref_1'], 'tried': ['good__ref_1'], 'at': '2026-11-01 10:00:00'},
    {'id': 'bad', 'group': '30_a', 'state': 'regen', 'picked': [], 'tried': ['bad__ref_1', 'bad__ref_2', 'bad__ref_3', 'bad__ref_4'],
     'at': '2026-11-01 10:01:00', 'note': 'лицо не то'},
    {'id': 'bad2', 'group': '30_a', 'state': 'regen', 'picked': [], 'tried': [f'bad2__ref_{i}' for i in range(1, 5)], 'at': '2026-11-01 10:01:00'},
    {'id': 'other', 'group': '99_old', 'state': 'ready', 'picked': [], 'tried': [], 'at': '2026-11-01 10:01:00'},
]}
remote['status/status.json'] = status
remote['regen/overrides.json'] = {'bad': {'action': 'NEW_ACTION'}}

base_bad = g.job_seed('bad')
base_good = g.job_seed('good')

# Полный запуск
out = run('--variants', '2')
ids = {p for p, s in out}
seeds = {s for p, s in out}
check(base_good not in seeds and base_good + 1 not in seeds, 'готовое задание не генерируется заново')
check(base_bad in seeds, 'задание regen в обычном запуске идёт как обычно (его файлов на этом ПК нет)')
check(g.job_seed('good_long') in seeds, 'совпадение начала имени не считается готовностью')
check(g.job_seed('other') in seeds, 'запись другой группы к этому заданию не относится')
check(os.path.isdir(os.path.join(results, '31_b', 'other')), 'задание другой группы создано')

# Второй полный запуск: всё на месте
out = run('--variants', '2')
check(out == [], 'повторный запуск ничего не делает')

# Подготовка к перегенерации: разобранные файлы ушли
for jid in ('bad', 'bad2'):
    for n in (1, 2):
        for suffix in ('.png', '_alpha.png'):
            os.remove(os.path.join(results, '30_a', jid, f'{jid}__ref_{n}{suffix}')) if os.path.exists(os.path.join(results, '30_a', jid, f'{jid}__ref_{n}{suffix}')) else None
        touch('_Ненужные', '30_a', jid, f'{jid}__ref_{n}.png')

out = run('--regen', '--variants', '4')
seeds = sorted(s - base_bad for p, s in out if s - base_bad in range(0, 20))
check(seeds == [4, 5, 6, 7], f'новые seed идут после отвергнутых: {seeds}')
check(all('NEW_ACTION' in p for p, s in out if s - base_bad in range(4, 8)), 'правка промпта из regen/overrides.json применена')
base2 = g.job_seed('bad2')
check(sorted(s - base2 for p, s in out if s - base2 in range(0, 20)) == [4, 6, 7], 'шаблон ПРОПУСКИ.txt действует и на новые номера')
check(os.path.isfile(os.path.join(results, '30_a', 'bad', 'bad__ref_5.png')), 'файл получил новый номер ref_5')
check(not os.path.exists(os.path.join(results, '30_a', 'bad', 'bad__ref_1.png')), 'старый номер не занят заново')
check(len({s for p, s in out}) == len(out), 'seed не повторяются')

out = run('--regen', '--variants', '4')
check(out == [], 'пока новые варианты не разобраны, вторая перегенерация не запускается')

# Перегенерация на другом ПК: файлов нет, номера берутся из tried
shutil.rmtree(results)
out = run('--regen', '--variants', '2')
check(sorted(s - base_bad for p, s in out if s - base_bad in range(0, 20)) == [4, 5], 'на другом ПК номера берутся из записи отбора')

# Локальная запись свежее удалённой
shutil.rmtree(results)
os.makedirs(results)
with open(os.path.join(results, '_статус.json'), 'w', encoding='utf-8') as f:
    json.dump({'jobs': [{'id': 'bad', 'group': '30_a', 'state': 'ready', 'picked': ['bad__ref_5'], 'tried': [], 'at': '2026-11-01 11:00:00'}]}, f)
out = run('--regen', '--variants', '2')
check(all(s - base_bad not in range(0, 20) for p, s in out), 'более свежее локальное решение важнее удалённого')

# Нечего перегенерировать: модель не загружается
remote['status/status.json'] = {'jobs': []}
loaded = []
g.Gen = lambda: loaded.append(1) or FakeGen()
out = run('--regen')
check(out == [] and not loaded, 'нечего перегенерировать — модель не загружается')

# GitHub недоступен, локального файла нет: обычный запуск работает, готовое определяется по файлам
del remote['status/status.json']
os.remove(os.path.join(results, '_статус.json'))
g.Gen = FakeGen
touch('_Нужные', '30_a', 'good', 'good__ref_1.png')
out = run('--variants', '1')
check(base_good not in {s for p, s in out}, 'без сети готовое определяется по папке _Нужные')

# --force по-прежнему пересиливает готовность только по явной просьбе
remote['status/status.json'] = status
out = run('--only', 'good', '--variants', '1', '--force')
check(base_good in {s for p, s in out}, '--force делает готовое заново')

# Настоящий запрос к GitHub
g2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g2)
print('живой запрос: файла нет ->', g2.fetch_repo_json('no/such/file.json'))

shutil.rmtree(root)
print(f'\nИТОГО: прошло {passed}, упало {failed}')
sys.exit(1 if failed else 0)
