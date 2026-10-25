"""Species illustration regression tests, independent of the Station asset checkout."""
from pathlib import Path
import tempfile
import unittest

from game_data import GameData


class SpeciesIllustrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.data = GameData(Path(self.temp.name))
        self.data.raw['species']['Avian'] = {
            'id': 'Avian', 'name': 'avian-name', 'dollPrototype': 'BirdDummy',
            'sprites': 'BirdSprites', 'markingLimits': 'BirdPoints',
            'skinColoration': 'Hues', 'defaultSkinTone': '#abcdef'}
        self.data.localization['avian-name'] = 'Птица'
        self.data.raw['entity']['BirdDummy'] = {'components': [
            {'type': 'HumanoidAppearance', 'species': 'Human'},
            {'type': 'Icon', 'sprite': 'Human/parts.rsi', 'state': 'full'},
            {'type': 'Sprite', 'layers': [
                {'map': ['enum.HumanoidVisualLayers.Chest']},
                {'map': ['enum.HumanoidVisualLayers.HeadTop']}]}]}
        self.data.raw['speciesBaseSprites']['BirdSprites'] = {
            'sprites': {'Chest': 'BirdChest', 'HeadTop': 'AnyMarking'}}
        self.data.raw['humanoidBaseSprite'].update({
            'BirdChestMale': {'baseSprite': {'sprite': 'Bird/parts.rsi', 'state': 'torso_m'}},
            'AnyMarking': {}})
        self.data.raw['markingPoints']['BirdPoints'] = {
            'points': {'HeadTop': {'points': 1, 'defaultMarkings': ['Crest']}}}
        self.data.raw['marking']['Crest'] = {
            'bodyPart': 'HeadTop', 'sprites': [{'sprite': 'Bird/crest.rsi', 'state': 'crest'}],
            'coloring': {'default': {'type': {'effectType': 'SimpleColoring', 'color': '#123456'}}}}

    def tearDown(self):
        self.temp.cleanup()

    def test_doll_species_wins_over_inherited_human_component_and_icon(self):
        layers = self.data.humanoid_sprite_layers('BirdDummy')
        self.assertEqual([layer['sprite'] for layer in layers], ['Bird/parts.rsi', 'Bird/crest.rsi'])
        self.assertEqual(self.data.humanoid_species_name('BirdDummy'), 'Птица')

    def test_default_marking_order_and_coloring_survive(self):
        layers = self.data.humanoid_sprite_layers('BirdDummy')
        self.assertEqual(layers[0]['state'], 'torso_m')
        self.assertEqual(layers[0]['color'], '#abcdefff')
        self.assertEqual(layers[1]['color'], '#123456')

    def test_zero_point_optional_marking_is_not_added(self):
        self.data.raw['markingPoints']['BirdPoints']['points']['HeadTop']['points'] = 0
        self.assertEqual(len(self.data.humanoid_sprite_layers('BirdDummy')), 1)

    def test_unknown_doll_does_not_fall_back_to_human_icon(self):
        self.data.raw['entity']['UnknownDummy'] = self.data.raw['entity']['BirdDummy']
        self.assertEqual(self.data.humanoid_sprite_layers('UnknownDummy'), [])
        self.data.raw['species']['Avian']['sprites'] = 'MissingSprites'
        self.data.cache.clear()
        self.assertEqual(self.data.humanoid_sprite_layers('BirdDummy'), [])

    def test_regular_entity_keeps_ordinary_icon_path(self):
        self.data.raw['entity']['Crate'] = {'components': [{'type': 'Sprite', 'state': 'icon'}]}
        self.assertIsNone(self.data.humanoid_sprite_layers('Crate'))

    def test_canonical_species_entity_uses_its_body_but_equipped_npc_does_not(self):
        self.data.raw['species']['Avian']['prototype'] = 'Bird'
        self.data.raw['entity']['Bird'] = self.data.raw['entity']['BirdDummy']
        self.data.raw['entity']['BirdSoldier'] = self.data.raw['entity']['BirdDummy']
        self.assertEqual(len(self.data.humanoid_sprite_layers('Bird')), 2)
        self.assertIsNone(self.data.humanoid_sprite_layers('BirdSoldier'))

    def test_category_coloring_uses_declared_fallback_when_category_absent(self):
        coloring = {'type': {'effectType': 'CategoryColoring', 'category': 'Hair'},
                    'fallbackTypes': [{'effectType': 'SimpleColoring', 'color': '#964b00'}]}
        self.assertEqual(self.data._marking_color(coloring, '#ffffff', '#000000', {}), '#964b00')


if __name__ == '__main__':
    unittest.main()
