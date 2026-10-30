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
        self.engine = self.root / "RobustToolbox"
        (self.engine / "MSBuild").mkdir(parents=True)
        (self.root / "MSBuild").mkdir()
        (self.root / "Content.Client").mkdir()
        self.version = self.engine / "MSBuild/Robust.Engine.Version.props"
        self.version.write_text("<Project><PropertyGroup><Version>" + self.contract["official_engine_version"] +
                                "</Version></PropertyGroup></Project>")
        self.save_contract()
        self.dirty = ""
        self.changed = ["Robust.Shared/Physics/Systems/FixtureSystem.cs"]
        self.mock = patch.object(gate, "_git", self.git)
        self.mock.start()
        self.addCleanup(self.mock.stop)

    def save_contract(self):
        (self.root / "ENGINE-COMPATIBILITY.json").write_text(json.dumps(self.contract))

    def git(self, root, *args):
        if args[0] == "status":
            return self.dirty
        if args[0] == "diff":
            self.assertIn("refs/tags/v" + self.contract["official_engine_version"], args)
            return "\n".join(self.changed)
        self.fail(f"Unexpected Git request (commit pins must not be used): {args}")

    def test_metadata_depends_only_on_release_version(self):
        self.assertEqual({"schema_version": 2, "engine_version": self.contract["official_engine_version"]},
                         gate.check(self.root))

    def test_other_server_revision_in_same_release_needs_no_contract_change(self):
        first = gate.check(self.root)
        self.changed.append("Robust.Shared/Serialization/Manager/SerializationManager.Reading.cs")
        self.assertEqual(first, gate.check(self.root))

    def test_stock_validation_requires_official_source(self):
        with self.assertRaisesRegex(gate.CompatibilityError, "Stock validation"):
            gate.check(self.root, allow_stock=True)
        self.changed = []
        self.assertEqual(self.contract["official_engine_version"], gate.check(self.root, allow_stock=True)["engine_version"])

    def test_modified_or_untracked_engine_is_rejected(self):
        for state in (" M Robust.Client/Graphics/OwnedTexture.cs", "?? Robust.Client/NewApi.cs", " M cefglue"):
            with self.subTest(state=state):
                self.dirty = state
                with self.assertRaisesRegex(gate.CompatibilityError, "uncommitted"):
                    gate.check(self.root)

    def test_reviewed_flight_safety_implementation_paths_are_accepted(self):
        self.changed = [
            "Robust.Client/UserInterface/UserInterfaceManager.Scaling.cs",
            "Robust.Client.Tests/UserInterface/UIScaleTest.cs",
            "Robust.Shared/Physics/Systems/SharedPhysicsSystem.cs",
            "Robust.Shared/Physics/Systems/SharedPhysicsSystem.GridSubsteps.cs",
            "Robust.Shared.IntegrationTests/Physics/GridMotionSubstepTests.cs",
        ]
        self.assertEqual(self.contract["official_engine_version"], gate.check(self.root)["engine_version"])
    def test_protected_sources_cannot_be_changed(self):
        for path in ("Robust.Client/Entry.cs", "Robust.Shared/Network/NetManager.cs", "Robust.LoaderApi",
                     "MSBuild/Robust.Engine.Version.props"):
            with self.subTest(path=path):
                self.changed = [path]
                with self.assertRaisesRegex(gate.CompatibilityError, "Unreviewed"):
                    gate.check(self.root)

    def test_contract_does_not_accept_commit_or_blob_pins(self):
        for field in ("engine_commit", "official_engine_commit", "allowed_engine_changes"):
            with self.subTest(field=field):
                self.contract[field] = "a" * 40
                self.save_contract()
                with self.assertRaisesRegex(gate.CompatibilityError, "only the official engine version"):
                    gate.read_contract(self.root)
                del self.contract[field]

    def test_version_mismatch_is_rejected(self):
        self.version.write_text("<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        with self.assertRaisesRegex(gate.CompatibilityError, "Engine version"):
            gate.check(self.root)

    def test_missing_release_tag_fails_closed(self):
        with patch.object(gate, "_git", side_effect=gate.CompatibilityError("missing release tag")):
            with self.assertRaisesRegex(gate.CompatibilityError, "missing release tag"):
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
