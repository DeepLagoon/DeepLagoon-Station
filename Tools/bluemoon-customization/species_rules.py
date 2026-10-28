"""Generate the approved species catalogue; unknown BlueMoon anatomy requires gene therapy."""
import json
import re
import subprocess
from collections import Counter, defaultdict
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'Resources/Prototypes/_DeepLagoon/Entities/Mobs/Customization/Markings/species_rules.yml'

FAMILIES = {
    ('Tajaran', 'Ariral'): r'cat|feline|tajaran|tiger|panther|leopard|kathari',
    ('Vulpkanin',): r'fox|fennec|wolf|canid|canine|husky|shepherd|corgi|dalmat|jackal|anub|dog',
    ('Rodentia',): r'mouse|mice|murid|rodent|rat(?:_|$)|rat(?=head|chest|tail|snout)',
    ('Feroxi',): r'shark|akula',
    ('Harpy', 'Resomi', 'Vox'): r'bird|beak|feather|harpy|teshari|tesh_|teshi_|corvid|hawk|pigeon|owl|duck|goose|toucan|swallow|shrike|vox',
    ('Reptilian', 'Hydrakin'): r'lizard|dragon|drake|gator|croc|snake|axolotl|guilmon|spines|noodle',
    ('Moth',): r'moth|moff|luna|monarch|atlas|citheronia|aspen|oakworm|moonfly|deathhead|rosy|whitefly|witchwing|firewatch',
    ('Arachnid',): r'spider|tarantula|arachn|scorpid|scorpion',
    ('Chitinid',): r'centipede|pede|scolo|insect|beetle|scarab|mandible|proboscis|mantis|bee|bug|chitin',
}
COMMON_BODY = r'^(?:abs(?:two|three)?|belly(?:slim(?:alt)?|andbutt)?|colorbelly|blushnormal|blushsmallalt|colouredlips|foreheadmark|handsfeet|heart|lbelly|gloss|plain|gradient|harlequin(?:_reversed)?|floof|floofer|splotches(?:swap)?|sixpack(?:.*)?|weight|tonage|anklet|bands(?:_foot|_hands)?|brows|eyeliner|flushed_cheeks|melatonin_arm|nose_blemish)$'
NO_NATIVE_ANIMAL = r'redpanda|panda|bear|antler|stag|deer|cow|bovine|bull|goat|ram(?:_|$)|bunny|bnnuy|rabbit|leporid|lop(?:_|$)|horse|zebra|elephant|rhino(?!beetle)|raccoon|raccalt|skunk|otter|orca|possum|sloth|bat(?:_|$)'
EXOTIC = r'xeno|sergal|shadekin|acrador|succubus|angel|halo|fairy|bluespace|foxflames|sylveon|eevee|renamon|sabresune|lunasune|zorgoia|deoxys|otie|nightstalker|tailmaw|monster'


def classify(prototype):
    identifier = prototype['id']
    category = prototype['markingCategory']
    if category in ('Hair', 'FacialHair'):
        return None
    if not identifier.startswith('BlueMoon'):
        # Existing species-specific art keeps its assignment; ordinary scars/tattoos are shared.
        if re.search(r'scar(?!ab)|tattoo', identifier, re.I) and not prototype.get('editorHidden'):
            return 'common'
        return None
    if identifier.startswith('BlueMoonHairDiona'):
        return ['Diona']
    text = identifier.removeprefix('BlueMoon')
    for suffix in ('ChestFemale', 'ChestMale', 'HeadSide', 'HeadTop', 'Head', 'Chest', 'LArm', 'RArm', 'LHand', 'RHand', 'LLeg', 'RLeg', 'LFoot', 'RFoot', 'Tail', 'Snout'):
        text = text.removesuffix(suffix)
    text = text.lower()
    if any(word in text for word in ('synth', 'robot', 'cyber', 'metaljaw', 'protogen', 'techarm', 'mechanical', 'pilotvisor', 'pilot_jaw')):
        return ['Synth', 'IPC']
    body = text.removeprefix('mam_body_markings')
    body = re.sub(r'^(secondary|tertiary|other)', '', body)
    if re.search(r'scar(?!ab)|tattoo', body) or re.fullmatch(COMMON_BODY, body) or body.startswith('plain_style'):
        return 'common'
    if re.search(NO_NATIVE_ANIMAL, text) or re.search(EXOTIC, text):
        return []
    if category == 'Wings' and re.search(r'dragon|wyvern|skeleton', text):
        return []
    if text.startswith('horns') and not re.search(r'pede|scolo|spider|sloog', text):
        return ['Oni', 'Demon', 'Reptilian', 'Hydrakin']
    for species, pattern in FAMILIES.items():
        if re.search(pattern, text):
            return list(species)
    # The source's colored wing/fluff patterns are ordinary moth variants.
    if text.startswith(('deco_wings', 'insect_fluff')):
        return ['Moth']
    return []


def generate():
    loader = getattr(yaml, 'CSafeLoader', yaml.SafeLoader)
    loader.add_multi_constructor('!', lambda l, t, n: l.construct_mapping(n) if isinstance(n, yaml.MappingNode) else l.construct_scalar(n))
    files = subprocess.check_output(['rg', '-l', r'^- type: marking\s*$', str(ROOT / 'Resources/Prototypes'), '-g', '*.yml'], text=True).splitlines()
    common, genetic, species = [], [], defaultdict(list)
    for file in sorted(map(Path, files)):
        for prototype in yaml.load(file.read_text(encoding='utf-8-sig'), Loader=loader) or []:
            if not isinstance(prototype, dict) or prototype.get('type') != 'marking':
                continue
            result = classify(prototype)
            if result is None:
                continue
            identifier = prototype['id']
            if result == 'common': common.append(identifier)
            elif not result: genetic.append(identifier)
            else:
                for name in result: species[name].append(identifier)
    rules = {'type': 'markingSpeciesRules', 'id': 'DeepLagoonAppearance', 'common': sorted(common),
             'species': {name: sorted(ids) for name, ids in sorted(species.items())}, 'geneticOnly': sorted(genetic)}
    OUTPUT.write_text('# Generated by Tools/bluemoon-customization/species_rules.py from the approved species families.\n'
                      + yaml.safe_dump([rules], sort_keys=False, allow_unicode=True), encoding='utf-8')
    print(json.dumps({'common': len(common), 'geneticOnly': len(genetic), 'species': {name: len(ids) for name, ids in species.items()}}, ensure_ascii=False))


if __name__ == '__main__':
    generate()
