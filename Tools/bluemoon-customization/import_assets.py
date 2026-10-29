"""Import BlueMoon character accessories into native SS14 markings (Pillow, PyYAML).

Run from any directory: python Tools/bluemoon-customization/import_assets.py SOURCE
Use --audit to inspect without writing resources. Reports belong in --report PATH.
"""
import argparse
import hashlib
import json
import re
import subprocess
import sys
from collections import Counter, defaultdict
from functools import lru_cache
from pathlib import Path

from PIL import Image
import yaml

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tools/body-customization'))
from dmi_states import states as dmi_states

PREFIX = '/datum/sprite_accessory'
DEST = '_DeepLagoon/Customization/BlueMoon'
CATEGORIES = {
    'hair': ('Hair', 'Hair'), 'facial_hair': ('FacialHair', 'FacialHair'),
    'ears': ('HeadSide', 'HeadSide'), 'horns': ('HeadTop', 'HeadTop'),
    'tails': ('Tail', 'Tail'), 'snouts': ('Snout', 'Snout'),
    'frills': ('HeadSide', 'HeadSide'), 'spines': ('Tail', 'Tail'),
    'wings': ('Wings', 'Wings'), 'deco_wings': ('Wings', 'Wings'),
    'insect_wings': ('Wings', 'Wings'), 'insect_fluff': ('Chest', 'Chest'),
    'antenna': ('HeadTop', 'HeadTop'), 'ipc_antenna': ('HeadTop', 'HeadTop'),
    'mam_body_markings': ('Chest', 'Chest'),
}
SOURCE_FIXUPS = {
    '/mam_body_markings/shark/datashark': {'icon': "'modular_citadel/icons/mob/markings_notmammals.dmi'"},
    '/ears/human/jellyfish': {'icon': "'modular_citadel/icons/mob/mam_ears.dmi'"},
    '/tails/mam_tail/takahiro_kitsune': {'icon': "'modular_citadel/icons/mob/mam_tails.dmi'"},
    '/mam_body_markings/synthliz_partial/scutes': {'icon': "'modular_citadel/icons/mob/synthliz_body_markings.dmi'"},
    '/mam_body_markings/synthliz_partial/pecs': {'icon': "'modular_citadel/icons/mob/synthliz_body_markings.dmi'"},
    '/hair/ressshoulder_alt': {'icon_state': '"hair_tressshoulder_alt"'},
    '/hair/vox_afro/vox_keetquills': {'icon_state': '"hair_vox_keetquills"'},
    '/tails/mam_tails/teshari/teshari_thin': {'icon': "'modular_bluemoon/kovac_shitcode/icons/tails.dmi'"},
}
LIMBS = {'Head': ('Head', 'Head'), 'Chest': ('Chest', 'Chest'),
         'Left Arm': ('LArm', 'Arms'), 'Right Arm': ('RArm', 'Arms'),
         'Left Leg': ('LLeg', 'Legs'), 'Right Leg': ('RLeg', 'Legs')}


def strip_comments(text):
    # Preserve quoted strings (including URLs and escaped quotes).
    return re.sub(r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|/\*.*?\*/|//[^\n]*',
                  lambda m: m[0] if m[0][0] in "\"'" else '\n' * m[0].count('\n'), text, flags=re.S)


def definitions(source):
    types = {}
    includes = re.findall(r'^#include "([^"]+\.dm)"', (source / 'tgstation.dme').read_text(encoding='utf-8-sig'), re.M)
    for filename in includes:
        path = source / filename.replace('\\', '/')
        raw = path.read_text(encoding='utf-8-sig')
        if 'datum/sprite_accessory' not in raw:
            continue
        text = strip_comments(raw)
        current = None
        for line in text.splitlines():
            if line and not line[0].isspace():
                match = re.fullmatch(r'/?datum/sprite_accessory(?:/[\w]+)*', line.strip())
                current = '/' + match[0].lstrip('/') if match else None
                if current:
                    types.setdefault(current, {})['_source'] = path.relative_to(source).as_posix()
            elif current:
                match = re.fullmatch(r'[ \t]+(?:var/(?:list/)?)?(\w+)\s*=\s*(.*)', line)
                if match:
                    types[current][match[1]] = match[2].strip()

    @lru_cache(None)
    def inherit(path):
        if not path.startswith(PREFIX):
            return {}
        own = types.get(path, {})
        parent = own.get('parent_type', path.rsplit('/', 1)[0])
        return inherit(parent) | own
    return [(path, inherit(path)) for path in types]


