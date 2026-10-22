"""Exercise sync against real local Git repositories, without network access."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import check_engine_compatibility as compatibility
import sync_engine


class EngineSyncTests(unittest.TestCase):
    def git(self, root, *args):
        env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
        return subprocess.run(
            [compatibility.find_git(), "-c", "safe.directory=" + root.as_posix(),
             "-c", "user.name=Engine sync test", "-c", "user.email=sync@example.invalid",
             "-c", "protocol.file.allow=always", "-C", str(root), *args],
            env=env, check=True, capture_output=True, text=True,
        ).stdout.strip()

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        base = Path(temporary.name).resolve()
        self.upstream = base / "official"
        self.fork = base / "fork"
        self.root = base / "station"
        self.upstream.mkdir()
        self.root.mkdir()
        self.version = compatibility.read_contract(compatibility.ROOT)["official_engine_version"]
        self.git(self.upstream, "init", "-b", "main")
        version_file = self.upstream / "MSBuild/Robust.Engine.Version.props"
        version_file.parent.mkdir()
        version_file.write_text("<Project><PropertyGroup><Version>" + self.version +
                                "</Version></PropertyGroup></Project>")
        self.relative = sorted(compatibility.REVIEWED_ENGINE_PATHS)[0]
        source = self.upstream / self.relative
        source.parent.mkdir(parents=True)
        source.write_text("official source\n")
        self.git(self.upstream, "add", ".")
        self.git(self.upstream, "commit", "-m", "Official fixture")
        self.git(self.upstream, "tag", "v" + self.version)
        self.git(base, "clone", str(self.upstream), str(self.fork))
        self.git(self.root, "init", "-b", "main")
        self.git(self.root, "submodule", "add", str(self.fork), "RobustToolbox")
        (self.root / "ENGINE-COMPATIBILITY.json").write_text(json.dumps(
            {"schema_version": 2, "official_engine_version": self.version}))
        self.git(self.root, "add", ".")
        self.git(self.root, "commit", "-m", "Station fixture")
        self.previous = self.git(self.fork, "rev-parse", "HEAD")
        self.engine = self.root / "RobustToolbox"
        for name, value in (("ENGINE_REMOTE", str(self.fork)), ("OFFICIAL_REMOTE", str(self.upstream))):
            mocked = patch.object(sync_engine, name, value)
            mocked.start()
            self.addCleanup(mocked.stop)

    def commit(self, relative=None, text="fork fix\n"):
        path = self.fork / (relative or self.relative)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        self.git(self.fork, "add", ".")
        self.git(self.fork, "commit", "-m", "Engine change")
        return self.git(self.fork, "rev-parse", "HEAD")

    def test_new_engine_commit_updates_only_gitlink_and_preserves_version(self):
        candidate = self.commit()
        self.assertEqual(sync_engine.prepare(self.root), {
            "changed": True, "engine_commit": candidate, "engine_version": self.version})
        self.assertEqual(candidate, self.git(self.engine, "rev-parse", "HEAD"))
        self.assertEqual("M RobustToolbox", self.git(self.root, "status", "--porcelain"))
        self.assertEqual(self.version, compatibility.read_contract(self.root)["official_engine_version"])
        self.assertEqual("", self.git(self.root, "diff", "--cached", "--name-only"))

    def test_same_commit_is_noop(self):
        self.assertFalse(sync_engine.prepare(self.root)["changed"])
        self.assertEqual("", self.git(self.root, "status", "--porcelain"))

    def test_shallow_actions_checkout_can_advance(self):
        self.git(self.engine, "fetch", "--depth=1", str(self.fork), self.previous)
        self.assertEqual("true", self.git(self.engine, "rev-parse", "--is-shallow-repository"))
        candidate = self.commit()
        self.assertEqual(candidate, sync_engine.prepare(self.root)["engine_commit"])

    def test_protected_change_rejected_before_checkout(self):
        self.commit("Robust.Client/Entry.cs")
        with self.assertRaisesRegex(compatibility.CompatibilityError, "Unreviewed"):
            sync_engine.prepare(self.root)
        self.assertEqual(self.previous, self.git(self.engine, "rev-parse", "HEAD"))
        self.assertEqual("", self.git(self.root, "status", "--porcelain"))

    def test_new_official_version_requires_compatibility_review(self):
        self.commit("MSBuild/Robust.Engine.Version.props",
                    "<Project><PropertyGroup><Version>999.0.0</Version></PropertyGroup></Project>")
        with self.assertRaisesRegex(compatibility.CompatibilityError, "signed WebView and offline builder"):
            sync_engine.prepare(self.root)
        self.assertEqual(self.previous, self.git(self.engine, "rev-parse", "HEAD"))

    def test_failed_final_validation_restores_previous_engine(self):
        self.commit()
        with patch.object(compatibility, "check", side_effect=RuntimeError("validation failed")), \
                self.assertRaisesRegex(RuntimeError, "validation failed"):
            sync_engine.prepare(self.root)
        self.assertEqual(self.previous, self.git(self.engine, "rev-parse", "HEAD"))
        self.assertEqual("", self.git(self.root, "status", "--porcelain"))

    def test_local_changes_are_preserved(self):
        self.commit()
        path = self.engine / self.relative
        path.write_text("local work\n")
        with self.assertRaisesRegex(compatibility.CompatibilityError, "clean Station"):
            sync_engine.prepare(self.root)
        self.assertEqual("local work\n", path.read_text())

    def test_engine_branch_rewind_is_rejected(self):
        self.commit()
        sync_engine.prepare(self.root)
        self.git(self.root, "add", "RobustToolbox")
        self.git(self.root, "commit", "-m", "Adopt new engine")
        self.git(self.fork, "reset", "--hard", self.previous)
        with self.assertRaises(subprocess.CalledProcessError):
            sync_engine.prepare(self.root)
        self.assertEqual("", self.git(self.root, "status", "--porcelain"))


if __name__ == "__main__":
    unittest.main()
