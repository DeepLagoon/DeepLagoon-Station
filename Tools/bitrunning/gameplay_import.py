"""Generate functional item families from WhiteMoon's authored DM data."""
from pathlib import Path
from copy import deepcopy
import json
import re
import yaml

from source_model import SourceModel, number, string, body_coverage
from storage_import import parse_contents, source_paths, all_of
from map_art import remap_prototypes, MapArt
from special_items import special_item

OUTPUT = Path("Resources/Prototypes/_DeepLagoon/Bitrunning/WhiteMoonGameplay.yml")


class Tagged(dict):
    def __init__(self, kind, **values):
        super().__init__(values)
        self.kind = kind


yaml.SafeDumper.add_representer(Tagged, lambda dumper, value:
                              dumper.represent_mapping('!type:' + value.kind, value.items()))

ALIASES = {
    '/obj/item/knife/hunting': 'SurvivalKnife', '/obj/item/knife/kitchen': 'KitchenKnife',
    '/obj/item/knife/butcher': 'ButchCleaver', '/obj/item/knife': 'CombatKnife',
    '/obj/item/hatchet': 'HydroponicsToolHatchet', '/obj/item/pickaxe': 'Pickaxe',
    '/obj/item/melee/baton/security': 'Stunbaton',
    '/obj/item/dualsaber': 'EnergySwordDouble', '/obj/item/melee/energy/sword': 'EnergySword',
    '/obj/item/melee/sickly_blade': 'CombatKnife', '/obj/item/grenade/smokebomb': 'SmokeGrenade',
    '/obj/item/flashlight/flare/candle/infinite': 'CandleInfinite',
    '/obj/item/flashlight/flare/candle': 'Candle', '/obj/item/flashlight/flare/torch': 'Torch',
    '/obj/item/crowbar': 'Crowbar', '/obj/item/extinguisher': 'FireExtinguisher',
    '/obj/item/radio/headset': 'ClothingHeadsetService', '/obj/item/rag': 'RagItem',
    '/obj/item/instrument/piano_synth': 'SynthesizerInstrument',
    '/obj/item/food/meat/slab/monkey': 'FoodMeat', '/obj/item/food/pie/cream': 'FoodPieBananaCream',
    '/obj/item/food/pizzaslice/dank': 'FoodPizzaMushroomSlice',
    '/obj/item/reagent_containers/blood': 'Bloodpack',
    '/obj/item/reagent_containers/condiment/flour': 'ReagentContainerFlour',
    '/obj/item/reagent_containers/condiment/rice': 'ReagentContainerRice',
    '/obj/item/reagent_containers/condiment/sugar': 'ReagentContainerSugar',
    '/obj/item/storage/backpack/duffelbag': 'ClothingBackpackDuffel',
    '/obj/item/storage/backpack/industrial': 'ClothingBackpack',
    '/obj/item/storage/backpack/satchel/leather': 'ClothingBackpackSatchelLeather',
    '/obj/item/storage/belt/military': 'ClothingBeltSecurity',
    '/obj/item/storage/bag/trash': 'TrashBag', '/obj/item/storage/inflatable': 'BoxInflatable',
    '/obj/item/storage/box/emergency_spacesuit': 'BoxSurvival',
    '/obj/item/storage/box/nri_survival_pack': 'BoxSurvival',
    '/obj/item/storage/wallet': 'ClothingWalletLeatherBrown',
    '/obj/item/storage/toolbox/emergency': 'ToolboxEmergency',
    '/obj/item/vending_refill/boozeomat': 'VendingMachineRestockBooze',
    '/obj/item/vending_refill/cigarette': 'VendingMachineRestockSmokes',
    '/obj/item/bodypart/arm/left/robot/surplus': 'LeftArmCyberneticBase',
    '/obj/item/bodypart/arm/right/robot/surplus': 'RightArmCyberneticBase',
    '/obj/item/bodypart/leg/left/robot/surplus': 'LeftLegCybernetic',
    '/obj/item/bodypart/leg/right/robot/surplus': 'RightLegCybernetic',
    '/obj/item/radio/headset/headset_eng': 'ClothingHeadsetEngineering',
    '/obj/item/radio/headset/headset_med': 'ClothingHeadsetMedical',
    '/obj/item/radio/headset/heads/ce': 'ClothingHeadsetCE',
    '/obj/item/radio/headset/heads/cmo': 'ClothingHeadsetCMO',
    '/obj/item/radio/headset/heads/hos': 'ClothingHeadsetCommand',
    '/obj/item/radio/headset/headset_sec': 'ClothingHeadsetSecurity',
    '/obj/item/modular_computer/pda': 'PassengerPDA',
    '/obj/item/modular_computer/pda/heads/ce': 'CEPDA',
    '/obj/item/modular_computer/pda/heads/cmo': 'CMOPDA',
    '/obj/item/modular_computer/pda/heads/hos': 'HoSPDA',
    '/obj/item/modular_computer/pda/engineering': 'EngineerPDA',
    '/obj/item/modular_computer/pda/medical': 'MedicalPDA',
    '/obj/item/modular_computer/pda/security': 'SecurityPDA',
    '/obj/item/modular_computer/pda/shaftminer': 'SalvagePDA',
    '/obj/item/modular_computer/pda/clown': 'ClownPDA',
    '/obj/item/storage/belt/utility/chief/full': 'ClothingBeltUtilityEngineering',
    '/obj/item/storage/belt/utility/full/engi': 'ClothingBeltUtilityEngineering',
    '/obj/item/analyzer': 'GasAnalyzer',
    '/obj/item/pinpointer/crew': 'HandheldCrewMonitor',
    '/obj/item/assembly/flash/handheld': 'Flash',
    '/obj/item/t_scanner': 'trayScanner',
    '/obj/item/t_scanner/adv_mining_scanner/lesser': 'MineralScanner',
    '/obj/item/mod/control/pre_equipped/engineering': 'ClothingModsuitEngineerPowerCell',
    '/obj/item/mod/control/pre_equipped/security': 'ClothingModsuitSecurityPowerCell',
    '/obj/item/grenade/c4/x4': 'C4',
    '/obj/item/grenade/chem_grenade/cleaner': 'CleanerGrenade',
    '/obj/item/grenade/chem_grenade/smart_metal_foam': 'MetalFoamGrenade',
    '/obj/item/storage/backpack/ert/odst/hecu': 'ClothingBackpackERTSecurity',
    '/obj/item/storage/bag/ore': 'OreBag',
    '/obj/item/stamp/clown': 'RubberStampClown',
    '/obj/item/melee/sabre': 'CaptainSabre',
    '/obj/item/reagent_containers/cup/glass/mug/coco': 'DrinkHotCoco',
}


