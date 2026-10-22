"""Check route coverage, links and generated recipe completeness before publishing."""
import argparse
import csv
from html.parser import HTMLParser
import json
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit


class Document(HTMLParser):
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.duplicates = []
        self.links = []
        self.images = []
        self.recipes = set()
        self.foods = 0
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if attrs.get('id'):
            if attrs['id'] in self.ids:
                self.duplicates.append(attrs['id'])
            self.ids.add(attrs['id'])
        if attrs.get('href'):
            self.links.append(attrs['href'])
        if attrs.get('src'):
            self.images.append(attrs['src'])
        if attrs.get('data-reaction-id'):
            self.recipes.add(attrs['data-reaction-id'])
        if 'dl-food' in attrs.get('class', '').split():
            self.foods += 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    args = parser.parse_args()
    folder = args.folder
    pages = json.loads((folder / 'pages.json').read_text(encoding='utf-8'))
    before = [json.loads(p.read_text(encoding='utf-8')) for p in (folder / 'before').glob('*.json')]
    paths = {p['path'] for p in before} | {p['path'] for p in pages}
    documents = {p['path']: Document(p['content']) for p in pages}
    missing_links = []
    missing_anchors = []
    duplicate_ids = []
    for page in pages:
        doc = documents[page['path']]
        duplicate_ids.extend((page['path'], key) for key in doc.duplicates)
        for forbidden in ('Справочные блоки обновлены по прототипам Station','Прямого рецепта синтеза в текущих прототипах нет','В текущих прототипах нет открытых рецептов'):
            assert forbidden not in page['content'], (page['path'], forbidden)
        assert 'Игровой объект:' not in page['content']
        assert 'Автоматический игровой список:' not in page['content']
        assert not re.search(r'<Guide\w+Embed', page['content'])
        for link in doc.links:
            url = urlsplit(link)
            if url.netloc and url.netloc != 'wiki.deep-lagoon-ss14.ru':
                continue
            if url.path and not url.path.startswith('/ru/'):
                continue
            path = unquote(url.path.removeprefix('/ru/')) if url.path else page['path']
            if path not in paths:
                missing_links.append({'page':page['path'], 'href':link, 'target':path})
            elif url.fragment and path in documents and unquote(url.fragment) not in documents[path].ids:
                missing_anchors.append({'page':page['path'], 'href':link, 'target':path})
    routes = {}
    for line in (folder / 'wiki.routes.txt').read_text(encoding='utf-8').splitlines():
        if line.strip() and not line.lstrip().startswith('#'):
            key, target = map(str.strip, line.split('=', 1))
            assert key not in routes, key
            routes[key] = target
    coverage = []
    for key in routes:
        target = key
        seen = set()
        while target in routes:
            assert target not in seen, (key, target)
            seen.add(target)
            target = routes[target]
        if target.startswith('/ru/'):
            exists = target.removeprefix('/ru/') in paths
        else:
            exists = target in ('local', '') or target.startswith(('local:', 'external:'))
        coverage.append({'binding':key, 'resolved':target, 'exists':exists})
    prototypes = json.loads((folder / 'prototypes.json').read_text(encoding='utf-8'))['raw']
    expected_reactions = set(prototypes['reaction'])
    actual_reactions = documents['guidebook/in_game/reference/reactions'].recipes
    expected_foods = sum(not p.get('hideInGuidebook') for p in prototypes['microwaveMealRecipe'].values())
    actual_foods = documents['guidebook/in_game/reference/cooking'].foods
    books = [p for p in pages if 'book' in p['tags']]
    for book in books:
        assert 'dlwiki theme-b' in book['content'] and 'class="book"' in book['content'], book['path']
    assert 'dlwiki theme-a' in next(p['content'] for p in pages if p['path'] == 'home')
    report = {'pages':len(pages), 'bindings':len(routes), 'unresolvedBindings':[r for r in coverage if not r['exists']],
              'books':len(books), 'missingPageLinks':missing_links, 'missingAnchors':missing_anchors,
              'duplicateIds':duplicate_ids, 'expectedReactions':len(expected_reactions), 'actualReactions':len(actual_reactions),
              'missingReactions':sorted(expected_reactions - actual_reactions),
              'expectedFoodRecipes':expected_foods, 'actualFoodRecipes':actual_foods}
    (folder / 'qa.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    with (folder / 'route-coverage.csv').open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.DictWriter(file, fieldnames=['binding','resolved','exists'])
        writer.writeheader()
        writer.writerows(coverage)
    print(json.dumps({key:value if not isinstance(value,list) else len(value) for key,value in report.items()}, ensure_ascii=False, indent=2))
    assert len(routes) == 521
    assert not report['unresolvedBindings'], report['unresolvedBindings']
    assert actual_reactions == expected_reactions
    assert actual_foods == expected_foods
    assert len(books) == 16
    assert not missing_links, 'Broken internal links; see qa.json'
    assert not duplicate_ids, 'Duplicate anchors; see qa.json'
    assert not missing_anchors, 'Broken anchors; see qa.json'


if __name__ == '__main__':
    main()
