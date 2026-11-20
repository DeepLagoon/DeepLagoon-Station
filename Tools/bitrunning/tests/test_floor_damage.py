"""Floor damage is an overlay on a solid source floor, not a replacement turf."""
from collections import Counter, defaultdict
from pathlib import Path
import sys
import unittest

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from import_maps import Atom, convert
from map_art import DAMAGE_ATLAS, FLOOR_ATLAS, MapArt
from source_model import SourceModel


class FloorDamageTest(unittest.TestCase):
    def art(self):
        model = SourceModel.__new__(SourceModel)
        model.types, model.procs = {}, {}
        model.read('floor.dm', '''/turf/open/floor
\ticon = 'icons/turf/floors.dmi'
\ticon_state = "floor"
\tdamaged_dmi = 'icons/turf/damaged.dmi'
/turf/open/floor/broken_states()
\treturn list("damaged1")
/turf/open/floor/burnt_states()
\treturn list("floorscorched1")
/turf/open/floor/wood
\ticon_state = "wood"
/turf/open/floor/wood/broken_states()
\treturn list("wood-broken")
/turf/open/floor/wood/burnt_states()
\treturn list()
/turf/open/floor/catwalk_floor/iron_dark
\ticon = 'icons/turf/floors/catwalk_plating.dmi'
\ticon_state = "darkiron_above"
\tcatwalk_type = "darkiron"
''')
        art = MapArt.__new__(MapArt)
        art.source_model, art.source_fields = model, model.fields
        art.available = {FLOOR_ATLAS, DAMAGE_ATLAS}
        art.tiles, art.tile_underlays, art.decals, art.animations, art.entities = {}, {}, {}, {}, {}
        art.exports, art.placements = defaultdict(set), defaultdict(set)
        art.manifest, art.missing = defaultdict(Counter), defaultdict(Counter)
        art.functional_targets = {}
        base = Image.new('RGBA', (32, 32), (100, 110, 120, 255))
        damage = Image.new('RGBA', (32, 32))
        damage.putpixel((1, 1), (20, 10, 5, 128))
        catwalk = Image.new('RGBA', (32, 64))
        catwalk.paste(damage, (0, 0))
        catwalk.paste(base, (0, 32))
        catwalk_path = 'icons/turf/floors/catwalk_plating.dmi'
        art.available.add(catwalk_path)
        atlases = {
            FLOOR_ATLAS: (base, (32, 32), {s: ('', 1, 1, 0) for s in ('floor', 'wood', 'plating')}),
            DAMAGE_ATLAS: (damage, (32, 32), {s: ('', 1, 1, 0) for s in ('damaged1', 'floorscorched1', 'wood-broken')}),
            catwalk_path: (catwalk, (32, 32), {'darkiron_above': ('', 1, 1, 0), 'darkiron_below': ('', 1, 1, 1)}),
        }
        art.atlas = atlases.__getitem__
        return art

    def convert(self, art, floor, *helpers):
        atoms = [Atom(floor, {})] + [Atom('/obj/effect/mapping_helpers/' + helper, {}) for helper in helpers]
        return convert({(0, 0): atoms}, 'damage_fixture', False, {'entity': set(), 'noRotation': set()}, art)

    def test_broken_floor_keeps_source_tile_physics_and_opaque_base(self):
        art = self.art()
        tiles, _, decals, _, _ = self.convert(art, '/turf/open/floor/iron', 'broken_floor')
        parent, _, image, atlas, state = art.tiles[tiles[0, 0]]
        self.assertEqual(parent, 'FloorSteel')
        self.assertEqual((atlas, state), (FLOOR_ATLAS, 'floor'))
        self.assertEqual(image.getchannel('A').getextrema(), (255, 255))
        self.assertEqual(len(decals), 1)
        self.assertEqual(art.decals[decals[0][0]]['state'], 'damaged1')

    def test_burnt_wood_uses_its_source_broken_overlay_fallback(self):
        art = self.art()
        tiles, _, decals, _, _ = self.convert(art, '/turf/open/floor/wood', 'burnt_floor')
        self.assertEqual(art.tiles[tiles[0, 0]][4], 'wood')
        self.assertEqual(art.decals[decals[0][0]]['state'], 'wood-broken')

    def test_broken_takes_precedence_over_burnt(self):
        art = self.art()
        _, _, decals, _, _ = self.convert(art, '/turf/open/floor/iron', 'burnt_floor', 'broken_floor')
        self.assertEqual(len(decals), 1)
        self.assertEqual(art.decals[decals[0][0]]['state'], 'damaged1')

    def test_damage_helper_does_not_fill_real_space(self):
        art = self.art()
        tiles, _, decals, _, _ = self.convert(art, '/turf/open/space', 'broken_floor')
        self.assertFalse(tiles)
        self.assertFalse(decals)

    def test_existing_catalog_tile_has_opaque_source_plating_underlay(self):
        art = self.art()
        tile = art.damaged_tile(Atom('/obj/effect/mapping_helpers/broken_floor', {}), 'PlatingDamaged')
        self.assertEqual(art.tiles[tile][2].getchannel('A').getextrema(), (255, 255))
        self.assertEqual(art.tiles[tile][2].getpixel((0, 0)), (100, 110, 120, 255))
        self.assertEqual(art.tile_underlays[tile], {'source': FLOOR_ATLAS, 'state': 'plating'})

    def test_catwalk_preserves_the_underlay_added_by_source_initialize(self):
        art = self.art()
        floor = Atom('/turf/open/floor/catwalk_floor/iron_dark', {})
        tile = art.tile(floor, 'FloorSteelDark')
        self.assertEqual(art.tiles[tile][2].getchannel('A').getextrema(), (255, 255))
        self.assertEqual(art.tile_underlays[tile]['state'], 'darkiron_below')
        pixels = art.tiles[tile][2].tobytes()
        self.assertEqual(art.tile(floor, 'FloorSteelDark'), tile)
        self.assertEqual(art.tiles[tile][2].tobytes(), pixels)
