from pathlib import Path
import tempfile
import unittest

from game_data import GameData
from industrial_reference import build_industrial_reference, ECONOMY, MEDICAL


class Blocks:
    def icon(self, key):
        return '<span class="dl-sprite">' + key + '</span>'

    def reagent_link(self, key):
        return '<a href="/reagents#' + key + '">' + key + '</a>'


class IndustrialReferenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.data = GameData(Path(self.temp.name))
        self.data.raw['entity']['BaseMachine'] = {'id': 'BaseMachine', 'components': [
            {'type': 'Lathe', 'staticPacks': ['Standard', 'Extras'], 'dynamicPacks': ['Research']}]}
        self.data.raw['entity']['MedicalAssembler'] = {'id': 'MedicalAssembler', 'name': 'Медицинский сборщик', 'parent': 'BaseMachine'}
        self.data.raw['latheRecipePack'].update({
            'Standard': {'id': 'Standard', 'recipes': ['Basic', 'Missing']},
            'Extras': {'id': 'Extras', 'recipes': ['Basic']},
            'Research': {'id': 'Research', 'recipes': ['Basic', 'Advanced']}})
        self.data.raw['latheRecipe'].update({
            'Basic': {'id': 'Basic', 'result': 'Product', 'resultCount': 2, 'completetime': 15,
                      'materials': {'Steel': 100}, 'entities': {'Blank': 1}, 'reagents': {'Medicine': 12}},
            'Advanced': {'id': 'Advanced', 'result': 'AdvancedProduct', 'completetime': 8}})
        self.data.raw['entity'].update({'Product': {'name': 'Продукт'}, 'AdvancedProduct': {'name': 'Улучшенный продукт'}, 'Blank': {'name': 'Заготовка'}})
        self.data.raw['material']['Steel'] = {'name': 'Сталь'}
        self.data.raw['technology']['AdvancedTech'] = {'name': 'Продвинутое производство', 'recipeUnlocks': ['Advanced']}

    def tearDown(self):
        self.temp.cleanup()

    def build(self, path=MEDICAL):
        return build_industrial_reference(path, self.data, Blocks())

    def test_reads_inherited_machine_packs_and_deduplicates_recipes(self):
        rendered = self.build()
        self.assertEqual(rendered.count('data-lathe-recipe-id="Basic"'), 1)
        self.assertEqual(rendered.count('data-lathe-recipe-id="Advanced"'), 1)
        self.assertNotIn('Missing', rendered)
        self.assertIn('Медицинский сборщик', rendered)

    def test_static_recipes_are_separate_from_researched_unlocks(self):
        rendered = self.build()
        basic, advanced = rendered.split('<h2>После исследований</h2>')
        self.assertIn('data-lathe-recipe-id="Basic"', basic)
        self.assertNotIn('data-lathe-recipe-id="Advanced"', basic)
        self.assertIn('data-lathe-recipe-id="Advanced"', advanced)
        self.assertIn('Исследование: Продвинутое производство', advanced)

    def test_all_input_kinds_output_count_and_explicit_base_time_survive(self):
        rendered = self.build()
        self.assertIn('Продукт</strong> × 2', rendered)
        self.assertIn('Заготовка × 1', rendered)
        self.assertIn('Сталь: 100 ед. материала', rendered)
        self.assertIn('<a href="/reagents#Medicine">Medicine</a>: 12 ед.', rendered)
        self.assertIn('<td>15 с</td>', rendered)

    def test_no_completion_time_is_invented_and_nonentity_outputs_are_preserved(self):
        self.data.raw['latheRecipe']['Basic'] = {'id': 'Basic', 'materialResult': {'Steel': 50}, 'resultReagents': {'Medicine': 5}}
        rendered = self.build()
        self.assertIn('Сталь: 50 ед. материала', rendered)
        self.assertIn('<a href="/reagents#Medicine">Medicine</a>: 5 ед.', rendered)
        self.assertIn('<td>—</td>', rendered)
        self.assertNotIn('<td>5 с</td>', rendered)

    def test_deterministic_despawn_product_uses_actual_inclusive_count(self):
        self.data.raw['entity']['Product'] = {'name': 'Набор пластин', 'components': [
            {'type': 'TimedDespawn', 'lifetime': 0.1}, {'type': 'SpawnOnDespawn', 'count': 7, 'prototype': 'Slice'}]}
        self.data.raw['entity']['Slice'] = {'name': 'Кремниевая пластина'}
        self.data.raw['latheRecipe']['Basic']['resultCount'] = 1
        rendered = self.build()
        self.assertIn('Кремниевая пластина</strong> × 8', rendered)
        self.assertNotIn('Набор пластин', rendered)

    def test_unknown_routes_do_not_replace_unrelated_articles(self):
        self.assertIsNone(self.build('guidebook/in_game/rules'))
        self.assertIsNone(self.build(ECONOMY + 'unknown'))
        self.assertIsNone(self.build(ECONOMY + 'arc-furnace'))

    def test_economy_index_links_to_both_machine_catalogues(self):
        rendered = self.build(ECONOMY + 'economy-recipes')
        self.assertIn('/ru/' + ECONOMY + 'arc-furnace', rendered)
        self.assertIn('/ru/' + ECONOMY + 'precision-assembler', rendered)
        self.assertNotIn('GuideMicrowave', rendered)


if __name__ == '__main__':
    unittest.main()
