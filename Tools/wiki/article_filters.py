"""Page-specific editorial choices, independent of game prototype definitions."""
import re

CHEMICALS_PATH = 'guidebook/in_game/chemicals'
_HIDDEN_CARDS = {'reagent-dlanus-growth', 'reagent-dlanus-reduction'}
_DETAILS = re.compile(r'<(?P<closing>/)?details\b[^>]*>', re.I)


def filter_chemicals(content):
    edits = []
    groups = {}
    depth = 0
    start = None
    for tag in _DETAILS.finditer(content):
        closing = bool(tag['closing'])
        if depth:
            depth += -1 if closing else 1
            if depth == 0:
                edits.append((start, tag.end(), ''))
                group = content.rfind('<div class="dl-generated">', 0, start)
                if group >= 0:
                    groups[group] = groups.get(group, 0) + 1
                start = None
            continue
        if not closing:
            identifier = re.search(r'\bid=["\']([^"\']+)', tag[0])
            if identifier and identifier[1] in _HIDDEN_CARDS:
                start = tag.start()
                depth = 1
    if depth:
        raise ValueError('Unclosed hidden reagent card')
    for group, removed in groups.items():
        count = re.search(r'·\s*(\d+)\s+реагентов', content[group:])
        if count:
            a, b = count.span(1)
            edits.append((group + a, group + b, str(int(count[1]) - removed)))
    for a, b, replacement in sorted(edits, reverse=True):
        content = content[:a] + replacement + content[b:]
    return re.sub(r'\s*\((?:рост|уменьшение)\)', '', content, flags=re.I)
