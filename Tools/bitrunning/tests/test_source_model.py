from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from source_model import SourceModel, number, body_coverage
from storage_import import parse_contents, UnsupportedContents
from gameplay_import import GameplayImport


class SourceReaderTest(unittest.TestCase):
    def test_clothing_coverage_does_not_turn_gloves_into_full_body_armour(self):
        self.assertEqual(set(body_coverage('HANDS|ARMS').split(', ')), {'LeftHand', 'RightHand', 'LeftArm', 'RightArm'})
        self.assertEqual(body_coverage('0'), '0')
        self.assertEqual(set(body_coverage('CHEST|GROIN').split(', ')), {'Torso', 'Groin'})
    def test_failed_special_storage_cannot_fall_back_to_an_empty_backpack(self):
        model = SourceModel.__new__(SourceModel)
        model.types, model.procs = {}, {}
        path = '/obj/item/storage/backpack/duffelbag/mining_conscript'
        model.read('fixture.dm', path + '/PopulateContents()\n\tnew /obj/item/unsupported_fixture(src)\n')
        importer = GameplayImport.__new__(GameplayImport)
        importer.source, importer.rows, importer.targets, importer.tags, importer.unresolved = model, {}, {}, set(), {}
        importer.native_mapping = lambda path: None
        self.assertIsNone(importer.ensure_item(path))
        self.assertNotIn(path, importer.targets)
        self.assertIn('contents lack adapters', importer.unresolved[path])

    def test_kinetic_subtypes_keep_actual_mining_and_source_range(self):
        model = SourceModel.__new__(SourceModel)
        model.types, model.procs = {}, {}
        model.read('fixture.dm', '/obj/projectile/kinetic\n\tdamage = 40\n\trange = 3\n\tspeed = 2.5\n'
                   '/obj/projectile/kinetic/miner\n\tdamage = 20\n\trange = 4\n\tspeed = 1.1\n')
        importer = GameplayImport.__new__(GameplayImport)
        importer.source, importer.rows, importer.targets, importer.tags = model, {}, {}, set()
        importer.unresolved = {}
        prototype = importer.projectile('/obj/projectile/kinetic/miner')
        row = importer.rows[prototype]
        self.assertEqual(row['parent'], 'BulletKinetic')
        components = {comp['type']: comp for comp in row['components']}
        self.assertEqual(components['Projectile']['damage']['types'], {'Blunt': 20})
        self.assertEqual(components['BitrunProjectileRange'], {'type': 'BitrunProjectileRange', 'range': 4, 'speed': 11, 'kinetic': True})

    def test_comments_do_not_end_a_type_definition(self):
        model = SourceModel.__new__(SourceModel)
        model.types, model.procs = {}, {}
        model.read('fixture.dm', '/obj/item/gun/example\n\tname = "Example"\n// old balance\n\tburst_size = 1\n\tfire_delay = 0.3 SECONDS//comment\n')
        self.assertEqual(model.fields('/obj/item/gun/example')['burst_size'], '1')
        self.assertEqual(number(model.fields('/obj/item/gun/example')['fire_delay']), 3)

    def test_charge_macro_uses_shots_per_original_cell(self):
        self.assertEqual(number('LASER_SHOTS(16, STANDARD_CELL_CHARGE)'), 625)
        self.assertEqual(number('LASER_SHOTS(1, STANDARD_CELL_CHARGE * 0.5)'), 5000)

    def test_root_comment_inside_initializer_does_not_drop_later_equipment(self):
        model = SourceModel.__new__(SourceModel)
        model.types, model.procs = {}, {}
        model.read('fixture.dm', '/obj/locker/PopulateContents()\n\t..()\n// Original equipment\n\tnew /obj/item/suit(src)\n#define LOCAL_SETTING 1\n')
        body = model.initialization('/obj/locker', 'PopulateContents')[0][2]
        self.assertIn('new /obj/item/suit(src)', body)
        self.assertNotIn('#define', body)

    def test_spawn_loop_preserves_count(self):
        tree = parse_contents('/obj/closet/PopulateContents()\n\t..()\n\tfor(var/i in 1 to 3)\n\t\tnew /obj/item/suit(src)\n')
        self.assertEqual(tree['children'][0]['rolls'], 3)
        self.assertEqual(tree['children'][0]['children'][0]['path'], '/obj/item/suit')

    def test_conditional_with_else_is_one_choice(self):
        tree = parse_contents('/obj/closet/PopulateContents()\n\tif(prob(40))\n\t\tnew /obj/item/red(src)\n\telse\n\t\tnew /obj/item/blue(src)\n')
        group = tree['children'][0]
        self.assertEqual(group['kind'], 'group')
        self.assertEqual([child['weight'] for child in group['children']], [40, 60])

    def test_unsupported_conditions_cannot_silently_fill_both_branches(self):
        with self.assertRaises(UnsupportedContents):
            parse_contents('/obj/closet/PopulateContents()\n\tif(something_runtime)\n\t\tnew /obj/item/red(src)\n')
