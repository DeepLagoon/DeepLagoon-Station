import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import check_engine_compatibility as gate


class GitDependencyTests(unittest.TestCase):
    def test_git_on_path_takes_precedence(self):
        with patch.object(gate.shutil, "which", return_value="custom/git"):
            self.assertEqual("custom/git", gate.find_git())

    def test_windows_install_is_found_without_path(self):
        for variable, relative in (("ProgramFiles", "Git/cmd/git.exe"),
                                   ("LocalAppData", "Programs/Git/cmd/git.exe")):
            with self.subTest(variable=variable), tempfile.TemporaryDirectory() as directory:
                executable = Path(directory) / relative
                executable.parent.mkdir(parents=True)
                executable.touch()
                with patch.object(gate.shutil, "which", return_value=None), \
                        patch.object(gate.sys, "platform", "win32"), \
                        patch.dict(os.environ, {variable: directory}, clear=True):
                    self.assertEqual(str(executable), gate.find_git())

    def test_missing_git_fails_closed_with_actionable_message(self):
        with patch.object(gate.shutil, "which", return_value=None), \
                patch.dict(os.environ, {}, clear=True), \
                self.assertRaisesRegex(gate.CompatibilityError, "Git was not found"):
            gate._git(Path("."), "rev-parse", "HEAD")

    def test_git_start_failure_is_reported(self):
        with patch.object(gate, "find_git", return_value="git"), \
                patch.object(gate.subprocess, "run", side_effect=FileNotFoundError("missing executable")), \
                self.assertRaisesRegex(gate.CompatibilityError, "Cannot start Git.*missing executable"):
            gate._git(Path("."), "rev-parse", "HEAD")

    def test_git_error_preserves_the_diagnostic(self):
        result = subprocess.CompletedProcess([], 128, "", "fatal: bad revision\n")
        with patch.object(gate, "find_git", return_value="git"), \
                patch.object(gate.subprocess, "run", return_value=result), \
                self.assertRaisesRegex(gate.CompatibilityError, "fatal: bad revision"):
            gate._git(Path("."), "rev-parse", "HEAD")


class EngineCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.contract = gate.read_contract(gate.ROOT)
        # Exercise a reviewed fork even when the production contract currently pins stock.
        self.contract["engine_commit"] = "f" * 40
        self.contract["allowed_engine_changes"] = {
            "Robust.Shared/Physics/Systems/FixtureSystem.cs": "a" * 40,
        }
        self.engine = self.root / "RobustToolbox"
        (self.engine / "MSBuild").mkdir(parents=True)
        (self.root / "MSBuild").mkdir()
        (self.root / "Content.Client").mkdir()
        self.version = self.engine / "MSBuild/Robust.Engine.Version.props"
        self.version.write_text("<Project><PropertyGroup><Version>" + self.contract["official_engine_version"] +
                                "</Version></PropertyGroup></Project>")
        self.save_contract()
        self.head = self.contract["engine_commit"]
        self.dirty = ""
        self.changed = list(self.contract["allowed_engine_changes"])
        self.blobs = copy.copy(self.contract["allowed_engine_changes"])
        self.gitlink = self.contract["engine_commit"]
        self.mock = patch.object(gate, "_git", self.git)
        self.mock.start()
        self.addCleanup(self.mock.stop)

    def save_contract(self):
        (self.root / "ENGINE-COMPATIBILITY.json").write_text(json.dumps(self.contract))

    def git(self, root, *args):
        if args == ("rev-parse", "HEAD"):
            return self.head
        if args[0] == "status":
            return self.dirty
        if args[0] == "ls-files":
            return f"160000 {self.gitlink} 0\tRobustToolbox"
        if args[0] == "diff":
            return "\n".join(self.changed)
        if args[0] == "rev-parse" and args[1].startswith("HEAD:"):
            return self.blobs[args[1][5:]]
        self.fail(f"Unexpected git request: {args}")

    def test_reviewed_server_revision_emits_official_client_identity(self):
        metadata = gate.check(self.root)
        self.assertEqual(self.contract["official_engine_version"], metadata["engine_version"])
        self.assertEqual(self.head, metadata["server_engine_commit"])

    def test_fork_head_is_rejected_even_with_matching_nearest_tag(self):
        self.head = "bf07fcf5d43f164cf6cdac3d61d95e257e8b14f0"
        with self.assertRaisesRegex(gate.CompatibilityError, "HEAD"):
            gate.check(self.root)

    def test_stock_validation_is_explicit_and_keeps_release_pin(self):
        self.head = self.contract["official_engine_commit"]
        self.changed = []
        with self.assertRaises(gate.CompatibilityError):
            gate.check(self.root)
        self.assertEqual(self.contract["official_engine_version"], gate.check(self.root, allow_stock=True)["engine_version"])

    def test_modified_or_untracked_engine_source_is_rejected(self):
        for state in (" M Robust.Client/Graphics/OwnedTexture.cs", "?? Robust.Client/NewApi.cs", " M cefglue"):
            with self.subTest(state=state):
                self.dirty = state
                with self.assertRaisesRegex(gate.CompatibilityError, "uncommitted"):
                    gate.check(self.root)

    def test_gitlink_drift_is_rejected(self):
        self.gitlink = self.contract["official_engine_commit"]
        with self.assertRaisesRegex(gate.CompatibilityError, "gitlink"):
            gate.check(self.root)

    def test_unreviewed_client_diff_is_rejected(self):
        self.changed.append("Robust.Client/Graphics/OwnedTexture.cs")
        with self.assertRaisesRegex(gate.CompatibilityError, "Unreviewed"):
            gate.check(self.root)

    def test_contract_cannot_whitelist_client_or_network_changes(self):
        for path in ("Robust.Client/Graphics/OwnedTexture.cs", "Robust.Shared/Network/NetManager.cs", "Robust.LoaderApi"):
            self.contract["allowed_engine_changes"][path] = "0" * 40
            self.save_contract()
            with self.subTest(path=path), self.assertRaisesRegex(gate.CompatibilityError, "outside"):
                gate.check(self.root)
            del self.contract["allowed_engine_changes"][path]

    def test_reviewed_shared_path_cannot_hide_different_content(self):
        self.blobs["Robust.Shared/Physics/Systems/FixtureSystem.cs"] = "0" * 40
        with self.assertRaisesRegex(gate.CompatibilityError, "reviewed source"):
            gate.check(self.root)

    def test_version_mismatch_is_rejected(self):
        self.version.write_text("<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        with self.assertRaisesRegex(gate.CompatibilityError, "Engine version"):
            gate.check(self.root)

    def test_engine_patch_target_cannot_return_to_station(self):
        (self.root / "MSBuild/DeepLagoon.Network.targets").write_text("<Project/>")
        with self.assertRaisesRegex(gate.CompatibilityError, "must not patch"):
            gate.check(self.root)

    def test_fork_only_content_dependency_is_rejected(self):
        for symbol in ("WebViewJson.ParseScalars", "context.ReadPostBody", "texture.SetSubImageBottomUp"):
            (self.root / "Content.Client/NewApi.cs").write_text(symbol)
            with self.subTest(symbol=symbol), self.assertRaisesRegex(gate.CompatibilityError, "Fork-only"):
                gate.check(self.root)


if __name__ == "__main__":
    unittest.main()