def quoted(value):
    if value and value[0] in "\"'" and value[-1] == value[0]:
        return re.sub(r'\\([\'"\\])', r'\1', value[1:-1])
    return None


@lru_cache(maxsize=64)
def read_dmi(filename):
    with Image.open(filename) as source:
        desc = source.info['Description']
        image = source.convert('RGBA')
    width = int(re.search(r'width = (\d+)', desc)[1])
    height = int(re.search(r'height = (\d+)', desc)[1])
    return image, width, height, {name: (block, dirs, frames, offset) for name, block, dirs, frames, offset in dmi_states(desc)}


def frame_layout(width, height, center=False, dimensions=None):
    """Place BYOND's 32px mob origin at the center of the padded RSI frame."""
    dimension_x, dimension_y = dimensions or (width, height)
    # center_image uses the declared dimensions, which can differ from the DMI.
    pixel_x = -abs(dimension_x - 32) / 2 if center else 0
    pixel_y = -abs(dimension_y - 32) / 2 if center else 0
    origin_x, origin_y = 16 - pixel_x, height - 16 + pixel_y
    out_w = int(2 * max(origin_x, width - origin_x))
    out_h = int(2 * max(origin_y, height - origin_y))
    return (out_w, out_h), (int(out_w / 2 - origin_x), int(out_h / 2 - origin_y))


def frames_for(filename, name, center=False, dimensions=None):
    image, width, height, states = read_dmi(str(filename))
    block, dirs, count, offset = states[name]
    if dirs not in (1, 4):
        raise ValueError(f'Unsupported directions: {dirs}')
    size, position = frame_layout(width, height, center, dimensions)
    columns = image.width // width
    output = []
    for direction in range(dirs):
        frames = []
        for frame in range(count):
            tile = offset + frame * dirs + direction
            x, y = tile % columns * width, tile // columns * height
            canvas = Image.new('RGBA', size)
            canvas.paste(image.crop((x, y, x + width, y + height)), position)
            frames.append(canvas)
        output.append(frames)
    delay = re.search(r'delay = ([\d.,]+)', block)
    delays = [float(v) / 10 for v in delay[1].split(',')] if delay else [0.1] * count
    if len(delays) != count:
        raise ValueError(f'Invalid delays: {filename}/{name}')
    return output, delays


