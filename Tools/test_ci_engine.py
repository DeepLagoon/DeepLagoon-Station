import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import call, patch

import prepare_ci_engine as setup
from check_engine_compatibility import CompatibilityError


class PrepareEngineTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name).resolve()
        (self.root / "ENGINE-COMPATIBILITY.json").write_text(json.dumps({
            "schema_version": 2, "official_engine_version": "292.0.1"}), encoding="utf-8")

    @patch.object(setup, "find_git", return_value="git")
    @patch.object(setup.subprocess, "run")
    def test_fetches_exact_official_baseline_without_changing_engine_revision(self, run, _):
        setup.prepare(self.root)
        self.assertEqual(run.call_args_list, [
            call(["git", "-C", str(self.root), "submodule", "update", "--init", "--recursive"], check=True),
            call(["git", "-C", str(self.root / "RobustToolbox"), "fetch", "--no-tags",
                  "https://github.com/space-wizards/RobustToolbox.git",
                  "+refs/tags/v292.0.1:refs/tags/v292.0.1"], check=True),
        ])

    @patch.object(setup.subprocess, "run")
    def test_invalid_contract_does_not_run_git(self, run):
        (self.root / "ENGINE-COMPATIBILITY.json").write_text('{"schema_version": 2}', encoding="utf-8")
        with self.assertRaises(CompatibilityError):
            setup.prepare(self.root)
        run.assert_not_called()

    @patch.object(setup, "find_git", return_value="git")
    @patch.object(setup.subprocess, "run")
    def test_submodule_or_fetch_failure_stops_ci(self, run, _):
        error = subprocess.CalledProcessError(1, "git")
        for results, count in (([error], 1), ([None, error], 2)):
            with self.subTest(stage=count):
                run.reset_mock()
                run.side_effect = results
                with self.assertRaises(subprocess.CalledProcessError):
                    setup.prepare(self.root)
                self.assertEqual(run.call_count, count)


if __name__ == "__main__":
    unittest.main()
