import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import install_tablet_nginx as installer


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="art-nginx-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.main = self.root / "nginx.conf"
        self.site = self.root / "site.conf"
        self.main.write_text('events {}\nhttp { include "' + self.site.as_posix() + '"; }\n', encoding="utf-8")
        self.site.write_text('server {\n    listen 443 ssl;\n    server_name deep-lagoon-ss14.ru;\n    location / { return 404; }\n}\n', encoding="utf-8")

    def dump(self, extra=()):
        return "".join(f"# configuration file {path}:\n{path.read_text(encoding='utf-8')}\n" for path in [self.main, self.site, *extra])

    def plan(self, **kwargs):
        return installer.plan(self.dump(), "deep-lagoon-ss14.ru", "http://127.0.0.1:1212", **kwargs)

    def test_targets_https_only_preserves_existing_site_and_is_idempotent(self):
        before = self.site.read_text(encoding="utf-8")
        self.site.write_text('server { listen 80; server_name deep-lagoon-ss14.ru; return 301 https://$host$request_uri; }\n' + before, encoding="utf-8")
        changes, count = self.plan()
        self.assertEqual(count, 1)
        self.assertEqual(set(changes), {self.site.resolve()})
        after = changes[self.site.resolve()][1]
        self.assertEqual(after.count(b"proxy_pass http://127.0.0.1:1212;"), 1)
        self.assertIn(b"location / { return 404; }", after)
        self.assertIn(b"return 301 https://$host$request_uri", after)
        self.site.write_bytes(after)
        self.assertEqual(self.plan()[0], {})

    def test_update_replaces_only_owned_location(self):
        changes, _ = self.plan()
        self.site.write_bytes(changes[self.site.resolve()][1])
        updated, _ = installer.plan(self.dump(), "deep-lagoon-ss14.ru", "http://10.0.0.5:1213")
        text = updated[self.site.resolve()][1].decode()
        self.assertEqual(text.count(installer.BEGIN), 1)
        self.assertIn("proxy_pass http://10.0.0.5:1213;", text)
        self.assertNotIn("proxy_pass http://127.0.0.1:1212;", text)

    def test_quotes_comments_variables_and_multiple_tls_blocks(self):
        self.site.write_text('''# server { listen 443 ssl; server_name fake; }
server { listen 443 ssl; server_name other.example; return 200 "}"; }
server { listen 443 ssl; server_name "deep-lagoon-ss14.ru";
  set $value "{ # not a block }"; set $uri ${host}; return 200 ";";
}
server { listen [::]:443 ssl; server_name deep-lagoon-ss14.ru; }
''', encoding="utf-8")
        changes, count = self.plan()
        self.assertEqual(count, 2)
        text = changes[self.site.resolve()][1].decode()
        self.assertEqual(text.count(installer.BEGIN), 2)
        self.assertIn('server { listen 443 ssl; server_name other.example; return 200 "}"; }', text)
        self.site.write_bytes(text.encode("utf-8"))
        self.assertEqual(self.plan()[0], {})

    def test_include_names_and_listen_are_recognized(self):
        snippet = self.root / "names.conf"
        snippet.write_text("listen 443 ssl; server_name deep-lagoon-ss14.ru;", encoding="utf-8")
        self.site.write_text('server { include "' + snippet.as_posix() + '"; location / { return 404; } }', encoding="utf-8")
        changes, count = installer.plan(self.dump([snippet]), "deep-lagoon-ss14.ru", "http://127.0.0.1:1212")
        self.assertEqual(count, 1)
        self.assertEqual(set(changes), {self.site.resolve()})
        self.assertNotIn(installer.BEGIN, snippet.read_text())

    def test_stream_server_is_not_modified_even_with_same_name(self):
        stream = self.root / "stream.conf"
        stream.write_text('server { listen 443 ssl; server_name deep-lagoon-ss14.ru; }', encoding="utf-8")
        self.main.write_text(self.main.read_text() + '\nstream { include "' + stream.as_posix() + '"; }', encoding="utf-8")
        changes, count = installer.plan(self.dump([stream]), "deep-lagoon-ss14.ru", "http://127.0.0.1:1212")
        self.assertEqual(count, 1)
        self.assertNotIn(stream.resolve(), changes)

    def test_existing_unmanaged_route_is_not_overwritten(self):
        self.site.write_text(self.site.read_text().replace("location /", "location /art-tablet/"), encoding="utf-8")
        original = self.site.read_bytes()
        with self.assertRaises(installer.InstallError):
            self.plan()
        self.assertEqual(self.site.read_bytes(), original)

    def test_route_in_include_cannot_be_mistaken_for_owned_spans(self):
        changes, _ = self.plan()
        self.site.write_bytes(changes[self.site.resolve()][1])
        snippet = self.root / "routes.conf"
        snippet.write_text("location /art-tablet/ { return 404; }", encoding="utf-8")
        self.site.write_text(self.site.read_text().replace("    listen", '    include "' + snippet.as_posix() + '";\n    listen'), encoding="utf-8")
        with self.assertRaises(installer.InstallError):
            installer.plan(self.dump([snippet]), "deep-lagoon-ss14.ru", "http://127.0.0.1:1212")

    def test_missing_https_block_and_broken_markers_fail_without_writes(self):
        original = self.site.read_bytes()
        with self.assertRaises(installer.InstallError):
            installer.plan(self.dump(), "wrong.example", "http://127.0.0.1:1212")
        self.assertEqual(self.site.read_bytes(), original)
        self.site.write_text(self.site.read_text().replace("location /", installer.BEGIN + "\nlocation /"), encoding="utf-8")
        with self.assertRaises(installer.InstallError):
            self.plan()

    def test_domain_and_upstream_reject_configuration_injection(self):
        for value in ["http://host:1212/", "https://host:1212", "http://u:p@host:1212", "http://host:1212;return", "http://host:0", "http://host:65536", "http://[::::]:1212", "http://host:1212\n}"]:
            with self.subTest(value=value), self.assertRaises(installer.InstallError):
                installer.validate_upstream(value)
        for value in ["domain;", "domain/path", "domain:443", "x\nserver", "a..b", "-bad.example"]:
            with self.subTest(value=value), self.assertRaises(installer.InstallError):
                installer.validate_domain(value)
        self.assertEqual(installer.validate_upstream("http://[::1]:1212"), "http://[::1]:1212")

    def transaction(self, command, check):
        before = self.site.read_bytes()
        after = before + b"# installed\n"
        changes = {self.site: (before, after)}
        with patch.object(installer, "atomic_write", side_effect=lambda path, content: path.write_bytes(content)), patch.object(installer, "run", side_effect=command), patch.object(installer.time, "sleep"), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return installer.install(changes, ["nginx"], check, self.root / "backups")

    def test_install_backups_are_exact_and_success_is_verified(self):
        before = self.site.read_bytes()
        calls, checked = [], []
        backup = self.transaction(lambda args: calls.append(args), lambda: checked.append(True))
        manifest = json.loads((backup / "manifest.json").read_text())
        self.assertEqual((backup / manifest[0]["backup"]).read_bytes(), before)
        self.assertEqual(calls, [["nginx", "-t"], ["nginx", "-s", "reload"]])
        self.assertEqual(checked, [True])

    def test_invalid_configuration_is_restored_without_reload(self):
        before, calls = self.site.read_bytes(), []
        def command(args):
            calls.append(args)
            if len(calls) == 1:
                raise installer.InstallError("bad nginx config")
        with self.assertRaisesRegex(installer.InstallError, "bad nginx config"):
            self.transaction(command, lambda: self.fail("must not probe"))
        self.assertEqual(self.site.read_bytes(), before)
        self.assertEqual(calls, [["nginx", "-t"], ["nginx", "-t"]])

    def test_failed_reload_restores_and_reloads_original(self):
        before, calls = self.site.read_bytes(), []
        def command(args):
            calls.append(args)
            if len(calls) == 2:
                raise installer.InstallError("reload failed")
        with self.assertRaisesRegex(installer.InstallError, "reload failed"):
            self.transaction(command, lambda: None)
        self.assertEqual(self.site.read_bytes(), before)
        self.assertEqual(calls[-2:], [["nginx", "-t"], ["nginx", "-s", "reload"]])

    def test_failed_https_probe_restores_and_reloads_original(self):
        before = self.site.read_bytes()
        def check():
            raise installer.InstallError("public endpoint is still 404")
        with self.assertRaisesRegex(installer.InstallError, "still 404"):
            self.transaction(lambda args: None, check)
        self.assertEqual(self.site.read_bytes(), before)

    def test_concurrent_admin_edit_is_not_overwritten_by_rollback(self):
        def check():
            self.site.write_bytes(b"# another administrator's edit\n")
            raise installer.InstallError("probe failed")
        with self.assertRaisesRegex(installer.InstallError, "probe failed"):
            self.transaction(lambda args: None, check)
        self.assertEqual(self.site.read_bytes(), b"# another administrator's edit\n")


if __name__ == "__main__":
    unittest.main()