def digest(layers, shape_only=False):
    """Compare every frame/direction, canonicalize transparent pixels and padding."""
    result = hashlib.sha256()
    for frames, delays in layers:
        result.update(str(delays if len(delays) > 1 else []).encode())
        for direction in range(4):
            for frame in frames[direction if len(frames) > 1 else 0]:
                if shape_only:
                    mask = Image.new('RGBA', frame.size, 'white')
                    mask.putalpha(frame.getchannel('A'))
                    frame = mask
                canvas = Image.new('RGBA', (256, 256))
                canvas.paste(frame, ((256 - frame.width) // 2, (256 - frame.height) // 2))
                # RGB under transparent pixels is irrelevant to the rendered image.
                normalized = Image.new('RGBA', canvas.size)
                normalized.alpha_composite(canvas)
                result.update(normalized.tobytes())
    return result.hexdigest()


def existing_markings():
    loader = getattr(yaml, 'CSafeLoader', yaml.SafeLoader)
    loader.add_multi_constructor('!', lambda loader, tag, node: loader.construct_mapping(node) if isinstance(node, yaml.MappingNode) else loader.construct_scalar(node))
    files = subprocess.check_output(['rg', '-l', r'^- type: marking\s*$', str(ROOT / 'Resources/Prototypes'), '-g', '*.yml'], text=True).splitlines()
    for path in sorted(map(Path, files)):
        if path.name == 'bluemoon.yml':
            continue
        raw = path.read_text(encoding='utf-8-sig')
        if not re.search(r'^- type: marking\s*$', raw, re.M):
            continue
        for proto in yaml.load(raw, Loader=loader) or []:
            if isinstance(proto, dict) and proto.get('type') == 'marking':
                yield proto


def rsi_frames(sprite):
    directory = ROOT / 'Resources/Textures' / sprite['sprite']
    meta = json.loads((directory / 'meta.json').read_text(encoding='utf-8-sig'))
    state = next(s for s in meta['states'] if s['name'] == sprite['state'])
    width, height = meta['size']['x'], meta['size']['y']
    dirs = state.get('directions', 1)
    delay = state.get('delays', [[0.1]] * dirs)
    if dirs not in (1, 4) or len({len(d) for d in delay}) != 1:
        raise ValueError('Non-uniform directions')
    with Image.open(directory / (sprite['state'] + '.png')) as image:
        image = image.convert('RGBA')
        count, columns = len(delay[0]), image.width // width
        frames = [[image.crop(((d * count + f) % columns * width, (d * count + f) // columns * height,
                               (d * count + f) % columns * width + width, (d * count + f) // columns * height + height))
                   for f in range(count)] for d in range(dirs)]
    return frames, delay[0]


def nonempty(frames):
    return any(image.getbbox() for direction in frames for image in direction)


def matrix_layers(frames, sections):
    """Turn BYOND palette regions into individually tintable native sprite layers.

    Accessory palette masks use red/green/blue regions, with greyscale intensity
    encoding shading. Black outlines stay on the first layer.
    """
    channels = [i for i, name in enumerate(('RED', 'GREEN', 'BLUE')) if name in sections or sections == 'MATRIX_ALL']
    if not channels:
        channels = [0, 1, 2]
    output = []
    for channel in channels:
        directions = []
        for direction in frames:
            images = []
            for image in direction:
                mask = Image.new('RGBA', image.size)
                pixels = []
                for pixel in image.getdata():
                    rgb = pixel[:3]
                    selected = max(range(3), key=lambda c: rgb[c]) if max(rgb) else channels[0]
                    if selected not in channels:
                        selected = channels[0]
                    value = min(255, sum(rgb[c] for c in channels))
                    pixels.append((value, value, value, pixel[3]) if selected == channel else (0, 0, 0, 0))
                mask.putdata(pixels)
                images.append(mask)
            directions.append(images)
        if nonempty(directions):
            output.append((('primary', 'secondary', 'tertiary')[channel], directions))
    return output


def slug(value):
    return re.sub(r'[^a-z0-9_]+', '_', value.lower()).strip('_')


def style_key(value):
    return re.sub(r'[^a-z0-9]', '', re.sub(r'^(hair|facial)_', '', value.lower()))


def candidates(source, path, data):
    data = data | SOURCE_FIXUPS.get(path[len(PREFIX):], {})
    category = path.split('/')[3] if len(path.split('/')) > 3 else ''
    if category not in CATEGORIES:
        return
    name, state, filename = (quoted(data.get(key)) for key in ('name', 'icon_state', 'icon'))
    if not name or not state or not filename or state.lower() in ('none', 'bald', 'null') or data.get('ignore') == 'TRUE':
        return
    file = source / filename
    if not file.exists():
        return
    _, width, height, states = read_dmi(str(file))
    center = data.get('center') == 'TRUE'
    dimensions = (int(data.get('dimension_x', '32')), int(data.get('dimension_y', '32')))
    base = {'sourceType': path, 'sourceFile': filename, 'definition': data['_source'], 'name': name,
            'bodyPart': CATEGORIES[category][0], 'category': CATEGORIES[category][1]}
    if base['category'] == 'Wings':
        # Ordinary species expose Chest, while Wings is only enabled on IPC.
        base['bodyPart'] = 'Chest'
    if '/diona/' in path:
        base.update(bodyPart='HeadTop', category='HeadTop', species=['Diona'])
    elif '/vox' in path and category == 'hair':
        base['species'] = ['Vox']
    if 'synthliz' in filename or 'synth_lizard' in path:
        base['species'] = ['Synth']
    if category == 'mam_body_markings':
        zones = {'Head': ('head', 'Head', 'Head'), 'Chest': ('chest', 'Chest', 'Chest'),
                 'Left Arm': ('l_arm', 'LArm', 'Arms'), 'Right Arm': ('r_arm', 'RArm', 'Arms'),
                 'Left Hand': ('l_hand', 'LHand', 'Arms'), 'Right Hand': ('r_hand', 'RHand', 'Arms'),
                 'Left Leg': ('l_leg', 'LLeg', 'Legs'), 'Right Leg': ('r_leg', 'RLeg', 'Legs'),
                 'Left Foot': ('l_foot', 'LFoot', 'Legs'), 'Right Foot': ('r_foot', 'RFoot', 'Legs')}
        covered = data.get('covered_limbs', '')
        for limb, (suffix, part, cat) in zones.items():
            coverage = limb if f'"{limb}"' in covered else limb.replace('Hand', 'Arm').replace('Foot', 'Leg')
            if f'"{coverage}"' not in covered:
                continue
            match = re.search('"' + coverage + r'"\s*=\s*(MATRIX_\w+)', covered)
            sections = match[1] if match else data.get('matrixed_sections', 'MATRIX_ALL')
            names = [(f'{state}_{suffix}', None)]
            if suffix == 'chest' and names[0][0] not in states:
                names = [(f'{state}_chest_m', 'Male'), (f'{state}_chest_f', 'Female')]
            for source_state, sex in names:
                if source_state not in states:
                    continue
                frames, delays = frames_for(file, source_state, center, dimensions)
                if not nonempty(frames):
                    continue
                layers = matrix_layers(frames, sections) if data.get('color_src') == 'MATRIXED' else [('base', frames)]
                yield base | {'bodyPart': part, 'category': cat, 'sex': sex,
                              'layers': [(source_state + '_' + channel, images, delays, part) for channel, images in layers]}
        if state == 'colorbelly':
            frames, delays = frames_for(file, 'm_body_markings_colorbelly_ADJ', center, dimensions)
            yield base | {'layers': [(state + '_' + channel, images, delays, 'Chest') for channel, images in matrix_layers(frames, 'MATRIX_GREEN')]}
        return
    if category in ('hair', 'facial_hair'):
        if state in states:
            frames, delays = frames_for(file, state, center, dimensions)
            if nonempty(frames):
                yield base | {'layers': [(state, frames, delays, base['bodyPart'])]}
        return
    if state in states:
        frames, delays = frames_for(file, state, center, dimensions)
        if nonempty(frames):
            yield base | {'layers': [(state, frames, delays, base['bodyPart'])]}
        return
    part = quoted(data.get('mutant_part_string')) or category
    suffixes = [('BEHIND', 'BODY_BEHIND_LAYER'), ('ADJ', 'BODY_ADJ_LAYER'), ('ADJUP', 'BODY_ADJ_UPPER_LAYER'),
                ('FRONT', 'BODY_FRONT_LAYER'), ('HORNS', 'HORNS_LAYER')]
    layers = []
    for suffix, constant in suffixes:
        if constant not in data.get('relevant_layers', ''):
            continue
        for extra in ('', 'extra_', 'extra2_'):
            if extra and data.get(extra.rstrip('_')) != 'TRUE':
                continue
            source_state = f'm_{part}_{extra}{state}_{suffix}'
            if source_state not in states:
                for region in ('primary', 'secondary', 'tertiary'):
                    split_state = source_state + '_' + region
                    if not extra and split_state in states:
                        frames, delays = frames_for(file, split_state, center, dimensions)
                        if nonempty(frames):
                            layers.append((split_state, frames, delays, base['bodyPart']))
                continue
            frames, delays = frames_for(file, source_state, center, dimensions)
            if not nonempty(frames):
                continue
            anchor = base['bodyPart']
            if suffix == 'BEHIND':
                anchor = 'before:Chest' if category in ('tails', 'spines') or base['category'] == 'Wings' else 'before:Head'
            elif suffix in ('FRONT', 'HORNS') and category in ('tails', 'spines'):
                anchor = 'Tail'
            elif suffix == 'FRONT' and base['category'] == 'Wings':
                anchor = 'Chest'
            color_src = data.get(extra + 'color_src' if extra else 'color_src')
            masks = matrix_layers(frames, data.get('matrixed_sections', 'MATRIX_ALL')) if color_src == 'MATRIXED' else [('base', frames)]
            layers.extend((source_state + '_' + channel, images, delays, anchor) for channel, images in masks)
    if layers:
        yield base | {'layers': layers}


def entry_id(entry):
    identifier = 'BlueMoon' + ''.join(word.capitalize() for word in entry['sourceType'][len(PREFIX):].split('/') if word)
    return identifier + entry['bodyPart'] + (entry.get('sex') or '')


def refresh_existing(source, definitions_list, audit):
    """Refresh frames in the shipped catalogue without changing IDs or deduplication."""
    loader = getattr(yaml, 'CSafeLoader', yaml.SafeLoader)
    loader.add_multi_constructor('!', lambda loader, tag, node: loader.construct_mapping(node))
    catalogue = ROOT / 'Resources/Prototypes/_DeepLagoon/Entities/Mobs/Customization/Markings/bluemoon.yml'
    prototypes = {p['id']: p for p in yaml.load(catalogue.read_text(encoding='utf-8'), Loader=loader)}
    revision = subprocess.check_output(['git', '-c', f'safe.directory={source.as_posix()}', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    generated_root = (ROOT / 'Resources/Textures' / DEST).resolve()
    pending, matched = {}, set()
    report = {'revision': revision, 'changed': [], 'prototypes': len(prototypes), 'statesChecked': 0}
    for path, data in definitions_list:
        prefix = 'BlueMoon' + ''.join(word.capitalize() for word in path[len(PREFIX):].split('/') if word)
        if not any(identifier.startswith(prefix) for identifier in prototypes):
            continue
        for entry in candidates(source, path, data):
            identifier = entry_id(entry)
            if identifier not in prototypes:
                continue
            prototype = prototypes[identifier]
            matched.add(identifier)
            expected_states = {sprite['state'] for sprite in prototype['sprites']}
            refreshed_states = set()
            for index, (state_name, frames, delays, _) in enumerate(entry['layers']):
                state = slug(state_name) + '_' + str(index)
                refreshed_states.add(state)
                sprite = next((s for s in prototype['sprites'] if s['state'] == state), None)
                if sprite is None:
                    raise ValueError(f'Source layers changed: {identifier}/{state}')
                directory = (ROOT / 'Resources/Textures' / sprite['sprite']).resolve()
                if directory.parent != generated_root:
                    raise ValueError(f'Asset path escapes output directory: {directory}')
                meta_file = directory / 'meta.json'
                meta = json.loads(meta_file.read_text(encoding='utf-8'))
                source_ref = f'/blob/{revision}/{entry["sourceFile"]}.'
                if source_ref not in meta['copyright']:
                    raise ValueError(f'Source revision/file changed: {identifier}')
                width, height = frames[0][0].size
                count, dirs = len(delays), len(frames)
                sheet = Image.new('RGBA', (width * dirs, height * count))
                for direction in range(dirs):
                    for frame in range(count):
                        tile = direction * count + frame
                        sheet.paste(frames[direction][frame], (tile % dirs * width, tile // dirs * height))
                file = directory / (state + '.png')
                with Image.open(file) as old:
                    changed = old.size != sheet.size or old.convert('RGBA').tobytes() != sheet.tobytes()
                report['statesChecked'] += 1
                if not changed:
                    continue
                pending[file] = sheet
                if directory not in pending:
                    meta['size'] = {'x': width, 'y': height}
                    pending[directory] = (meta_file, meta)
                report['changed'].append({'id': identifier, 'state': state, 'size': [width, height]})
            if refreshed_states != expected_states:
                raise ValueError(f'Source state list changed: {identifier}')
    if matched != prototypes.keys():
        raise ValueError('Source definitions missing for: ' + ', '.join(sorted(prototypes.keys() - matched)))
    # Validate all sources and paths before the first write.
    if not audit:
        for file, value in pending.items():
            if isinstance(value, tuple):
                meta_file, meta = value
                if json.loads(meta_file.read_text(encoding='utf-8')) != meta:
                    meta_file.write_text(json.dumps(meta, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
            else:
                value.save(file)
    print(json.dumps({'prototypes': len(prototypes), 'statesChecked': report['statesChecked'],
                      'statesChanged': len(report['changed']), 'setsChanged': sum(isinstance(v, tuple) for v in pending.values())}))
    return report


def export(source, definitions_list, audit):
    report = {'added': [], 'duplicates': [], 'unmapped': [], 'existingUnreadable': []}
    known = {}
    shapes = {}
    styles = {}
    for proto in existing_markings():
        try:
            images = [rsi_frames(sprite) for sprite in proto.get('sprites', [])]
            signature = digest(images)
            sexes = proto.get('sexRestriction')
            for sex in (sexes if isinstance(sexes, list) else [sexes] if sexes else [None, 'Male', 'Female']):
                known.setdefault((proto['bodyPart'], sex, signature), proto['id'])
            if proto['bodyPart'] in ('Hair', 'FacialHair'):
                shapes.setdefault((proto['bodyPart'], digest(images, shape_only=True)), proto['id'])
                if not proto.get('speciesRestriction'):
                    for sprite in proto.get('sprites', []):
                        styles.setdefault((proto['bodyPart'], style_key(sprite.get('state', ''))), proto['id'])
        except (OSError, ValueError, KeyError, StopIteration) as error:
            report['existingUnreadable'].append({'id': proto['id'], 'error': str(error)})
    print(f'Indexed {len(known)} existing marking images', flush=True)
    revision = subprocess.check_output(['git', '-c', f'safe.directory={source.as_posix()}', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    output, locales = [], []
    used_directories = set()
    for path, data in definitions_list:
        entries = list(candidates(source, path, data))
        category = path.split('/')[3] if len(path.split('/')) > 3 else ''
        if not entries and category in CATEGORIES and quoted(data.get('icon_state')):
            report['unmapped'].append({'type': path, 'state': quoted(data.get('icon_state'))})
        for entry in entries:
            layers = entry['layers']
            signature = digest([(frames, delays) for _, frames, delays, _ in layers])
            key = (entry['bodyPart'], entry.get('sex'), signature)
            shape = (entry['bodyPart'], digest([(frames, delays) for _, frames, delays, _ in layers], shape_only=True)) if entry['bodyPart'] in ('Hair', 'FacialHair') else None
            style = (entry['bodyPart'], style_key(quoted(data.get('icon_state')) or '')) if shape else None
            # Names are never used as duplicate evidence: same labels may have different art.
            if key in known or shape in shapes or style in styles:
                report['duplicates'].append({'type': path, 'part': entry['bodyPart'], 'existing': known.get(key) or shapes.get(shape) or styles[style]})
                continue
            identifier = entry_id(entry)
            for sex in ([entry['sex']] if entry.get('sex') else [None, 'Male', 'Female']):
                known[(entry['bodyPart'], sex, signature)] = identifier
            if shape:
                shapes[shape] = identifier
            rsi_name = slug(path[len(PREFIX):].replace('/', '_')) + '_' + entry['bodyPart'].lower() + (entry.get('sex') or '').lower()
            directory = ROOT / 'Resources/Textures' / DEST / (rsi_name + '.rsi')
            used_directories.add(directory.resolve())
            sprites, states, anchors, before_anchors = [], [], {}, {}
            for index, (state_name, frames, delays, anchor) in enumerate(layers):
                target_state = slug(state_name) + '_' + str(index)
                width, height = frames[0][0].size
                count, dirs = len(delays), len(frames)
                sheet = Image.new('RGBA', (width * dirs, height * count))
                for direction in range(dirs):
                    for frame in range(count):
                        tile = direction * count + frame
                        sheet.paste(frames[direction][frame], (tile % dirs * width, tile // dirs * height))
                if not audit:
                    directory.mkdir(parents=True, exist_ok=True)
                    sheet.save(directory / (target_state + '.png'))
                states.append({'name': target_state, 'directions': dirs, **({'delays': [delays] * dirs} if count > 1 else {})})
                sprites.append({'sprite': f'{DEST}/{rsi_name}.rsi', 'state': target_state})
                if anchor.startswith('before:'):
                    before_anchors[target_state] = anchor[7:]
                elif anchor != entry['bodyPart'] or entry['category'] == 'Tail':
                    anchors[target_state] = anchor
            proto = {'type': 'marking', 'id': identifier, 'bodyPart': entry['bodyPart'], 'markingCategory': entry['category']}
            if category == 'ears':
                proto['editorType'] = 'Ears'
            if entry.get('species'):
                proto['speciesRestriction'] = entry['species']
            if entry.get('sex'):
                proto['sexRestriction'] = entry['sex']
            if data.get('do_colouration') == 'FALSE' or data.get('color_src') in ('NONE', 'FALSE', '0'):
                proto['forcedColoring'] = True
                proto['coloring'] = {'default': {'type': {'__tag': 'SimpleColoring', 'color': '#FFFFFF'}}}
            if anchors:
                proto['spriteLayers'] = anchors
            if before_anchors:
                proto['spriteLayersBefore'] = before_anchors
            proto['sprites'] = sprites
            output.append(proto)
            part_name = {'Head': 'голова', 'Chest': 'торс', 'LArm': 'левая рука', 'RArm': 'правая рука',
                         'LHand': 'левая кисть', 'RHand': 'правая кисть', 'LLeg': 'левая нога', 'RLeg': 'правая нога'}.get(entry['bodyPart'])
            display = entry['name'] + (f' ({part_name})' if category == 'mam_body_markings' and part_name else '')
            # Keep original searchable style names; namespace comes from the prototype ID.
            locales.append(f'marking-{identifier} = {display}')
            locales.extend(f'marking-{identifier}-{sprite["state"]} = Слой {index + 1}' for index, sprite in enumerate(sprites))
            report['added'].append({key: value for key, value in entry.items() if key != 'layers'} | {'id': identifier})
            if not audit:
                meta = {'version': 1, 'size': {'x': width, 'y': height}, 'license': 'CC-BY-SA-3.0',
                        'copyright': f'tgstation, Citadel, SPLURT, BlueMoon and NovaSector contributors. Converted from https://github.com/BlueMoon-Labs/MOLOT-BlueMoon-Station/blob/{revision}/{entry["sourceFile"]}. Definitions: {entry["definition"]}. Palette regions split into tintable layers; origin preserved with transparent padding.', 'states': states}
                (directory / 'meta.json').write_text(json.dumps(meta, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    if not audit:
        proto_file = ROOT / 'Resources/Prototypes/_DeepLagoon/Entities/Mobs/Customization/Markings/bluemoon.yml'
        proto_file.parent.mkdir(parents=True, exist_ok=True)
        class Dumper(yaml.SafeDumper):
            pass
        def tagged_dict(dumper, value):
            return dumper.represent_mapping('!type:' + value['__tag'], {k: v for k, v in value.items() if k != '__tag'}) if '__tag' in value else dumper.represent_dict(value)
        Dumper.add_representer(dict, tagged_dict)
        proto_file.write_text('# Generated by Tools/bluemoon-customization/import_assets.py.\n' + yaml.dump(output, Dumper=Dumper, sort_keys=False, allow_unicode=True), encoding='utf-8')
        for locale in ('en-US', 'ru-RU'):
            locale_file = ROOT / f'Resources/Locale/{locale}/_DeepLagoon/bluemoon-customization.ftl'
            locale_file.parent.mkdir(parents=True, exist_ok=True)
            locale_file.write_text('# BlueMoon character customization; original style names retained for search.\n' + '\n'.join(locales) + '\n', encoding='utf-8')
        generated_root = (ROOT / 'Resources/Textures' / DEST).resolve()
        for directory in generated_root.glob('*.rsi'):
            resolved = directory.resolve()
            if resolved in used_directories:
                continue
            if resolved.parent != generated_root:
                raise ValueError('Generated asset path escapes output directory')
            meta_file = directory / 'meta.json'
            if not meta_file.exists() or 'MOLOT-BlueMoon-Station/blob/' not in json.loads(meta_file.read_text(encoding='utf-8'))['copyright']:
                raise ValueError(f'Refusing to remove an unrecognized asset: {directory}')
            files = list(directory.iterdir())
            if any(not file.is_file() or file.name != 'meta.json' and file.suffix != '.png' for file in files):
                raise ValueError(f'Unexpected files in generated asset: {directory}')
            for file in files:
                file.unlink()
            directory.rmdir()
        from species_rules import generate
        generate()
    report['revision'] = revision
    print(json.dumps({'added': dict(Counter(p['category'] for p in report['added'])), 'duplicates': len(report['duplicates']),
                      'unmapped': len(report['unmapped']), 'unreadable': len(report['existingUnreadable'])}, ensure_ascii=False), flush=True)
    return report


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--audit', action='store_true')
    parser.add_argument('--inventory', action='store_true')
    parser.add_argument('--refresh-existing', action='store_true', help='Refresh shipped sprite frames without changing the catalogue or IDs')
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    definitions_list = definitions(args.source)
    if args.refresh_existing:
        report = refresh_existing(args.source, definitions_list, args.audit)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        return
    counts = Counter(path.split('/')[3] for path, data in definitions_list if len(path.split('/')) > 3)
    examples = defaultdict(list)
    for path, data in definitions_list:
        category = path.split('/')[3] if len(path.split('/')) > 3 else ''
        if category not in CATEGORIES or not quoted(data.get('name')) or not quoted(data.get('icon_state')):
            continue
        filename = quoted(data.get('icon'))
        if not filename or not (args.source / filename).exists():
            continue
        _, w, h, states = read_dmi(str(args.source / filename))
        examples[category].append({'path': path, 'name': quoted(data.get('name')), 'state': quoted(data.get('icon_state')),
                                  'icon': filename, 'size': [w, h], 'states': list(states)[:8], 'data': data})
    report = {'types': counts, 'examples': examples} if args.inventory else export(args.source, definitions_list, args.audit)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
