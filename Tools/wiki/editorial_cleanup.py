"""Conservative cleanup of generated cards, leaving authored prose intact."""
import html
import re
from collections import Counter

from editorial_content import Elements, protected_page
from game_data import slug


_TAG = re.compile(r'<(?P<close>/)?(?P<tag>div)\b[^>]*>', re.I)
_CLASS = re.compile(r'\bclass=["\']([^"\']*)["\']', re.I)
_ID = re.compile(r'\bdata-entity-id=["\']([^"\']+)["\']', re.I)
_PROTECTED = {'rules', 'rule', 'lore', 'sop', 'srp', 'standard-operating-procedures',
              'space-law', 'spacelaw', 'corporate-law', 'corporatelaw', 'law'}


def protected_path(path):
    parts = str(path).strip('/').lower().split('/')
    return protected_page(str(path)) or any(part in _PROTECTED or part.startswith(('monolith-rule', 'server-rule'))
                                            for part in parts)


def normalized_text(value):
    value = html.unescape(re.sub(r'<[^>]*>', ' ', value))
    value = re.sub(r'[*_`#]', '', value)
    return ' '.join(value.casefold().split()).strip(' .!?:;—–-')


def entity_cards(text):
    """Yield complete generated card spans, including their nested divs."""
    start = None
    depth = 0
    for tag in _TAG.finditer(text):
        if start is not None:
            depth += -1 if tag['close'] else 1
            if depth == 0:
                yield start, tag.end(), text[start:tag.end()]
                start = None
        elif not tag['close']:
            classes = _CLASS.search(tag[0])
            if classes and 'dl-entity' in classes[1].split():
                start, depth = tag.start(), 1


def _card_name(card):
    match = re.search(r'<strong\b[^>]*>(.*?)</strong>', card, re.S | re.I)
    return normalized_text(match[1]) if match else ''


def _figurine(data, identifier):
    if identifier.startswith('ToyFigurine'):
        return True
    if data is None:
        return False
    comps = data.components(identifier)
    return any('figurines.rsi' in str(comps.get(kind, {}).get('sprite', ''))
               for kind in ('Sprite', 'Icon'))


def _figurine_catalogue(path):
    return any(part in {'figurines', 'toys', 'figurine'} for part in path.split('/'))


def _existing_entities(text, data):
    names = {_card_name(card) for _, _, card in entity_cards(text)}
    identifiers = {html.unescape(match[1]) for match in _ID.finditer(text)}
    if data is not None and names:
        index = _entity_index(data)
        identifiers.update(identifier for name in names for identifier in index['names'].get(name, []))
    return identifiers


def _entity_index(data):
    index = getattr(data, '_wiki_editorial_index', None)
    if index is None:
        names, figurines = {}, set()
        for identifier in data.raw['entity']:
            name = normalized_text(data.name('entity', identifier))
            names.setdefault(name, []).append(identifier)
            if _figurine(data, identifier):
                figurines.add(name)
        index = {'names': names, 'figurines': figurines}
        data._wiki_editorial_index = index
    return index


def prepare_embeds(path, text, embeds, data):
    """Do not append source illustrations already present on a live article."""
    if protected_path(path):
        return embeds
    existing = _existing_entities(text, data)
    seen = set()
    result = []
    for kind, attrs in embeds:
        key = attrs.get('Entity') or attrs.get('Reagent') or attrs.get('Group') or attrs.get('Discipline') or ''
        identity = kind, key
        if identity in seen:
            continue
        seen.add(identity)
        if kind == 'GuideEntityEmbed':
            if key in existing or (_figurine(data, key) and not _figurine_catalogue(path)):
                continue
        result.append((kind, attrs))
    return result


