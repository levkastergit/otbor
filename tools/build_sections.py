"""Собирает data/sections.json: разделы отбора (персонажи, локации, мини-игры, интерфейс, сюжет) и строку «Куда идёт».

  python tools/build_sections.py <jobs.json> <items_map.json> <SearchLists.csv> <выход.json>

Комнаты и уровни: GameDesign.md §4.2 и §12.1. Предмет попадает в локацию своей первой сцены из SearchLists.csv,
повторы в других сценах показаны в строке «Куда идёт». Если задание не нашло раздел, скрипт останавливается.
"""
import csv
import json
import re
import sys

LEVELS = {
    'P0': ('П0', 'Пустая папка'), 'P1': ('П1', 'Утро'), 'P2': ('П2', 'Утренник'), 'P3': ('П3', 'Бабушкины пауки'),
    'Z1': ('Ж1', 'Физра'), 'Z2': ('Ж2', 'Захлопнулась'), 'Z3': ('Ж3', 'Гараж'),
    'G1': ('Г1', 'Контрольная'), 'G2': ('Г2', 'Говорящий попугай'),
    'D1': ('Д1', 'Чёрная комната'), 'D2': ('Д2', 'Ритуал'),
    'M2': ('М2', 'Зубной'), 'M1': ('М1', 'Двор'), 'M3': ('М3', 'Издатель'),
    'F1': ('Ф1', 'Пять версий'), 'F2': ('Ф2', 'Ночь перед понедельником'), 'E1': ('Э1', 'Поезд'),
}
RU_LEVELS = {ru: (ru, name) for ru, name in LEVELS.values()}

# Комната из SearchLists.csv -> локация и уточнение для строки «Куда идёт» (GameDesign.md §12.1).
ROOMS = {
    'PD1/Room15': ('Приёмная клиники', ''),
    'PD2/Room05': ('Кабинет Папы в клинике', ''),
    'PD2/Room04': ('Гостиная', ''),
    'PD1/Room03': ('Гостиная', ''),
    'PD1/Room06': ('Гостиная', 'у компьютера'),
    'PD1/Room09': ('Гостиная', 'у зеркала'),
    'NEW/PapaRoom': ('Комната Папы', ''),
    'PD1/Room01': ('Детская', ''),
    'PD2/Room01': ('Детская', ''),
    'PD1/Room02': ('Кухня', ''),
    'PD2/Room03': ('Кухня', 'ночь'),
    'PD1/Room04': ('Прихожая', ''),
    'PD2/Room09': ('Прихожая', ''),
    'PD2/Room02': ('Спальня Маши и Даши', ''),
    'PD1/Room05': ('Детский сад', ''),
    'PD1/Room10': ('Комната бабушки', ''),
    'PD1/Room12': ('Комната бабушки', ''),
    'PD1/Room11': ('Лестничная площадка', ''),
    'PD2/Room11': ('Библиотека', ''),
    'PD2/Room14': ('Гараж', 'снаружи'),
    'PD2/Room12': ('Гараж', 'внутри'),
    'PD1/Room08': ('Школьный класс', ''),
    'PD2/Room10': ('Кафе', ''),
    'PD2/Room06': ('Подвал', ''),
    'PD2/Room07': ('Двор', ''),
    'PD2/Room16': ('Кабинет Антонова', ''),
    'PD2/Room18': ('Холл гостиницы', ''),
    'PD2/Room13': ('Купе поезда', ''),
}
LOCATIONS = ['Приёмная клиники', 'Кабинет Папы в клинике', 'Гостиная', 'Комната Папы', 'Детская', 'Кухня', 'Прихожая',
             'Спальня Маши и Даши', 'Детский сад', 'Комната бабушки', 'Лестничная площадка', 'Коридор у библиотеки',
             'Библиотека', 'Гараж', 'Школьный класс', 'Кафе', 'Подвал', 'Двор', 'Кабинет Антонова', 'Холл гостиницы',
             'Купе поезда', 'Пляж']

