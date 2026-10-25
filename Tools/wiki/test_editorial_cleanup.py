import unittest
from collections import defaultdict
import json
from pathlib import Path
import struct
import tempfile
from urllib.parse import unquote

from editorial_cleanup import clean_article, prepare_embeds, entity_cards, refresh_generated, layout_species


class Data:
    def __init__(self):
        self.raw = defaultdict(dict, entity={
            'ToyFigurineChef': {'name': 'фигурка повара'},
            'KitchenMicrowave': {'name': 'микроволновка'},
            'Knife': {'name': 'нож'},
        })

    def name(self, kind, key):
        return self.raw[kind][key]['name']

    def components(self, key):
        return {}

    def humanoid_species_name(self, key):
        return ''

    def illustration_species(self, key):
        return self.raw['species'].get(key, {})

    def get(self, kind, key):
        return self.raw[kind].get(key, {})


class Blocks:
    def __init__(self, data):
        self.data = data

    def entity(self, key):
        return card(key, self.data.name('entity', key), 'Текущее описание')

    def icon(self, key):
        return '<span class="dl-sprite">' + key + '</span>'

    def embed(self, kind, attrs):
        if kind == 'GuideReagentGroupEmbed':
            return '<div class="dl-generated">Текущие рецепты</div>'
        return '<details class="dl-reagent" id="reagent-medicine">Текущий реагент</details>'


def card(identifier, name, description=''):
    return f'<div class="dl-entity" data-entity-id="{identifier}"><span>image</span><div><strong>{name}</strong><p>{description}</p></div></div>'


