"""Extract only the verified official WebView compilation reference.

This does not install modules, rewrite engine assemblies or change launcher trust.
The official launcher still downloads and verifies its own platform module.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
from urllib.parse import urlsplit
from urllib.request import urlopen
import zipfile

from cryptography.hazmat.primitives.serialization import load_pem_public_key

MANIFEST_ROOT = 'https://robust-builds.cdn.spacestation14.com/'
MODULE_VERSION = '288.1.0'
ARCHIVE_SHA256 = '69a9f86a3dd0f7977b332a9112f70b88a5a49150c71c59d6313f8d4a1725194c'
DLL_SHA256 = 'f6e1831f8bd7e46c40e01906440f5d3b2d7609bd025bc52aaa0891ee69ad9a9b'
PUBLIC_KEY = b'''-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEApQ9mAhMLbmhQqRH7itgNo75S5rCSMsMXvVRmMv1d9NQ=
-----END PUBLIC KEY-----
'''


def download(url, limit):
    if urlsplit(url).scheme != 'https':
        raise ValueError('HTTPS required')
    with urlopen(url, timeout=60) as response:
        if urlsplit(response.url).scheme != 'https':
            raise ValueError('Insecure redirect')
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ValueError('Download exceeds limit')
    return data


def version(value):
    if not re.fullmatch(r'\d+(?:\.\d+){1,3}', value):
        raise ValueError('Invalid version')
    parts = tuple(map(int, value.split('.')))
    return parts + (-1,) * (4 - len(parts))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    contract = json.loads((Path(__file__).resolve().parents[1] / 'ENGINE-COMPATIBILITY.json').read_text())
    engine = contract['official_engine_version']
    engines = json.loads(download(MANIFEST_ROOT + 'manifest.json', 16 * 1024 * 1024))
    entry = engines[engine]
    if entry.get('insecure') or entry.get('redirect'):
        raise ValueError('Pinned engine was revoked or redirected; review the release baseline')
    modules = json.loads(download(MANIFEST_ROOT + 'modules.json', 16 * 1024 * 1024))
    versions = modules['modules']['Robust.Client.WebView']['versions']
    selected = max((v for v in versions if version(v) <= version(engine)), key=version)
    module = versions[selected]
    if selected != MODULE_VERSION or module.get('insecure'):
        raise ValueError('Official launcher selection differs from the reviewed signed WebView')
    metadata = module['platforms']['win-x64']
    if metadata['sha256'].lower() != ARCHIVE_SHA256:
        raise ValueError('Official archive differs from the reviewed pin')
    payload = download(metadata['url'], 512 * 1024 * 1024)
    if hashlib.sha256(payload).hexdigest() != ARCHIVE_SHA256:
        raise ValueError('Archive SHA-256 mismatch')
    load_pem_public_key(PUBLIC_KEY).verify(bytes.fromhex(metadata['sig']), payload)
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        if archive.testzip() is not None:
            raise ValueError('Corrupt module archive')
        assembly = archive.read('Robust.Client.WebView.dll')
        cef_api = archive.read('SpaceWizards.CefGlue.dll')
    if hashlib.sha256(assembly).hexdigest() != DLL_SHA256:
        raise ValueError('Assembly SHA-256 mismatch')
    args.output.mkdir(parents=True, exist_ok=True)
    destination = args.output / 'Robust.Client.WebView.dll'
    destination.write_bytes(assembly)
    (args.output / 'SpaceWizards.CefGlue.dll').write_bytes(cef_api)
    print(f'Verified official WebView {selected}: {destination.resolve()}')


if __name__ == '__main__':
    main()
