"""Source smoothing must remain dynamic, including corners and asymmetric groups."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from map_art import MapArt
from smoothing_import import groups, junctions


class SmoothingTest(unittest.TestCase):
    def art(self, diagonal=False):
        art = MapArt.__new__(MapArt)
        records = {'wall-' + str(mask): None for mask in junctions()}
        if diagonal:
            records.update({'wall-' + str(mask) + '-diagonal': None for mask in (5, 6, 9, 10, 21, 38, 74, 137)})
        art.atlas = lambda _: (None, (32, 32), records)
        art.rsi = lambda path: path + '.rsi'
        art.exported = {}

        def add(path, state, alias=None):
            self.assertIn(state, records)
            art.exported[alias or state] = state
            return {'sprite': art.rsi(path), 'state': alias or state}
        art.add = add
        return art

    def source(self, **values):
        return {'base_icon_state': '"wall"', 'smoothing_flags': 'SMOOTH_BITMASK',
                'smoothing_groups': 'SMOOTH_GROUP_WALLS + SMOOTH_GROUP_CLOSED_TURFS',
                'canSmoothWith': 'SMOOTH_GROUP_WALLS', **values}

    def test_source_has_47_junctions_with_no_unsupported_corner_masks(self):
        self.assertEqual(len(junctions()), 47)
        self.assertEqual(junctions(True), list(range(16)))
        self.assertIn(255, junctions())
        self.assertNotIn(16, junctions())
        self.assertNotIn(17, junctions())

    def test_exports_every_reachable_state_and_keeps_asymmetric_groups(self):
        art = self.art()
        style, component = art.smoothing(self.source(), {}, 'wall', 'wall-255')
        self.assertEqual(style['state'], 'wall-0')
        self.assertEqual(component['mode'], 'Bitmask8')
        self.assertEqual(component['groups'], ['SMOOTH_GROUP_CLOSED_TURFS', 'SMOOTH_GROUP_WALLS'])
        self.assertEqual(component['canSmoothWith'], ['SMOOTH_GROUP_WALLS'])
        self.assertEqual(set(art.exported), {'wall-' + str(mask) for mask in junctions()})

    def test_diagonal_walls_use_cut_corner_art_without_changing_normal_walls(self):
        art = self.art(True)
        _, component = art.smoothing(self.source(smoothing_flags='SMOOTH_BITMASK | SMOOTH_DIAGONAL_CORNERS'), {}, 'wall', 'wall-0')
        self.assertEqual(component['base'], 'wall-diagonal-')
        self.assertEqual(art.exported['wall-diagonal-21'], 'wall-21-diagonal')
        self.assertEqual(art.exported['wall-diagonal-255'], 'wall-255')

    def test_incomplete_smoothing_cannot_silently_become_static(self):
        art = self.art()
        art.atlas = lambda _: (None, (32, 32), {'wall-0': None})
        with self.assertRaisesRegex(ValueError, 'Incomplete source smoothing atlas'):
            art.smoothing(self.source(), {}, 'wall', 'wall-0')

    def test_membership_does_not_force_static_barricades_to_change_sprite(self):
        art = self.art()
        _, component = art.smoothing(self.source(smoothing_flags='NONE',
                                                smoothing_groups='SMOOTH_GROUP_SECURITY_BARRICADE', canSmoothWith='null'), {}, 'wall', 'wall-0')
        self.assertEqual(component['mode'], 'NoSprite')
        self.assertNotIn('enabled', component)
        self.assertEqual(component['canSmoothWith'], [])

    def test_group_parser_deduplicates_without_matching_partial_identifiers(self):
        self.assertEqual(groups('SMOOTH_GROUP_WALLS + SMOOTH_GROUP_WALLS'), ['SMOOTH_GROUP_WALLS'])
        self.assertEqual(groups('MY_SMOOTH_GROUP_WALLS'), [])

    def test_native_random_variant_cannot_replace_the_smoothed_base_layer(self):
        native = {'GenericVisualizer': {'visuals': {'variant': {
            'base': {'1': {'state': 'native_1'}, '2': {'state': 'native_2', 'color': '#FF0000'}},
            'glow': {'1': {'state': 'glow'}},
        }}}}
        visual = MapArt.smoothing_visuals(native, ['base'], 0)
        self.assertEqual(set(visual['variant']), {'base'})
        self.assertIsNone(visual['variant']['base']['1']['state'])
        self.assertIsNone(visual['variant']['base']['1']['sprite'])
        self.assertEqual(visual['variant']['base']['2']['color'], '#FF0000')


if __name__ == '__main__':
    unittest.main()
