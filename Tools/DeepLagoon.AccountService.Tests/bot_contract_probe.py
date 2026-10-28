"""Exercise the unmodified Python Bot adapters against the actual AccountService."""
import asyncio
import json
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path
from types import SimpleNamespace

from account_transport import Unconfirmed, Rejected
from economy_api import EconomyApi, AccountDispatcher
from lagoon_coins import LagoonCoinApi
from operation_queue import OperationQueue
from ss14_api import SS14Error
import test_fixture_env
from supporter_lease import SupporterLease

TARGET, ACTOR = '1554164947804487771', '1554164947804487770'


async def main():
    base, directory = sys.argv[1:]
    identity = SimpleNamespace(account_base=base, token_for=lambda scope: ('s' if scope == 'supporter' else 'c') * 64)
    api = EconomyApi(identity)
    coins = LagoonCoinApi(identity)
    dispatch = AccountDispatcher(coins, api)
    counter = 1554164947804488000

    def body(actor=TARGET, staff=False, **fields):
        nonlocal counter
        counter += 1
        return dict(discord_id=TARGET, actor_discord_id=actor, request_id=str(counter), host_authorized=staff, **fields)

    initial = await api.request('economy_state', body())
    assert initial['balance'] == 1000
    assert initial['state']['passTier'] == 0
    member = SimpleNamespace(bot=False, roles=[], premium_since=datetime.now(timezone.utc))
    async def fetch_member(member_id): return member
    lease = SupporterLease(api)
    booster = await lease.sync(SimpleNamespace(fetch_member=fetch_member), int(TARGET))
    assert booster['state']['externalTier'] == 2 and booster['balance'] == 1000
    member.premium_since = None
    ended = await lease.sync(SimpleNamespace(fetch_member=fetch_member), int(TARGET))
    assert ended['state']['externalTier'] == 0
    observed = datetime.now(timezone.utc)
    await api.request('economy_supporter', body(external_tier=1, observed_at=observed.isoformat()))
    await api.request('economy_color', body(color='#80C080'))
    await api.request('economy_supporter', body(external_tier=0, observed_at=(observed + timedelta(milliseconds=1)).isoformat()))
    try:
        await api.request('economy_color', body(color='#8080C0', external_tier=4))
    except Rejected:
        pass
    else:
        raise AssertionError('Financial body restored revoked entitlement')

    queue_path = Path(directory) / 'real-bot-queue.db'
    queue = OperationQueue(queue_path, dispatch)
    await queue.submit('economy_task_create', body(staff=True, task_id='cross-repo-42', category='code-small', amount=250, reason='Accepted result'))
    try:
        await queue.submit('economy_task_approve', body(staff=True, task_id='cross-repo-42'))
    except Rejected:
        pass
    else:
        raise AssertionError('Executor approved own work')
    approved = await queue.submit('economy_task_approve', body(ACTOR, True, task_id='cross-repo-42'))
    assert approved['applied'] and approved['awarded'] == 250
    replay = await queue.submit('economy_task_approve', body(ACTOR, True, task_id='cross-repo-42'))
    assert not replay['applied'] and replay['balance'] == 1250
    status = await api.request('economy_status', body(task_id='cross-repo-42'))
    assert status['status'] == 'committed' and status['record']['Amount'] == 250

    paid_at = datetime.now(timezone.utc).isoformat()
    payments = [body(ACTOR, True, receipt='boosty:cross-repo-period', tier=2, paid_at=paid_at) for _ in range(4)]
    results = await asyncio.gather(*(api.request('economy_payment', request) for request in payments))
    assert sum(result['applied'] for result in results) == 1
    assert (await api.request('economy_state', body()))['balance'] == 1350
    purchase = body(tier=2)
    bought = await queue.submit('economy_pass', purchase)
    assert bought['balance'] == 750 and bought['state']['passTier'] == 2
    repeated = await api.request('economy_pass', purchase)
    assert repeated['balance'] == 750
    assert repeated['state']['passTicks'] <= bought['state']['passTicks']
    status = await api.request('economy_status', body(operation_id='spend:discord:' + purchase['request_id']))
    assert status['status'] == 'committed' and status['amount'] == -600

    # Real API commits, but the Bot's transport never records that acknowledgement.
    ambiguous = body(ACTOR, True, receipt='boosty:lost-ack', tier=2, paid_at=paid_at)

    async def lose_ack(action, payload):
        await dispatch(action, payload)
        raise Unconfirmed('Synthetic acknowledgement loss after real DB commit')

    queue.dispatch = lose_ack
    try:
        await queue.submit('economy_payment', ambiguous)
    except SS14Error:
        pass
    else:
        raise AssertionError('Expected acknowledgement loss')
    assert queue.get(ambiguous['request_id'])['state'] == 'pending'
    queue.close()
    queue = OperationQueue(queue_path, dispatch)
    with queue.db:
        queue.db.execute('UPDATE operations SET due=0 WHERE id=?', (ambiguous['request_id'],))
    recovered = await queue.execute(ambiguous['request_id'])
    assert not recovered['applied'] and recovered['balance'] == 850
    assert queue.get(ambiguous['request_id'])['state'] == 'confirmed'
    queue.close()
    history = await coins.history(TARGET, ACTOR, body()['request_id'], 1)
    assert history['balance'] == 850 and isinstance(history['rows'], list)
    print(json.dumps({'verified': ['camelCase', 'PascalCase status record', 'self approval', 'unique payment',
        'pass replay', 'revoked external lease', 'durable queue recovery', 'legacy history'], 'balance': 850}))


asyncio.run(main())
