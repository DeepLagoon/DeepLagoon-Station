from pathlib import Path
import tempfile
import unittest

from game_data import GameData
from weapon_reference import build_weapon_reference, PREFIX, _flight


class Blocks:
    def icon(self, key):
        return '<span class="dl-sprite" aria-label="' + key + '"></span>'


class WeaponReferenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.data = GameData(Path(self.temp.name))

    def tearDown(self):
        self.temp.cleanup()

    def entity(self, key, components, name=None, parent=None):
        self.data.raw['entity'][key] = {'id': key, 'name': name or key,
            'components': [dict(type=kind, **attrs) for kind, attrs in components.items()]}
        if parent:
            self.data.raw['entity'][key]['parent'] = parent

    def build(self, kind):
        return build_weapon_reference(PREFIX + kind, self.data, Blocks())

    def test_explicit_routes_only(self):
        self.assertIsNone(build_weapon_reference('guidebook/in_game/rules', self.data, Blocks()))
        self.assertIsNone(build_weapon_reference(PREFIX + 'unknown', self.data, Blocks()))

    def test_inherited_magazine_infinite_flag_wins_over_capacity(self):
        self.entity('BaseWeapon', {'Gun': {'fireRate': 10, 'projectileSpeed': 192},
            'ItemSlots': {'slots': {'gun_magazine': {'startingItem': 'Magazine'}}},
            'HardpointAnchorableOnly': {'class': 'Ballistic', 'size': 'Light'}})
        self.entity('WeaponTurretL85Autocannon', {}, name='L85 14 мм', parent='BaseWeapon')
        self.entity('Magazine', {'BallisticAmmoProvider': {'capacity': 1, 'infiniteUnspawned': True, 'proto': 'Cartridge'}})
        self.entity('Cartridge', {'CartridgeAmmo': {'proto': 'Bullet'}})
        self.entity('Bullet', {'TimedDespawn': {'lifetime': 2}})
        rendered = self.build('ballistics')
        self.assertIn('L85 14 мм', rendered)
        self.assertIn('10 выстр./с', rendered)
        self.assertIn('Без ограничения боезапаса', rendered)
        self.assertNotIn('Ёмкость: 1', rendered)
        self.assertIn('Пуск: 192 м/с', rendered)
        self.assertIn('Время существования: 2 с', rendered)
        self.assertNotIn('384 м', rendered)
        self.assertNotIn('2025', rendered)

    def test_burst_displays_shots_rate_and_cooldown_instead_of_misleading_continuous_rate(self):
        self.entity('WeaponTurretVespera', {'Gun': {'selectedMode': 'Burst', 'fireRate': 99,
            'shotsPerBurst': 13, 'burstFireRate': 16, 'burstCooldown': 20},
            'BallisticAmmoProvider': {'capacity': 78, 'proto': 'Rocket'},
            'HardpointAnchorableOnly': {'class': 'Missile', 'size': 'Medium'}})
        self.entity('Rocket', {'TargetSeeking': {'maxSpeed': 300}, 'TimedDespawn': {'lifetime': 5}})
        rendered = self.build('missiles')
        for label in ['Серия: 13 выстр.', '16 выстр./с внутри серии', 'Пауза: 20 с', 'Ёмкость: 78', 'До 300 м/с с наведением']:
            self.assertIn(label, rendered)
        self.assertNotIn('99 выстр./с', rendered)
        self.assertNotIn('1500 м', rendered)

    def test_hitscan_uses_explicit_raycast_range_without_projectile_speed(self):
        self.entity('WeaponLaserTurretL1Phalanx', {'Gun': {'fireRate': 20, 'projectileSpeed': 25},
            'Battery': {'maxCharge': 100}, 'HitscanBatteryAmmoProvider': {'proto': 'Ray', 'fireCost': 3}})
        self.entity('Ray', {'HitscanBasicRaycast': {'maxDistance': 512}})
        rendered = self.build('energy')
        self.assertIn('Дальность: 512 м', rendered)
        self.assertIn('Полный заряд: 33 выстр.', rendered)
        self.assertNotIn('Пуск: 25', rendered)

    def test_manual_loading_without_default_projectile_does_not_invent_flight_values(self):
        self.entity('WeaponTurretASM220', {'Gun': {'fireRate': 0.1}, 'BallisticAmmoProvider': {'capacity': 4}})
        rendered = self.build('missiles')
        self.assertIn('Снаряд выбирается при загрузке', rendered)
        self.assertIn('Зависит от заряженного снаряда', rendered)
        self.assertNotIn('1747', rendered)

    def test_mounting_class_takes_priority_over_console_classification(self):
        self.entity('WeaponTurretTarnyxReload', {'Gun': {'fireRate': 0.066},
            'HardpointAnchorableOnly': {'class': 'Ballistic', 'size': 'Medium'},
            'ShipGunClass': {'shipClass': 'Heavy'}, 'ShipGunType': {'shipType': 'Energy'}})
        rendered = self.build('ballistics')
        self.assertIn('Среднее<br>Баллистическое', rendered)
        self.assertNotIn('Тяжёлое', rendered)
        self.assertIn('0,066 выстр./с', rendered)

    def test_cartridge_cycles_do_not_recurse_forever(self):
        self.entity('A', {'CartridgeAmmo': {'proto': 'B'}})
        self.entity('B', {'CartridgeAmmo': {'proto': 'A'}})
        self.assertEqual(_flight(self.data, {}, 'A'), 'Зависит от заряженного снаряда')


if __name__ == '__main__':
    unittest.main()
