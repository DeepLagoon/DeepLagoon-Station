"""Regression checks for map formats, PR ranges and the privileged publication boundary."""
import base64
from collections import Counter
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Tools/map_diff"))
from model import Grid, read_map, match_grids, compare
from build import changed_maps


def sample(uid=1, tile_id=1, version=7, rotation=0, object_uid=2):
    fmt = "<iBBB" if version >= 7 else "<iBB" if version >= 6 else "<HBB"
    values = (tile_id, 0, 2, 0) if version >= 7 else (tile_id, 0, 2)
    return yaml.safe_dump({
        "meta": {"format": 7}, "tilemap": {0: "Space", tile_id: "Plating"},
        "entities": [
            {"proto": "", "entities": [{"uid": uid, "components": [
                {"type": "MetaData", "name": "Station"},
                {"type": "MapGrid", "chunks": {"-1,0": {"ind": "-1,0", "size": 1,
                 "version": version, "tiles": base64.b64encode(struct.pack(fmt, *values)).decode()}}},
                {"type": "DecalGrid", "chunkCollection": {"version": 2, "nodes": [
                    {"node": {"id": "Arrows"}, "decals": {object_uid+30: "-1,0"}}]}}]}]},
            {"proto": "ComputerShuttleParking", "entities": [{"uid": object_uid, "components": [
                {"type": "Transform", "parent": uid, "pos": "-0.5,0.5", "rot": str(rotation)+" rad"}]}]}
        ]}).encode()


class MapDiffTests(unittest.TestCase):
    def test_resave_ignores_uid_and_tile_palette_renumbering(self):
        old = read_map(sample())[0]
        new = read_map(sample(uid=99, tile_id=25, object_uid=400))[0]
        self.assertEqual([], compare(old,new)[1])
        self.assertEqual([(old,new)], list(match_grids([old],[new])))

    def test_legacy_tile_encoding_and_missing_rotation_match_version_seven(self):
        current = read_map(sample())[0]
        for version in [1,5,6]:
            self.assertFalse(compare(read_map(sample(version=version))[0], current)[1])

    def test_rotation_is_one_change_not_added_and_removed(self):
        old,new = read_map(sample())[0],read_map(sample(rotation=3.141592653589793))[0]
        stats,changes = compare(old,new)
        self.assertEqual(1,stats["objects_rotated"])
        self.assertEqual(0,stats["objects_added"])
        self.assertEqual(180,changes[0]["after"])
        self.assertFalse(compare(old,read_map(sample(rotation=6.283185307179586))[0])[1])

    def test_added_deleted_grids_and_multigrid_order(self):
        a,b = Grid(1,"A",{(0,0):("Steel",0,0,0)}),Grid(2,"B",{(100,0):("Steel",0,0,0)})
        moved = Grid(99,"A",dict(a.tiles))
        self.assertEqual([(b,b),(a,moved)],list(match_grids([a,b],[b,moved])))
        self.assertEqual(1,compare(None,a)[0]["tiles_added"])
        self.assertEqual(1,compare(a,None)[0]["tiles_removed"])

    def test_object_movement_and_decal_changes_have_coordinates(self):
        a=Grid(1,"",objects=Counter({("Chair",-2.5,3.5,0):1}))
        b=Grid(1,"",objects=Counter({("Chair",-1.5,3.5,0):1}),decals=Counter({("Arrows",0,0):1}))
        stats,changes=compare(a,b)
        self.assertEqual((1,1,1),(stats["objects_added"],stats["objects_removed"],stats["decals_added"]))
        self.assertEqual({-2.5,-1.5,.5},{c["x"] for c in changes})

    def test_merge_base_excludes_unrelated_base_branch_changes_and_handles_rename(self):
        scratch=ROOT/".local/codex/map-diff-tests"
        scratch.mkdir(parents=True,exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as folder:
            repo=Path(folder)
            def git(*args):
                return subprocess.check_output(["git","-C",str(repo),*args],stderr=subprocess.DEVNULL).decode().strip()
            git("init","-b","main")
            git("config","user.email","tests@example.invalid");git("config","user.name","Map diff test")
            maps=repo/"Resources/Maps";maps.mkdir(parents=True)
            (maps/"name with spaces.yml").write_text("original map\n")
            git("add",".");git("commit","-m","base")
            ancestor=git("rev-parse","HEAD")
            git("checkout","-b","feature")
            git("mv","Resources/Maps/name with spaces.yml","Resources/Maps/renamed.yml")
            git("commit","-m","rename")
            head=git("rev-parse","HEAD")
            git("checkout","main")
            (maps/"base-only.yml").write_text("unrelated\n")
            git("add",".");git("commit","-m","base advanced")
            base=git("rev-parse","HEAD")
            merge,files=changed_maps(base,head,repo)
            self.assertEqual(ancestor,merge)
            self.assertEqual([{"before":"Resources/Maps/name with spaces.yml","after":"Resources/Maps/renamed.yml"}],files)

    def test_workflows_separate_rendering_from_writes_and_use_default_checkout(self):
        load=lambda name:yaml.load((ROOT/".github/workflows"/name).read_text(),Loader=yaml.BaseLoader)
        render=load("map-diff-render.yml");publish=load("map-diff-comment.yml")
        self.assertIn("pull_request",render["on"])
        self.assertEqual({"contents":"read"},render["permissions"])
        self.assertEqual(["Render PR map diff"],publish["on"]["workflow_run"]["workflows"])
        for step in publish["jobs"]["comment"]["steps"]:
            if step.get("uses","").startswith("actions/checkout@"):
                self.assertNotIn("ref",step.get("with",{}))
                self.assertEqual("false",step["with"]["persist-credentials"])
        self.assertTrue(any(s.get("with",{}).get("run-id")=="${{ github.event.workflow_run.id }}"
                            for s in publish["jobs"]["comment"]["steps"]))


if __name__ == "__main__":
    unittest.main()
