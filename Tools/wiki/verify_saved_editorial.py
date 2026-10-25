"""Compare authenticated post-publication reads with the reviewed plan and backup."""
import argparse
import hashlib
import json
from pathlib import Path

from editorial_content import protected_page


def digest(page):
    value = {key: (page.get(key) or '').strip() for key in ('content', 'scriptCss', 'title', 'description')}
    value['tags'] = sorted(tag['tag'] if isinstance(tag, dict) else tag for tag in page.get('tags', []))
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    folder = parser.parse_args().folder
    plan = {p['path']: p for p in json.loads((folder / 'pages.json').read_text(encoding='utf-8'))}
    before = {p['path']: p for f in (folder / 'before').glob('*.json') if (p := json.loads(f.read_text(encoding='utf-8')))}
    history = json.loads((folder / 'publication.json').read_text(encoding='utf-8'))
    after = {p['path']: p for f in folder.glob('after-part-*.json') for p in json.loads(f.read_text(encoding='utf-8'))['pages'].values() if p}
    assert set(before) <= set(after), sorted(set(before) - set(after))
    assert set(plan) <= set(after), sorted(set(plan) - set(after))
    failures, preserved, published = [], [], []
    for path, actual in after.items():
        if path not in plan or protected_page(path):
            original = before[path]
            fields = ('content', 'scriptCss', 'scriptJs', 'title', 'description', 'editor', 'isPublished', 'isPrivate', 'locale', 'tags', 'updatedAt')
            differences = [field for field in fields if actual.get(field) != original.get(field)]
            if differences:
                failures.append({'path': path, 'unchangedFieldsDiffer': differences})
            preserved.append(path)
            continue
        expected = plan[path]
        differences = [field for field in ('content', 'title', 'description', 'editor', 'isPublished', 'isPrivate') if actual.get(field) != expected.get(field)]
        if sorted(tag['tag'] for tag in actual['tags']) != sorted(expected['tags']):
            differences.append('tags')
        record = history.get(path, {})
        if record.get('savedDigest') != digest(actual):
            differences.append('savedDigest')
        if record.get('planDigest') != digest(expected):
            differences.append('planDigest')
        if differences:
            failures.append({'path': path, 'fieldsDiffer': differences})
        published.append(path)
    report = {'pagesRead': len(after), 'publishedPagesVerified': len(published),
              'untouchedPagesVerified': len(preserved), 'mismatches': failures}
    (folder / 'saved-editorial-verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False, indent=2))
    assert not failures, 'Saved Wiki.js payload differs from the reviewed plan or protected backup'


if __name__ == '__main__':
    main()