BACKGROUNDS = {
    'bg_p0_reception': 'Приёмная клиники', 'bg_p0_office': 'Кабинет Папы в клинике', 'bg_hub_living': 'Гостиная',
    'bg_papa_room_story': 'Комната Папы', 'bg_folder_closeup': 'Комната Папы', 'bgB_papa_room_empty': 'Комната Папы',
    'bg_kids_room': 'Детская', 'bg_bedroom': 'Спальня Маши и Даши', 'bg_kitchen': 'Кухня', 'bg_basement': 'Подвал',
    'bg_granny_room': 'Комната бабушки', 'bg_hallway': 'Прихожая', 'bg_cafe': 'Кафе', 'bg_library': 'Библиотека',
    'bg_garage_in': 'Гараж', 'bg_garage_out': 'Гараж', 'bg_train': 'Купе поезда',
    'bg_corridor_lib': 'Коридор у библиотеки', 'bg_dentist': 'Кабинет Антонова', 'bg_hotel': 'Холл гостиницы',
    'bg_kindergarten': 'Детский сад', 'bg_classroom': 'Школьный класс', 'bg_stairwell': 'Лестничная площадка',
    'bg_yard': 'Двор', 'bg_beach': 'Пляж',
}
BACKGROUND_USE = {
    'bg_p0_reception': 'П0, сцена 1', 'bg_p0_office': 'П0, сцена 2', 'bg_hub_living': 'Совет и хаб, Ф1',
    'bg_papa_room_story': 'Стори-кадр 20:10 в П0', 'bg_folder_closeup': 'Стори-кадр 20:10 в П0',
    'bgB_papa_room_empty': 'Ф2, сцена 2', 'bg_kids_room': 'П1, Ф1', 'bg_bedroom': 'Д1, Ф1', 'bg_kitchen': 'П1, Ф2',
    'bg_basement': 'Д2', 'bg_granny_room': 'П3, Ж2', 'bg_hallway': 'П1, Ф1', 'bg_cafe': 'Г2', 'bg_library': 'Ж1',
    'bg_garage_in': 'Ж3, сцена 2', 'bg_garage_out': 'Ж3, сцена 1', 'bg_train': 'Э1',
    'bg_corridor_lib': 'Катсцена Ж1', 'bg_dentist': 'М2, сцена 2', 'bg_hotel': 'М3, катсцена «Понедельник, 9:00»',
    'bg_kindergarten': 'П2', 'bg_classroom': 'Г1, сцена 2', 'bg_stairwell': 'Ж2, сцена 1', 'bg_yard': 'М1',
    'bg_beach': 'Финал Э1',
}
DECOR_PREFIXES = [
    ('dec_rec_', 'Приёмная клиники'), ('dec_off_', 'Кабинет Папы в клинике'), ('dec_hub_', 'Гостиная'),
    ('dec_bed_', 'Спальня Маши и Даши'), ('dec_dresser_', 'Спальня Маши и Даши'), ('dec_wardrobe_', 'Спальня Маши и Даши'),
    ('dec_kids_', 'Детская'), ('dec_kitchen_', 'Кухня'), ('dec_shoe_', 'Прихожая'), ('dec_toybox_', 'Детский сад'),
    ('dec_bureau_', 'Комната бабушки'), ('dec_metal_', 'Гараж'), ('dec_glass_', 'Кабинет Антонова'),
    ('dec_dentist_', 'Кабинет Антонова'), ('dec_cafe_', 'Кафе'), ('dec_school_', 'Школьный класс'),
    ('dec_lib_', 'Библиотека'), ('dec_yard_', 'Двор'), ('dec_beach_', 'Пляж'), ('dec_deck_', 'Пляж'),
]
# Реквизит вне списков поиска: комната и сцена взяты из поля where в jobs.json.
MISC = {
    'folder_blue_full': ('Комната Папы', 'Ф2, на столе Папы'),
    'proom_desk': ('Комната Папы', 'Ф2, сцена 2'), 'proom_drawer_open': ('Комната Папы', 'Ф2, сцена 2, открываемый ящик'),
    'proom_chair': ('Комната Папы', 'Ф2, сцена 2'), 'proom_lamp': ('Комната Папы', 'Ф2, сцена 2'),
    'proom_books': ('Комната Папы', 'Ф2, сцена 2'), 'gift_parrot_feather': ('Комната Папы', 'Ф2, силуэты «подарки дочерей»'),
    'paper_hat': ('Детский сад', 'П2, шляпы из страниц 96–98'), 'cage_pages': ('Кафе', 'Г2, страницы 41–50 в клетке'),
    'crow': ('Двор', 'М1, только если нужна в сцене'), 'papa_home_computer': ('Гостиная', 'Хаб и Ф1, папин стол'),
    'antonov_printer_jam': ('Кабинет Антонова', 'М2, сцена 2'), 'masha_handbag': ('Прихожая', 'Ф1, сцена 2'),
    'zhenya_sneakers_pink_laces': ('Прихожая', 'Ф1, сцена 2'),
}
MINIGAMES = [
    ('Ежедневник', 'П0', ('mg_diary_',)), ('Паровозик', 'П2', ('mg_rail_',)), ('Террариум', 'П3', ('mg_terra_',)),
    ('Морской бой', 'Ж1', ('mg_sea_',)), ('Связка ключей', 'Ж2', ('mg_key_', 'mg_lock_')),
    ('Кодовый замок', 'Ж3', ('mg_code_', 'ui_codelock_')), ('Синхрофазотрон', 'Г1', ('mg_circ_',)),
    ('Кроссворд', 'Г2', ('mg_cross_',)), ('Шкаф', 'Д1', ('mg_ward_',)), ('Пентаграмма', 'Д2', ('mg_penta_',)),
    ('Челюсть', 'М2', ('mg_jaw_', 'mg_tweezers')), ('Шахматный этюд', 'М1', ('mg_chess_',)),
    ('Кофейный автомат', 'М3', ('mg_plinko_', 'plinko_')), ('Доска улик', 'Ф1', ('mg_evid_',)),
    ('Рукопись', 'Ф2', ('mg_page_', 'mg_book_', 'page_')), ('Домино', 'Э1', ('mg_domino_',)),
]
# Порядок: семья, затем остальные по порядку появления в игре. Шаблоны проверяются сверху вниз.
PEOPLE = [
    ('Папа', r'^(char_papa_|port_papa_|papa_)'), ('Маша', r'(^|_)(masha|mas)(_|$)'), ('Даша', r'(^|_)(dasha|das)(_|$)'),
    ('Женя', r'(^|_)(zhenya|zhe)(_|$)'), ('Галина Сергеевна', r'(^|_)gs(_|$)'), ('Пуговка', r'(^|_)(pugovka|pug)(_|$)'),
    ('Тамара (приёмная)', r'tamara'), ('Бабушка', r'babushka'), ('Миша', r'port_misha'),
    ('Физрук', r'fizruk'), ('Т. Ивановна (учительница)', r'uchilka'), ('Кассир кафе', r'kassir'),
    ('Посетитель кафе', r'posetitel'), ('Полежайкин', r'polezhaikin'), ('Паук', r'pauk'), ('Петя', r'petya'),
    ('Антонов', r'antonov'), ('Дедушка', r'dedushka'), ('Монтёр', r'monter'), ('Администратор гостиницы', r'admin'),
    ('Издатель', r'izdatel|^publisher$'), ('Сосед по купе', r'sosed'),
]
CHARACTER_GROUPS = ('30_char_p0', '31_portraits', '80_chars2', '81_ports2')
CHARACTER_USE = {
    'char_papa_stand': 'П0, обе сцены', 'char_papa_pack': 'П0, сцена 1', 'char_papa_phone': 'П0, сцена 2',
    'char_papa_shock_folder': 'П0, сцена 20:10 и совет', 'char_tamara_desk': 'П0, сцена 1',
    'babushka_sit_sleep': 'П3, модификатор шума', 'babushka_sit_awake': 'П3, модификатор шума',
    'babushka_white_jacket': 'Ж2, сцена 2', 'masha_sleep_bed': 'Д1', 'masha_woken_bed': 'Д1',
    'izdatel_sit_read': 'М3, холл гостиницы', 'publisher': 'М3, катсцена «Понедельник, 9:00», Ф1',
    'papa_read_book': 'Финал Э1, пляж', 'papa_sofa_sleep_shirt': 'П1, Г1 сцена 1, М2 сцена 1, гостиная',
    'papa_sofa_wake_shirt': 'П1, карточка «8:05»', 'uchilka_board_back': 'Г1, сцена 2, класс',
    'dasha_wax_walk': 'М1, катсцена', 'pauk_wax_walk': 'М1, катсцена', 'petya_wax_walk': 'М1, катсцена',
    'polezhaikin_train_belt': 'Э1, купе',
}
MECHANICS = {
    'mech_kid_chair': 'П1, опора Пуговки (модификатор «Рост»)', 'mech_taburet': 'П1, опора Пуговки (модификатор «Рост»)',
    'mech_blocks_set': 'П2, кубики Миши', 'mech_zzz': 'П3, символ шума (модификатор «Тихо!»)',
}
INTERFACE = {'10_ui_search': 'Экран поиска', '11_ui_windows': 'Окна', '12_ui_menu': 'Меню', '13_ui_cursors': 'Курсоры'}


