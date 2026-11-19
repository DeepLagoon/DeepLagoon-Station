"""Explicit adapters for the elite MOD's source equipment and installed modules."""
from source_model import number, string

MODULES = {
    'storage/syndicate': ('ModModuleStorage', 'Storage', 'storage', None, False),
    'shock_absorber': ('ModModuleBase', 'Passive', 'shock-absorber', 'ModBatonResistance', True),
    'emp_shield': ('ModModuleEmpShield', 'Passive', 'emp-shield', 'ModEmpShield', False),
    'magnetic_harness': ('ModModuleBase', 'Passive', 'magnetic-harness', 'ModMagneticHarness', True),
    'jetpack/advanced': ('ModModuleJetpackAdvanced', 'Jetpack', 'jetpack', None, False),
    'jump_jet': ('ModModuleBase', 'JumpJet', 'jump-jet', None, False),
    'flashlight': ('ModModuleFlashlight', 'Flashlight', 'light', None, False),
    'hat_stabilizer/syndicate': ('ModModuleBase', 'Passive', 'hat-stabilizer', 'ModHatStabilizer', False),
    'quick_cuff': ('ModModuleBase', 'Passive', 'restraint-assist', 'CuffingTimeModifier', True),
    'welding/syndicate': ('ModModuleWelding', 'Passive', 'welding', 'EyeProtection', False),
    'night': ('ModModuleNightVision', 'Toggle', 'night-vision', None, False),
    'hearing_protection': ('ModModuleBase', 'Passive', 'hearing-protection', 'ModHearingProtection', True),
}


def elite_mod(g, path):
    if path.startswith('/obj/item/mod/module/'):
        key = path.removeprefix('/obj/item/mod/module/')
        if key not in MODULES:
            return None
        parent, mode, family, effect, wearer = MODULES[key]
        fields = g.source.fields(path)
        def energy(field):
            expression = fields.get(field, 'DEFAULT_CHARGE_DRAIN * 0')
            return number(expression.replace('DEFAULT_CHARGE_DRAIN', '50'), 0)
        module = {'type': 'ModModule', 'mode': mode, 'family': family,
            'complexity': int(number(fields.get('complexity'), 0)),
            'removable': fields.get('removable') != 'FALSE', 'affectsWearer': wearer,
            'useCharge': energy('use_energy_cost'), 'idleCharge': energy('idle_power_cost'),
            'activeCharge': energy('active_power_cost')}
        if effect:
            module['effects'] = [{'type': effect}]
            if key == 'emp_shield': module['effects'][0]['pulseCost'] = 0
        if key in ('hat_stabilizer/syndicate', 'welding/syndicate', 'night', 'hearing_protection'):
            module['slot'] = 'head'
        elif key == 'quick_cuff':
            module['slot'] = 'gloves'
        if key == 'jump_jet': module['useDelay'] = 30
        if key in ('jump_jet', 'jetpack/advanced'): module['startPinned'] = True
        components = [module]
        if key not in ('jetpack/advanced', 'quick_cuff', 'night', 'hearing_protection'):
            components.insert(0, {'type': 'Sprite', 'sprite': '_DeepLagoon/Bitrunning/elite_mod_modules.rsi',
                                'state': string(fields.get('icon_state'))})
        if key == 'storage/syndicate':
            module.update(storageMaxItems=21, storageMaxWeight=30)
            components.append({'type': 'Storage', 'grid': ['0,0,11,7'], 'maxItemSize': 'Normal'})
        return g.entity(path, parent, components)

    if path != '/obj/item/mod/control/pre_equipped/elite':
        return None
    fields = g.source.fields(path)
    theme = g.source.fields('/datum/mod_theme/elite')
    import re
    installed = re.findall(r'/obj/item/mod/module/[\w/]+', fields['applied_modules'])
    inbuilt = re.findall(r'/obj/item/mod/module/[\w/]+', theme['inbuilt_modules'])
    modules = [elite_mod(g, item) for item in installed + inbuilt]
    if None in modules:
        g.unresolved[path] = 'The elite MOD has an unsupported source module.'
        return None
    cell = g.entity('/obj/item/stock_parts/power_store/cell/bluespace', 'PowerCellHigh', [
        {'type': 'Battery', 'maxCharge': 400000, 'startingCharge': 400000}])
    parts = {}
    for slot, native, part in [('head', 'ClothingModsuitHelmetStandard', 'helmet'),
            ('outerClothing', 'ClothingModsuitChestplateStandard', 'chestplate'),
            ('gloves', 'ClothingModsuitGauntletsStandard', 'gauntlets'),
            ('shoes', 'ClothingModsuitBootsStandard', 'boots')]:
        prototype = 'BitrunModElite' + part.title()
        components = visuals(part, slot) + [
            {'type': 'Armor', 'modifiers': {'coefficients': {
                'Blunt': 0.4, 'Slash': 0.4, 'Piercing': 0.4, 'Heat': 0.5, 'Shock': 0.5,
                'Poison': 0, 'Caustic': 0, 'Radiation': 1}},
             'coverage': {'head': 'Head', 'outerClothing': 'Torso, Groin', 'gloves': 'Hands, Arms', 'shoes': 'Feet, Legs'}[slot],
             'coveredExplosionCoefficient': 0.45},
            {'type': 'ExplosionResistance', 'damageCoefficient': 1},
            {'type': 'Insulated', 'coefficient': 0},
            {'type': 'FireProtection', 'reduction': 1},
            {'type': 'ClothingSpeedModifier', 'walkModifier': 1, 'sprintModifier': 1},
            {'type': 'StaminaDamageResistance', 'coefficient': 1}]
        if part == 'boots':
            # Native standard MOD boots supply built-in magboots and sealed
            # NoSlip. WhiteMoon supplies those through optional modules only.
            native = 'ClothingShoesBase'
            components.extend([
                {'type': 'Appearance'},
                {'type': 'SealableClothing',
                 'sealUpPopup': 'sealable-clothing-seal-up-boots',
                 'sealDownPopup': 'sealable-clothing-seal-down-boots'}])
        g.rows[prototype] = {'type': 'entity', 'id': prototype, 'parent': native,
            'name': 'elite MOD ' + part, 'description': string(theme['desc']),
            'categories': ['HideSpawnMenu'], 'components': components}
        parts[slot] = prototype
    result = g.entity(path, 'ClothingModsuitBase', visuals('control', 'back') + [
        {'type': 'ModControl', 'capacity': 19},
        {'type': 'ModStartingModules', 'modules': modules},
        {'type': 'ToggleableClothing', 'clothingPrototypes': parts},
        {'type': 'ModLink', 'frequency': 1213},
        {'type': 'AccessReader', 'access': [['SyndicateAgent'], ['NuclearOperative']]},
        {'type': 'ItemSlots', 'slots': {'cell_slot': {'startingItem': cell},
                                      'core-container': {'startingItem': 'ModsuitCoreStandard'}}},
        {'type': 'PowerCellDraw', 'drawRate': 50, 'useRate': 0},
        {'type': 'Insulated', 'coefficient': 0},
        {'type': 'FireProtection', 'reduction': 1},
        {'type': 'ExplosionResistance', 'damageCoefficient': 1}])
    g.rows[result]['name'] = 'elite MOD control unit'
    g.rows[result]['description'] = string(theme['desc'])
    return result