class EditorialCleanupTests(unittest.TestCase):
    def setUp(self):
        self.data = Data()
        self.path = 'guidebook/in_game/service/chef'

    def test_removes_role_figurines_but_preserves_equipment_and_prose(self):
        prose = 'Авторская инструкция о профессии.\n'
        text = prose + card('ToyFigurineChef', 'фигурка повара') + card('Knife', 'нож')
        result = clean_article(self.path, text, self.data)
        self.assertTrue(result.startswith(prose))
        self.assertNotIn('ToyFigurine', result)
        self.assertIn('Knife', result)
        self.assertEqual(clean_article(self.path, result, self.data), result)

    def test_legacy_cards_and_source_markers_are_removed(self):
        text = '<div class="dl-entity"><div><strong>Фигурка повара</strong></div></div>\n- Игровой объект: фигурка (`ToyFigurineChef`)'
        self.assertNotIn('фигурк', clean_article(self.path, text, self.data).lower())

    def test_catalogue_may_document_actual_toys(self):
        text = card('ToyFigurineChef', 'фигурка повара')
        self.assertEqual(clean_article('guidebook/in_game/toys', text, self.data), text)

    def test_empty_html_equipment_heading_is_removed_after_figurines(self):
        text = '<p>Инструкция</p><h2 id="equipment">Оборудование и предметы</h2><div class="dl-entities">' + card('ToyFigurineChef', 'фигурка повара') + '</div>'
        self.assertEqual(clean_article(self.path, text, self.data), '<p>Инструкция</p>')

    def test_nested_card_is_parsed_and_deduplicated_as_one_unit(self):
        first = card('Knife', 'нож', 'Режет продукты.')
        distinct = card('KitchenMicrowave', 'микроволновка')
        text = first + distinct + first
        self.assertEqual(len(list(entity_cards(text))), 3)
        report = {}
        self.assertEqual(clean_article(self.path, text, self.data, report), first + distinct)
        self.assertEqual(report, {'duplicate_cards': 1})

    def test_only_verbatim_description_is_removed_from_card(self):
        desc = 'Используется для приготовления пищи из нескольких ингредиентов.'
        authored = f'<p>{desc}</p>\n'
        text = authored + card('KitchenMicrowave', 'микроволновка', desc)
        result = clean_article(self.path, text, self.data)
        self.assertTrue(result.startswith(authored))
        self.assertEqual(result.count(desc), 1)
        different = 'Готовит блюда из продуктов с соблюдением рецепта.'
        self.assertIn(different, clean_article(self.path, authored + card('Knife', 'нож', different), self.data))

    def test_existing_legacy_card_prevents_reappending_xml_embed(self):
        text = '<div class="dl-entity"><div><strong>микроволновка</strong></div></div>'
        embeds = [('GuideEntityEmbed', {'Entity': key}) for key in
                  ['KitchenMicrowave', 'ToyFigurineChef', 'Knife', 'Knife']]
        self.assertEqual(prepare_embeds(self.path, text, embeds, self.data),
                         [('GuideEntityEmbed', {'Entity': 'Knife'})])

    def test_refresh_preserves_authored_prose_and_consumes_entities(self):
        prose = '<p>Это пользовательская правка, которую необходимо сохранить.</p>\n'
        text = prose + card('Knife', 'нож', 'Старое описание') * 2
        embeds = [('GuideEntityEmbed', {'Entity': 'Knife'}), ('GuideEntityEmbed', {'Entity': 'KitchenMicrowave'})]
        result, remaining = refresh_generated(self.path, text, embeds, Blocks(self.data))
        self.assertEqual(result, prose + Blocks(self.data).entity('Knife'))
        self.assertEqual(remaining, [('GuideEntityEmbed', {'Entity': 'KitchenMicrowave'})])

    def test_refresh_group_replaces_outer_block_without_rewriting_nested_reagents(self):
        self.data.raw['reagent']['Medicine'] = {'group': 'Medicine'}
        old = '<div class="dl-generated"><p>Лекарства</p><details class="dl-reagent" id="reagent-medicine"><summary>Старые реакции</summary></details></div>'
        embeds = [('GuideReagentGroupEmbed', {'Group': 'Medicine'})]
        result, remaining = refresh_generated(self.path, '<p>Сохранить</p>' + old + old, embeds, Blocks(self.data))
        self.assertEqual(result, '<p>Сохранить</p><div class="dl-generated">Текущие рецепты</div>')
        self.assertEqual(remaining, [])

    def test_refresh_legacy_cooking_and_technology_when_multiple_groups_exist(self):
        self.data.raw['entity']['Apple'] = {'name':'Яблоко'}
        self.data.raw['microwaveMealRecipe']['ApplePie'] = {'result':'Apple','group':'Dessert'}
        self.data.raw['technology']['Physics'] = {'name':'Физика','discipline':'Experimental'}
        cooking = '<div class="dl-cooking"><details class="dl-food"><summary><span>Яблоко</span></summary></details></div>'
        technology = '<div class="dl-table"><table><tr><th>Технология</th></tr><tr><td>Физика</td></tr></table></div>'
        embeds = [('GuideMicrowaveGroupEmbed', {'Group':'Dessert'}), ('GuideMicrowaveGroupEmbed', {'Group':'Breads'}), ('GuideTechDisciplineEmbed', {'Discipline':'Experimental'}), ('GuideTechDisciplineEmbed', {'Discipline':'Industrial'})]
        result, remaining = refresh_generated(self.path, cooking + technology, embeds, Blocks(self.data))
        self.assertNotIn('Физика', result)
        self.assertNotIn('Яблоко', result)
        self.assertEqual(remaining, [embeds[1], embeds[3]])

    def test_refresh_individual_reagent_consumes_source_and_keeps_intro(self):
        self.data.raw['reagent']['Medicine'] = {'group': 'Medicine'}
        old = '<details class="dl-reagent" id="reagent-medicine"><summary>Старые реакции</summary></details>'
        embeds = [('GuideReagentEmbed', {'Reagent': 'Medicine'})]
        result, remaining = refresh_generated(self.path, 'Авторский текст\n' + old, embeds, Blocks(self.data))
        self.assertEqual(result, 'Авторский текст\n' + Blocks(self.data).embed(*embeds[0]))
        self.assertEqual(remaining, [])

    def test_refresh_legacy_english_dummy_name_uses_raw_prototype_name(self):
        self.data.raw['entity']['HarpyDummy'] = {'name': 'Urist McHands'}
        original_name = self.data.name
        self.data.name = lambda kind, key: 'Манекен гарпии' if key == 'HarpyDummy' else original_name(kind, key)
        self.data.humanoid_species_name = lambda key: 'Гарпия' if key == 'HarpyDummy' else ''
        old = card('', 'Urist McHands', 'A dummy Harpy meant for use in character setup.')
        embeds = [('GuideEntityEmbed', {'Entity': 'HarpyDummy'})]
        result, remaining = refresh_generated(self.path, old, embeds, Blocks(self.data))
        self.assertEqual(result, Blocks(self.data).entity('HarpyDummy'))
        self.assertNotIn('Urist', result)
        self.assertEqual(remaining, [])

    def test_species_portrait_moves_up_without_touching_prose_or_food(self):
        self.data.raw['species']['HarpyDummy'] = {'name': 'Гарпия'}
        self.data.humanoid_species_name = lambda key: 'Гарпия' if key == 'HarpyDummy' else ''
        prose = '<p>Авторский текст со ссылками и оформлением.</p>\n'
        food = card('Knife', 'нож', 'Предмет для профессии')
        body = prose + '<h2>Оборудование и предметы</h2><div class="dl-entities">' + card('HarpyDummy', 'Гарпия') + food + '</div>'
        result = layout_species('guidebook/in_game/mobs/dv/harpy', body, self.data, Blocks(self.data))
        self.assertTrue(result.startswith('<figure class="dl-species-portrait"'))
        self.assertIn('<figcaption>Гарпия</figcaption></figure>\n' + prose, result)
        self.assertIn(food, result)
        self.assertEqual(len(list(entity_cards(result))), 1)
        self.assertEqual(layout_species('guidebook/in_game/mobs/dv/harpy', result, self.data, Blocks(self.data)), result)

    def test_species_only_equipment_section_disappears_and_hostiles_are_untouched(self):
        self.data.raw['species']['HarpyDummy'] = {'name': 'Гарпия'}
        self.data.humanoid_species_name = lambda key: 'Гарпия'
        body = '<p>Авторская инструкция</p><h2 id="equipment">Оборудование и предметы</h2><div class="dl-entities">' + card('HarpyDummy', 'Гарпия') + '</div>'
        result = layout_species('guidebook/in_game/mobs/dv/harpy', body, self.data, Blocks(self.data))
        self.assertNotIn('Оборудование и предметы', result)
        self.assertNotIn('dl-entities', result)
        self.assertIn('<p>Авторская инструкция</p>', result)
        self.assertEqual(layout_species('guidebook/in_game/imported/nf/mobs-hostile-expedition/harpy', body, self.data, Blocks(self.data)), body)

    def test_rules_lore_sop_and_law_remain_byte_identical(self):
        text = card('ToyFigurineChef', 'фигурка повара') * 2
        for topic in ['rules', 'lore', 'sop', 'standard-operating-procedures', 'space-law', 'monolith-rule-overview']:
            path = 'guidebook/in_game/' + topic + '/index'
            self.assertEqual(clean_article(path, text, self.data), text)
            embeds = [('GuideEntityEmbed', {'Entity': 'ToyFigurineChef'})] * 2
            self.assertEqual(prepare_embeds(path, text, embeds, self.data), embeds)


