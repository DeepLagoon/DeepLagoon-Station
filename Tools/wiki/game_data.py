"""Read localized Station prototypes for reproducible Wiki.js reference exports."""
from collections import defaultdict
from copy import deepcopy
from pathlib import Path
import re
import json
import colorsys
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

    def doll_species(self, identifier):
        """Dolls are loaded with their species profile in-game, not their inherited Icon."""
        return next((self.get('species', key) for key in self.raw['species']
                     if self.get('species', key).get('dollPrototype') == identifier), {})

    def illustration_species(self, identifier):
        return self.doll_species(identifier) or next((self.get('species', key) for key in self.raw['species']
                     if self.get('species', key).get('prototype') == identifier), {})

    def humanoid_species_name(self, identifier):
        species = self.illustration_species(identifier)
        return self.loc(species.get('name', species.get('id', '')))

    @staticmethod
    def _rgb(color):
        value = str(color).lstrip('#')
        if len(value) == 3:
            value = ''.join(c * 2 for c in value)
        if len(value) not in (6, 8):
            return 1., 1., 1.
        return tuple(int(value[i:i + 2], 16) / 255 for i in (0, 2, 4))

    @staticmethod
    def _hex(rgb, alpha=1):
        return '#' + ''.join(f'{round(max(0, min(1, v)) * 255):02x}' for v in (*rgb, alpha))

    def _skin_color(self, species):
        coloration = species.get('skinColoration', 'HumanToned')
        if coloration == 'HumanToned':
            tone = max(0, min(100, species.get('defaultHumanSkinTone', 20)))
            offset = tone - 20
            hue, saturation, value = (25 + max(0, -offset)) / 360, (20 + max(0, offset)) / 100, (100 - max(0, offset)) / 100
            return self._hex(colorsys.hsv_to_rgb(hue, saturation, value))
        rgb = self._rgb(species.get('defaultSkinTone', '#ffffff'))
        if coloration == 'TintedHues':
            # Mirrors SkinColor.TintedHues: ToHsl, modify saturation/lightness, FromHsv.
            hue, lightness, saturation = colorsys.rgb_to_hls(*rgb)
            rgb = colorsys.hsv_to_rgb(hue, saturation * .1, .85 + lightness * .15)
        return self._hex(rgb)

    def _marking_color(self, definition, skin, eyes, categories):
        definition = definition or {}
        for kind in [definition.get('type', {'effectType': 'SkinColoring'}), *definition.get('fallbackTypes', [])]:
            name = kind.get('effectType')
            color = None
            if name == 'SkinColoring':
                color = skin
            elif name == 'EyeColoring':
                color = eyes
            elif name == 'SimpleColoring':
                color = kind.get('color', '#ffffff')
            elif name == 'CategoryColoring':
                color = categories.get(kind.get('category'))
            elif name == 'TattooColoring':
                hue, saturation, _ = colorsys.rgb_to_hsv(*self._rgb(skin))
                color = self._hex(colorsys.hsv_to_rgb(hue, saturation, .4))
            if color:
                return self._hex(tuple(1 - c for c in self._rgb(color))) if kind.get('negative') else color
        return definition.get('fallbackColor', '#ffffff')

    def humanoid_sprite_layers(self, identifier):
        """Static species-profile illustration; None keeps ordinary entity icon rendering.

        Resolve dolls and canonical bodies through the species prototype because
        some inherit another species' appearance component. Equipped NPC variants
        keep their own rendering path; a bare body would misrepresent their outfit.
        """
        species = self.illustration_species(identifier)
        if not species:
            return [] if identifier.endswith('Dummy') and 'HumanoidAppearance' in self.components(identifier) else None
        components = self.components(identifier)
        if 'HumanoidAppearance' not in components:
            return None
        sprites = self.get('speciesBaseSprites', species.get('sprites', '')).get('sprites', {})
        if not sprites:
            return []
        appearance = components['HumanoidAppearance']
        skin, eyes = self._skin_color(species), appearance.get('eyeColor', '#000000')
        hidden = set(appearance.get('permanentlyHidden', []))
        sex = appearance.get('sex', 'Male')
        order = []
        for layer in components.get('Sprite', {}).get('layers', []):
            for key in layer.get('map', []):
                if str(key).startswith('enum.HumanoidVisualLayers.'):
                    order.append(key.rsplit('.', 1)[1])
        order.extend(key for key in sprites if key not in order)
        rendered = defaultdict(list)
        settings = {}
        for part, key in sprites.items():
            morphed = key + sex if key and part in ('Head', 'Chest') and sex != 'Unsexed' else key
            info = self.get('humanoidBaseSprite', morphed) or self.get('humanoidBaseSprite', key)
            settings[part] = info
            image = info.get('baseSprite')
            if not isinstance(image, dict) or part in hidden:
                continue
            layer = dict(image)
            if info.get('matchSkin', True):
                layer['color'] = self._hex(self._rgb(eyes if part == 'Eyes' else skin), info.get('layerAlpha', 1))
            rendered[part].append(layer)
        categories = {}
        points = self.get('markingPoints', species.get('markingLimits', '')).get('points', {})
        for category, point in points.items():
            for key in point.get('defaultMarkings', [])[:point.get('points', 0)]:
                marking = self.get('marking', key)
                part = marking.get('bodyPart')
                setting = settings.get(part)
                if setting is None or not setting.get('allowsMarkings', True) or part in hidden:
                    continue
                coloring = marking.get('coloring', {})
                for image in marking.get('sprites', []):
                    if not isinstance(image, dict):
                        continue
                    definition = (coloring.get('layers') or {}).get(image.get('state'), coloring.get('default'))
                    color = self._marking_color(definition, skin, eyes, categories)
                    if setting.get('markingsMatchSkin'):
                        color = self._hex(self._rgb(skin), setting.get('layerAlpha', 1))
                    layer = dict(image, color=color)
                    anchor = marking.get('spriteLayers', {}).get(image.get('state'), part)
                    rendered[anchor].append(layer)
                    categories.setdefault(category, color)
        return [layer for part in order for layer in rendered[part]]
