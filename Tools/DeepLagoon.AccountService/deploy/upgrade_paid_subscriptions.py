#!/usr/bin/env python3
"""Install a reviewed AccountService/Bot bundle; preserve all account and API receipts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import sqlite3
import stat
import subprocess
import time
import tomllib
import urllib.request
import uuid
import zipfile

ROOT = Path('/home/ss14/ss14')
APP = Path('/opt/deeplagoon-accounts')
ENV = Path('/etc/deeplagoon-accounts.env')
BOT = ROOT / 'ticket-bot'

# Run the Discord-only operation under the same network identity as the healthy
# Bot. The controller UID is deliberately excluded from its TLS egress relay.
PALETTE_PROGRAM = r'''
import asyncio,json,sys
sys.path.insert(0,'/home/ss14/ss14/ticket-bot')
import aiohttp
from discord_transport import DiscordTransport
from discord.http import HTTPClient
config=json.load(sys.stdin)
palette=[('blue','Цвет: Синий',0x3498DB),('cyan','Цвет: Бирюзовый',0x1ABC9C),
('green','Цвет: Зелёный',0x2ECC71),('gold','Цвет: Золотой',0xF1C40F),
('orange','Цвет: Оранжевый',0xE67E22),('red','Цвет: Красный',0xE74C3C),
('purple','Цвет: Фиолетовый',0x9B59B6),('pink','Цвет: Розовый',0xE91E9C)]
async def main():
    async with DiscordTransport() as t:
        c=HTTPClient(asyncio.get_running_loop(),proxy=t.url,proxy_auth=t.auth,
            connector=aiohttp.TCPConnector(force_close=True,limit=2))
        try:
            me=await asyncio.wait_for(c.static_login(config['token']),15)
            guild=int(config['guild'])
            roles=await c.get_roles(guild)
            bm=await c.get_member(guild,int(me['id']))
            top=max(r['position'] for r in roles if r['id'] in bm['roles'])
            chosen=[]
            for slug,name,color in palette:
                matches=[r for r in roles if r['name']==name]
                assert len(matches)<=1
                if matches:
                    r=matches[0]
                else:
                    try:
                        r=await c.create_role(guild,name=name,permissions='0',color=color,
                            hoist=False,mentionable=False,reason='Configure paid nickname palette without subscription badge')
                    except (aiohttp.ClientError,TimeoutError):
                        roles=await c.get_roles(guild)
                        found=[r for r in roles if r['name']==name]
                        if len(found)!=1: raise
                        r=found[0]
                assert int(r['permissions'])==0 and not r['managed'] and not r['hoist']
                assert not r['mentionable'] and r['color']==color and r['position']<top
                chosen.append((slug,str(r['id'])))
            channels=await c.get_all_guild_channels(guild)
            ids={i for _,i in chosen}
            assert not any(str(o['id']) in ids and int(o.get('allow',0)) for ch in channels for o in ch.get('permission_overwrites',[]))
            print(json.dumps(chosen))
        finally:
            await c.close()
try:
    asyncio.run(main())
except Exception as e:
    print(json.dumps({'error':type(e).__name__}))
    sys.exit(1)
'''


def run(args, **kwargs):
    result = subprocess.run(args, capture_output=True, text=True, **kwargs)
    if result.returncode:
        # Subprocess errors may contain connection strings. Never print them.
        raise RuntimeError('Command failed: ' + args[0] + ' (exit ' + str(result.returncode) + ')')
    return result.stdout.strip()


def read_env(path):
    result = {}
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        key, value = line.removeprefix('export ').split('=', 1)
        if not value.strip():
            result[key] = ''
            continue
        values = shlex.split(value, comments=True)
        if len(values) != 1:
            raise RuntimeError('Unsupported environment value for ' + key)
        # Existing systemd EnvironmentFiles and python-dotenv both use the last
        # assignment. Preserve their effective value without rewriting old keys.
        result[key] = values[0]
    return result


def health(url):
    with urllib.request.urlopen(url.rstrip('/') + '/health/ready', timeout=3) as response:
        data = json.load(response)
    return data.get('ready') is True and data.get('paid_subscription_api_version') == 1


def configure_palette(bot_env):
    if bot_env.get('DISCORD_COLOR_ROLES'):
        return
    print('Configuring the promised Discord color palette under the Bot network identity...', flush=True)
    response = run(['/usr/sbin/runuser', '-u', 'lagoon-bot', '--',
        str(BOT / '.venv/bin/python'), '-c', PALETTE_PROGRAM], timeout=90,
        input=json.dumps({'token':bot_env['DISCORD_TOKEN'],
                          'guild':bot_env.get('DISCORD_GUILD_ID') or '1554134059490349066'}))
    roles = json.loads(response)
    assert isinstance(roles, list) and len(roles) == 8
    assert all(re.fullmatch('[a-z]+', slug) and re.fullmatch('[0-9]{15,20}', role_id) for slug,role_id in roles)
    with (BOT / '.env').open('a', encoding='utf-8') as output:
        output.write('\nDISCORD_COLOR_ROLES=' + ','.join(slug+':'+role_id for slug,role_id in roles) + '\n')
        output.flush()
        os.fsync(output.fileno())
    print('Eight ordinary color choices configured; no member or subscription roles assigned.', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    assert re.fullmatch('[0-9a-f]{64}', args.sha256), 'Invalid bundle hash'
    assert hashlib.sha256(args.bundle.read_bytes()).hexdigest() == args.sha256, 'Bundle hash mismatch'
    with zipfile.ZipFile(args.bundle) as archive:
        names = archive.namelist()
        assert len(names) == len(set(names)), 'Duplicate archive paths'
        for name in names:
            assert not name.startswith('/') and '\\' not in name and '..' not in Path(name).parts, 'Unsafe archive path'
            assert name == 'manifest.json' or name == 'grants.sql' or name.startswith(('accounts/', 'bot/')), 'Unexpected archive path'
            assert not stat.S_ISLNK(archive.getinfo(name).external_attr >> 16), 'Archive symlink'
        manifest = json.loads(archive.read('manifest.json'))
        assert manifest['schema_version'] == 1
        for name, digest in manifest['files'].items():
            assert hashlib.sha256(archive.read(name)).hexdigest() == digest, 'Manifest hash mismatch: ' + name
        assert set(names) == set(manifest['files']) | {'manifest.json'}, 'Unmanifested archive entry'
        if not args.apply:
            print('Bundle verified. --apply requires root; no service or data was changed.')
            return
        assert os.geteuid() == 0, 'Run --apply with sudo; no credential is accepted by this script'
        assert APP.is_dir() and not APP.is_symlink() and APP.stat().st_uid == 0
        assert ENV.is_file() and not ENV.is_symlink() and ENV.stat().st_uid == 0
        assert not ENV.stat().st_mode & 0o077, 'Service environment must remain root-private'
        assert hashlib.sha256((APP / 'DeepLagoon.AccountService.dll').read_bytes()).hexdigest() == manifest['account_before_sha256'], 'AccountService changed after bundle preparation'
        for name, digest in manifest['bot_before_sha256'].items():
            assert name in ('rewards.py', 'account_transport.py')
            assert not (BOT / name).is_symlink()
            assert hashlib.sha256((BOT / name).read_bytes().replace(b'\r\n', b'\n')).hexdigest() == digest, 'Bot changed after bundle preparation: ' + name
        service_env = read_env(ENV)
        bot_env = read_env(BOT / '.env')
        token = bot_env['SS14_SUPPORTER_API_TOKEN']
        assert len(token) >= 32 and token not in (service_env['ACCOUNT_COIN_TOKEN'], service_env['ACCOUNT_WHITELIST_TOKEN'])
        assert not service_env.get('ACCOUNT_SUPPORTER_TOKEN') or service_env['ACCOUNT_SUPPORTER_TOKEN'] == token, 'Existing supporter key mismatch'
        url = bot_env['SS14_ACCOUNT_API_URL'].rstrip('/')
        port = int(service_env.get('ACCOUNT_LISTEN_PORT', '1213'))
        assert url == 'http://127.0.0.1:' + str(port), 'Keep the verified actual account port'
        config = tomllib.loads((ROOT / 'config/server_config.toml').read_text())
        db = config['database']
        assert db['engine'] == service_env['ACCOUNT_DB_ENGINE'] == 'postgres'
        assert db['pg_host'] in ('127.0.0.1', 'localhost')
        connection = service_env['ACCOUNT_DB_CONNECTION']
        user_match = re.search(r'(?:^|;)\s*(?:Username|User ID)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:;|$)', connection, re.I)
        database_match = re.search(r'(?:^|;)\s*Database\s*=\s*([^;]+)', connection, re.I)
        assert user_match and database_match and database_match[1] == db['pg_database'], 'Unexpected service database mapping'
        account_role = user_match[1]
        assert account_role not in ('postgres', db['pg_username']), 'AccountService must use a restricted separate login'
        psql = ['/usr/sbin/runuser', '-u', 'postgres', '--', 'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-d', db['pg_database'], '-t', '-A']
        assert run(psql + ['-c', "SELECT rolsuper OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname='" + account_role + "'"]) == 'f'
        assert run(psql + ['-c', "SELECT to_regclass('lagoon_economy_records') IS NOT NULL"]) == 't', 'Apply the existing economy migration separately first'
        link_path = Path(service_env['ACCOUNT_LINK_DATABASE'])
        assert link_path.resolve() == ROOT / 'runtime-data/identity-links/discord-links.db', 'Unexpected identity database'
        receipt_path = Path(service_env['ACCOUNT_RECEIPT_DATABASE'])
        assert receipt_path.is_file() and receipt_path.resolve().is_relative_to('/var/lib/deeplagoon-accounts'), 'Unexpected receipt database'
        assert run(['systemctl', 'is-active', 'deeplagoon-accounts.service']) == 'active'
        bot_active = subprocess.run(['systemctl', 'is-active', '--quiet', 'deeplagoon-tickets.service']).returncode == 0
        identifier = time.strftime('%Y%m%d-%H%M%S', time.gmtime()) + '-' + uuid.uuid4().hex[:8]
        backup = Path('/var/backups/deeplagoon-accounts-upgrades') / identifier
        backup.mkdir(parents=True, mode=0o700)
        backup.chmod(0o700)
        shutil.copy2(ENV, backup / 'accounts.env')
        for name in manifest['bot_before_sha256']:
            shutil.copy2(BOT / name, backup / name)
        next_app = APP.with_name(APP.name + '.next-' + identifier)
        old_app = APP.with_name(APP.name + '.before-' + identifier)
        next_app.mkdir(mode=0o755)
        for name in names:
            if not name.startswith('accounts/'):
                continue
            target = next_app / name[len('accounts/'):]
            target.parent.mkdir(parents=True, exist_ok=True)
            target.parent.chmod(0o755)
            target.write_bytes(archive.read(name))
            target.chmod(0o644)
        grant_sql = archive.read('grants.sql').decode().replace('lagoon_accounts', '"' + account_role + '"')
        run(psql, input='BEGIN;\n' + grant_sql + '\nCOMMIT;')
        run(['systemctl', 'stop', 'deeplagoon-accounts.service'])
        replaced = False
        try:
            source = sqlite3.connect(receipt_path.as_uri() + '?mode=ro', uri=True)
            target = sqlite3.connect(str(backup / 'receipts.db'))
            source.backup(target)
            target.close()
            source.close()
            (backup / 'receipts.db').chmod(0o600)
            os.replace(APP, old_app)
            os.replace(next_app, APP)
            replaced = True
            if not service_env.get('ACCOUNT_SUPPORTER_TOKEN'):
                temporary = ENV.with_name(ENV.name + '.next-' + identifier)
                temporary.write_text(ENV.read_text().rstrip() + '\nACCOUNT_SUPPORTER_TOKEN=' + token + '\n')
                temporary.chmod(0o600)
                os.replace(temporary, ENV)
            run(['systemctl', 'start', 'deeplagoon-accounts.service'])
            for attempt in range(15):
                try:
                    if health(url):
                        break
                except Exception:
                    pass
                time.sleep(1)
            else:
                raise RuntimeError('Candidate AccountService failed readiness')
            for name in manifest['bot_before_sha256']:
                existing = BOT / name
                metadata = existing.stat()
                temporary = existing.with_name(name + '.next-' + identifier)
                shutil.copy2(existing, temporary)
                temporary.write_bytes(archive.read('bot/' + name))
                os.chown(temporary, metadata.st_uid, metadata.st_gid)
                os.replace(temporary, existing)
            try:
                configure_palette(bot_env)
            except Exception as error:
                # Discord availability must not roll back a healthy independent
                # AccountService or its financial receipts.
                print('Discord palette still needs setup:', type(error).__name__, flush=True)
            if bot_active:
                run(['systemctl', 'restart', 'deeplagoon-tickets.service'])
        except Exception:
            run(['systemctl', 'stop', 'deeplagoon-accounts.service'])
            if replaced:
                os.replace(APP, APP.with_name(APP.name + '.failed-' + identifier))
                os.replace(old_app, APP)
            shutil.copy2(backup / 'accounts.env', ENV)
            for name in manifest['bot_before_sha256']:
                shutil.copy2(backup / name, BOT / name)
            run(['systemctl', 'start', 'deeplagoon-accounts.service'])
            if bot_active:
                run(['systemctl', 'restart', 'deeplagoon-tickets.service'])
            # Never rewind financial receipts after an application has run.
            raise
        print('Installed paid_subscription_api_version=1; AccountService ready; Bot restarted.')
        print('Game service was not restarted. Wallets, payment receipts and Bot queue retained.')
        print('Private backup:', backup)
        print('Previous application:', old_app)


if __name__ == '__main__':
    main()
