import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

from game_data import GameData, Loader
from reference_blocks import Blocks
import wiki_api
import yaml


class ReferenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.data = GameData(self.root)

    def tearDown(self):
        self.temp.cleanup()

    def blocks(self):
        return Blocks(self.data, self.root / 'out')

    def test_inherited_reagent_group_excludes_abstract_template(self):
        self.data.raw['reagent'].update({'BaseDrink':{'id':'BaseDrink','abstract':True,'group':'Drinks'},
            'Sample':{'id':'Sample','parent':'BaseDrink','name':'test-name'}})
        self.data.localization['test-name']='Напиток'
        rendered=self.blocks().reagent_group('Drinks')
        self.assertIn('1 реагентов', rendered)
        self.assertIn('Напиток', rendered)
        self.assertNotIn('reagent-base-drink', rendered)

    def test_catalyst_mixing_temperature_and_whole_batches_survive_export(self):
        self.data.raw['reagent'].update({key:{'id':key,'name':key} for key in ['A','B','C']})
        self.data.raw['mixingCategory']['Shake']={'id':'Shake','verbText':'mix-shake'}
        self.data.localization['mix-shake']='встряхнуть'
        recipe={'id':'Sample','reactants':{'A':{'amount':2},'B':{'amount':5,'catalyst':True}},
                'products':{'C':3},'minTemp':400,'maxTemp':500,'requiredMixerCategories':['Shake'],'quantized':True}
        text=self.blocks().reaction(recipe)
        self.assertIn('Катализатор — не расходуется',text)
        self.assertIn('400 K (126.85 °C)',text)
        self.assertIn('500 K (226.85 °C)',text)
        self.assertIn('Смешивание: встряхнуть',text)
        self.assertIn('Только целые порции',text)
        self.assertIn('data-reaction-id="Sample"',text)
        self.assertNotIn('B</a> — 5 ед.</div><b',text)

    def test_item_spawning_reaction_does_not_disappear_without_liquid_product(self):
        self.data.raw['entity']['Soap']={'id':'Soap','name':'Мыло'}
        text=self.blocks().reaction({'id':'SoapReaction','reactants':{},'effects':[{'effectType':'CreateEntityReactionEffect','entity':'Soap'}]})
        self.assertIn('Мыло (предмет)',text)

    def test_grinding_and_juice_read_different_source_fields(self):
        self.data.raw['reagent']['Water']={'id':'Water','name':'Вода'}
        self.data.raw['entity']['Fruit']={'id':'Fruit','name':'Плод','components':[
            {'type':'Extractable','grindableSolutionName':'food','juiceSolution':{'reagents':[{'ReagentId':'Water','Quantity':7}]}},
            {'type':'SolutionContainerManager','solutions':{'food':{'reagents':[{'ReagentId':'Water','Quantity':3}]}}}]}
        blocks=self.blocks()
        self.assertEqual(blocks.grind['Water'],[('Fruit',3)])
        self.assertEqual(blocks.juice['Water'],[('Fruit',7)])

    def test_cooking_preserves_machine_time_count_and_hides_hidden_recipe(self):
        self.data.raw['entity']['Bun']={'id':'Bun','name':'Булочка'}
        self.data.raw['microwaveMealRecipe'].update({'Visible':{'id':'Visible','group':'Breads','result':'Bun','resultCount':3,'recipeType':['Oven'],'time':7},
            'Hidden':{'id':'Hidden','group':'Breads','result':'Bun','hideInGuidebook':True}})
        text=self.blocks().cooking('Breads')
        self.assertEqual(text.count('class="dl-food"'),1)
        self.assertIn('духовка',text)
        self.assertIn('7 с.',text)
        self.assertIn('Булочка × 3',text)

    def test_yaml_effect_type_and_component_inheritance_are_preserved(self):
        value=yaml.load('effects:\n- !type:CreateEntityReactionEffect\n  entity: Soap\n', Loader=Loader)
        self.assertEqual(value['effects'][0]['effectType'],'CreateEntityReactionEffect')
        self.data.raw['entity'].update({'Base':{'id':'Base','components':[{'type':'Sprite','sprite':'x.rsi','state':'old'},{'type':'GuideHelp','guides':['A']}]},
            'Child':{'id':'Child','parent':'Base','components':[{'type':'Sprite','state':'new'}]}})
        components=self.data.components('Child')
        self.assertEqual(components['Sprite']['sprite'],'x.rsi')
        self.assertEqual(components['Sprite']['state'],'new')
        self.assertEqual(components['GuideHelp']['guides'],['A'])

    def test_concurrent_wiki_edit_is_not_overwritten(self):
        page={'path':'sample','title':'Sample','description':'new','content':'new','scriptCss':'',
              'tags':[],'id':1,'beforeUpdatedAt':'before'}
        (self.root/'pages.json').write_text(json.dumps([page]),encoding='utf-8')
        current={'id':1,'path':'sample','title':'Sample','description':'edited','content':'human edit','scriptCss':'','tags':[],'updatedAt':'after'}
        with patch('wiki_api.graphql',side_effect=[{'pages':{'list':[{'id':1,'path':'sample','title':'Sample','locale':'ru'}]}},{'pages':{'single':current}}]) as request:
            with self.assertRaises(RuntimeError):wiki_api.apply_pages(self.root)
            self.assertEqual(request.call_count,2)
            self.assertTrue(all(not call.args[0].startswith('mutation') for call in request.call_args_list))
        self.assertIn('refusing overwrite', (self.root/'publication-failures.json').read_text(encoding='utf-8'))

    def test_missing_recipe_does_not_generate_absence_notice(self):
        self.data.raw['reagent']['Water'] = {'id':'Water','name':'Вода'}
        text = self.blocks().reagent('Water')
        self.assertNotIn('Прямого рецепта', text)
        self.assertEqual(self.blocks().cooking('Empty'), '')

    def test_new_managed_page_human_edit_is_not_overwritten(self):
        page={'path':'sample','title':'Sample','description':'new','content':'new','scriptCss':'','tags':[],'id':None}
        (self.root/'pages.json').write_text(json.dumps([page]),encoding='utf-8')
        (self.root/'publication.json').write_text(json.dumps({'sample':{'savedDigest':'old'}}),encoding='utf-8')
        current={'id':1,'path':'sample','title':'Sample','description':'edited','content':'human edit','scriptCss':'','tags':[],'updatedAt':'after'}
        with patch('wiki_api.graphql',side_effect=[{'pages':{'list':[{'id':1,'path':'sample','locale':'ru'}]}},{'pages':{'single':current}}]) as request:
            with self.assertRaises(RuntimeError): wiki_api.apply_pages(self.root)
            self.assertEqual(request.call_count,2)

    def test_editor_only_conversion_can_resume_from_original_payload(self):
        page={'path':'sample','title':'Sample','description':'new','content':'new','scriptCss':'','tags':[], 'id':1,'beforeUpdatedAt':'before','editor':'code','isPublished':True,'isPrivate':False}
        (self.root/'pages.json').write_text(json.dumps([page]),encoding='utf-8')
        current={'id':1,'path':'sample','title':'Old','description':'old','content':'old','scriptCss':'','tags':[],'updatedAt':'converted','editor':'code'}
        (self.root/'before').mkdir()
        (self.root/'before/1.json').write_text(json.dumps({**current,'updatedAt':'before','editor':'markdown'}),encoding='utf-8')
        saved={**page,'updatedAt':'saved'}
        responses=[{'pages':{'list':[{'id':1,'path':'sample','locale':'ru'}]}},{'pages':{'single':current}},
                   {'pages':{'update':{'responseResult':{'succeeded':True},'page':{'id':1}}}},{'pages':{'single':saved}}]
        with patch('wiki_api.graphql',side_effect=responses) as request:
            wiki_api.apply_pages(self.root)
            self.assertEqual(request.call_count,4)
        self.assertEqual(json.loads((self.root/'publication.json').read_text())['sample']['status'],'update')

    def test_chemicals_editorial_filter_preserves_nested_neighbours(self):
        from article_filters import filter_chemicals
        source=('<div class="dl-generated"><p>Медицина · 4 реагентов.</p>'
                '<details class="dl-reagent" id="reagent-keep"><summary>Абдомосилин (рост)</summary><details><summary>Рецепт</summary>сохранить</details></details>'
                '<details class="dl-reagent" id="reagent-dlanus-growth"><summary>Проктосилин (рост)</summary><details><summary>Рецепт</summary>убрать</details></details>'
                '<details class="dl-reagent" id="reagent-dlanus-reduction"><summary>Проктосилин (уменьшение)</summary></details>'
                '<details class="dl-reagent" id="reagent-other"><summary>Маммосилин (уменьшение)</summary></details></div>')
        result=filter_chemicals(source)
        self.assertNotIn('Проктосилин',result)
        self.assertNotIn('(рост)',result)
        self.assertNotIn('(уменьшение)',result)
        self.assertIn('Медицина · 2 реагентов.',result)
        self.assertIn('сохранить</details></details>',result)
        self.assertNotIn('убрать',result)
        self.assertEqual(filter_chemicals(result),result)

    def test_unknown_outcome_mutation_is_not_blindly_retried(self):
        with patch.dict('os.environ',{'DEEPLAGOON_WIKI_TOKEN':'fixture'}), patch('urllib.request.urlopen',side_effect=urllib.error.URLError('lost connection')) as request:
            with self.assertRaises(urllib.error.URLError):wiki_api.graphql('mutation{pages{render(id:1){responseResult{succeeded}}}}')
            self.assertEqual(request.call_count,1)


if __name__=='__main__':unittest.main()
