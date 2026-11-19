"""Handwritten adapters for source items whose callbacks cannot be inferred from fields.

Keep callbacks explicit: an inventory picture is not a functional implementation.
"""
from source_model import number
from storage_import import parse_contents, source_paths, all_of
from mod_import import elite_mod
import re

BEACON_COLORS = ('random', 'burgundy', 'bronze', 'yellow', 'lime', 'olive', 'jade', 'teal', 'cerulean', 'indigo', 'purple', 'violet', 'fuchsia')

BALLOON_COLORS = ('red', 'blue', 'green', 'yellow', 'orange', 'purple')
BALLOON_ANIMALS = ('guy', 'nukie', 'clown', 'cat', 'fly', 'podguy', 'ai', 'dog', 'xeno',
                   'banana', 'lizard', 'slime', 'moth', 'ethereal', 'plasmaman')


def special_item(g, path):
    if path.startswith('/obj/item/mod/'):
        return elite_mod(g, path)
    def entity(parent, components):
        if parent == 'BaseItem':
            components.insert(0, {'type': 'Sprite', 'sprite': '_DeepLagoon/Bitrunning/circuitboard.rsi', 'state': 'circuit_board'})
        return g.entity(path, parent, components)

    if path in ('/obj/item/suit_voucher', '/obj/item/mining_voucher'):
        suit = path == '/obj/item/suit_voucher'
        prefix = '/datum/voucher_set/' + ('mining_suit/' if suit else 'mining/')
        rewards = []
        missing = []
        for choice in sorted(p for p in g.source.types if p.startswith(prefix) and '/' not in p[len(prefix):]):
            source_items = re.findall(r'/[\w/]+', g.source.fields(choice).get('set_items', ''))
            items = [g.mob(item) if item.startswith('/mob/') else g.ensure_item(item) for item in source_items]
            if not items or None in items:
                missing.append(choice + ': ' + ', '.join(item for item, target in zip(source_items, items) if target is None))
                continue
            rewards.append({'name': ('bitrun-suit-voucher-' if suit else 'bitrun-mining-voucher-') + choice[len(prefix):], 'items': items})
        if missing:
            g.unresolved[path] = 'Source voucher rewards lack functional adapters: ' + '; '.join(missing)
            return None
        terminal = '/obj/machinery/computer/order_console/mining'
        g.entity(terminal, g.native_mapping(terminal) or 'ComputerFrame', [{'type': 'BitrunVoucherRedeemer'}])
        return entity('BaseItem', [{'type': 'BitrunEquipmentVoucher', 'rewards': rewards}])

    if path.startswith('/obj/item/resonator'):
        fields = g.source.fields(path)
        return entity('BaseItem', [{'type': 'BitrunResonator',
            'fieldLimit': int(number(fields.get('fieldlimit'), 4)),
            'quickBurstModifier': number(fields.get('quick_burst_mod'), 0.8),
            'spreadFailureIncrement': int(number(fields.get('adding_failure'), 50)),
            'modes': 3 if path.endswith('/upgraded') else 2},
            {'type': 'MeleeWeapon', 'damage': {'types': {'Blunt': number(fields.get('force'), 15)}}}])

    if path == '/obj/item/kinetic_crusher':
        fields = g.source.fields(path)
        bolt = g.entity('/obj/projectile/destabilizer', 'BulletCharge', [
            {'type': 'DamageMarkerOnCollide', 'duration': 30, 'whitelist': {'tags': [g.tag('BitrunCrusherMarkable')]},
             'damage': {'types': {'Blunt': 0}}},
            {'type': 'BitrunProjectileRange', 'range': 6, 'speed': 12.5},
            {'type': 'TimedDespawn', 'lifetime': 120}])
        return entity('WeaponCrusher', [
            {'type': 'BitrunCrusher', 'detonationDamage': number(fields.get('detonation_damage'), 50),
             'backstabBonus': number(fields.get('backstab_bonus'), 30)},
            {'type': 'MeleeWeapon', 'damage': {'types': {'Blunt': 0, 'Slash': 0, 'Structural': 0}}, 'armorPenetration': 0.1, 'autoAttack': False},
            {'type': 'IncreaseDamageOnWield', 'damage': {'types': {'Blunt': 0, 'Structural': 0, 'Slash': number(fields.get('force_wielded'), 20)}}},
            {'type': 'LeechOnMarker', 'leech': {'groups': {'Brute': 0}}},
            {'type': 'Gun', 'fireRate': 1 / 1.5, 'selectedMode': 'SemiAuto', 'availableModes': ['SemiAuto'], 'resetOnHandSelected': False},
            {'type': 'RechargeBasicEntityAmmo', 'rechargeCooldown': 1.5},
            {'type': 'BasicEntityAmmoProvider', 'proto': bolt, 'capacity': 1, 'count': 1},
            {'type': 'Item', 'size': 'Large', 'shape': ['0,0,1,1']},
            {'type': 'PointLight', 'radius': 5, 'energy': 1.2, 'color': '#ffff66', 'enabled': False}])

    if path == '/obj/item/card/id/advanced/mining':
        return entity('SalvageIDCard', [{'type': 'Access', 'tags': ['Cargo', 'Salvage']}])
    if path == '/obj/item/weldingtool/hugetank':
        volume = number(g.source.fields(path).get('max_fuel'), 80)
        return entity('WelderIndustrial', [{'type': 'SolutionContainerManager', 'solutions': {'Welder': {
            'maxVol': volume, 'reagents': [{'ReagentId': 'WeldingFuel', 'Quantity': volume}]}}}])
    if path == '/obj/item/extraction_pack':
        return entity('Fulton', [{'type': 'Stack', 'count': int(number(g.source.fields(path).get('uses_left'), 3))}])
    if path == '/obj/item/fulton_core':
        return entity('FultonBeacon', [])
    if path.startswith('/obj/item/survivalcapsule'):
        bathroom = path.endswith('/bathroom')
        if path not in ('/obj/item/survivalcapsule', '/obj/item/survivalcapsule/bathroom'):
            g.unresolved[path] = 'This shelter template needs an explicit source map import.'
            return None
        return entity('BaseItem', [{'type': 'BitrunShelterCapsule', 'template': '/Maps/_DeepLagoon/Bitrunning/Shelters/' + ('shelter_t.yml' if bathroom else 'shelter_1.yml'),
            'width': 3 if bathroom else 5, 'height': 3 if bathroom else 5}])
    if path == '/obj/machinery/smartfridge/survival_pod/preloaded':
        tree = all_of([{'kind': 'spawn', 'path': '/obj/item/food/donkpocket/warm', 'count': 5},
            {'kind': 'group', 'children': [{'kind': 'spawn', 'path': '/obj/item/storage/dice', 'count': 1, 'weight': 1},
                                          {'kind': 'spawn', 'path': '/obj/item/instrument/guitar', 'count': 1, 'weight': 1}]}])
        aliases = {'/obj/item/food/donkpocket/warm': 'FoodDonkpocketWarm', '/obj/item/storage/dice': 'DiceBag',
                   '/obj/item/instrument/guitar': 'AcousticGuitarInstrument'}
        for item, parent in aliases.items():
            g.entity(item, parent, [])
        return entity('SmartFridge', [{'type': 'SmartFridge', 'whitelist': None}, {'type': 'BitrunShelterFridge'},
            {'type': 'EntityTableContainerFill', 'containers': {'smart_fridge_inventory': g.table(tree, {})}}])

    if path in ('/obj/item/storage/backpack/duffelbag/mining_conscript',
                '/obj/item/storage/backpack/duffelbag/mining_bunny/conscript',
                '/obj/item/storage/belt/mining/vendor', '/obj/item/storage/lunchbox/bunny/carrot'):
        tree = all_of([parse_contents(body) for _, _, body in g.source.initialization(path, 'PopulateContents')])
        for item in sorted(source_paths(tree)):
            g.ensure_item(item)
        missing = [item for item in source_paths(tree) if not g.targets.get(item)]
        if missing:
            g.unresolved[path] = 'Source storage contents lack adapters: ' + ', '.join(sorted(missing))
            return None
        parent = 'ClothingBackpackDuffel' if '/duffelbag/' in path else 'ClothingBeltStorageBase' if '/belt/' in path else 'LunchboxGeneric'
        target = entity(parent, [{'type': 'Item', 'size': 'Large' if '/duffelbag/' in path else 'Normal', 'shape': ['0,0,1,1']},
            {'type': 'Storage', 'grid': ['0,0,9,7'] if '/duffelbag/' in path else ['0,0,5,3'],
            'maxItemSize': 'Huge' if '/duffelbag/' in path else 'Normal', 'whitelist': None, 'blacklist': None}, {'type': 'StorageFill', 'contents': []},
            {'type': 'EntityTableContainerFill', 'containers': {'storagebase': g.table(tree, {})}}])
        return target

    if path == '/obj/item/storage/box/balloons':
        balloon = g.ensure_item('/obj/item/toy/balloon/long')
        return entity('BoxCardboard', [{'type': 'Storage', 'grid': ['0,0,11,7'], 'maxItemSize': 'Normal',
            'quickInsert': False, 'areaInsert': False, 'whitelist': {'tags': [g.tag('BitrunLongBalloon')]}},
            {'type': 'StorageFill', 'contents': [{'id': balloon, 'amount': 24}]}])
    if path == '/obj/item/toy/balloon/long':
        for animal in BALLOON_ANIMALS:
            g.ensure_item('/obj/item/toy/balloon_animal/' + animal)
        modes = {color: {'inhandVisuals': {
            side: [{'rsi': f'_DeepLagoon/Bitrunning/long_balloons_{side}.rsi', 'state': 'balloon_long_' + color}]
            for side in ('left', 'right')}} for color in BALLOON_COLORS}
        return entity('BalloonNT', [{'type': 'Sprite', 'sprite': '_DeepLagoon/Bitrunning/long_balloons.rsi',
            'layers': [{'state': 'balloon_long_red', 'map': ['source-balloon']}]},
            {'type': 'Item', 'size': 'Normal'}, {'type': 'Tag', 'tags': ['Balloon', g.tag('BitrunLongBalloon')]},
            {'type': 'BitrunLongBalloon', 'colorVisuals': modes},
            {'type': 'Appearance'}, {'type': 'GenericVisualizer', 'visuals': {'enum.BitrunEquipmentVisuals.Mode': {
                'source-balloon': {color: {'state': 'balloon_long_' + color} for color in BALLOON_COLORS}}}}])
    if path.startswith('/obj/item/toy/balloon_animal/'):
        return entity('BalloonNT', [{'type': 'Destructible', 'thresholds': [{
            'trigger': g.tagged('DamageTrigger', damage=200), 'behaviors': [g.tagged('DoActsBehavior', acts=['Destruction'])]}]}])

    if path == '/obj/item/clothing/accessory/waistcoat':
        return entity('BitrunImportedClothingOuterBase', [])
    if path == '/obj/item/circuitboard/machine/dish_drive':
        machine = g.entity('/obj/machinery/dish_drive', 'BitrunDishDrive', [])
        return entity('BaseMachineCircuitboard', [{'type': 'BitrunDishDriveBoard'}, {'type': 'MachineBoard',
            'prototype': machine, 'requirements': {'Manipulator': 1, 'MatterBin': 2}, 'stackRequirements': {'Glass': 1}}])
    if path == '/obj/item/grenade/mirage':
        return entity('GrenadeBase', [{'type': 'BitrunMirageGrenade'}, {'type': 'OnUseTimerTrigger', 'delay': 3}, {'type': 'DeleteOnTrigger'}])
    if path == '/obj/item/laser_pointer/blue':
        return entity('BaseItem', [{'type': 'BitrunLaserPointer'}])
    if path == '/obj/item/melee/rune_carver':
        return entity('BaseItem', [{'type': 'MeleeWeapon', 'damage': {'types': {'Slash': 10}}}, {'type': 'BitrunRuneCarver'}])
    if path == '/obj/item/melee/baton/abductor':
        return entity('BaseItem', [{'type': 'MeleeWeapon', 'damage': {'types': {'Blunt': 7}}}, {'type': 'BitrunAbductorBaton'}])
    if path == '/obj/item/melee/baton/telescopic/silver':
        return entity('BaseItem', [{'type': 'ItemToggle'}, {'type': 'ItemToggleMeleeWeapon',
            'activatedDamage': {'types': {'Blunt': 10}}, 'deactivatedDamage': {'types': {'Blunt': 0}}},
            {'type': 'MeleeWeapon', 'damage': {'types': {'Blunt': 0}}},
            {'type': 'BitrunTelescopicBaton'}])
    if path == '/obj/item/melee/chainofcommand':
        return entity('BaseItem', [{'type': 'MeleeWeapon', 'damage': {'types': {'Blunt': 10}}}, {'type': 'BitrunCommandWhip'}])
    if path == '/obj/item/melee/skateboard/hoverboard':
        return entity('BaseItem', [{'type': 'MeleeWeapon', 'damage': {'types': {'Blunt': 12}}},
                                  {'type': 'BitrunDeployable', 'prototype': 'BitrunRiddenHoverboard', 'mount': True}])
    if path.startswith('/obj/item/reagent_containers/spray/waterflower'):
        lube = path.endswith(('/superlube', '/lube'))
        volume = number(g.source.fields(path).get('volume'), 10)
        return entity('SprayBottle', [
            {'type': 'Spray', 'transferAmount': 1, 'sprayDistance': 1},
            {'type': 'SolutionContainerManager', 'solutions': {'spray': {
                'maxVol': volume, 'reagents': [{'ReagentId': 'SpaceLube' if lube else 'Water', 'Quantity': volume}]}}}])
    if path == '/obj/item/grenade/chem_grenade/clf3':
        return entity('CleanerGrenade', [{'type': 'SmokeOnTrigger', 'duration': 15,
            'spreadAmount': 100, 'smokePrototype': 'Foam', 'solution': {
                'reagents': [{'ReagentId': 'ChlorineTrifluoride', 'Quantity': 100}]}}])
    if path == '/obj/item/evidencebag':
        return entity('BaseItem', [
            {'type': 'BitrunEvidenceBag'},
            {'type': 'ContainerContainer', 'containers': {'evidence': g.container_slot()}},
            {'type': 'ItemSlots', 'slots': {'evidence': {'name': 'bitrun-evidence-slot',
                'insertOnInteract': True, 'ejectOnUse': True, 'whitelist': {'components': ['Item']}}}}])
    if path == '/obj/item/etherealballdeployer':
        return entity('BaseItem', [{'type': 'BitrunDeployable', 'prototype': 'BitrunEtherealDiscoBall'}])
    if path.startswith('/obj/item/stack/marker_beacon'):
        if path != '/obj/item/stack/marker_beacon':
            g.ensure_item('/obj/item/stack/marker_beacon')
        return entity('BaseItem', [
            {'type': 'Sprite', 'sprite': '_DeepLagoon/Bitrunning/marker_beacon.rsi', 'layers': [{'state': 'markerrandom', 'map': ['beacon']}]},
            {'type': 'Appearance'}, {'type': 'BitrunBeaconColor'},
            {'type': 'GenericVisualizer', 'visuals': {'enum.BitrunEquipmentVisuals.Mode': {'beacon': {color: {'state': 'marker' + color} for color in BEACON_COLORS}}}},
            {'type': 'Stack', 'stackType': 'BitrunMarkerBeacon', 'count': int(number(g.source.fields(path).get('amount'), 1))}])
    return None
