"""Restore Wiki.js pages from backed-up Russian prose and current game prototypes.

Builds a reviewable plan; does not contact or modify Wiki.js. The publication
step is separate, resumable and preserves the original remote page snapshots.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import html
import json
from pathlib import Path
import re
import shutil
import subprocess
import tomllib

from game_data import GameData, slug, source_path
from reference_blocks import Blocks, E, GROUPS
from article_filters import CHEMICALS_PATH, filter_chemicals
from editorial_content import article_body, article_title, protected_page
from guide_directory import GUIDES_PATH, DIRECTORY_CSS, directory
from controls_reference import ControlsReference, CONTROLS_PATH, CONTROLS_CSS
from editorial_cleanup import clean_article, prepare_embeds, refresh_generated, layout_species
from prose_editorial import editorial_prose
from weapon_reference import build_weapon_reference, CATALOGUES, PREFIX as WEAPONS_PREFIX
from industrial_reference import build_industrial_reference
from book_sources import BOOK_SOURCE_PATHS, DEFERRED_BOOKS


EDITORIAL = {
    'guidebook/in_game/chemicals': '''Химические вещества можно синтезировать, извлекать из предметов или получать из готовых запасов. Здесь собраны реагенты из текущих прототипов DeepLagoon.

Откройте карточку вещества: в ней указаны ингредиенты, выход продукта, катализаторы и условия реакции. Не переносите рецепт из другой сборки, если он отличается от приведённого здесь.

## Как читать рецепты

- Количества заданы в единицах реагента и масштабируются с сохранением пропорций.
- Катализатор должен присутствовать в указанном количестве, но не расходуется.
- Температура в прототипах измеряется в кельвинах; рядом приведён пересчёт в градусы Цельсия.
- Встряхивание, перемешивание и другие способы смешивания — отдельные требования.
- Реакции, создающие предметы, могут требовать целую порцию ингредиентов.

## Группы реагентов
''',
    'guidebook/in_game/medical/chemist': '''Химик готовит реагенты и лекарства для экипажа. Начните с чистой ёмкости, проверьте рецепт и заранее согласуйте с врачами, что нужно медицинскому отделу.

## Рабочее место

Раздатчик выдаёт базовые реагенты. ХимМастер помогает разделять смесь и фасовать её. Измельчитель нужен для извлечения веществ из подходящих предметов. Держите рядом стаканы, ёмкости для таблеток и маркировщик.

## Первая партия

1. Проверьте состав пустого стакана и установите его в раздатчик.
2. Найдите рецепт в справочнике реагентов. Сначала проверьте пропорции, температуру и способ смешивания.
3. Добавьте компоненты по этапам. Промежуточный реагент тоже занимает объём в ёмкости.
4. Проверьте получившийся состав. Один только цвет смеси не подтверждает её чистоту.
5. Для фасовки переставьте стакан в ХимМастер и перенесите нужный реагент в буфер.
6. На вкладке вывода задайте количество и дозу таблеток либо наполните бутылку. Подпишите содержимое.

## Контроль перед выдачей

Проверьте название, состав, объём и выбранную дозу. Лимит дозы таблетки у ХимМастера — техническое ограничение устройства, а не рекомендация для лечения. Действие и пороги реагента смотрите в его карточке.

[Практика: 30 единиц бикаридина](/ru/guidebook/in_game/chemistry/bicaridine).
''',
    'guidebook/in_game/service/bartender': '''Бармен готовит и подаёт напитки. Посетителям нужны чистая посуда, правильный состав заказа и понятное название готового коктейля.

## Оборудование

Ингредиенты можно брать из раздатчиков алкоголя и газировки, а также торгового автомата. Для точных мер пригодится джиггер, для рецептов со встряхиванием — шейкер.

## Выполнение заказа

1. Найдите напиток в справочнике и проверьте соотношение компонентов.
2. Подготовьте пустую ёмкость. Остатки предыдущего напитка считаются частью новой смеси.
3. Отмерьте ингредиенты, сохраняя пропорции.
4. Выполните указанный способ смешивания. Некоторые коктейли не образуются от простого переливания.
5. Проверьте состав и перелейте напиток в посуду для подачи.

## Пример: мартини

20 единиц джина и 10 единиц вермута дают 30 единиц мартини. Для реакции требуется встряхивание: возьмите шейкер и используйте действие «Встряхнуть» в контекстном меню. Дождитесь завершения действия.

Полные рецепты доступны в справочнике напитков. Книжное руководство бармена открывается отдельно и имеет оформление печатного издания.
''',
    'guidebook/in_game/reference-tables/drinks': '''В этом справочнике перечислены напитки текущей сборки и способы их получения. Откройте нужную карточку, чтобы увидеть все найденные рецепты, количество продукта и требования к смешиванию.

Проверяйте не только пропорции: некоторые рецепты требуют встряхивания, перемешивания или специальной обработки. Катализаторы указываются отдельно от расходуемых ингредиентов.
''',
    'guidebook/in_game/service/food-recipes': '''Рецепты кухни собраны из действующих прототипов. Для каждого блюда указаны твёрдые ингредиенты, жидкости, время приготовления, подходящее оборудование и количество результата.

Не все эти рецепты готовятся в микроволновке: используйте именно то устройство, которое указано в карточке. Скрытые в игровом руководстве рецепты и закомментированные определения не публикуются как доступные.
''',
    'guidebook/in_game/imported/nf/medical/medical-recipes': '''Этот раздел показывает рецепты медицинского сборщика, доступные в текущей сборке. Список формируется из действующих рецептов, а не из старых инструкций Frontier.

Если рецепт отключён или закомментирован в прототипах, он не считается доступным. Материалы и средства лечения проверяйте по актуальным карточкам и справочнику реагентов.
''',
}

EXTRA_CSS = '''
.dlwiki.theme-b.dl-book-page{--bg:#091f29;--surface:#122f3a;--ink:#e3eff1;--muted:#9cb6be;--line:#284650;--accent:#39cdbd;background:var(--bg)!important;color:var(--ink)!important}
.dlwiki.dl-book-page .book{--bg:#fffaf0;--surface:#f5efdf;--ink:#36342b;--muted:#71634f;--line:#d8c9aa;--accent:#80633b;background:#fffaf0!important;color:#36342b!important}
.dlwiki.dl-book-page .book :is(h1,h2,h3,h4,h5,h6){color:#36342b!important;font-family:Georgia,"Times New Roman",serif!important;background:transparent!important}
.dlwiki.dl-book-page .book :is(.dl-reagent,.dl-food,.dl-reaction,.dl-entity,details,summary){background:#f5efdf!important;color:#36342b!important;border-color:#d8c9aa!important;box-shadow:none!important}
.dlwiki.dl-book-page .book summary{padding:14px 18px!important;border-radius:0!important;font-family:Georgia,"Times New Roman",serif!important}
.dlwiki.dl-book-page .book :is(p,li,td,th,strong,b){color:inherit}
.dlwiki.dl-book-page .book :is(table,thead,tbody,tr,td,th){background:transparent!important;border-color:#d8c9aa!important}
.dlwiki.dl-book-page .book a{color:#78562d!important}

.dlwiki :is(h1,h2,h3,h4){color:var(--ink);padding:0;border:0}
.dlwiki :is(h1,h2,h3,h4):after{display:none!important}
.dlwiki .toc-anchor{display:none!important}
.dlwiki summary{background:transparent;color:var(--ink);border:0}
.dlwiki details{color:var(--ink)}
.dlwiki .hero-art>img{height:100%!important;width:100%!important;max-height:none!important;object-fit:cover!important;margin:0!important}
.dlwiki.theme-a .btn.dl-primary{background:var(--accent)!important;border-color:var(--accent)!important;color:#0d2425!important}
.dlwiki .dl-entity{display:flex;align-items:center;gap:18px;padding:18px;border:1px solid var(--line);background:var(--surface);margin:12px 0;break-inside:avoid}
.dlwiki .dl-entity strong{font-size:16px}.dlwiki .dl-entity p{font-size:13px;line-height:1.65;margin:5px 0;color:var(--muted)}.dlwiki .dl-entity small{color:var(--accent)}
.dlwiki .dl-sprite{display:inline-block;position:relative;flex:0 0 72px;width:72px;height:72px;vertical-align:middle;image-rendering:pixelated}
.dlwiki .dl-sprite>span{position:absolute;inset:0;background-repeat:no-repeat;background-position:0 0}
.dlwiki .dl-entities{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.dlwiki .dl-entities .dl-entity{margin:0;min-width:0}
.dlwiki .dl-reagent{border:1px solid var(--line);padding:0;margin:10px 0;background:var(--surface);scroll-margin-top:20px}
.dlwiki .dl-reagent>summary{padding:15px 18px;line-height:1.5}.dlwiki .dl-reagent-body{padding:0 20px 20px;min-width:0;font-size:14px}.dlwiki .dl-reagent-body p{overflow-wrap:anywhere}
.dlwiki .dl-dot{display:inline-block;width:11px;height:11px;margin:0 10px 0 0;border:1px solid #8886;border-radius:50%}
.dlwiki .dl-reaction{border:1px solid var(--line);margin:14px 0;padding:16px}.dlwiki .dl-equation{display:grid;grid-template-columns:minmax(0,1fr) 24px minmax(0,1fr);gap:14px;align-items:center;font-size:13px;line-height:1.8}
.dlwiki .dl-equation>b{color:var(--accent);text-align:center}.dlwiki .dl-reagent a,.dlwiki .prose>p a{color:var(--accent);text-decoration:underline;text-underline-offset:3px}
.dlwiki .dl-catalyst{font-size:12px;padding-top:14px;color:var(--warm)}.dlwiki .dl-conditions{font-size:12px;color:var(--muted);margin:12px 0 0}
.dlwiki .dl-food{border:1px solid var(--line);margin:10px 0;padding:0;background:var(--surface)}.dlwiki .dl-food>summary{padding:8px 16px;line-height:1.6}.dlwiki .dl-food>summary .dl-sprite{width:48px;height:48px;margin-right:16px;vertical-align:middle}
.dlwiki .dl-table{width:100%;overflow-x:auto}.dlwiki .dl-table td{overflow-wrap:anywhere}.dlwiki .dl-table table{min-width:440px}
.dlwiki .book .prose h2{font:normal 29px/1.25 Georgia,serif}.dlwiki .book .prose h3{font:normal 23px/1.3 Georgia,serif}.dlwiki .book .dl-reagent>summary{font-family:Georgia,serif;font-size:17px}.dlwiki .book .dl-reagent-body{font-family:Georgia,serif;line-height:1.8}.dlwiki .book .dl-food>summary{font-family:Georgia,serif}.dlwiki .book .dl-entity{background:transparent}.dlwiki .book .dl-entity p{font-family:Georgia,serif}
.dlwiki .book .dl-reaction{background:transparent}.dlwiki .dl-reference-index{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px;margin:22px 0}.dlwiki .dl-reference-index a{display:block;border-bottom:1px solid var(--line);padding:12px 4px;color:var(--accent)}
.dlwiki .dl-reference-index small{display:block;color:var(--muted)}.dlwiki .dl-metadata{font-size:11px;color:var(--muted);border-top:1px solid var(--line);padding:18px 0;margin-top:32px;overflow-wrap:anywhere}
.dlwiki .dl-chapter{padding-top:20px;margin-top:30px;border-top:3px double var(--line)}.dlwiki .dl-chapter>h2{margin-top:0}
.dlwiki .article-grid>.prose{width:100%;max-width:100%;overflow-wrap:anywhere}.dlwiki .dl-controls-table{width:100%;max-width:100%}.dlwiki .article-head>div{min-width:0;overflow-wrap:anywhere}.dlwiki .book{min-width:0;max-width:100%}
.dlwiki .prose pre{max-width:100%;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere}.dlwiki .prose code{white-space:pre-wrap;overflow-wrap:anywhere}
.dlwiki .dl-species-portrait{display:inline-flex;flex-direction:column;align-items:center;vertical-align:top;gap:14px;width:180px;max-width:100%;padding:20px;margin:0 18px 24px 0!important;border:1px solid var(--line);background:var(--surface)}.dlwiki .dl-species-portrait .dl-sprite{width:128px;height:128px;flex-basis:128px}.dlwiki .dl-species-portrait figcaption{font-size:14px;line-height:1.5;color:var(--ink);text-align:center}
@media(max-width:700px){.dlwiki .dl-entities,.dlwiki .dl-reference-index{grid-template-columns:1fr}.dlwiki .dl-equation{gap:6px;font-size:12px;grid-template-columns:minmax(0,1fr) 16px minmax(0,1fr)}.dlwiki .dl-reaction{padding:12px}.dlwiki .dl-reagent-body{padding:0 12px 14px}.dlwiki .dl-entity{padding:12px;gap:12px}.dlwiki .dl-entity .dl-sprite{flex-basis:56px;width:56px;height:56px}.dlwiki .book .dl-reagent>summary{padding:12px;font-size:16px}}
'''


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--station', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--design', type=Path, required=True)
    parser.add_argument('--refresh-book-links', action='store_true', help='Rebuild reviewed book copies from their guide topics/wiki sources, including existing SOP copies; source articles and deferred books remain protected')
    args = parser.parse_args()
    station = args.station.resolve()
    output = args.folder.resolve()
    data = GameData(station, output / 'prototypes.json')
    assert not data.duplicates, data.duplicates
    blocks = Blocks(data, output)
    controls = ControlsReference(station)
    snapshots = {p['path']: p for file in sorted((output / 'before').glob('*.json')) if (p := read_json(file))}
    def canonical_source(source):
        proposed = source_path(source, station)
        if proposed in snapshots:
            return proposed
        matches = [path for path in snapshots if path.startswith('guidebook/') and path.rsplit('/', 1)[-1] == slug(source.stem)]
        return matches[0] if len(matches) == 1 else proposed
    docs = {}
    for source in (station / 'Resources/ServerInfo').rglob('*.xml'):
        if 'Guidebook' in source.parts:
            docs[canonical_source(source)] = source
    for value in data.raw['guideEntry'].values():
        source = station / ('Resources' + value.get('text', ''))
        if source.is_file():
            docs[canonical_source(source)] = source
    guides = {}
    for identifier in data.raw['guideEntry']:
        value = data.get('guideEntry', identifier)
        source = station / ('Resources' + value.get('text', ''))
        if source.is_file():
            guides[identifier] = canonical_source(source)
    by_stem = defaultdict(list)
    for path, source in docs.items():
        by_stem[slug(source.stem)].append(path)
    aliases = {'TEG': 'ThermoelectricGenerator', 'ThermoelectricGenerator': 'TEG',
               'Psionics': 'PsionicsGuide', 'YourFirstCharacter': 'CharacterCreation',
               'AirAlarm': 'AirAlarms', 'AirVents': 'AirVent',
               'MonolithRuleRoleplayEightSafeZones': 'MonolithRuleRoleplaySevenSafeZones',
               'SS14': 'NF14', 'SecurityWeapons': 'WeaponsSystems',
               'SpaceLawCrimeList': 'SpaceLaw'}
    collection_ids = {'EinsteinEngines': 'Einstein Engines', 'HephaestusIndustries': 'Hephaestus Industries',
                      'SolAlliance': 'Альянс Солнца', 'TCFL': 'TCFL',
                      'ZavodskoiInterstellar': 'Zavodskoi Interstellar', 'ZengHuPharmaceuticals': 'Zeng-Hu Pharmaceuticals'}
    unresolved = set()
    def guide_path(identifier):
        if identifier in guides:
            return guides[identifier]
        alternate = aliases.get(identifier)
        if alternate and alternate in guides:
            return guides[alternate]
        if alternate and len(by_stem.get(slug(alternate), [])) == 1:
            return by_stem[slug(alternate)][0]
        choices = by_stem.get(slug(identifier), [])
        if len(choices) == 1:
            return choices[0]
        if identifier not in collection_ids:
            unresolved.add(identifier)
        return 'guidebook/in_game/reference/' + slug(identifier)

    pending = []
    for path, source in sorted(docs.items()):
        old = snapshots.get(path)
        if not old:
            raw = source.read_text(encoding='utf-8-sig')
            plain = re.sub(r'<[^>]+>', '', raw)
            if len(re.findall('[А-Яа-яЁё]', plain)) > len(re.findall('[A-Za-z]', plain)):
                text = '\n'.join(line.strip() for line in plain.splitlines()).strip()
                title = re.search(r'(?m)^#\s+(.+)$', text)
                snapshots[path] = {'path':path,'title':title[1] if title else source.stem,
                                   'content':text,'tags':[]}
                continue
            # A new source needs an editorial translation; never silently publish English.
            pending.append({'path': path, 'source': str(source), 'reason': 'no existing Russian prose'})
            continue
    (output / 'pending-translations.json').write_text(json.dumps(pending, ensure_ascii=False, indent=2), encoding='utf-8')
    body_cache = {}
    used_embeds = Counter()
    link_targets = defaultdict(set)
    def body(path):
        if path in body_cache:
            return body_cache[path]
        source = docs.get(path)
        old = snapshots.get(path, {})
        text = article_body(old['content']) if old.get('content') else EDITORIAL.get(path, '')
        if protected_page(path):
            body_cache[path] = text
            return text
        weapon_html = build_weapon_reference(path, data, blocks)
        if weapon_html is not None:
            body_cache[path] = weapon_html
            return weapon_html
        industrial_html = build_industrial_reference(path, data, blocks)
        if industrial_html is not None:
            body_cache[path] = industrial_html
            return industrial_html
        text = controls.editorial_body(path, text)
        text = editorial_prose(path, text, data)
        text = clean_article(path, text, data)
        text = re.sub(r'^\s*#\s+[^\n]+\n?', '', text, count=1)
        text = re.sub(r'(?m)^# ', '## ', text)
        text = text.replace('Для статической вики понадобится отдельная выгрузка списка и свойств реагентов.', '')
        text = text.replace('Рецепты в эту статическую страницу не включены.', '')
        # Repair a known incorrect unit conversion in the earlier machine translation.
        if path.endswith('/psionics/glimmer'):
            text = text.replace('400 Цельсия', '400 K (около 127 °C)')
        xml = source.read_text(encoding='utf-8-sig') if source else ''
        embeds = []
        for match in re.finditer(r'<(Guide\w+Embed)\b([^>]*?)/?>', xml, flags=re.S):
            attrs = dict(re.findall(r'(\w+)="([^"]*)"', match[2]))
            embeds.append((match[1], attrs))
        text, embeds = refresh_generated(path, text, embeds, blocks)
        embeds = prepare_embeds(path, text, embeds, data)
        automatic = {(attrs.get('Group') or attrs.get('Discipline') or ''): (kind, attrs) for kind, attrs in embeds if kind not in ('GuideEntityEmbed', 'GuideReagentEmbed')}
        replaced = set()
        def entity_replacement(match):
            identifier = match[1]
            replaced.add(('GuideEntityEmbed', identifier))
            used_embeds['GuideEntityEmbed'] += 1
            return '\n' + blocks.entity(identifier) + '\n'
        text = re.sub(r'(?m)^\s*- Игровой объект:[^\n]*\(`([^`]+)`\)[^\n]*$', entity_replacement, text)
        def reagent_replacement(match):
            identifier = match[1]
            replaced.add(('GuideReagentEmbed', identifier))
            used_embeds['GuideReagentEmbed'] += 1
            return '\n' + blocks.reagent(identifier) + '\n'
        text = re.sub(r'(?m)^\s*- Игровой реагент:[^\n]*`([^`]+)`[^\n]*$', reagent_replacement, text)
        def automatic_replacement(match):
            key = match[1]
            if key not in automatic:
                return ''
            kind, attrs = automatic[key]
            replaced.add((kind, key))
            used_embeds[kind] += 1
            return '\n' + blocks.embed(kind, attrs) + '\n'
        text = re.sub(r'(?m)^\s*- Автоматический игровой список:\s*`([^`]+)`[^\n]*$', automatic_replacement, text)
        extra = []
        equipment = []
        for kind, attrs in embeds:
            key = attrs.get('Entity') or attrs.get('Reagent') or attrs.get('Group') or attrs.get('Discipline') or ''
            if (kind, key) in replaced:
                continue
            replaced.add((kind, key))
            used_embeds[kind] += 1
            rendered = blocks.embed(kind, attrs)
            if not rendered:
                continue
            if kind == 'GuideEntityEmbed':
                equipment.append(rendered)
            else:
                title = GROUPS.get(key, data.loc(data.get('techDiscipline', key).get('name', key)))
                extra.append(('\n\n## ' + title + '\n\n' if title else '\n\n## Справочные данные\n\n') + rendered)
        if equipment:
            text += '\n\n## Оборудование и предметы\n\n<div class="dl-entities">' + ''.join(equipment) + '</div>\n'
        text += ''.join(extra)
        for match in re.finditer(r'\[textlink="([^"]*)"\s+link="([^"]+)"\]', xml):
            target = guide_path(html.unescape(match[2]))
            if target != path:
                link_targets[path].add(target)
        for match in re.finditer(r'\]\((/ru/[^)\s]+)\)', text):
            url = match[1]
            route, sep, anchor = url.partition('#')
            raw_path = route.removeprefix('/ru/')
            if raw_path.startswith('in_game/') and 'guidebook/' + raw_path in snapshots:
                text = text.replace(url, '/ru/guidebook/' + raw_path + (sep + anchor if sep else ''))
        missing_related = {target for target in link_targets[path] if '/ru/' + target not in text}
        if missing_related:
            text += '\n\n## Связанные разделы\n\n' + '\n'.join('- [' + article_title(target, snapshots.get(target, {}).get('title', next((data.loc(data.get('guideEntry', key).get('name', key)) for key, value in guides.items() if value == target), target.rsplit('/', 1)[-1]))) + '](/ru/' + target + ')' for target in sorted(missing_related))
        text = clean_article(path, text, data)
        text = layout_species(path, text, data, blocks)
        body_cache[path] = text
        return text

    # All the previously delivered binding keys remain present.
    preset = station / 'Resources/ConfigPresets/DeepLagoon/wiki.toml'
    old_config = tomllib.loads(preset.read_text(encoding='utf-8-sig'))
    route_pairs = [tuple(x.strip() for x in line.split('=', 1)) for line in old_config['wiki']['routes'].splitlines() if line.strip() and not line.lstrip().startswith('#')]
    book_routes = {}
    for key, target in route_pairs:
        if key.startswith('entity:Book'):
            identifier = key.split(':', 1)[1]
            name = slug(identifier.removeprefix('Book'))
            book_routes[identifier] = 'guidebook/in_game/books/' + name
    routes = []
    for key, target in route_pairs:
        if key.startswith(('guide:', 'link:')):
            target = '/ru/' + guide_path(key.split(':', 1)[1])
        elif key.startswith('entity:Book'):
            target = '/ru/' + book_routes[key.split(':', 1)[1]]
        elif key.startswith(('job:', 'vessel:')) and target.startswith('guide:'):
            target = 'guide:' + target.split(':', 1)[1]
        elif key == 'page:InGame':
            target = '/ru/in_game'
        routes.append((key, target))
    assert len(routes) == len(route_pairs) == 521
    assert len({k for k, _ in routes}) == 521

    css = ((args.design / 'theme.css').read_text(encoding='utf-8') + EXTRA_CSS + CONTROLS_CSS + DIRECTORY_CSS).replace('.dlwiki', '.dlwiki.dlwiki.dlwiki').replace('.btn.primary', '.btn.dl-primary')
    (output / 'page-styles.css').write_text(css, encoding='utf-8')
    logo = blocks.asset(station / 'Resources/Textures/_DeepLagoon/Logo/logo.png')
    css = css.replace('assets/', '/station-assets/')
    def header(theme='a'):
        return f'<div class="dlwiki theme-{theme}"><header class="masthead"><div class="wrap masthead-inner"><a class="brand" href="/ru/home"><img src="{logo}" alt="Эмблема DeepLagoon"><span>DEEP LAGOON<small>БИБЛИОТЕКА СЕКТОРА</small></span></a><nav class="topnav" aria-label="Основная навигация"><a href="/ru/home">Главная</a><a href="/ru/{GUIDES_PATH}">Гайды</a><a href="/ru/guidebook/in_game/books">Книги</a></nav></div></header>'
    footer = '<footer class="footer"><div class="wrap footer-row"><span>DeepLagoon · Библиотека сектора</span><a href="/ru/guidebook/in_game/illustrations">Источники иллюстраций</a></div></footer></div>'
    pages = []
    def add(path, title, markdown='', html_content=None, book=False, sources=None):
        if path == CHEMICALS_PATH:
            markdown = filter_chemicals(markdown)
            if html_content is not None:
                html_content = filter_chemicals(html_content)
        old = snapshots.get(path)
        deferred_paths = {'guidebook/in_game/books/' + slug(key.removeprefix('Book')) for key in DEFERRED_BOOKS}
        if old and protected_page(path) and not (book and args.refresh_book_links and path not in deferred_paths):
            pages.append({**{key: old[key] for key in ('path', 'title', 'id', 'description', 'content', 'scriptCss', 'editor', 'isPublished', 'isPrivate')},
                          'tags': [tag['tag'] for tag in old.get('tags', [])],
                          'beforeUpdatedAt': old.get('updatedAt'), 'markdown': '', 'prefix': '', 'suffix': '', 'sources': sources or [], 'preserveExact': True})
            return
        title = article_title(path, title)
        if book:
            prefix = header('b').replace('dlwiki theme-b', 'dlwiki theme-b dl-book-page') + '<main class="wrap"><div class="breadcrumb"><a href="/ru/guidebook/in_game/books">Книжная полка</a> / ' + E(title) + '</div><div class="book-stage"><article class="book"><div class="running"><span>Библиотека DeepLagoon</span><span>Прикладные руководства</span></div><header class="titlepage"><h1>' + E(title) + '</h1><div class="ornament" aria-hidden="true">— ❦ —</div></header><div class="prose">'
            suffix = '</div><div class="folio"><span>' + E(title) + '</span><span>DeepLagoon</span></div></article></div></main>' + footer
        else:
            prefix = header() + '<main class="wrap"><div class="breadcrumb"><a href="/ru/in_game">Справочник</a> / ' + E(title) + '</div><header class="article-head"><div><span class="eyebrow">БОРТОВОЙ СПРАВОЧНИК · DEEPLAGOON</span><h1>' + E(title) + '</h1></div></header><div class="article-grid"><article class="prose">'
            suffix = '</article><aside class="toc" aria-label="Навигация"><strong>Разделы вики</strong><a href="/ru/in_game">Справочник</a><a href="/ru/guidebook/in_game/chemicals">Реагенты и реакции</a><a href="/ru/guidebook/in_game/service/food-recipes">Рецепты кухни</a><a href="/ru/guidebook/in_game/books">Книжная полка</a><a href="/ru/rules">Правила</a></aside></div></main>' + footer
        pages.append({'path': path, 'title': title, 'id': old.get('id') if old else None,
                      'beforeUpdatedAt': old.get('updatedAt') if old else None,
                      'description': 'Библиотека DeepLagoon · ' + title,
                      'markdown': markdown, 'content': html_content,
                      'prefix': prefix, 'suffix': suffix, 'scriptCss': css,
                      'editor': 'code', 'isPublished': old.get('isPublished', True) if old else True,
                      'isPrivate': old.get('isPrivate', False) if old else False,
                      'tags': list(dict.fromkeys([*(t['tag'] for t in (old or {}).get('tags', [])), 'deeplagoon-reference', 'book' if book else 'guidebook'])),
                      'sources': sources or []})

    for path, source in sorted(docs.items()):
        if path not in snapshots:
            continue
        sources = [str(source.relative_to(station))]
        if path.startswith(WEAPONS_PREFIX):
            sources.extend(data.sources[('entity', key)] for key in CATALOGUES.get(path.removeprefix(WEAPONS_PREFIX), ()) if ('entity', key) in data.sources)
        if path.startswith('guidebook/in_game/imported/mono/economy/') or path == 'guidebook/in_game/imported/nf/medical/medical-recipes':
            sources.extend(['Resources/Prototypes/_Mono/Recipes/Lathes', 'Resources/Prototypes/_Mono/Entities/Structures/Machines', 'Resources/Prototypes/_NF/Entities/Structures/Machines'])
        add(path, snapshots[path]['title'], body(path), sources=sources)
    for group in sorted({data.get('reagent', key).get('group', 'Unknown') for key in data.raw['reagent']}):
        add('guidebook/in_game/reagents/' + slug(group), GROUPS.get(group, group), html_content=blocks.reagent_group(group), sources=['Resources/Prototypes/Reagents', 'Resources/Prototypes/Recipes/Reactions'])
    for identifier, title in collection_ids.items():
        entities = [key for key, value in data.raw['entity'].items() if not value.get('abstract') and identifier in data.components(key).get('GuideHelp', {}).get('guides', [])]
        intro = '<p>В этом разделе собраны предметы с маркировкой «' + E(title) + '» из каталога Station. Иллюстрации и описания относятся к существующим игровым предметам.</p>'
        content = intro + '<h2>Экипировка и предметы</h2><div class="dl-entities">' + ''.join(blocks.entity(key) for key in sorted(entities, key=lambda key: data.name('entity', key))) + '</div>'
        if not entities:
            content += '<p>В текущей выборке нет доступных предметов этой марки.</p>'
        add('guidebook/in_game/reference/' + slug(identifier), title + ': каталог экипировки', html_content=content,
            sources=sorted({data.sources[('entity', key)] for key in entities}))
    reaction_cards = []
    for recipe in sorted(blocks.reactions, key=lambda value: value['id']):
        names = [data.name('reagent', key) for key in recipe.get('products', {})]
        if not names:
            names = [data.name('entity', effect.get('entity', '')) for effect in recipe.get('effects', []) if isinstance(effect, dict) and effect.get('entity')]
        title = ', '.join(names) or ' + '.join(data.name('reagent', key) for key in recipe.get('reactants', {}))
        reaction_cards.append('<details><summary>' + E(title or recipe['id']) + '</summary>' + blocks.reaction(recipe) + '</details>')
    add('guidebook/in_game/reference/reactions', 'Все химические реакции', html_content='<p>Все действующие определения реакций, включая образование предметов и реакции без жидкого продукта. Катализаторы не включены в расходуемую часть.</p>' + ''.join(reaction_cards), sources=['Resources/Prototypes/Recipes/Reactions'])
    add('guidebook/in_game/reference/cooking', 'Все рецепты кухни и сборщиков', html_content='<p>Рецепты из прототипов кухни. Проверяйте тип устройства, время и выход продукции в карточке.</p>' + blocks.cooking(), sources=['Resources/Prototypes/Recipes/Cooking'])
    for identifier, path in sorted(book_routes.items()):
        components = data.components(identifier)
        book_guides = components.get('GuideHelp', {}).get('guides', [])
        chapters = []
        targets = BOOK_SOURCE_PATHS.get(identifier) or [guide_path(key) for key in book_guides]
        for i, target in enumerate(targets, 1):
            key = book_guides[min(i - 1, len(book_guides) - 1)]
            title = snapshots.get(target, {}).get('title', data.loc(data.get('guideEntry', key).get('name', key)))
            if target in docs and target in snapshots:
                chapters.append(f'<section class="dl-chapter"><h2><span class="chapter">Глава {i}</span>{E(title)}</h2>\n\n' + body(target) + '\n\n</section>')
        add(path, data.name('entity', identifier), '\n\n'.join(chapters), book=True,
            sources=[data.sources.get(('entity', identifier), '')])

    books_index = '\n'.join(f'- [{article_title(path, data.name("entity", key))}](/ru/{path})' for key, path in sorted(book_routes.items()))
    add('guidebook/in_game/books', 'Книжная полка', books_index)
    # Build all source-driven content before resolving historical broken links.
    existing_paths = set(snapshots) | {p['path'] for p in pages}
    def repair_href(match):
        url = match[1]
        target, sep, anchor = url.partition('#')
        raw = target.removeprefix('/ru/')
        if raw in existing_paths:
            return match[0]
        if 'guidebook/' + raw in existing_paths:
            return match[0].replace(url, '/ru/guidebook/' + raw + (sep + anchor if sep else ''))
        candidates = [p for p in existing_paths if p.rsplit('/', 1)[-1] == raw.rsplit('/', 1)[-1]]
        if len(candidates) == 1:
            return match[0].replace(url, '/ru/' + candidates[0] + (sep + anchor if sep else ''))
        return match[0]
    for page in pages:
        page['markdown'] = re.sub(r'\]\((/ru/[^)\s]+)\)', repair_href, page['markdown'])
    # Unknown legacy guide IDs are reported for editorial resolution, not invented.
    (output / 'unresolved-guides.json').write_text(json.dumps(sorted(unresolved), ensure_ascii=False, indent=2), encoding='utf-8')
    directory_pages = [*pages, {'path':'guidebook/in_game/chemistry/bicaridine', 'title':'Первая партия бикаридина'}]
    index = directory(directory_pages)
    add(GUIDES_PATH, 'Практические гайды', html_content=index)
    add('in_game', 'Бортовой справочник', html_content=index)
    add('guidebook', 'Справочник DeepLagoon', html_content=index)

    # The approved homepage is variant A; all book pages above use variant B.
    home = (args.design / 'wikijs/a-home.html').read_text(encoding='utf-8')
    home = home.replace('btn primary', 'btn dl-primary')
    for asset in json.loads((args.design / 'asset-credits.json').read_text(encoding='utf-8')):
        url = blocks.asset(station / asset['source'])
        home = home.replace('/wiki-design-assets/' + asset['file'], url)
    home = home.replace('/ru/in_game/chemistry/bicaridine', '/ru/guidebook/in_game/chemistry/bicaridine')
    home = home.replace('/ru/in_game/books/bartenders-manual', '/ru/' + book_routes['BookBartendersManual'])
    home = home.replace('href="/ru/guidebook/in_game/chemistry/bicaridine" >Гайды', 'href="/ru/' + GUIDES_PATH + '" >Гайды')
    home = home.replace('href="/ru/guidebook/in_game/chemistry/bicaridine">Гайды', 'href="/ru/' + GUIDES_PATH + '">Гайды')
    home = home.replace('href="/ru/' + book_routes['BookBartendersManual'] + '">Книги', 'href="/ru/guidebook/in_game/books">Книги')
    home = home.replace('href="/ru/' + book_routes['BookBartendersManual'] + '"><span class="number">03</span>Книжная полка', 'href="/ru/guidebook/in_game/books"><span class="number">03</span>Книжная полка')
    home = re.sub(r'Макет для выбора оформления · 21 октября 2026.*?Открыть действующую вики ↗</a>', 'Рецепты и справка DeepLagoon<br><a href="/ru/guidebook/in_game/illustrations">Источники иллюстраций</a>', home, flags=re.S)
    home = home.replace('href="/ru/guidebook/in_game/chemistry/bicaridine"><span class="number">02</span>Практические гайды', 'href="/ru/' + GUIDES_PATH + '"><span class="number">02</span>Практические гайды')
    add('home', 'Главная', html_content=home)
    pages[-1]['scriptCss'] += '\n' + (args.design / 'home-features.css').read_text(encoding='utf-8').replace('.dlwiki','.dlwiki.dlwiki.dlwiki')
    pages[-1]['prefix'] = pages[-1]['suffix'] = ''
    # Reuse the reviewed practical guide as a standalone worked example.
    guide = (args.design / 'wikijs/a-guide.html').read_text(encoding='utf-8')
    article = re.search(r'<article class="prose">(.*?)</article>', guide, re.S)[1]
    for asset in json.loads((args.design / 'asset-credits.json').read_text(encoding='utf-8')):
        article = article.replace('/wiki-design-assets/' + asset['file'], blocks.asset(station / asset['source']))
    add('guidebook/in_game/chemistry/bicaridine', 'Первая партия бикаридина', html_content=article)
    pages[-1]['scriptCss'] += '\n@media(max-width:650px){.dlwiki.dlwiki.dlwiki .prose{min-width:0;width:100%}.dlwiki.dlwiki.dlwiki .equipment{grid-template-columns:repeat(3,minmax(0,1fr))}.dlwiki.dlwiki.dlwiki .equipment figure{min-width:0;overflow-wrap:anywhere}.dlwiki.dlwiki.dlwiki .formula{grid-template-columns:1fr;text-align:center}.dlwiki.dlwiki.dlwiki .prose table{display:block;max-width:100%;overflow-x:auto}}'


    credit_rows = []
    for asset in sorted(blocks.assets.values(), key=lambda x: x['source']):
        credit_rows.append('- `' + asset['source'] + '` — ' + str(asset.get('copyright') or 'Ресурсы DeepLagoon Station') + '; ' + str(asset.get('license') or 'см. атрибуцию в репозитории') + '.')
    add('guidebook/in_game/illustrations', 'Источники иллюстраций', 'Игровые изображения взяты из ресурсов DeepLagoon Station. Атрибуция исходных файлов сохранена.\n\n' + '\n'.join(credit_rows))
    assert len({p['path'] for p in pages}) == len(pages)
    stats = {'pages':len(pages), 'existing':sum(bool(p['id']) for p in pages), 'new':sum(not p['id'] for p in pages),
             'books':len(book_routes), 'restoredEmbeds':dict(used_embeds),
             'reagents':sum(not value.get('abstract') for value in data.raw['reagent'].values()), 'reactions':len(data.raw['reaction']),
             'foodRecipes':sum(not value.get('hideInGuidebook') for value in data.raw['microwaveMealRecipe'].values()),
             'assets':len(blocks.assets), 'unresolvedGuides':sorted(unresolved),
             'missingEntityIds':sorted(blocks.missing_entities), 'missingImages':sorted(blocks.missing_images),
             'pendingTranslations':pending, 'bindings':len(routes)}
    (output / 'build-stats.json').write_text(json.dumps(stats,ensure_ascii=False,indent=2),encoding='utf-8')
    (output / 'pages-source.json').write_text(json.dumps(pages,ensure_ascii=False),encoding='utf-8')
    (output / 'assets.json').write_text(json.dumps(blocks.assets,ensure_ascii=False,indent=2),encoding='utf-8')
    route_text = '# Привязки к опубликованным статьям и книжным редакциям DeepLagoon.\n' + '\n'.join(key + ' = ' + target for key,target in routes) + '\n'
    (output / 'wiki.routes.txt').write_text(route_text, encoding='utf-8')
    (output / 'wiki.toml').write_text('[wiki]\norigin = "https://wiki.deep-lagoon-ss14.ru"\ntree_root = "/ru/guidebook/in_game"\ntree_locale = "ru"\nnew_player_minutes = 180.0\nroutes = \'\'\'\n' + route_text + "'''\n", encoding='utf-8')
    print(json.dumps(stats,ensure_ascii=False,indent=2), flush=True)


if __name__ == '__main__':
    main()
