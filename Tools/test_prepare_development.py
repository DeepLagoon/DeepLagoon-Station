import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import prepare_development as setup


class DevelopmentPreparationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name).resolve()
        self.env = {"PATH": ""}

    def test_missing_node_has_actionable_error(self):
        with patch.object(setup.shutil, "which", return_value=None):
            with self.assertRaisesRegex(setup.PreparationError, "Node.js 22.13.*restart VS Code"):
                setup.find_node(self.env)

    def test_old_node_is_rejected_before_package_installation(self):
        node = self.root / "node"
        node.touch()
        with patch.object(setup.shutil, "which", return_value=str(node)), \
                patch.object(setup, "run", return_value=subprocess.CompletedProcess([], 0, "v20.19.0\n")):
            with self.assertRaisesRegex(setup.PreparationError, "Node.js 22.13"):
                setup.find_node(self.env)

    def test_supported_node_is_put_in_child_path(self):
        node = self.root / "node"
        node.touch()
        with patch.object(setup.shutil, "which", return_value=str(node)), \
                patch.object(setup, "run", return_value=subprocess.CompletedProcess([], 0, "v24.22.1\n")):
            setup.find_node(self.env)
        self.assertEqual(self.env["PATH"].split(os.pathsep)[0], str(self.root))

    def test_existing_pinned_pnpm_does_not_install(self):
        with patch.object(setup.shutil, "which", return_value="pnpm"), \
                patch.object(setup, "run", return_value=subprocess.CompletedProcess([], 0, setup.PNPM_VERSION + "\n")) as run:
            setup.prepare_pnpm(self.root, self.env)
        self.assertEqual(run.call_count, 1)

    def test_missing_pnpm_is_installed_locally_with_exact_version(self):
        with patch.object(setup.shutil, "which", side_effect=[None, "npm", "pnpm"]), \
                patch.object(setup, "run", side_effect=[subprocess.CompletedProcess([], 0),
                                                       subprocess.CompletedProcess([], 0, setup.PNPM_VERSION + "\n")]) as run:
            setup.prepare_pnpm(self.root, self.env)
        install = run.call_args_list[0].args[0]
        self.assertIn(f"pnpm@{setup.PNPM_VERSION}", install)
        self.assertIn(str(self.root / ".local/codex/development-environment/pnpm"), install)
        self.assertNotIn("-g", install)

    def test_missing_npm_does_not_attempt_installation(self):
        with patch.object(setup.shutil, "which", return_value=None), patch.object(setup, "run") as run:
            with self.assertRaisesRegex(setup.PreparationError, "npm was not found"):
                setup.prepare_pnpm(self.root, self.env)
        run.assert_not_called()

    def test_preparation_failure_prevents_requested_command(self):
        with patch.object(setup, "prepare", side_effect=setup.PreparationError("missing Node.js")), \
                patch.object(setup, "run") as run:
            self.assertEqual(setup.main(["dotnet", "build"]), 1)
        run.assert_not_called()

    def test_arguments_and_environment_reach_child_and_failure_propagates(self):
        args = ["dotnet", "test", "path with spaces", "/consoleloggerparameters:ForceNoAlign;NoSummary"]
        error = subprocess.CalledProcessError(7, args)
        with patch.object(setup, "prepare", return_value=self.env), \
                patch.object(setup, "run", side_effect=error) as run:
            self.assertEqual(setup.main(["--", *args]), 7)
        run.assert_called_once_with(args, env=self.env)

    def test_all_vscode_launches_and_build_tasks_prepare_environment(self):
        launch = json.loads((setup.ROOT / ".vscode/launch.json").read_text(encoding="utf-8"))
        tasks = json.loads((setup.ROOT / ".vscode/tasks.json").read_text(encoding="utf-8"))
        labels = {task["label"] for task in tasks["tasks"]}
        for config in launch["configurations"]:
            self.assertIn(config["preLaunchTask"], labels, config["name"])
        for task in tasks["tasks"]:
            self.assertIn("prepareDevelopment.sh", task["args"][0])
            self.assertIn("${workspaceFolder}/Scripts/prepare-development.ps1", task["windows"]["args"])
        no_build = next(item for item in launch["compounds"] if item["name"] == "Server/Client (No Build)")
        for name in no_build["configurations"]:
            config = next(item for item in launch["configurations"] if item["name"] == name)
            self.assertEqual(config["preLaunchTask"], "prepare development")

    @unittest.skipUnless(os.name == "nt", "Windows batch argument handling")
    def test_batch_command_from_path_with_spaces_and_ampersand(self):
        path = self.root / "tools & spaces" / "version.cmd"
        path.parent.mkdir()
        path.write_text('@echo off\necho 11.25.0\n', encoding="utf-8")
        result = setup.run([str(path), "--version"], root=self.root, capture=True)
        self.assertEqual(result.stdout.strip(), "11.25.0")

    @unittest.skipUnless(os.name == "nt", "Windows child PATH handling")
    def test_command_is_resolved_using_prepared_path(self):
        path = self.root / "tools" / "local-pnpm.cmd"
        path.parent.mkdir()
        path.write_text('@echo off\necho local version\n', encoding="utf-8")
        env = os.environ.copy()
        setup.prepend_path(env, path.parent)
        result = setup.run(["local-pnpm", "--version"], root=self.root, env=env, capture=True)
        self.assertEqual(result.stdout.strip(), "local version")


@unittest.skipUnless(shutil.which("git"), "Git required")
class EngineReferenceIntegrationTests(unittest.TestCase):
    def test_fetches_missing_exact_tag_once_without_changing_engine_head(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            engine = root / "RobustToolbox"
            official = root / "official"
            engine.mkdir()
            official.mkdir()
            git = shutil.which("git")

            def command(path, *args):
                return subprocess.run([git, "-C", str(path), *args], check=True,
                                      capture_output=True, text=True).stdout.strip()

            command(root, "init")
            for path in (official, engine):
                command(path, "init")
                command(path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                        "commit", "--allow-empty", "-m", "fixture")
            command(official, "tag", "v292.0.2")
            (root / "ENGINE-COMPATIBILITY.json").write_text(json.dumps({
                "schema_version": 2, "official_engine_version": "292.0.2"}), encoding="utf-8")
            previous = command(engine, "rev-parse", "HEAD")
            with patch.object(setup, "OFFICIAL_ENGINE", str(official)):
                setup.prepare_engine(root, os.environ.copy())
            self.assertEqual(command(engine, "rev-parse", "HEAD"), previous)
            self.assertEqual(command(engine, "rev-parse", "refs/tags/v292.0.2"),
                             command(official, "rev-parse", "refs/tags/v292.0.2"))
            # A prepared checkout also works when the remote is unavailable.
            with patch.object(setup, "OFFICIAL_ENGINE", str(root / "offline")):
                setup.prepare_engine(root, os.environ.copy())


if __name__ == "__main__":
    unittest.main()
