"""Real nginx installation test; run ONLY in a disposable container (see README)."""

import http.server
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import urllib.error
import urllib.request


def main():
    if not Path("/.dockerenv").exists() or os.environ.get("DL_ART_NGINX_TEST") != "1":
        sys.exit("This test is restricted to a disposable nginx container with DL_ART_NGINX_TEST=1.")
    tool = Path(__file__).with_name("install_tablet_nginx.py")
    domain = "art-nginx-test.local"

    class Backend(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/art-tablet/":
                content, kind = b'<html data-art-tablet="true">draft editor</html>', "text/html"
            elif self.path == "/art-tablet/tgui.bundle.js":
                content, kind = b"console.log('art editor');", "application/javascript"
            elif self.path == "/art-tablet/tgui.bundle.css":
                content, kind = b"body {color:gray}", "text/css"
            elif self.path == "/art-tablet/check-headers":
                names = ("Authorization", "Origin", "If-Match", "X-Art-Draft-Writer", "X-Art-Draft-Sequence", "X-Art-Draft-Epoch", "X-Art-Draft-Version")
                content, kind = "|".join(self.headers.get(name, "") for name in names).encode(), "text/plain"
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", kind)
            self.end_headers()
            self.wfile.write(content)

        def log_message(self, *args):
            pass

    def status(path):
        try:
            with opener.open("https://" + domain + path, timeout=5) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.read()

    def execute(*args, success=True):
        result = subprocess.run([sys.executable, str(tool), "--domain", domain, *args], capture_output=True, text=True)
        print(result.stdout, end="")
        if (result.returncode == 0) != success:
            raise AssertionError(result.stderr + result.stdout)
        return result

    with tempfile.TemporaryDirectory(prefix="art-nginx-runtime-") as temporary:
        root = Path(temporary)
        cert, key = root / "cert.pem", root / "key.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", str(key), "-out", str(cert),
                        "-days", "1", "-subj", "/CN=" + domain, "-addext", "subjectAltName=DNS:" + domain], check=True, capture_output=True)
        os.environ["SSL_CERT_FILE"] = str(cert)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with open("/etc/hosts", "a") as output:
            output.write("\n127.0.0.1 " + domain + "\n")
        available = Path("/etc/nginx/sites-available")
        available.mkdir(exist_ok=True)
        site = available / "art-test.conf"
        enabled = Path("/etc/nginx/conf.d/art-test.conf")
        enabled.symlink_to(site)
        names = Path("/etc/nginx/art-test-names.conf")
        names.write_text("server_name " + domain + ";\n")
        original = (f"server {{\n    listen 443 ssl;\n    include {names};\n    ssl_certificate {cert};\n    ssl_certificate_key {key};\n"
                    '    location = /keep { return 200 "untouched"; }\n    location / { return 404; }\n}\n').encode()
        site.write_bytes(original)
        site.chmod(0o640)
        backends = [http.server.ThreadingHTTPServer(("127.0.0.1", port), Backend) for port in (1212, 1213)]
        for backend in backends:
            threading.Thread(target=backend.serve_forever, daemon=True).start()
        subprocess.run(["nginx", "-t"], check=True)
        subprocess.run(["nginx"], check=True)
        try:
            assert status("/art-tablet/")[0] == 404
            execute("--dry-run")
            assert site.read_bytes() == original
            execute()
            installed = site.read_bytes()
            assert enabled.is_symlink(), "Do not replace the sites-enabled symlink"
            assert site.stat().st_mode & 0o777 == 0o640
            assert status("/art-tablet/")[0] == 200
            assert status("/keep") == (200, b"untouched")
            headers = {"Authorization": "Bearer test", "Origin": "https://" + domain, "If-Match": "7",
                       "X-Art-Draft-Writer": "writer", "X-Art-Draft-Sequence": "8", "X-Art-Draft-Epoch": "2", "X-Art-Draft-Version": "3"}
            with opener.open(urllib.request.Request("https://" + domain + "/art-tablet/check-headers", headers=headers)) as response:
                assert response.read() == "|".join(headers.values()).encode(), "Preserve all authentication/revision/draft headers and URI"
            execute()
            assert site.read_bytes() == installed, "Repeat installation must not create a duplicate location"
            execute("--upstream", "http://127.0.0.1:1213")
            updated = site.read_bytes()
            assert b"proxy_pass http://127.0.0.1:1213;" in updated
            assert updated.count(b"location ^~ /art-tablet/") == 1
            execute("--upstream", "http://127.0.0.1:1", success=False)
            assert site.read_bytes() == updated, "Failed preflight must not modify the site"
            # A server-level return defeats locations: a failed public verification must restore this exact file.
            blocked = original.replace(b"    location /", b"    return 404;\n    location /", 1)
            site.write_bytes(blocked)
            subprocess.run(["nginx", "-t"], check=True)
            subprocess.run(["nginx", "-s", "reload"], check=True)
            failed = execute(success=False)
            assert site.read_bytes() == blocked
            assert "восстановлена" in failed.stderr
            assert status("/art-tablet/")[0] == 404
            print("PASS: real nginx HTTPS install, dry-run, symlink/mode preservation, include discovery, URI/auth/draft headers, idempotency, upstream update, preflight rejection and rollback after failed HTTPS verification.")
        finally:
            subprocess.run(["nginx", "-s", "quit"], check=False)
            for backend in backends:
                backend.shutdown()


if __name__ == "__main__":
    main()
