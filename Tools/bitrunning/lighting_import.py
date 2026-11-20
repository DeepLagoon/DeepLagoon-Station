"""Area ambient light and authored local emitters from the pinned DM data."""
import re

from source_model import number, string


class LightingImport:
    def __init__(self, model, colors):
        self.model = model
        self.colors = dict(re.findall(r'^#define\s+(\w+)\s+("#[A-Fa-f0-9]+"|\w+)\s*(?://.*)?$', colors, re.M))
        self.constants = dict(re.findall(r'^#define\s+(\w+)\s+([^\r\n]+)', colors, re.M))

    def numeric(self, value, default=0):
        if value is None:
            return default
        for _ in range(16):
            expanded = re.sub(r'\b[A-Z][A-Z0-9_]+\b',
                              lambda m: self.constants.get(m[0], m[0]).split('//')[0].strip(), value)
            if expanded == value:
                break
            value = expanded
        result = number(value)
        if result is None:
            raise ValueError(f'Unsupported source light number: {value}')
        return result

    def color(self, value):
        value = value or 'COLOR_WHITE'
        visited = set()
        while value in self.colors and value not in visited:
            visited.add(value)
            value = self.colors[value]
        result = string(value)
        if not re.fullmatch(r'#[A-Fa-f0-9]{6}', result):
            raise ValueError(f'Unsupported source light color: {value}')
        return result.upper()

    def area(self, atom):
        fields = {**self.model.fields(atom.path), **atom.fields}
        alpha = number(fields.get('base_lighting_alpha'), 0)
        if not alpha or fields.get('base_lighting_color') == 'null':
            return '#000000'
        color = self.color(fields.get('base_lighting_color'))
        return '#' + ''.join(f'{round(int(color[i:i+2], 16) * alpha / 255):02X}' for i in (1, 3, 5))

    def point(self, atom):
        fields = {**self.model.fields(atom.path), **atom.fields}
        emitter = atom.path.startswith('/obj/effect/light_emitter')
        radius = self.numeric(fields.get('set_luminosity') if emitter else fields.get('light_range'), 0)
        energy = self.numeric(fields.get('set_cap') if emitter else fields.get('light_power'), 1)
        if not radius:
            return None
        return {'type': 'PointLight', 'radius': radius, 'energy': energy,
                'color': self.color(fields.get('light_color')),
                'enabled': energy > 0 and fields.get('light_on', 'TRUE') not in ('FALSE', '0')}

    def components(self, art, atom, native):
        fields = {**self.model.fields(atom.path), **atom.fields}
        if 'PoweredLight' in native and atom.path.startswith('/obj/machinery/light/'):
            bulb = None
            if fields.get('status', 'LIGHT_OK') != 'LIGHT_EMPTY':
                from map_art import digest
                parameters = {'color': self.color(fields.get('bulb_colour')),
                              'lightRadius': number(fields.get('brightness'), 8),
                              'lightEnergy': number(fields.get('bulb_power'), 1),
                              'startingState': {'LIGHT_BROKEN': 'Broken', 'LIGHT_BURNED': 'Burned'}.get(fields.get('status'), 'Normal')}
                bulb = 'BitrunLightBulb' + digest(repr((atom.path, parameters)))
                parent = 'LightBulb' if fields.get('fitting') == '"bulb"' else 'LightTube'
                art.entities[bulb] = {'parent': parent, 'components': [{'type': 'LightBulb', **parameters}]}
                art.placements[atom.path].add(bulb)
            return [{'type': 'PoweredLight', 'bulb': 'Bulb' if fields.get('fitting') == '"bulb"' else 'Tube', 'hasLampOnSpawn': bulb}]
        # Native toggle systems own portable lights. Do not turn an off torch,
        # firearm flashlight or beacon into an always-on light source.
        if any(c in native for c in ('HandheldLight', 'ItemTogglePointLight', 'ExpendableLight', 'PoweredLight')):
            return []
        point = self.point(atom)
        return [point] if point else []