def ident(path, prefix="BitrunSource"):
    return prefix + "".join(part[:1].upper() + part[1:] for part in re.findall(r"[A-Za-z0-9]+", path))


def paths(value):
    return re.findall(r"/[A-Za-z_][\w/]*", value or "")


class GameplayImport:
    def __init__(self, source, station, verify, native_mapping=None, mob_mapping=None):
        self.source = source
        self.station = station
        self.verify = verify
        self.rows = {}
        self.targets = {}
        self.tags = set()
        self.native_mapping = native_mapping or (lambda path: None)
        self.storage_trees = {}
        self.unresolved = {}
        self.gear = {}
        self.gear_variants = {}
        self.mob_mapping = mob_mapping or (lambda path: None)
        self.mob_pools = {}
        self.domain_gear = {}
        self.native_art = MapArt.__new__(MapArt)
        self.native_art.native = MapArt.index_native(station)

    def tag(self, name):
        self.tags.add(name)
        return name

    @staticmethod
    def container_slot():
        return Tagged('ContainerSlot')

    @staticmethod
    def tagged(kind, **values):
        return Tagged(kind, **values)

    def entity(self, path, parent, components):
        self.unresolved.pop(path, None)
        if any(component['type'] in ('Projectile', 'MeleeWeapon') for component in components):
            components.append({'type': 'DamageArmorOverride', 'sourceArmorPenetration': True})
        # Preserve source dish ancestry independently of generated names/art.
        ancestry = path
        while ancestry.startswith('/obj/item/'):
            disposable = ancestry.startswith(('/obj/item/broken_bottle', '/obj/item/plate_shard', '/obj/item/shard', '/obj/item/trash/tray'))
            if disposable or ancestry.startswith(('/obj/item/kitchen/fork', '/obj/item/plate', '/obj/item/reagent_containers/cup/bowl', '/obj/item/reagent_containers/cup/glass/drinkingglass')):
                components.append({'type': 'BitrunDish', 'disposable': disposable})
                break
            ancestry = self.source.parent(ancestry) or ''
        fields = self.source.fields(path)
        prototype = ident(path)
        merged = {}
        for component in components:
            merged[component['type']] = {**merged.get(component['type'], {}), **component}
        components = list(merged.values())
        self.rows[prototype] = dict(type="entity", id=prototype, parent=parent,
                                    name=string(fields.get("name"), path.rsplit("/", 1)[-1].replace("_", " ")),
                                    description=string(fields.get("desc")), categories=["HideSpawnMenu"], components=components)
        self.targets[path] = prototype
        return prototype

    def caliber(self, casing):
        fields = self.source.fields(casing)
        return self.tag(ident(fields.get("caliber") or casing, "BitrunCaliber"))

    def projectile(self, path):
        prototype = ident(path)
        if prototype in self.rows:
            return prototype
        fields = self.source.fields(path)
        damage = number(fields.get("damage"), 0)
        stamina_damage = damage if fields.get('damage_type') == 'STAMINA' else number(fields.get('stamina'), 0)
        if fields.get('damage_type') == 'STAMINA':
            damage = 0
        kinetic = path.startswith('/obj/projectile/kinetic')
        damage_type = "Heat" if fields.get("damage_type") == "BURN" else "Blunt" if fields.get("damage_type") == "STAMINA" or kinetic else "Piercing"
        components = [{"type": "Projectile", "damage": {"types": {damage_type: damage}},
                       "armorPenetration": number(fields.get("armour_penetration"), 0) / 100},
                      {"type": "BitrunProjectileRange", "range": number(fields.get("range"), 50),
                       "speed": number(fields.get("speed"), 2.5) * 10,
                       "kinetic": kinetic}]
        if stamina_damage > 0:
            components.append({"type": "StaminaDamageOnCollide", "damage": stamina_damage})
        paralyze = number(fields.get('paralyze'), 0) / 10
        knockdown = 10 if path == '/obj/projectile/bullet/honker' else number(fields.get('knockdown'), 0) / 10
        shrink = number(fields.get('shrink_time'), 0) / 10
        if paralyze or knockdown or shrink:
            components.append({'type': 'BitrunProjectileEffects', 'paralyze': paralyze, 'knockdown': knockdown,
                               'shrink': shrink})
        if 'PASSCLOSEDTURF' in fields.get('pass_flags', ''):
            components.append({'type': 'BitrunProjectilePhase'})
        # A generous cleanup timeout does not determine the source's tile range.
        components.append({"type": "TimedDespawn", "lifetime": 120})
        parent = "BulletKinetic" if kinetic else "BulletLaser" if "/beam/" in path else "BulletRocket" if path.endswith("/rocket") else "BulletPistol"
        return self.entity(path, parent, components)

    def cartridge(self, path):
        prototype = ident(path)
        if prototype in self.rows:
            return prototype
        fields = self.source.fields(path)
        projectiles = paths(fields.get("projectile_type"))
        if not projectiles:
            raise ValueError(f"No projectile for source casing {path}")
        projectile = self.projectile(projectiles[0])
        pellets = int(number(fields.get("pellets"), 1))
        if pellets > 1:
            spread = ident(path, "BitrunPelletSpread")
            self.rows[spread] = {"type": "entity", "id": spread, "parent": projectile, "categories": ["HideSpawnMenu"],
                                 "components": [{"type": "ProjectileSpread", "proto": projectile,
                                                 "count": pellets, "spread": number(fields.get("variance"), 10)}]}
            projectile = spread
        return self.entity(path, "Cartridge9x19mmFMJ", [
            {"type": "Tag", "tags": ["Cartridge", self.caliber(path)]},
            {"type": "CartridgeAmmo", "proto": projectile},
        ])

    def ammo_box(self, path):
        prototype = ident(path)
        if prototype in self.rows:
            return prototype
        fields = self.source.fields(path)
        casings = paths(fields.get("ammo_type"))
        if not casings:
            raise ValueError(f"No source ammunition for {path}")
        casing = casings[0]
        capacity = int(number(fields.get("max_ammo"), 6))
        ancestors = []
        ancestor = path
        while ancestor.startswith('/obj/item/ammo_box/'):
            ancestors.append(self.tag(ident(ancestor, "BitrunMagazine")))
            ancestor = self.source.parent(ancestor)
        components = [
            {"type": "Tag", "tags": ancestors},
            {"type": "BallisticAmmoProvider", "proto": self.cartridge(casing), "capacity": capacity,
             "mayTransfer": True, "whitelist": {"tags": ["Cartridge", self.caliber(casing)], "requireAll": True}},
        ]
        return self.entity(path, "Magazine9x19mmPistolFMJ", components)

    def gun(self, path):
        fields = self.source.fields(path)
        components = []
        delay = number(fields.get("fire_delay"), 0) / 10
        rate = 1 / max(delay, 0.4)  # CLICK_CD_RANGE = 4 deciseconds in WhiteMoon.
        bursts = int(number(fields.get("burst_size"), 1))
        modes = ["SemiAuto"]
        body = "\n".join(b for _, _, b in self.source.initialization(path, "Initialize"))
        automatic = re.search(r"automatic_fire,\s*(?:autofire_shot_delay\s*=\s*)?([\d.]+(?:\s*SECONDS)?|[A-Za-z_]\w*)", body)
        if automatic:
            interval = number(fields.get(automatic[1]), None) if automatic[1] in fields else number(automatic[1])
            if interval is None or interval <= 0:
                raise ValueError(f'Unresolved automatic firing interval for {path}: {automatic[1]}')
            rate = 10 / interval
            modes.append("FullAuto")
        if bursts > 1:
            modes.append("Burst")
        gun = {"type": "Gun", "fireRate": rate, "damageModifier": number(fields.get("projectile_damage_multiplier"), 1),
               "selectedMode": "FullAuto" if automatic else "Burst" if bursts > 1 else "SemiAuto", "availableModes": modes,
               "projectileSpeed": 25, "minAngle": 0, "maxAngle": number(fields.get("spread"), 0),
               "shotsPerBurst": bursts, "burstFireRate": 10 / max(0.1, number(fields.get("burst_delay"), 2))}
        gun['burstCooldown'] = max(delay, 0.4)
        components.append(gun)
        components.extend([{"type": "BitrunSourceGun"}, {"type": "GunWieldBonus", "minAngle": 0, "maxAngle": 0}])
        if fields.get("weapon_weight") == "WEAPON_HEAVY":
            components.extend([{"type": "Wieldable"}, {"type": "GunRequiresWield"}])
        components.append({"type": "MeleeWeapon", "damage": {"types": {"Blunt": number(fields.get("force"), 5)}}})
        if path.startswith("/obj/item/gun/ballistic/"):
            magazine = paths(fields.get("accepted_magazine_type"))[0]
            mf = self.source.fields(magazine)
            casing = paths(mf.get("ammo_type"))[0]
            cartridge = self.cartridge(casing)
            mag_id = self.ammo_box(magazine)
            capacity = int(number(mf.get("max_ammo"), 6))
            if fields.get("internal_magazine") == "TRUE":
                if "/revolver" in path:
                    parent = "WeaponRevolverInspector"
                    components.append({"type": "RevolverAmmoProvider", "proto": cartridge, "capacity": capacity,
                                       "chambers": [True] * capacity, "ammoSlots": [None] * capacity,
                                       "whitelist": {"tags": ["Cartridge", self.caliber(casing)], "requireAll": True}})
                else:
                    parent = "WeaponSniperMosin"
                    components.append({"type": "BallisticAmmoProvider", "proto": cartridge, "capacity": capacity,
                                       "autoCycle": fields.get("semi_auto") != "FALSE",
                                       "whitelist": {"tags": ["Cartridge", self.caliber(casing)], "requireAll": True}})
                    components.append({"type": "BitrunSourceGun", "manualCycle": fields.get("semi_auto") == "FALSE"})
            else:
                parent = "WeaponPistolMk58"
                components.append({"type": "ChamberMagazineAmmoProvider", "boltClosed": True,
                                   "autoCycle": fields.get("semi_auto") != "FALSE",
                                   "canRack": fields.get("semi_auto") != "FALSE"})
                components.append({"type": "BitrunSourceGun", "chamberConsumesMagazine": True})
                components.append({"type": "ItemSlots", "slots": {
                    "gun_magazine": {"startingItem": mag_id, "whitelist": {"tags": [self.tag(ident(magazine, "BitrunMagazine"))]}},
                    "gun_chamber": {"startingItem": cartridge, "whitelist": {"tags": [self.caliber(casing)]}},
                }})
            components.append({"type": "Item", "size": "Huge" if fields.get("weapon_weight") == "WEAPON_HEAVY" else "Normal"})
        elif path.startswith("/obj/item/gun/energy/recharge/kinetic_accelerator"):
            parent = "WeaponProtoKineticAccelerator"
            casing = paths(fields.get("ammo_type"))[0]
            projectile = self.projectile(paths(self.source.fields(casing).get("projectile_type"))[0])
            components.extend([{"type": "BasicEntityAmmoProvider", "proto": projectile, "capacity": 1, "count": 1},
                               {"type": "RechargeBasicEntityAmmo", "rechargeCooldown": number(fields["recharge_time"]) / 10},
                               {"type": "GunWieldBonus", "minAngle": 0, "maxAngle": 0},
                               {"type": "BitrunKineticAccelerator", "capacity": int(number(fields.get("max_mod_capacity"), 100)),
                                "recharge": number(fields["recharge_time"]) / 10},
                               {"type": "UseDelay", "delay": 0}])
        elif path.startswith('/obj/item/gun/magic/'):
            parent = "WeaponProtoKineticAccelerator"
            casing = paths(fields.get("ammo_type"))[0]
            projectile = self.projectile(paths(self.source.fields(casing).get("projectile_type"))[0])
            components.extend([{"type": "BasicEntityAmmoProvider", "proto": projectile,
                                "capacity": int(number(fields.get("max_charges"), 1)), "count": int(number(fields.get("max_charges"), 1))},
                               {"type": "RechargeBasicEntityAmmo", "rechargeCooldown": number(fields.get("recharge_rate"), 8)},
                               {"type": "GunWieldBonus", "minAngle": 0, "maxAngle": 0}, {"type": "UseDelay", "delay": 0}])
        else:
            casing = paths(fields.get("ammo_type"))[0]
            cf = self.source.fields(casing)
            projectile = self.projectile(paths(cf.get("projectile_type"))[0])
            cell = paths(fields.get("cell_type"))
            max_charge = number(self.source.fields(cell[0]).get("maxcharge"), 10000) if cell else 10000
            cost = number(cf.get("e_cost"), 100)
            components.extend([{"type": "Battery", "maxCharge": max_charge, "startingCharge": max_charge},
                               {"type": "ProjectileBatteryAmmoProvider", "proto": projectile, "fireCost": cost}])
            casings = paths(fields.get('ammo_type'))
            if len(casings) > 1:
                fire_modes = []
                for mode in casings:
                    mode_fields = self.source.fields(mode)
                    mode_projectile = paths(mode_fields.get('projectile_type'))[0]
                    fire_modes.append({'proto': self.projectile(mode_projectile),
                                       'fireCost': number(mode_fields.get('e_cost'), 100)})
                components.append({'type': 'BatteryWeaponFireModes', 'fireModes': fire_modes})
            # WeaponLaserCarbine already owns a hitscan provider. Inheriting it
            # would fire both its native hitscan and the source projectile.
            parent = "BaseWeaponBattery"
            components.append({"type": "Sprite", "sprite": "Objects/Weapons/Guns/Battery/xray.rsi", "state": "icon"})
            if (recharge := number(fields.get("recharge_time"), 0)) > 0:
                components.append({"type": "BatterySelfRecharger", "autoRecharge": True, "autoRechargeRate": cost / (recharge / 10)})
        return self.entity(path, parent, components)

    def modkit(self, path):
        fields = self.source.fields(path)
        values = {"type": "BitrunKineticModkit", "cost": int(number(fields.get("cost"), 30))}
        kind = path.rsplit('/', 1)[-1]
        if kind == 'minebot_passthrough':
            values['minebotPassthrough'] = True
        elif kind in ('cooldown', 'damage', 'range'):
            values[kind] = number(fields["modifier"]) / (10 if kind == "cooldown" else 1)
        else:
            self.unresolved[path] = 'The source modkit callback has no adapter: ' + kind
            return None
        return self.entity(path, "BaseItem", [{"type": "Sprite", "sprite": "Objects/Tools/flashlight.rsi", "state": "flashlight"}, values])

    def clothing(self, path):
        fields = self.source.fields(path)
        family = path.split('/')[4]
        family_base = {'glasses': 'Eyes', 'gloves': 'Hands', 'head': 'Head', 'mask': 'Mask',
                       'neck': 'Neck', 'shoes': 'Shoes', 'suit': 'Outer', 'under': 'Uniform'}.get(family)
        if family_base is None:
            raise ValueError(f'Clothing has no equipment slot: {path}')
        parent = self.native_mapping(path)
        if path.startswith('/obj/item/clothing/shoes/magboots'):
            parent = 'ClothingShoesBootsMag'
        elif path.startswith('/obj/item/clothing/glasses/hud/security'):
            parent = 'ClothingEyesHudSecurity'
        if not parent or not parent.startswith('Clothing'):
            parent = f'BitrunImportedClothing{family_base}Base'
        components = []
        if path == '/obj/item/clothing/gloves/fingerless/punch_mitts':
            components.extend([{'type': 'BitrunHunterMitts'}, {'type': 'MeleeWeapon',
                'mustBeEquippedToUse': True, 'attackRate': 1.25, 'damage': {'types': {'Blunt': 5}}}])
        if '/glasses/' in path and '/night' in path:
            components.append({'type': 'NightVision', 'relayOverlay': True})
        if 'SEE_MOBS' in fields.get('vision_flags', ''):
            components.append({'type': 'ThermalVision', 'relayOverlay': True})
        if number(fields.get('flash_protect'), 0) > 0 or fields.get('flash_protect') in ('FLASH_PROTECTION_FLASH', 'FLASH_PROTECTION_WELDER'):
            components.append({'type': 'EyeProtection'})
        if path == '/obj/item/clothing/shoes/clown_shoes/banana_shoes':
            components.extend([{'type': 'BitrunBananaShoes'}, {'type': 'ItemToggle', 'onUse': False},
                               {'type': 'Sprite', 'sprite': '_DeepLagoon/Bitrunning/bitrunner_uniform.rsi',
                                'layers': [{'state': 'bitrunner', 'map': ['enum.ToggleableVisuals.Layer']}]},
                               {'type': 'ToggleClothing', 'action': 'ActionBitrunBananaShoes'},
                               {'type': 'Appearance'}, {'type': 'GenericVisualizer', 'visuals': {
                                   'enum.ToggleableVisuals.Enabled': {'enum.ToggleableVisuals.Layer': {
                                       'True': {'state': 'icon-on'}, 'False': {'state': 'icon'}}}}}])
        armor_path = paths(fields.get('armor_type'))
        armor = self.source.fields(armor_path[0]) if armor_path else {}
        coefficients = {}
        for source, targets in {'melee': ('Blunt', 'Slash'), 'bullet': ('Piercing',),
                                 'laser': ('Heat',), 'energy': ('Shock',), 'bio': ('Poison',),
                                 'rad': ('Radiation',), 'acid': ('Caustic',)}.items():
            for target in targets:
                coefficients[target] = max(0, 1 - number(armor.get(source), 0) / 100)
        components.append({'type': 'Armor', 'modifiers': {'coefficients': coefficients},
            'coverage': body_coverage(fields.get('body_parts_covered')),
            'coveredExplosionCoefficient': max(0, 1 - number(armor.get('bomb'), 0) / 100)})
        components.append({'type': 'ExplosionResistance', 'damageCoefficient': 1})
        components.append({'type': 'Insulated', 'coefficient': number(fields.get('siemens_coefficient'), 1)})
        slowdown = number(fields.get('slowdown'), 0)
        components.append({'type': 'ClothingSpeedModifier', 'walkModifier': 1 / (1 + slowdown), 'sprintModifier': 1 / (1 + slowdown)})
        if 'STOPSPRESSUREDAMAGE' in fields.get('clothing_flags', ''):
            components.append({'type': 'PressureProtection', 'highPressureMultiplier': 0.01, 'lowPressureMultiplier': 1000})
        cold = fields.get('cold_protection') not in (None, '0', 'NONE')
        heat = fields.get('heat_protection') not in (None, '0', 'NONE')
        if cold or heat:
            components.append({'type': 'TemperatureProtection', 'coolingCoefficient': 0.1 if cold else 1,
                               'heatingCoefficient': 0.1 if heat else 1})
        return self.entity(path, parent, components)

    def ensure_item(self, path):
        if path in self.targets:
            return self.targets[path]
        if special := special_item(self, path):
            return special
        if path in self.unresolved:
            return None
        if path.startswith('/obj/item/gun/'):
            return self.gun(path)
        if path.startswith('/obj/item/ammo_box/'):
            return self.ammo_box(path)
        if path.startswith('/obj/item/clothing/') and path.split('/')[4] != 'accessory':
            return self.clothing(path)
        if path.startswith('/obj/item/borg/upgrade/modkit/'):
            return self.modkit(path)
        aliases = [p for p in ALIASES if path == p or path.startswith(p + '/')]
        parent = ALIASES[max(aliases, key=len)] if aliases else self.native_mapping(path)
        if not parent:
            self.unresolved.setdefault(path, 'No functional native equivalent')
            return None
        fields = self.source.fields(path)
        components = []
        sizes = {'WEIGHT_CLASS_TINY': 'Tiny', 'WEIGHT_CLASS_SMALL': 'Small', 'WEIGHT_CLASS_NORMAL': 'Normal',
                 'WEIGHT_CLASS_BULKY': 'Large', 'WEIGHT_CLASS_HUGE': 'Huge', 'WEIGHT_CLASS_GIGANTIC': 'Ginormous'}
        if size := sizes.get(fields.get('w_class')):
            components.append({'type': 'Item', 'size': size})
        force = number(fields.get('force'), 0)
        if force > 0:
            damage_type = 'Heat' if fields.get('damtype') == 'BURN' else 'Slash' if fields.get('sharpness', 'NONE') != 'NONE' else 'Blunt'
            components.append({'type': 'MeleeWeapon', 'damage': {'types': {damage_type: force}},
                               'armorPenetration': number(fields.get('armour_penetration'), 0) / 100})
        if path == '/obj/item/melee/sickly_blade':
            components.append({'type': 'BitrunHereticBlade'})
        native = self.native_art.components(parent)
        if 'ItemToggleMeleeWeapon' in native:
            active = number(fields.get('active_force', fields.get('two_hand_force', fields.get('force_wielded'))), force)
            components.append({'type': 'ItemToggleMeleeWeapon', 'activatedDamage': {'types': {'Slash': active}}})
        if 'Stack' in native and (count := number(fields.get('amount'))) is not None:
            components.append({'type': 'Stack', 'count': int(count)})
        return self.entity(path, parent, components)

    def storage(self, path):
        parent = self.native_mapping(path)
        if not parent:
            return
        if path.startswith('/obj/machinery/suit_storage_unit'):
            fields = self.source.fields(path)
            items = [p for key in ('suit_type', 'helmet_type', 'mask_type', 'storage_type') for p in paths(fields.get(key))]
            tree = all_of([{'kind': 'spawn', 'path': item, 'count': 1} for item in items])
        else:
            tree = all_of([parse_contents(body) for _, _, body in self.source.initialization(path, 'PopulateContents')])
        for item in sorted(source_paths(tree)):
            self.ensure_item(item)
        self.storage_trees[path] = tree
        self.entity(path, parent, [])

    def outfit(self, path):
        gear_id = ident(path, 'BitrunOutfit')
        if gear_id in self.gear:
            return gear_id
        fields = self.source.fields(path)
        equipment = {}
        hands = []
        slots = {'uniform': 'jumpsuit', 'suit': 'outerClothing', 'head': 'head', 'shoes': 'shoes',
                 'mask': 'mask', 'glasses': 'eyes', 'gloves': 'gloves', 'back': 'back', 'belt': 'belt',
                 'neck': 'neck', 'ears': 'ears', 'l_pocket': 'pocket1', 'r_pocket': 'pocket2', 'suit_store': 'suitStorage'}
        for source, slot in slots.items():
            if item := paths(fields.get(source)):
                if target := self.ensure_item(item[0]):
                    equipment[slot] = target
        for hand in ('l_hand', 'r_hand'):
            if item := paths(fields.get(hand)):
                if target := self.ensure_item(item[0]):
                    hands.append(target)
        back = []
        for item, count in re.findall(r'(/[\w/]+)(?:\s*=\s*(\d+))?', fields.get('backpack_contents', '')):
            if target := self.ensure_item(item):
                back.extend([target] * int(count or 1))
        self.gear[gear_id] = {'type': 'startingGear', 'id': gear_id, 'equipment': equipment,
                              **({'inhand': hands} if hands else {}), **({'storage': {'back': back}} if back else {})}
        if weapons := paths(fields.get('ranged_weaps')):
            ammunition = paths(fields.get('corresponding_ammo'))
            if len(weapons) != len(ammunition):
                raise ValueError(f'Outfit weapon/ammo selection is mismatched: {path}')
            variants = []
            for weapon, ammo in zip(weapons, ammunition):
                gun_id, ammo_id = self.ensure_item(weapon), self.ensure_item(ammo)
                for glasses in (False, True):
                    variant = gear_id + 'Weapon' + ident(weapon, '') + ('Sunglasses' if glasses else 'BareEyes')
                    row = deepcopy(self.gear[gear_id])
                    row['id'] = variant
                    row['inhand'] = [gun_id]
                    row['equipment'].update(pocket1=ammo_id, pocket2=ammo_id)
                    if glasses:
                        row['equipment']['eyes'] = self.ensure_item('/obj/item/clothing/glasses/sunglasses')
                    self.gear[variant] = row
                    variants.append(variant)
            self.gear_variants[path] = variants
        return gear_id

    def ghost_role(self, path):
        fields = self.source.fields(path)
        outfit = fields.get('outfit')
        gear = self.outfit(outfit)
        role = path.rsplit('/', 1)[-1]
        body_id = ident(path, 'BitrunRoleBody')
        component = {'type': 'BitrunDomainGuest', 'islander': role == 'islander',
                     'antagonist': fields.get('antag', 'TRUE') != 'FALSE'}
        self.rows[body_id] = {'type': 'entity', 'id': body_id, 'parent': 'MobHuman', 'name': string(fields.get('name')),
                             'categories': ['HideSpawnMenu'], 'components': [component, {'type': 'Loadout', 'prototypes': self.gear_variants.get(outfit, [gear])}]}
        for source, native in (('syndie', 'Syndicate'), ('pirate', 'PirateNF'), ('ancient_milsim', 'Syndicate')):
            if role == source:
                self.rows[body_id]['components'].append({'type': 'NpcFactionMember', 'factions': [native]})
        key = 'bitrun-source-role-' + role.replace('_', '-')
        components = [{'type': 'GhostRole', 'name': key, 'description': key + '-desc', 'rules': key + '-rules',
                       'allowMovement': True, 'allowSpeech': True, 'mindRoles': ['MindRoleGhostRoleSoloAntagonist' if role in ('islander', 'pirate', 'syndie', 'ancient_milsim') else 'MindRoleGhostRoleFreeAgent']},
                      {'type': 'GhostRoleMobSpawner', 'prototype': body_id},
                      {'type': 'Transform', 'anchored': True}]
        return self.entity(path, 'MarkerBase', components)

    def mob(self, path):
        if path in self.targets:
            return self.targets[path]
        if path == '/mob/living/basic/mining_drone':
            gun = self.gun('/obj/item/gun/energy/recharge/kinetic_accelerator/minebot')
            return self.entity(path, 'BitrunMinebot', [{'type': 'BitrunMinebot', 'gunPrototype': gun}])
        fields = self.source.fields(path)
        parent = self.mob_mapping(path)
        if '/megafauna/' in path:
            # Bosses remain separate prototypes. Their source phase AI is not
            # generated by this adapter and must be audited separately.
            if parent:
                self.targets[path] = parent
            return parent
        if not path.startswith('/mob/living/basic/'):
            return parent
        if not parent:
            parent = 'BitrunHuntAnimal' if '/deer' in path else 'MobSyndicateFootsoldier' if '/trooper/' in path or '/revolutionary' in path else 'BaseMobAsteroid'
        if '/hivebot' in path:
            parent = 'BaseMobAsteroid'
        hp = number(fields.get('maxHealth'), 100)
        low = number(fields.get('melee_damage_lower'), 0)
        high = number(fields.get('melee_damage_upper'), low)
        factions = fields.get('faction', '')
        neutral = any(word in path for word in ('/pet/', '/butterfly', '/chicken', '/deer', '/parrot', '/bear/butter', '/crab')) or '/fake_crewman' in path and 'instant_hostile' not in fields.get('ai_controller', '')
        inactive = fields.get('ai_controller') == 'null'
        faction = 'SimpleNeutral' if neutral or inactive else 'Syndicate' if 'ROLE_SYNDICATE' in factions else 'PirateNF' if 'FACTION_PIRATE' in factions else 'Xeno' if 'ROLE_ALIEN' in factions else 'SimpleHostile'
        if 'FACTION_HERETIC' in factions:
            # heretic_hunt.setup_domain installs an active hostile controller;
            # its faction still makes it friendly to the heretic avatar.
            faction = 'BitrunHeretic'
            inactive = False
        damage_type = 'Heat' if fields.get('melee_damage_type') == 'BURN' else 'Slash' if fields.get('sharpness', 'NONE') != 'NONE' else 'Blunt'
        components = [{'type': 'MobThresholds', 'thresholds': {0: 'Alive', hp: 'Dead'}},
                      {'type': 'MeleeWeapon', 'damage': {'types': {damage_type: (low + high) / 2}},
                       'armorPenetration': number(fields.get('armour_penetration'), 0) / 100,
                       'attackRate': 10 / max(1, number(fields.get('melee_attack_cooldown'), 10))},
                      {'type': 'NpcFactionMember', 'factions': [faction]}]
        if fields.get('mob_size') in ('MOB_SIZE_LARGE', 'MOB_SIZE_HUGE'):
            tags = list(self.native_art.components(parent).get('Tag', {}).get('tags', []))
            components.append({'type': 'Tag', 'tags': tags + [self.tag('BitrunCrusherMarkable')]})
        biotypes = fields.get('mob_biotypes', '')
        if 'MOB_HUMANOID' not in biotypes and any(kind in biotypes for kind in
                ('MOB_BEAST', 'MOB_SPECIAL', 'MOB_PLANT', 'MOB_BUG', 'MOB_MINING', 'MOB_CRUSTACEAN', 'MOB_REPTILE')):
            components.append({'type': 'BitrunHunterPrey'})
        if '/trooper/abductor' in path:
            components.append({'type': 'BitrunAbductorActor'})
        if '/deer' in path:
            components.append({'type': 'BitrunHuntTarget'})
        if '/trooper/cin_soldier/' in path:
            components.append({'type': 'BitrunMilsimEnemy'})
        if '/fake_crewman/' in path:
            components.append({'type': 'BitrunRitualOffering', 'value': 3 if '/boss/' in path else 2 if '/sec' in path else 1})
        casing = paths(fields.get('casingtype'))
        direct_projectiles = paths(fields.get('projectiletype')) if '/alien/sentinel' in path or '/alien/queen' in path else []
        if '/hivebot' in path and fields.get('ranged_attacker') == 'TRUE':
            casing = ['/obj/item/ammo_casing/hivebot']
        if casing or direct_projectiles:
            projectile_paths = paths(self.source.fields(casing[0]).get('projectile_type')) if casing else direct_projectiles
            if not projectile_paths:
                raise ValueError(f'No projectile for NPC ranged attack: {path}: {casing}')
            projectile = self.projectile(projectile_paths[0])
            cooldown = number(fields.get('ranged_attack_cooldown', fields.get('ranged_cooldown')), 10) / 10
            burst = int(number(fields.get('burst_shots'), 1))
            components.extend([{'type': 'Gun', 'useKey': False, 'fireRate': 1 / max(0.1, cooldown),
                                'selectedMode': 'Burst' if burst > 1 else 'SemiAuto',
                                'availableModes': ['Burst'] if burst > 1 else ['SemiAuto'],
                                'shotsPerBurst': burst, 'burstFireRate': 5, 'burstCooldown': cooldown,
                                'showExamineText': False, 'soundEmpty': None,
                                'minAngle': 0, 'maxAngle': 0},
                               {'type': 'BasicEntityAmmoProvider', 'proto': projectile, 'capacity': None, 'count': None},
                               {'type': 'BitrunSourceGun'}])
        root = 'SimpleRangedHostileCompound' if casing or direct_projectiles else 'SimpleHostileCompound'
        if '/mimic/' not in path:
            components.append({'type': 'HTN', 'rootTask': {'task': root}, 'enabled': not inactive})
        if neutral:
            components.append({'type': 'NPCRetaliation'})
        spawner = paths(fields.get('mob_spawner', fields.get('death_spawner')))
        if not spawner and '/revolutionary' in path:
            spawner = ['/obj/effect/mob_spawn/corpse/human/revolutionary']
        if spawner:
            outfit = paths(self.source.fields(spawner[0]).get('outfit'))
            if outfit:
                components.append({'type': 'Loadout', 'prototypes': [self.outfit(outfit[0])]})
        if parent == 'BaseMobAsteroid':
            components.append({'type': 'Sprite', 'sprite': 'Mobs/Animals/crab.rsi', 'state': 'crab'})
        return self.entity(path, parent, components)

    def table(self, tree, appearances):
        extra = {}
        if 'weight' in tree: extra['weight'] = tree['weight']
        if 'probability' in tree: extra['prob'] = tree['probability']
        if 'rolls' in tree:
            rolls = tree['rolls']
            extra['rolls'] = Tagged('RangeNumberSelector', range=','.join(map(str, rolls))) if isinstance(rolls, tuple) else Tagged('ConstantNumberSelector', value=rolls)
        if tree['kind'] == 'spawn':
            target = appearances.get(tree['path']) or self.targets.get(tree['path'])
            if not target:
                return Tagged('NoneSelector', **extra)
            return Tagged('EntSelector', id=target, amount=Tagged('ConstantNumberSelector', value=tree['count']), **extra)
        return Tagged('AllSelector' if tree['kind'] == 'all' else 'GroupSelector',
                      children=[self.table(child, appearances) for child in tree['children']], **extra)

    def bind_appearances(self, appearances, art_entities):
        # Starting gear, disks and admin spawning can reference the functional
        # prototype directly. Give it the same working source art as map items,
        # including the state aliases consumed by native visualizers.
        visual_types = {'Sprite', 'Item', 'Clothing', 'GenericVisualizer', 'Appearance', 'Icon',
                        'RandomSprite', 'SpriteMovement', 'SolutionContainerVisuals',
                        'TimerTriggerVisuals', 'StorageFillVisualizer', 'ItemToggleVisuals', 'BitrunAbductorBaton',
                        'SpentAmmoVisuals', 'IconSmooth'}
        for path, appearance in appearances.items():
            if appearance not in art_entities:
                continue
            row = self.rows[self.targets[path]]
            components = {part['type']: part for part in row['components']}
            for part in art_entities[appearance].get('components', []):
                if part['type'] in visual_types:
                    components[part['type']] = {**components.get(part['type'], {}), **deepcopy(part)}
            row['components'] = list(components.values())
        for path, tree in self.storage_trees.items():
            row = self.rows[self.targets[path]]
            row['components'].append({'type': 'EntityTableContainerFill',
                                      'containers': {'entity_storage': self.table(tree, appearances)}})
            # Suppress any unrelated inherited contents from the fallback closet.
            row['components'].append({'type': 'StorageFill', 'contents': []})
        for row in self.gear.values():
            for slot, target in list(row.get('equipment', {}).items()):
                source = next((path for path, proto in self.targets.items() if proto == target), None)
                row['equipment'][slot] = appearances.get(source, target)
            for target_list in [row.get('inhand', []), *row.get('storage', {}).values()]:
                target_list[:] = [appearances.get(next((p for p, proto in self.targets.items() if proto == target), None), target) for target in target_list]

    def write(self, names=None):
        rows = [{"type": "Tag", "id": tag} for tag in sorted(self.tags)] + [self.rows[p] for p in sorted(self.rows)] + [self.gear[p] for p in sorted(self.gear)]
        encoded = "# Generated from pinned WhiteMoon DM by Tools/bitrunning/gameplay_import.py.\n" + yaml.safe_dump(remap_prototypes(rows, names or {}), sort_keys=False, allow_unicode=True)
        output = self.station / OUTPUT
        if self.verify:
            if not output.exists() or output.read_text(encoding="utf-8") != encoded:
                raise ValueError(f"Functional source definitions have changed: {output}")
        else:
            output.write_text(encoded, encoding="utf-8", newline="\n")

    def generate(self, source_paths):
        for path in sorted(source_paths):
            if path == '/obj/machinery/smartfridge/survival_pod/preloaded':
                self.ensure_item(path)
                continue
            if path.startswith(("/obj/item/gun/", "/obj/item/ammo_box/", '/obj/item/borg/upgrade/modkit/', '/obj/item/clothing/',
                                '/obj/item/knife/', '/obj/item/hatchet/', '/obj/item/melee/', '/obj/item/dualsaber/',
                                '/obj/item/grenade/', '/obj/item/flashlight/flare/')):
                self.ensure_item(path)
            elif path.startswith(('/obj/structure/closet/', '/obj/machinery/suit_storage_unit')) and '/bitrunning/encrypted' not in path:
                self.storage(path)
            elif path.startswith('/obj/effect/mob_spawn/ghost_role/'):
                self.ghost_role(path)
            elif path.startswith('/mob/living/basic/'):
                self.mob(path)
        for path in sorted(self.source.types):
            if not path.startswith('/datum/lazy_template/virtual_domain/'):
                continue
            fields = self.source.fields(path)
            key = string(fields.get('map_name'))
            forced = paths(fields.get('forced_outfit'))
            if key and forced:
                self.domain_gear[key] = self.outfit(forced[0])
            modules = paths(fields.get('mob_modules'))
            mobs = [mob for module in modules for mob in paths(self.source.fields(module).get('mobs'))]
            if not mobs:
                continue
            self.mob_pools[key] = [(mob, self.mob(mob)) for mob in mobs]
        # Written once before indexing native components, and again after the
        # generated source appearances have been bound to contents and outfits.
        if not self.verify:
            self.write()
        return self.targets
