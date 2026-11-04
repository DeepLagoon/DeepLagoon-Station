#!/usr/bin/env python3
"""Install ONLY the independent Boosty worker. No game, Bot or accounts restart."""
import argparse
import json
import os
from pathlib import Path
import pwd
import shlex
import shutil
import subprocess
import sys
import time
import urllib.request

APP = Path('/opt/deeplagoon-boosty')
STATE = Path('/var/lib/deeplagoon-boosty')
UNIT = Path('/etc/systemd/system/deeplagoon-boosty.service')
USER = 'lagoon-boosty'


def command(args, check=True):
    result = subprocess.run(args, capture_output=True, text=True, timeout=180)
    if check and result.returncode:
        # No subprocess output: it may contain a config error or a private path.
        raise RuntimeError('command_failed:' + args[0])
    return result


def read_environment(path):
    values = {}
    for line in path.read_text().splitlines():
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        key, value = line.removeprefix('export ').split('=', 1)
        fields = shlex.split(value, comments=True)
        if len(fields) > 1:
            raise RuntimeError('unsupported_environment_value')
        values[key.strip()] = fields[0] if fields else ''
    return values


def private_write(path, data, uid, gid):
    temp = path.with_name(path.name + '.install-new')
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.chown(temp, uid, gid)
    os.replace(temp, path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--auth', type=Path, required=True)
    parser.add_argument('--account-env', type=Path, default=Path('/etc/deeplagoon-accounts.env'))
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise RuntimeError('root_required')
    if command(['systemctl', 'is-active', '--quiet', 'deeplagoon-boosty.service'], check=False).returncode == 0:
        raise RuntimeError('stop_only_boosty_worker_before_upgrade')
    source = args.source.resolve(strict=True)
    if not source.is_dir() or source == APP:
        raise RuntimeError('invalid_source')
    for path in [source/'boosty_service.py', source/'deploy/deeplagoon-boosty.service', args.config, args.auth]:
        if not path.is_file() or path.is_symlink():
            raise RuntimeError('invalid_input_file')
    config = json.loads(args.config.read_text(encoding='utf-8-sig'))
    if any(config.get(k) != str(STATE/v) for k,v in {
        'auth_file':'auth.json', 'state_file':'events.sqlite3', 'account_token_file':'account-token'}.items()):
        raise RuntimeError('unexpected_service_paths')
    env = read_environment(args.account_env)
    token = env.get('ACCOUNT_COIN_TOKEN', '')
    if len(token) < 32:
        raise RuntimeError('missing_account_coin_key')
    try:
        identity = pwd.getpwnam(USER)
    except KeyError:
        command(['useradd', '--system', '--home-dir', str(STATE), '--shell', '/usr/sbin/nologin', USER])
        identity = pwd.getpwnam(USER)
    APP.mkdir(mode=0o755, parents=True, exist_ok=True)
    STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(STATE, 0o700)
    os.chown(STATE, identity.pw_uid, identity.pw_gid)
    script = APP/'boosty_service.py'
    if script.exists():
        shutil.copy2(script, APP/f'boosty_service.py.before-{time.time_ns()}')
    # Do not overwrite an already rotated service session with a stale staging copy.
    if not (STATE/'auth.json').exists():
        private_write(STATE/'auth.json', args.auth.read_bytes(), identity.pw_uid, identity.pw_gid)
    private_write(STATE/'config.json', args.config.read_bytes(), identity.pw_uid, identity.pw_gid)
    private_write(STATE/'account-token', token.encode(), identity.pw_uid, identity.pw_gid)
    private_write(script, (source/'boosty_service.py').read_bytes(), 0, 0)
    os.chmod(script, 0o644)
    run = ['runuser', '-u', USER, '--', '/usr/bin/python3', str(script)]
    command(run + ['discover', '--config', str(STATE/'config.json'), '--output', str(STATE/'discovery.json')])
    discovery = json.loads((STATE/'discovery.json').read_text())
    if any(key not in ('supported_paid_event', 'ignored_nonpayment') for key in discovery['validation']):
        raise RuntimeError('author_event_schema_requires_review')
    if discovery['validation'].get('supported_paid_event', 0) < 1:
        raise RuntimeError('real_paid_event_sample_required_before_activation')
    command(run + ['probe', '--config', str(STATE/'config.json')])
    # Units are installed disabled. No enable/start/restart call is hidden here.
    private_write(UNIT, (source/'deploy/deeplagoon-boosty.service').read_bytes(), 0, 0)
    os.chmod(UNIT, 0o644)
    command(['systemctl', 'daemon-reload'])
    print('Boosty worker installed, NOT started. Review /var/lib/deeplagoon-boosty/discovery.json and bindings.')
    print('Start only this unit: sudo systemctl enable --now deeplagoon-boosty.service')
    print('Game, Bot and AccountService were not restarted or stopped.')


if __name__ == '__main__':
    os.umask(0o077)
    try:
        main()
    except Exception as error:
        print('Installation stopped: ' + (str(error) if type(error) is RuntimeError else type(error).__name__), file=sys.stderr)
        sys.exit(1)