def load(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def clean(text):
    return re.sub(r'\s*\([^)]*\)', '', text).strip().lower()


def scene_rows(jobs, items, rows):
    """Для каждого задания — строки SearchLists.csv в порядке игры, по карте предметов и подписям заданий."""
    by_name = {}
    for name, target in items.items():
        by_name[clean(name)] = target['job']
    for job in jobs:
        for name in job['ru'].split(';'):
            by_name.setdefault(clean(name), job['id'])
    used = {}
    for row in rows:
        job = by_name.get(clean(row['DisplayName'])) or by_name.get(clean(row['Item']))
        if job:
            used.setdefault(job, []).append(row)
    return used


def place_text(row):
    level = LEVELS[row['Level']]
    location, part = ROOMS[row['Room']]
    return f"{level[0]}, {location[0].lower() + location[1:]}" + (f' ({part})' if part else '')


def prop_use(job_rows):
    first = job_rows[0]
    places = []
    for row in job_rows:
        text = place_text(row)
        if text not in places:
            places.append(text)
    text = f"{places[0]}, список «{first['ListTitle']}»"
    if len(places) > 1:
        text += '; ' + '; '.join(places[1:3])
    if len(places) > 3:
        text += f'; ещё {len(places) - 3}'
    return text


def character_use(job):
    if job['id'] in CHARACTER_USE:
        return CHARACTER_USE[job['id']]
    if job['group'] in ('31_portraits', '81_ports2'):
        return 'Портрет говорящего в окне сценок' + (' и в HUD' if 'HUD' in job['where'] else '')
    if job['where'] == 'П0 20:15, хаб':
        return 'Семейный совет и хаб'
    if job['id'].startswith('hub_'):
        return 'Хаб: дочь уснула после своего показания'
    found = re.search(r'\(([ПЖГДМФЭ]\d)', job['ru'])
    if found:
        code, name = RU_LEVELS[found.group(1)]
        return f'{code} «{name}»'
    sys.exit(f'{job["id"]}: не определено, куда идёт персонаж')


def classify(job, used):
    """Возвращает (вид, раздел, вес внутри раздела, куда идёт)."""
    i, group = job['id'], job['group']
    if group in CHARACTER_GROUPS:
        for name, pattern in PEOPLE:
            if re.search(pattern, i):
                return 'Персонажи', name, 1 if i.startswith('port_') else 0, character_use(job)
        sys.exit(f'{i}: не определён персонаж')
    if i in BACKGROUNDS:
        return 'Локации', BACKGROUNDS[i], 0, 'Фон: ' + BACKGROUND_USE[i]
    if i in MISC:
        return 'Локации', MISC[i][0], 2, MISC[i][1]
    for name, level, prefixes in MINIGAMES:
        if i.startswith(prefixes):
            return 'Мини-игры', name, 0, f'Мини-игра «{name}», {level}'
    if i == 'ui_noise_meter':
        return 'Интерфейс', 'Экран поиска', 0, 'П3, модификатор «Шум»'
    if i == 'ui_app_icon':
        return 'Интерфейс', 'Меню', 0, 'Иконка окна и exe'
    if group in INTERFACE:
        return 'Интерфейс', INTERFACE[group], 0, job['where'].split(';')[0] if group == '13_ui_cursors' else INTERFACE[group]
    if i in MECHANICS:
        return 'Сюжет', 'Механики уровней', 0, MECHANICS[i]
    if i.startswith('folder_blue'):
        return 'Сюжет', 'Синяя папка', 0, {'весь сюжет': 'Весь сюжет', 'П0 «Пусто.»': 'П0, реплика «Пусто.»',
                                           'стори-кадры, финал': 'Стори-кадры и финал'}[job['where']]
    if i.startswith('kid_drawing'):
        return 'Сюжет', 'Рисунки Пуговки', 0, 'П2, оборот шляп; Ф2, выбор рисунка на обложку'
    for prefix, location in DECOR_PREFIXES:
        if i.startswith(prefix):
            use = 'Декор этой комнаты' if job['where'] == 'декор, план v4' else job['where'].split(';')[0]
            return 'Локации', location, 1, use
    if i in used:
        first = used[i][0]
        return 'Локации', ROOMS[first['Room']][0], 2, prop_use(used[i])
    fallback = {'40_props_reception': ('Приёмная клиники', 'П0, сцена 1'),
                '41_props_office': ('Кабинет Папы в клинике', 'П0, сцена 2')}.get(group)
    if fallback:
        return 'Локации', fallback[0], 2, fallback[1]
    return None


def main():
    jobs_path, items_path, csv_path, out = sys.argv[1:5]
    jobs = load(jobs_path)['jobs']
    items = load(items_path)
    with open(csv_path, encoding='utf-8-sig', newline='') as f:
        rows = list(csv.DictReader(f))
    used = scene_rows(jobs, items, rows)

    kinds = {'Персонажи': [p[0] for p in PEOPLE], 'Локации': LOCATIONS, 'Мини-игры': [m[0] for m in MINIGAMES],
             'Интерфейс': ['Экран поиска', 'Окна', 'Меню', 'Курсоры'], 'Сюжет': ['Синяя папка', 'Рисунки Пуговки', 'Механики уровней']}
    buckets = {(kind, name): [] for kind, names in kinds.items() for name in names}
    missing = []
    for order, job in enumerate(jobs):
        found = classify(job, used)
        if found is None:
            missing.append(job['id'] + ' — ' + job['ru'])
            continue
        kind, name, weight, use = found
        buckets[(kind, name)].append((weight, order, job['id'], use))
    if missing:
        sys.exit('Задания без раздела:\n' + '\n'.join(missing))

    sections = []
    for (kind, name), entries in buckets.items():
        if not entries:
            sys.exit(f'Пустой раздел: {kind} / {name}')
        sections.append({'kind': kind, 'name': name,
                         'jobs': [{'id': e[2], 'goes': e[3]} for e in sorted(entries)]})
    placed = sum(len(s['jobs']) for s in sections)
    if placed != len(jobs):
        sys.exit(f'Разложено {placed} из {len(jobs)}')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'sections': sections}, f, ensure_ascii=False, indent=0)
        f.write('\n')
    for s in sections:
        print(f"{s['kind']:<10} {s['name']:<32} {len(s['jobs'])}")
    print('Всего', placed)


if __name__ == '__main__':
    main()
