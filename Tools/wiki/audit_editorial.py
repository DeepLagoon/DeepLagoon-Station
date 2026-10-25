"""Validate an editorial publication against its immutable live snapshot."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re

from controls_reference import ControlsReference, CONTROLS_PATH, preserved_movement
from editorial_content import article_body, protected_page
from editorial_cleanup import clean_article, entity_cards
from game_data import GameData


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    parser.add_argument('--station', type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    folder = args.folder
    pages = json.loads((folder / 'pages.json').read_text(encoding='utf-8'))
    before = {p['path']: p for f in (folder / 'before').glob('*.json') if (p := json.loads(f.read_text(encoding='utf-8')))}
    data = GameData(args.station, folder / 'prototypes.json')
    controls = ControlsReference(args.station)
    before_counts, after_counts = Counter(), Counter()
    protected, modified, bad_wrappers, duplicate_cards = [], [], [], []
    before_card_total = after_card_total = 0
    for page in pages:
        path = page['path']
        old = before.get(path)
        if protected_page(path):
            assert old is not None
            for field in ('content', 'title', 'description', 'scriptCss', 'editor', 'isPublished', 'isPrivate'):
                assert page[field] == old[field], (path, field)
            assert page['tags'] == [tag['tag'] for tag in old['tags']], path
            protected.append(path)
            continue
        if old:
            old_body = article_body(old['content'])
            clean_article(path, old_body, data, before_counts)
            before_card_total += len(list(entity_cards(old_body)))
            if any(page[k] != old[k] for k in ('content', 'title', 'description')):
                modified.append(path)
        body = article_body(page['content'])
        clean_article(path, body, data, after_counts)
        cards = list(entity_cards(body))
        after_card_total += len(cards)
        if len(re.findall(r'class="dlwiki\b', page['content'])) != 1:
            bad_wrappers.append(path)
        identifiers = [m[1] for _, _, card in cards if (m := re.search(r'data-entity-id="([^"]+)"', card))]
        if len(identifiers) != len(set(identifiers)):
            duplicate_cards.append(path)
        assert not any('ToyFigurine' in identifier for identifier in identifiers), path
        assert all(identifier in data.raw['entity'] for identifier in identifiers), path
    old_movement = preserved_movement(article_body(before[CONTROLS_PATH]['content']))
    new_movement = preserved_movement(article_body(next(p['content'] for p in pages if p['path'] == CONTROLS_PATH)))
    assert old_movement == new_movement
    marker_audit = controls.audit(pages)
    assert marker_audit['markers'] == 0, marker_audit
    assert not bad_wrappers, bad_wrappers
    assert not duplicate_cards, duplicate_cards
    assert after_counts.get('duplicate_cards', 0) == 0, after_counts
    assert after_counts.get('figurine_cards', 0) == 0, after_counts
    report = {'snapshotPages': len(before), 'plannedPages': len(pages), 'modifiedPages': len(modified),
              'newPages': [p['path'] for p in pages if p['path'] not in before],
              'protectedPagesExact': len(protected), 'protectedPaths': protected,
              'unselectedExistingPaths': sorted(set(before) - {p['path'] for p in pages}),
              'movementPreservedExactly': True, 'actionMarkersRemaining': marker_audit['markers'],
              'cardsBefore': before_card_total, 'cardsAfter': after_card_total,
              'redundancyBefore': dict(before_counts), 'redundancyAfter': dict(after_counts),
              'badWrappers': bad_wrappers, 'duplicateEntityIds': duplicate_cards,
              'sourceLimitations': json.loads((folder / 'build-stats.json').read_text(encoding='utf-8'))['missingImages']}
    (folder / 'editorial-audit.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    (folder / 'publication-selection.json').write_text(json.dumps(modified + report['newPages'], ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('protectedPaths', 'sourceLimitations')}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
