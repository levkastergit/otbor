"""Собирает datasets/char_v2 для дообучения: все исходные образцы плюс варианты персонажей, отобранные в Otbor.exe.

Модель учится заново на расширенном наборе, прежняя остаётся на месте.

Что попадает в набор и почему:
- исходные образцы целиком, а отобранных у персонажа не больше, чем исходных (минимум MIN_CAP): иначе модель училась бы
  в основном на собственных картинках. Bohacek, Farid, «Nepotistically Trained Generative Image Models Collapse»
  (arXiv 2311.12202): качество падало при многократном дообучении на своих же картинках, поэтому картинки самой
  pd_char_v2 в набор не берутся;
- не больше MAX_PER_JOB вариантов на задание: одинаковые позы не добавляют разнообразия;
- задания со словом invisible пропускаются: модель могла дорисовать мебель, а подпись должна описывать всё видимое
  (docs.bfl.ai/flux_2/flux2_klein_training).

  python retrain.py --kit K
"""
import argparse
import collections
import glob
import json
import os
import shutil
import sys

NEW_LORA = 'pd_char_v2'
MAX_PER_JOB = 2
MIN_CAP = 4


def person(text):
    """Имя в начале описания: 'Masha, a teenage girl…' -> 'Masha'."""
    return text.split(',', 1)[0].strip()


def read_originals(folder):
    """Исходные образцы: [(png, txt)] и сколько их у каждого персонажа. Первое поле подписи — [trigger]."""
    pairs, counts = [], collections.Counter()
    for png in sorted(glob.glob(os.path.join(glob.escape(folder), '*.png'))):
        txt = png[:-4] + '.txt'
        if not os.path.isfile(txt):
            continue
        with open(txt, encoding='utf-8') as f:
            fields = f.read().split(',', 2)
        if len(fields) > 1:
            counts[fields[1].strip()] += 1
        pairs.append((png, txt))
    return pairs, counts


def read_status(results):
    path = os.path.join(results, '_статус.json')
    if not os.path.isfile(path):
        return []
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f).get('jobs', [])


def made_by_new_model(results, job, name):
    path = os.path.join(results, job['group'], job['id'], 'meta.json')
    if not os.path.isfile(path):
        return False
    with open(path, encoding='utf-8') as f:
        return NEW_LORA in (json.load(f).get(name, {}).get('lora') or '')


def picked_file(results, job, name):
    return os.path.join(results, '_Нужные', job['group'], job['id'], name + '.png')


def choose(jobs, rows, results, counts):
    """Возвращает (выбранное [(задание, вариант)], пропущено заданий с мебелью, отсечено лимитом)."""
    ready = {(r['group'], r['id']): r for r in rows if r.get('state') == 'ready'}
    rounds = [[] for _ in range(MAX_PER_JOB)]
    furniture = 0
    for job in jobs:
        row = ready.get((job['group'], job['id']))
        if job.get('lora') != 'char' or row is None:
            continue
        if 'invisible' in job.get('action', ''):
            furniture += 1
            continue
        names = [n for n in row.get('picked') or []
                 if os.path.isfile(picked_file(results, job, n)) and not made_by_new_model(results, job, n)]
        for place, name in enumerate(names[:MAX_PER_JOB]):
            rounds[place].append((job, name))
    # Сначала по варианту на задание: лимит должен срезать повторы поз, а не сами позы.
    taken, chosen, over = collections.Counter(), [], 0
    for group in rounds:
        for job, name in group:
            who = person(job['desc'])
            if taken[who] < max(counts[who], MIN_CAP):
                taken[who] += 1
                chosen.append((job, name))
            else:
                over += 1
    return chosen, furniture, over


def caption(job):
    return f"[trigger], {job['desc']}, {job['action']}, plain white background"


def build(kit):
    source = os.path.join(kit, 'datasets', 'char')
    target = os.path.join(kit, 'datasets', 'char_v2')
    results = os.path.join(kit, 'results')
    with open(os.path.join(kit, 'jobs.json'), encoding='utf-8') as f:
        jobs = json.load(f)['jobs']
    pairs, counts = read_originals(source)
    chosen, furniture, over = choose(jobs, read_status(results), results, counts)
    if not chosen:
        sys.exit('Нечего добавить: среди отобранных нет подходящих картинок персонажей.')
    if os.path.isdir(target):
        shutil.rmtree(target)
    os.makedirs(target)
    for png, txt in pairs:
        shutil.copy2(png, target)
        shutil.copy2(txt, target)
    added = collections.Counter()
    for job, name in chosen:
        shutil.copy2(picked_file(results, job, name), os.path.join(target, name + '.png'))
        with open(os.path.join(target, name + '.txt'), 'w', encoding='utf-8') as f:
            f.write(caption(job))
        added[person(job['desc'])] += 1
    print(f'Исходных образцов: {len(pairs)}, отобранных добавлено: {len(chosen)}')
    for who, count in added.most_common():
        print(f'  {who}: было {counts[who]}, добавлено {count}')
    print(f'Пропущено заданий, где мебель дорисована моделью: {furniture}; отсечено лимитом на персонажа: {over}')
    return len(chosen)


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--kit', required=True)
    build(ap.parse_args().kit)