def clean_article(path, text, data=None, report=None):
    """Remove only generated redundancy; ``report`` receives change counts."""
    if protected_path(path):
        return text
    counts = report if report is not None else {}

    def count(kind):
        counts[kind] = counts.get(kind, 0) + 1

    # Legacy exports identify cards by their localized heading, newer ones by ID.
    figurine_names = set()
    if data is not None and not _figurine_catalogue(path):
        figurine_names = _entity_index(data)['figurines']
    cards = list(entity_cards(text))
    prose = text
    for start, end, _ in reversed(cards):
        prose = prose[:start] + '\n' + prose[end:]
    prose_normalized = normalized_text(prose)
    seen = set()
    edits = []
    for start, end, card in cards:
        identifier = _ID.search(card)
        identifier = html.unescape(identifier[1]) if identifier else ''
        if not _figurine_catalogue(path) and (_figurine(data, identifier) or _card_name(card) in figurine_names):
            edits.append((start, end, ''))
            count('figurine_cards')
            continue
        identity = ('id', identifier) if identifier else ('html', re.sub(r'\s+', ' ', card).strip())
        if identity in seen:
            edits.append((start, end, ''))
            count('duplicate_cards')
            continue
        seen.add(identity)
        def description(match):
            value = normalized_text(match[1])
            # A verbatim sentence in the article is evidence; similar meaning is not.
            if len(value) >= 30 and value in prose_normalized:
                count('repeated_descriptions')
                return ''
            return match[0]
        cleaned = re.sub(r'<p\b[^>]*>(.*?)</p>', description, card, flags=re.S | re.I)
        if cleaned != card:
            edits.append((start, end, cleaned))
    for start, end, replacement in reversed(edits):
        text = text[:start] + replacement + text[end:]
    def legacy_marker(match):
        if _figurine(data, match[1]) and not _figurine_catalogue(path):
            count('figurine_markers')
            return ''
        return match[0]
    text = re.sub(r'(?m)^\s*- Игровой объект:[^\n]*\(`([^`]+)`\)[^\n]*$', legacy_marker, text)
    text = re.sub(r'<aside\b[^>]*class=["\']note warning["\'][^>]*>Объект <code>[^<]*</code> отсутствует в текущих прототипах\. Старая инструкция с ним требует пересмотра\.</aside>', '', text)
    # Empty generated containers/headings have no editorial meaning.
    text = re.sub(r'<div\b[^>]*class=["\']dl-entities["\'][^>]*>\s*</div>', '', text, flags=re.I)
    text = re.sub(r'(?m)^## (?:Оборудование и предметы|Экипировка и предметы)\s*\n(?=\s*(?:## |</article>|</section>|$))', '', text)
    text = re.sub(r'<h2\b[^>]*>\s*(?:Оборудование и предметы|Экипировка и предметы)\s*</h2>\s*(?=<(?:h[12]\b|/article>|/section>)|$)', '', text, flags=re.I)
    text = text.replace(' Откройте карточку, чтобы посмотреть состав и условия.', '')
    return text


