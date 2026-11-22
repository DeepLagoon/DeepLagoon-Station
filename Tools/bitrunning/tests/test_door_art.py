"""A passive smoothing group must not replace a door's animated RSI."""
from collections import defaultdict
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from map_art import MapArt


class DoorArtTest(unittest.TestCase):
    def test_passive_smoothing_preserves_all_door_states_in_the_effective_atlas(self):
        for directions in (1, 4):
            for custom_states in (False, True):
                with self.subTest(directions=directions, custom_states=custom_states):
                    art = MapArt.__new__(MapArt)
                    records = {state: ('', directions, 1, 0) for state in
                               ('door_closed', 'door_open', 'door_opening', 'door_closing')}
                    art.atlas = lambda _: (None, (32, 32), records)
                    art.rsi = lambda path, variant='': path + ('.' + variant if variant else '') + '.rsi'
                    exports = defaultdict(dict)

                    def add(path, state, alias=None, variant=''):
                        self.assertIn(state, records)
                        alias = state if alias is None else alias
                        sprite = art.rsi(path, variant)
                        exports[sprite][alias] = state
                        return {'sprite': sprite, 'state': alias}

                    art.add = add
                    states = {key + 'SpriteState': key + '_custom' for key in
                              ('closed', 'open', 'opening', 'closing')} if custom_states else {}
                    native = {'Door': states, 'Sprite': {'layers': [
                        {'state': 'closed', 'map': ['enum.DoorVisualLayers.Base']}]}}
                    source = {'smoothing_flags': 'NONE', 'smoothing_groups': 'SMOOTH_GROUP_AIRLOCK',
                              'canSmoothWith': 'null'}
                    components = art.entity_components(source, native, 'airlock', 'door_closed', 'animated')
                    sprite = next(c for c in components if c['type'] == 'Sprite')
                    base = sprite['layers'][0]
                    for key, source_state in zip(('closed', 'open', 'opening', 'closing'), records):
                        alias = states.get(key + 'SpriteState', key)
                        self.assertEqual(exports[base['sprite']].get(alias), source_state,
                                         f'The effective door atlas must contain {alias}')
                    smooth = next(c for c in components if c['type'] == 'IconSmooth')
                    self.assertEqual(smooth['mode'], 'NoSprite')
                    self.assertEqual(smooth['groups'], ['SMOOTH_GROUP_AIRLOCK'])


if __name__ == '__main__':
    unittest.main()
