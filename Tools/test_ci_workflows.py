"""Repository invariants that prevent CI setup from masking content test results."""
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github/workflows"


def workflow(name):
    return yaml.load((WORKFLOWS / name).read_text(encoding="utf-8"), Loader=yaml.BaseLoader) or {}


class WorkflowContracts(unittest.TestCase):
    def test_content_builds_prepare_the_engine_before_dotnet(self):
        for name in ("build-test-debug.yml", "build-map-renderer.yml", "yaml-linter.yml",
                     "test-packaging.yml", "nf-shipyard-tests.yml", "publish.yml", "publish-testing.yml"):
            with self.subTest(workflow=name):
                ready = False
                for step in workflow(name)["jobs"]["build"]["steps"]:
                    ready |= step.get("uses") == "./.github/actions/prepare-engine"
                    if "dotnet " in step.get("run", ""):
                        self.assertTrue(ready, f"{name} must prepare the official engine baseline before dotnet")

    def test_label_and_comment_automation_has_explicit_write_permissions(self):
        actions = ("actions-ecosystem/action-add-labels@", "actions-ecosystem/action-remove-labels@",
                   "pascalgn/size-label-action@", "eps1lon/actions-label-merge-conflict@",
                   "peter-evans/create-or-update-comment@", "superbrothers/close-pull-request@")
        for path in WORKFLOWS.glob("*.yml"):
            data = workflow(path.name)
            for name, job in data.get("jobs", {}).items():
                if not any(step.get("uses", "").startswith(actions) for step in job.get("steps", [])):
                    continue
                with self.subTest(workflow=path.name, job=name):
                    permissions = job.get("permissions", data.get("permissions", {}))
                    self.assertEqual("write", permissions.get("pull-requests"))
                    if "issues" in data.get("on", {}):
                        self.assertEqual("write", permissions.get("issues"))

    def test_title_workflow_needs_no_repository_secret_or_untrusted_checkout(self):
        data = workflow("prtitlecase.yml")
        self.assertEqual("${{ github.token }}", data["env"]["GITHUB_TOKEN"])
        self.assertEqual("write", data["permissions"]["pull-requests"])
        for step in data["jobs"]["prtitlecase"]["steps"]:
            if not step.get("uses", "").startswith("actions/checkout@"):
                continue
            self.assertNotIn("ref", step.get("with", {}), "pull_request_target must use trusted base code")
            self.assertEqual("false", step["with"]["persist-credentials"])

    def test_full_release_packaging_verifies_the_official_signed_webview(self):
        for name in ("test-packaging.yml", "publish.yml", "publish-testing.yml"):
            with self.subTest(workflow=name):
                ready = False
                for step in workflow(name)["jobs"]["build"]["steps"]:
                    ready |= step.get("uses") == "./.github/actions/prepare-signed-webview"
                    if "dotnet run --project Content.Packaging" in step.get("run", ""):
                        self.assertTrue(ready, f"{name} must verify the signed module before FullRelease")
        action = yaml.safe_load((ROOT / ".github/actions/prepare-signed-webview/action.yml").read_text())
        commands = "\n".join(step.get("run", "") for step in action["runs"]["steps"])
        self.assertIn("Tools/prepare_signed_webview.py", commands)
        self.assertIn("WebViewSignedReference=", commands)

    def test_integration_tests_release_compiler_memory_without_reducing_coverage(self):
        steps = workflow("build-test-debug.yml")["jobs"]["build"]["steps"]
        shutdown = next(i for i, step in enumerate(steps) if step.get("run") == "dotnet build-server shutdown")
        integrations = next(i for i, step in enumerate(steps) if step.get("name") == "Run Content.IntegrationTests")
        self.assertLess(shutdown, integrations)
        command = steps[integrations]["run"]
        self.assertIn("Tools/run_integration_tests.py", command)
        runner = (ROOT / "Tools/run_integration_tests.py").read_text()
        self.assertIn('DOTNET_gcServer="0"', runner)
        self.assertIn("NUnit.NumberOfTestWorkers=1", runner)
        self.assertIn('BASE_FILTER = "FullyQualifiedName!~ShipyardTest"', runner)
        self.assertIn("NUnit.MapWarningTo=Failed", runner)

    def test_yaml_linter_runs_the_configuration_it_built(self):
        steps = workflow("yaml-linter.yml")["jobs"]["build"]["steps"]
        commands = [step.get("run", "") for step in steps]
        self.assertTrue(any("dotnet build --configuration Release" in command for command in commands))
        self.assertTrue(any("Content.YAMLLinter.csproj --configuration Release --no-build" in command for command in commands))


if __name__ == "__main__":
    unittest.main()
