"""Read localized Station prototypes for reproducible Wiki.js reference exports."""
from collections import defaultdict
from copy import deepcopy
from pathlib import Path
import re
import json
import yaml


class Loader(getattr(yaml, 'CSafeLoader', yaml.SafeLoader)):
    pass


def tagged(loader, tag, node):
    if isinstance(node, yaml.MappingNode):
        return {'effectType': tag, **loader.construct_mapping(node, deep=True)}
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node, deep=True)
    return loader.construct_scalar(node)


Loader.add_multi_constructor('!type:', tagged)
Loader.add_multi_constructor('!color:', tagged)


def slug(value):
    value = re.sub(r'([a-z0-9])([A-Z])', r'\1-\2', value)
    return re.sub(r'[^a-z0-9]+', '-', value.lower()).strip('-')


def source_path(source, station):
    parts = source.relative_to(station / 'Resources/ServerInfo').parts
    if parts[0] == 'Guidebook':
        tail = [slug(p) for p in parts[1:-1]]
    else:
        tail = ['imported', slug(parts[0]), *[slug(p) for p in parts[2:-1]]]
    return '/'.join(['guidebook', 'in_game', *tail, slug(source.stem)])


def merge(base, child):
    result = deepcopy(base)
    for key, value in child.items():
        if key == 'components':
            items = {v['type']: v for v in result.get(key, []) if isinstance(v, dict) and 'type' in v}
            for component in value or []:
                if isinstance(component, dict) and 'type' in component:
                    items[component['type']] = merge(items.get(component['type'], {}), component)
            result[key] = list(items.values())
        elif isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = merge(result[key], value)
        else:
            result[key] = deepcopy(value)
    return result


class GameData:
    def __init__(self, station, index=None):
        self.station = Path(station)
        self.raw = defaultdict(dict)
        self.sources = {}
        self.cache = {}
        self.localization = {}
        self.duplicates = []
        self.russian_keys = set()
        locale_files = [*sorted((self.station / 'Resources/Locale/en-US').rglob('*.ftl')),
                        *sorted((self.station / 'Resources/Locale/ru-RU').rglob('*.ftl'))]
        for path in locale_files:
            current = None
            attr = None
            for line in path.read_text(encoding='utf-8-sig').splitlines():
                match = re.match(r'^([A-Za-z][\w-]*)\s*=\s*(.*)$', line)
                attribute = re.match(r'^\s+\.([\w-]+)\s*=\s*(.*)$', line)
                if match:
                    current, value = match.groups()
                    attr = current
                    self.localization[attr] = value
                    if 'ru-RU' in path.parts:
                        self.russian_keys.add(attr)
                elif attribute and current:
                    key, value = attribute.groups()
                    attr = current + '.' + key
                    self.localization[attr] = value
                    if 'ru-RU' in path.parts:
                        self.russian_keys.add(attr)
                elif attr and line.startswith((' ', '\t')) and line.strip():
                    self.localization[attr] += '\n' + line.strip()
                else:
                    current = attr = None
        overrides = Path(__file__).with_name('localization_ru.json')
        if overrides.exists():
            translations = json.loads(overrides.read_text(encoding='utf-8'))
            self.localization.update(translations)
            self.russian_keys.update(translations)
        if index:
            snapshot = json.loads(Path(index).read_text(encoding='utf-8'))
            self.raw.update(snapshot['raw'])
            self.sources = {tuple(k.split(':', 1)): v for k, v in snapshot['sources'].items()}
            self.duplicates = snapshot['duplicates']
            return
        for path in sorted((self.station / 'Resources/Prototypes').rglob('*.yml')):
            values = yaml.load(path.read_text(encoding='utf-8-sig').replace('\t', '  '), Loader=Loader)
            if not isinstance(values, list):
                continue
            for value in values:
                if not isinstance(value, dict) or not value.get('type') or not value.get('id'):
                    continue
                key = (value['type'], str(value['id']))
                if key[1] in self.raw[key[0]]:
                    self.duplicates.append((key, self.sources[key], str(path.relative_to(self.station))))
                self.raw[key[0]][key[1]] = value
                self.sources[key] = str(path.relative_to(self.station)).replace('\\', '/')

    def get(self, kind, identifier, chain=()):
        key = (kind, identifier)
        if key in self.cache:
            return self.cache[key]
        value = self.raw[kind].get(identifier)
        if value is None or key in chain:
            return {}
        parents = value.get('parent', [])
        if isinstance(parents, str):
            parents = [parents]
        result = {}
        for parent in parents:
            result = merge(result, self.get(kind, parent, (*chain, key)))
        result = merge(result, value)
        self.cache[key] = result
        return result

    def loc(self, value):
        if not value:
            return ''
        text = self.localization.get(str(value), str(value))
        # Resolve simple Fluent references; do not evaluate dynamic game functions.
        for _ in range(5):
            new = re.sub(r'\{\s*([A-Za-z][\w.-]*)\s*\}',
                         lambda m: self.localization.get(m[1], m[0]), text)
            if new == text:
                break
            text = new
        return re.sub(r'\[/?(?:color|bold|italic)[^\]]*\]', '', text).strip()

    def name(self, kind, identifier):
        value = self.get(kind, identifier)
        direct = self.localization.get('ent-' + identifier) if kind == 'entity' else None
        return self.loc(direct or value.get('name') or identifier)

    def description(self, kind, identifier):
        value = self.get(kind, identifier)
        direct = self.localization.get('ent-' + identifier + '.desc') if kind == 'entity' else None
        return self.loc(direct or value.get('desc') or value.get('description') or '')

    def components(self, identifier):
        return {c['type']: c for c in self.get('entity', identifier).get('components', [])}
