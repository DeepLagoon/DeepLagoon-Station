import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from game_data import GameData
from species_reference import field, breathing_gases, build_species_reference, PREFIX


class SpeciesMechanicsTests(unittest.TestCase):
    def setUp(self):
        self.folder = TemporaryDirectory()
        self.data = GameData(Path(self.folder.name))
        self.data.raw['entity'].update({
            'Base': {'components':[{'type':'MobThresholds','thresholds':{0:'Alive',100:'Critical',200:'Dead'}}]},
            'Synth': {'parent':'Base','components':[
                {'type':'MobThresholds','thresholds':{0:'Alive',165:'Dead'}},
                {'type':'MobState','allowedStates':['Alive','Dead']}, {'type':'DeadStartupButton'}]},
            'Organic': {'components':[{'type':'Body','prototype':'Body'}]},
            'Lungs': {'components':[{'type':'Lung'},{'type':'Metabolizer','metabolizerTypes':['Slime']}]},
        })
        self.data.raw['body']['Body']={'slots':{'torso':{'organs':{'lungs':'Lungs'}}}}
        self.data.raw['reagent'].update({
            'Nitrogen':{'metabolisms':{'Gas':{'effects':[{'effectType':'Oxygenate','conditions':[{'effectType':'OrganType','type':'Slime'}]}]}}},
            'Oxygen':{'metabolisms':{'Gas':{'effects':[{'effectType':'Oxygenate','conditions':[{'effectType':'OrganType','type':'Human'}]}]}}},
            'CarbonDioxide':{'metabolisms':{'Gas':{'effects':[{'effectType':'Oxygenate','conditions':[{'effectType':'OrganType','type':'Slime'}]},{'effectType':'Oxygenate','factor':-4,'conditions':[{'effectType':'OrganType','type':'Plant','shouldHave':False}]}]}}},
        })

    def tearDown(self):
        self.folder.cleanup()

    def test_threshold_override_does_not_retain_parent_critical_or_death_values(self):
        self.assertEqual(field(self.data,'Synth','MobThresholds','thresholds'),{0:'Alive',165:'Dead'})
        self.assertFalse(field(self.data,'Synth','MobThresholds','allowRevives',False))

    def test_breathing_follows_the_actual_lung_metabolizer_and_gas_effect(self):
        self.assertEqual(breathing_gases(self.data,'Organic'),['Nitrogen'])
        self.data.raw['entity']['Lungs']['components'][1]['metabolizerTypes']=['Human']
        self.assertEqual(breathing_gases(self.data,'Organic'),['Oxygen'])
        self.assertEqual(breathing_gases(self.data,'Synth'),[])

    def test_synthetic_revival_describes_repair_then_restart_without_a_critical_threshold(self):
        self.data.raw['species']['Synth']={'name':'Синт','prototype':'Synth','dollPrototype':'Doll','roundStart':True}
        class Portrait:
            def icon(self, entity):
                return '<img src="actual.png">'
        body=build_species_reference(PREFIX+'mobs/synth',self.data,Portrait())
        self.assertIn('165 суммарного урона',body)
        self.assertNotIn('Порог критического состояния',body)
        self.assertIn('Перезапустить',body)
        self.assertIn('Один ремонт не запускает',body)
        self.assertIn('actual.png',body)


if __name__ == '__main__':
    unittest.main()
