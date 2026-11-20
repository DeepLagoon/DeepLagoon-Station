from collections import defaultdict
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from import_maps import Atom, map_yaml
from lighting_import import LightingImport
from map_art import PrototypeLoader
from source_model import SourceModel
import yaml


class LightingTest(unittest.TestCase):
    def setUp(self):
        model = SourceModel.__new__(SourceModel)
        model.types, model.procs = {}, {}
        model.read('light.dm', '''/area
\tbase_lighting_color = COLOR_WHITE
\tbase_lighting_alpha = 0
\tstatic_lighting = TRUE
/area/fullbright
\tstatic_lighting = FALSE
\tbase_lighting_alpha = 255
/area/starlight
\tbase_lighting_alpha = 255
\tbase_lighting_color = COLOR_STARLIGHT
/obj/machinery/light/cold
\tbrightness = 7.5
\tbulb_power = 0.9
\tbulb_colour = LIGHT_COLOR_FAINT_BLUE
\tstatus = LIGHT_OK
/obj/effect/light_emitter
\tset_luminosity = 8
\tset_cap = 0
\tlight_color = COLOR_WHITE
''')
        self.lighting = LightingImport(model, '''#define COLOR_WHITE "#FFFFFF"
#define COLOR_STARLIGHT "#8589fa"
#define LIGHT_COLOR_FAINT_BLUE "#DEEFFF"
#define MINIMUM_USEFUL_LIGHT_RANGE 1.5 // source mapping default
''')

    def test_dark_fullbright_and_starlight_areas_remain_distinct(self):
        self.assertEqual(self.lighting.area(Atom('/area', {})), '#000000')
        self.assertEqual(self.lighting.area(Atom('/area/fullbright', {})), '#FFFFFF')
        self.assertEqual(self.lighting.area(Atom('/area/starlight', {})), '#8589FA')
        self.assertEqual(self.lighting.area(Atom('/area/fullbright', {'base_lighting_alpha': '128'})), '#808080')

    def test_grid_serializes_cell_lighting_without_a_global_white_roof(self):
        text = map_yaml('fixture', {(0, 0): 'Plating', (1, 0): 'Plating'}, [], [], None,
                        area_lighting={(1, 0): '#FFFFFF'})
        doc = yaml.load(text, Loader=PrototypeLoader)
        components = {c['type']: c for c in doc['entities'][0]['entities'][0]['components']}
        self.assertNotIn('ImplicitRoof', components)
        self.assertEqual(components['BitrunAreaLighting']['tiles'], {'1,0': '#FFFFFF'})

    def test_empty_ambient_data_serializes_as_a_dictionary(self):
        doc = yaml.load(map_yaml('fixture', {(0, 0): 'Plating'}, [], [], None, area_lighting={}), Loader=PrototypeLoader)
        component = next(c for c in doc['entities'][0]['entities'][0]['components'] if c['type'] == 'BitrunAreaLighting')
        self.assertEqual(component['tiles'], {})

    def test_source_bulb_parameters_drive_the_real_replaceable_native_bulb(self):
        class Art:
            entities = {}
            placements = defaultdict(set)
        art = Art()
        atom = Atom('/obj/machinery/light/cold', {})
        components = self.lighting.components(art, atom, {'PoweredLight': {}})
        bulb = art.entities[components[0]['hasLampOnSpawn']]['components'][0]
        self.assertEqual(bulb, {'type': 'LightBulb', 'color': '#DEEFFF', 'lightRadius': 7.5,
                               'lightEnergy': 0.9, 'startingState': 'Normal'})
        empty = self.lighting.components(art, Atom(atom.path, {'status': 'LIGHT_EMPTY'}), {'PoweredLight': {}})
        self.assertIsNone(empty[0]['hasLampOnSpawn'])
        broken = self.lighting.components(art, Atom(atom.path, {'status': 'LIGHT_BROKEN'}), {'PoweredLight': {}})
        self.assertEqual(art.entities[broken[0]['hasLampOnSpawn']]['components'][0]['startingState'], 'Broken')

    def test_hidden_emitters_keep_authored_power_and_radius(self):
        atom = Atom('/obj/effect/light_emitter', {'set_luminosity': '4', 'set_cap': '0.5'})
        point = self.lighting.point(atom)
        self.assertEqual((point['radius'], point['energy'], point['enabled']), (4, 0.5, True))
        self.assertFalse(self.lighting.point(Atom(atom.path, {}))['enabled'])

    def test_native_switchable_lights_are_not_forced_on_by_static_import(self):
        atom = Atom('/obj/item/flashlight', {'light_range': '6', 'light_color': 'COLOR_WHITE'})
        self.assertEqual(self.lighting.components(None, atom, {'HandheldLight': {}}), [])

    def test_source_numeric_constants_do_not_silently_drop_machine_lights(self):
        atom = Atom('/obj/machinery/vending/cola', {'light_range': 'MINIMUM_USEFUL_LIGHT_RANGE', 'light_power': '0.7'})
        self.assertEqual(self.lighting.point(atom)['radius'], 1.5)
        with self.assertRaisesRegex(ValueError, 'Unsupported source light number'):
            self.lighting.point(Atom(atom.path, {'light_range': 'UNSUPPORTED_LIGHT_RANGE'}))


if __name__ == '__main__':
    unittest.main()