def visuals(part, slot):
    state = 'elite-' + part
    inv = '_DeepLagoon/Bitrunning/elite_mod_inventory.rsi'
    worn = '_DeepLagoon/Bitrunning/elite_mod_worn.rsi'
    normal = {slot: [{'state': state}]}
    sealed = {slot: [{'sprite': worn, 'state': state + '-sealed'}]}
    if part in ('boots', 'chestplate'):
        normal[slot + '-digitigrade'] = [{'sprite': '_DeepLagoon/Bitrunning/elite_mod_anthro.rsi', 'state': state}]
        sealed[slot + '-digitigrade'] = [{'sprite': '_DeepLagoon/Bitrunning/elite_mod_anthro.rsi', 'state': state + '-sealed'}]
    if part == 'helmet':
        for species in ('vulpkanin', 'hydrakin'):
            normal[slot + '-' + species] = [{'sprite': '_DeepLagoon/Bitrunning/elite_mod_anthro.rsi', 'state': state}]
            sealed[slot + '-' + species] = [{'sprite': '_DeepLagoon/Bitrunning/elite_mod_anthro.rsi', 'state': state + '-sealed'}]
    return [{'type': 'Sprite', 'sprite': inv, 'layers': [
                {'state': state, 'map': ['unsealed']},
                {'state': state + '-sealed', 'visible': False, 'map': ['sealed']}]},
            {'type': 'Clothing', 'sprite': worn, 'clothingVisuals': normal},
            {'type': 'SealableClothingVisuals', 'spriteLayer': 'sealed', 'replaceBaseLayers': True,
                'clothingVisuals': sealed}]