class SpriteExportTests(unittest.TestCase):
    def setUp(self):
        from game_data import GameData
        from reference_blocks import Blocks as ActualBlocks
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        directory = self.root / 'Resources/Textures/test.rsi'
        directory.mkdir(parents=True)
        (directory / 'meta.json').write_text(json.dumps({'size': {'x': 32, 'y': 32}}))
        (directory / 'icon.png').write_bytes(b'\x89PNG\r\n\x1a\n' + b'\0' * 8 + struct.pack('>II', 32, 32))
        self.data = GameData(self.root)
        self.blocks = ActualBlocks(self.data, self.root / 'output')

    def tearDown(self):
        self.temp.cleanup()

    def test_unknown_entity_is_audited_without_public_technical_warning(self):
        self.assertEqual(self.blocks.entity('DeletedItem'), '')
        self.assertIn('DeletedItem', self.blocks.missing_entities)

    def test_explicit_fallback_state_texture_prefix_and_sprite_tint(self):
        self.data.raw['entity']['Sample'] = {'id': 'Sample', 'components': [{'type': 'Sprite',
            'sprite': '/Textures/test.rsi', 'state': 'icon', 'color': '#80ff0080',
            'layers': [{'state': 'hidden', 'visible': False}]}]}
        rendered = self.blocks.icon('Sample')
        decoded = unquote(rendered)
        self.assertIn('feColorMatrix', decoded)
        self.assertIn('0.501961 0 0 0 0 0 1', decoded)
        self.assertNotIn('mask-image', decoded)
        self.assertNotIn('background-image:none', decoded)

    def test_action_icon_fallback_and_per_layer_sprite_are_resolved(self):
        self.data.raw['entity']['Action'] = {'id': 'Action', 'components': [{'type': 'InstantAction',
            'icon': {'sprite': '/Textures/test.rsi', 'state': 'icon'}}]}
        self.assertIn('background-image', self.blocks.icon('Action'))
        self.data.raw['entity']['Layer'] = {'id': 'Layer', 'components': [{'type': 'Sprite',
            'layers': [{'sprite': 'test.rsi', 'state': 'icon'}]}]}
        self.assertIn('background-image', self.blocks.icon('Layer'))

    def test_humanoid_static_scale_preserves_species_proportions(self):
        self.data.raw['entity']['Dwarf'] = {'id': 'Dwarf', 'components': [{'type': 'Sprite', 'scale': '1, 0.8'}]}
        self.data.humanoid_sprite_layers = lambda key: [{'sprite': 'test.rsi', 'state': 'icon'}]
        self.data.humanoid_species_name = lambda key: 'Дварф'
        text = self.blocks.icon('Dwarf')
        self.assertIn('transform:scale(1,0.8)', text)
        self.assertIn('aria-label="Дварф"', text)


if __name__ == '__main__':
    unittest.main()
