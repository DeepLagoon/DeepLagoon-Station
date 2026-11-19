"""Public names must survive repacking the same WhiteMoon source art."""
from collections import defaultdict
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from map_art import MapArt, remap_prototypes


class PrototypeNamesTest(unittest.TestCase):
    def art(self, states):
        art = MapArt.__new__(MapArt)
        art.entities = {
            key: {"parent": "WeaponSniperMosin", "components": [
                {"type": "Sprite", "sprite": "_DeepLagoon/Bitrunning/WhiteMoon/rifle_012345abcdef.rsi", "state": state}
            ]} for key, state in states.items()
        }
        art.tiles, art.decals, art.animations = {}, {}, {}
        art.placements = defaultdict(set, {"/obj/item/gun/ballistic/rifle/ak47": set(states)})
        return art

    def test_names_come_from_source_and_state(self):
        art = self.art({"BitrunArt012345abcdef": "open", "BitrunArtfedcba543210": "closed"})
        names = art.readable_ids()
        self.assertEqual(set(names.values()), {
            "BitrunMapItemGunBallisticRifleAk47SpriteStateOpen",
            "BitrunMapItemGunBallisticRifleAk47SpriteStateClosed",
        })

    def test_repacking_and_insertion_order_do_not_choose_variant_names(self):
        first = self.art({"BitrunArt012345abcdef": "open", "BitrunArtfedcba543210": "closed"})
        second = self.art({"BitrunArt111111111111": "closed", "BitrunArt999999999999": "open"})
        self.assertEqual(set(first.readable_ids().values()), set(second.readable_ids().values()))

    def test_nested_references_are_remapped_without_changing_player_text(self):
        old, new = "BitrunArt012345abcdef", "BitrunMapItemHat"
        actual = remap_prototypes({old: {"parent": old, "name": "Hat 47", "contents": [old]}}, {old: new})
        self.assertEqual(actual, {new: {"parent": new, "name": "Hat 47", "contents": [new]}})

    def test_missing_source_is_an_error_instead_of_an_id_with_a_digest(self):
        art = self.art({"BitrunArt012345abcdef": "open"})
        art.placements.clear()
        with self.assertRaisesRegex(ValueError, "no source type"):
            art.readable_ids()


if __name__ == "__main__":
    unittest.main()
