"""Check actual guidebook inheritance, default/config routes and published chapters."""
import argparse
import html
import json
from pathlib import Path
import re
import tomllib

from editorial_content import article_body
from game_data import GameData, slug, source_path
from book_sources import BOOK_SOURCE_PATHS, DEFERRED_BOOKS


BOOK_TOPICS = {
    'BookBartendersManual': ['Bartender', 'Drinks'],
    'BookChemicalCompendium': ['Chemicals', 'Chemist'],
    'BookEngineersHandbook': ['Engineering'],
    'BookHowToCookForFortySpaceman': ['Chef', 'FoodRecipes'],
    'BookHowToKeepStationClean': ['Janitorial'],
    'BookHowToRockAndStone': ['Salvage'],
    'BookHowToSurvive': ['Survival'],
    'BookLeafLoversSecret': ['Botany', 'Chemicals'],
    'BookMedicalReferenceBook': ['Medical'],
    'BookScientistsGuidebook': ['Science'],
    'BookSecurity': ['Security'],
    'BookSpaceEncyclopedia': ['NF14'],
    'BookSpaceLaw': ['SpaceLaw'],
    'BookTSFSOP': ['TSFMC_SOP'],
    'BookTheBookOfControl': ['Controls'],
    'BookUSSPLaw': ['USSP_SoP'],
}


def text(value):
    return ' '.join(html.unescape(re.sub(r'<[^>]*>', ' ', value)).replace('#', '').split())


def inspect(data, defaults, routes, pages):
    books = {}
    issues = []
    for key, raw in data.raw['entity'].items():
        components = data.components(key)
        guide = components.get('GuideHelp', {})
        if not raw.get('abstract') and 'Book' in components.get('Tag', {}).get('tags', []) and guide.get('openOnActivation') and guide.get('guides'):
            books[key] = guide['guides']
    if set(books) != set(BOOK_TOPICS):
        issues.append({'unreviewedBooks': sorted(set(books) - set(BOOK_TOPICS)), 'missingBooks': sorted(set(BOOK_TOPICS) - set(books))})
    rows = []
    deferred = []
    for identifier, guides in sorted(books.items()):
        expected = '/ru/guidebook/in_game/books/' + slug(identifier.removeprefix('Book'))
        if identifier not in BOOK_SOURCE_PATHS and identifier not in DEFERRED_BOOKS and guides != BOOK_TOPICS.get(identifier):
            issues.append({'book': identifier, 'topics': guides, 'expectedTopics': BOOK_TOPICS.get(identifier)})
        if defaults.get(identifier) != expected or routes.get('entity:' + identifier) != expected:
            issues.append({'book': identifier, 'default': defaults.get(identifier), 'configured': routes.get('entity:' + identifier), 'expected': expected})
        page = pages.get(expected.removeprefix('/ru/'))
        if not page:
            issues.append({'book': identifier, 'missingPage': expected})
            continue
        if identifier in DEFERRED_BOOKS:
            deferred.append({'id': identifier, 'url': expected, 'reason': DEFERRED_BOOKS[identifier]})
            rows.append({'id': identifier, 'url': expected, 'guides': guides, 'status': 'deferred'})
            continue
        chapters = []
        body = article_body(page['content'])
        for index, guide in enumerate(BOOK_TOPICS.get(identifier, guides)):
            if identifier in BOOK_SOURCE_PATHS:
                path = BOOK_SOURCE_PATHS[identifier][index]
            else:
                definition = data.get('guideEntry', guide)
                source = data.station / ('Resources' + definition.get('text', ''))
                if not source.is_file():
                    issues.append({'book': identifier, 'missingGuideSource': guide})
                    continue
                path = source_path(source, data.station)
            if path not in pages:
                matches = [key for key in pages if key.startswith('guidebook/') and key.rsplit('/', 1)[-1] == slug(source.stem)]
                path = matches[0] if len(matches) == 1 else path
            original = pages.get(path)
            if not original:
                issues.append({'book': identifier, 'missingSourcePage': path})
                continue
            sample = text(article_body(original['content']))[:120]
            if sample and sample not in text(body):
                issues.append({'book': identifier, 'missingTopicContent': guide, 'sourcePage': path, 'sample': sample})
            chapters.append({'guide': guide, 'sourcePage': path, 'sample': sample})
        count = body.count('class="dl-chapter"')
        expected_chapters = len(BOOK_SOURCE_PATHS.get(identifier, guides))
        if count != expected_chapters:
            issues.append({'book': identifier, 'chapters': count, 'expectedChapters': expected_chapters})
        rows.append({'id': identifier, 'name': data.name('entity', identifier), 'url': expected, 'guides': guides, 'chapters': chapters})
    return {'books': len(books), 'verified': len(books) - len(deferred), 'deferred': deferred, 'rows': rows, 'issues': issues}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    parser.add_argument('--station', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--before', action='store_true')
    args = parser.parse_args()
    station = args.station.resolve()
    data = GameData(station, args.folder / 'prototypes.json')
    defaults = {key: value for page in data.raw['wikiPage'].values() for key, value in page.get('entityRoutes', {}).items()}
    preset = tomllib.loads((station / 'Resources/ConfigPresets/DeepLagoon/wiki.toml').read_text(encoding='utf-8-sig'))
    routes = dict(line.split('=', 1) for line in preset['wiki']['routes'].splitlines() if '=' in line and not line.lstrip().startswith('#'))
    routes = {key.strip(): value.strip() for key, value in routes.items()}
    if args.before:
        pages = {page['path']: page for file in (args.folder / 'before').glob('*.json') if (page := json.loads(file.read_text(encoding='utf-8')))}
    else:
        pages = {page['path']: page for page in json.loads((args.folder / 'pages.json').read_text(encoding='utf-8'))}
    report = inspect(data, defaults, routes, pages)
    suffix = 'before' if args.before else 'plan'
    (args.folder / ('book-links-' + suffix + '.json')).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({key: report[key] for key in ('books', 'verified', 'deferred', 'issues')}, ensure_ascii=False, indent=2))
    assert not report['issues'], 'Book topics or destinations do not match; see book-links-' + suffix + '.json'


if __name__ == '__main__':
    main()