def refresh_generated(path, text, embeds, blocks, report=None):
    """Refresh source-derived HTML in place and consume its source embeds.

    This handles legacy cards lacking IDs as well as current exports. Authored
    paragraphs outside generated blocks remain byte-for-byte unchanged.
    """
    if protected_path(path):
        return text, embeds
    data = blocks.data
    counts = report if report is not None else {}
    consumed = set()
    entity_ids = list(dict.fromkeys(attrs.get('Entity', '') for kind, attrs in embeds if kind == 'GuideEntityEmbed'))
    entity_names = {}
    humanoid_ids = []
    for identifier in entity_ids:
        value = data.get('entity', identifier)
        names = [data.name('entity', identifier), value.get('name', '')]
        species = data.humanoid_species_name(identifier)
        if species:
            names.append(species)
            humanoid_ids.append(identifier)
        for name in names:
            entity_names.setdefault(normalized_text(name), []).append(identifier)
    edits = []
    seen_entities = set()
    for start, end, card in entity_cards(text):
        match = _ID.search(card)
        identifier = html.unescape(match[1]) if match else ''
        if not identifier:
            candidates = list(dict.fromkeys(entity_names.get(_card_name(card), [])))
            if len(candidates) == 1:
                identifier = candidates[0]
            elif len(humanoid_ids) == 1 and re.search(r'\bdummy\b|манекен|urist mchands', normalized_text(card)):
                identifier = humanoid_ids[0]
        if not identifier:
            continue
        consumed.add(('GuideEntityEmbed', identifier))
        if identifier in seen_entities or (_figurine(data, identifier) and not _figurine_catalogue(path)):
            replacement = ''
        else:
            replacement = blocks.entity(identifier)
        seen_entities.add(identifier)
        edits.append((start, end, replacement))
        counts['refreshed_entities'] = counts.get('refreshed_entities', 0) + 1

    parsed = Elements(text)
    elements = sorted(parsed.elements, key=lambda e: (e[2], -e[5]))
    generated_spans = []
    reagent_ids = {slug(identifier): identifier for identifier in data.raw['reagent']}
    source_groups = [(kind, attrs) for kind, attrs in embeds if kind not in ('GuideEntityEmbed', 'GuideReagentEmbed')]
    cooking_names = {}
    for recipe in data.raw['microwaveMealRecipe'].values():
        result = recipe.get('result', '')
        for name in (data.name('entity', result), data.get('entity', result).get('name', '')):
            cooking_names.setdefault(normalized_text(name), set()).add(recipe.get('group', ''))
    technology_names = {}
    for key, technology in data.raw['technology'].items():
        for name in (data.name('technology', key), technology.get('name', '')):
            technology_names.setdefault(normalized_text(name), set()).add(technology.get('discipline', ''))
    automation = next(((kind, attrs) for kind, attrs in source_groups if kind == 'GuideAutomationSlotsEmbed'), None)
    legacy_automation = None
    if automation:
        groups = [e for e in elements if 'dl-entities' in e[1].get('class', '').split()]
        if groups:
            largest = max(groups, key=lambda e: len(list(entity_cards(text[e[3]:e[4]]))))
            if len(list(entity_cards(text[largest[3]:largest[4]]))) > 10:
                legacy_automation = largest[2]
    for tag, attrs, start, inner, end, finish in elements:
        if any(a <= start and finish <= b for a, b in generated_spans):
            continue
        classes = attrs.get('class', '').split()
        kind = None
        source_attrs = None
        if 'dl-source-block' in classes:
            candidates = [(k, a) for k, a in source_groups if k == attrs.get('data-guide-kind') and (a.get('Group') or a.get('Discipline') or '') == attrs.get('data-guide-key', '')]
            if len(candidates) == 1:
                kind, source_attrs = candidates[0]
        elif automation and ('dl-automation' in classes or start == legacy_automation):
            kind, source_attrs = automation
        elif 'dl-generated' in classes:
            # Each reagent group contains entries with stable slug IDs.
            first = re.search(r'id=["\']reagent-([^"\']+)', text[inner:end])
            reagent = data.get('reagent', reagent_ids.get(first[1], '')) if first else {}
            candidates = [(k, a) for k, a in source_groups if k == 'GuideReagentGroupEmbed'
                          and (not reagent or a.get('Group') == reagent.get('group', 'Unknown'))]
            if len(candidates) == 1:
                kind, source_attrs = candidates[0]
        elif 'dl-cooking' in classes:
            candidates = [(k, a) for k, a in source_groups if k in ('GuideMicrowaveGroupEmbed', 'GuideMedicalGroupEmbed')]
            if len(candidates) > 1:
                names = re.findall(r'<summary\b[^>]*>.*?<span>([^<]+)</span>', text[inner:end], re.S)
                scores = Counter(group for name in names for group in cooking_names.get(normalized_text(name), ()))
                groups = {group for group, score in scores.items() if score == max(scores.values(), default=0)}
                candidates = [(k, a) for k, a in candidates if a.get('Group', '') in groups]
            if len(candidates) == 1:
                kind, source_attrs = candidates[0]
        elif 'dl-table' in classes and '<th>Технология</th>' in text[inner:end]:
            candidates = [(k, a) for k, a in source_groups if k == 'GuideTechDisciplineEmbed']
            if len(candidates) > 1:
                names = re.findall(r'<tr><td>([^<]+)', text[inner:end])
                scores = Counter(discipline for name in names for discipline in technology_names.get(normalized_text(name), ()))
                disciplines = {discipline for discipline, score in scores.items() if score == max(scores.values(), default=0)}
                candidates = [(k, a) for k, a in candidates if a.get('Discipline', '') in disciplines]
            if len(candidates) == 1:
                kind, source_attrs = candidates[0]
        elif 'dl-reagent' in classes:
            identifier = reagent_ids.get(attrs.get('id', '').removeprefix('reagent-'))
            if identifier:
                kind, source_attrs = 'GuideReagentEmbed', {'Reagent': identifier}
        if kind is None:
            continue
        key = source_attrs.get('Reagent') or source_attrs.get('Group') or source_attrs.get('Discipline') or ''
        identity = kind, key
        replacement = '' if identity in consumed else blocks.embed(kind, source_attrs)
        consumed.add(identity)
        generated_spans.append((start, finish))
        edits.append((start, finish, replacement))
        counts['refreshed_blocks'] = counts.get('refreshed_blocks', 0) + 1
    # The group spans may contain entity cards; the outer replacement owns them.
    edits = [edit for edit in edits if not any(a <= edit[0] and edit[1] <= b and (a, b) != edit[:2]
                                               for a, b in generated_spans)]
    for start, end, replacement in sorted(edits, reverse=True):
        text = text[:start] + replacement + text[end:]
    remaining = []
    for kind, attrs in embeds:
        key = attrs.get('Entity') or attrs.get('Reagent') or attrs.get('Group') or attrs.get('Discipline') or ''
        if (kind, key) not in consumed:
            remaining.append((kind, attrs))
    return clean_article(path, text, data, counts), remaining


def layout_species(path, text, data, blocks):
    """Place declared playable-species illustrations before their article prose."""
    if '/mobs/' not in '/' + path.strip('/') + '/' or protected_path(path):
        return text
    candidates = list(entity_cards(text))
    for tag, attrs, start, inner, end, finish in Elements(text).elements:
        if tag == 'figure' and 'dl-species-portrait' in attrs.get('class', '').split():
            # Own the separator produced below, so another export is idempotent.
            if text[finish:finish + 1] == '\n':
                finish += 1
            candidates.append((start, finish, text[start:finish]))
    portraits = {}
    edits = []
    for start, end, card in sorted(candidates):
        match = _ID.search(card)
        if not match:
            continue
        identifier = html.unescape(match[1])
        if not data.illustration_species(identifier):
            continue
        icon = blocks.icon(identifier)
        if not icon:
            continue
        species = data.humanoid_species_name(identifier)
        portraits[identifier] = '<figure class="dl-species-portrait" data-entity-id="' + html.escape(identifier, quote=True) + '">' + icon + '<figcaption>' + html.escape(species) + '</figcaption></figure>'
        edits.append((start, end))
    if not edits:
        return text
    for start, end in reversed(edits):
        text = text[:start] + text[end:]
    text = clean_article(path, text, data)
    return '\n'.join(portraits.values()) + '\n' + text
