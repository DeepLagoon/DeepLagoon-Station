"""Actual Python worker + actual AccountService; all data belongs to the C# fixture."""
import json
from pathlib import Path
import sys
import time

from boosty_service import Accounts, Http, ServiceError, Settings, Store, deliver, paid_event


class LoseFirstAcknowledgement:
    def __init__(self):
        self.http, self.bodies = Http(), []

    def request(self, method, url, headers=None, body=None):
        result = self.http.request(method, url, headers, body)
        if url.endswith('/economy_payment'):
            self.bodies.append(body)
            if len(self.bodies) == 1:
                raise ServiceError('transport_unavailable')
        return result


def main():
    url, directory = sys.argv[1:]
    root = Path(directory)
    token = root/'boosty-test-key'
    token.write_text('c' * 64)
    now = int(time.time())
    settings = Settings('fixture', '45945494', now - 60, '1554164947804487770', {'4125182':3},
                        {'1234':'1554164947804487771'}, url, token, root/'boosty-auth', root/'boosty-state.db')
    event = paid_event(dict(eventType='recurrent', createdAt=now, amount=500,
                            period=1, levelId=4125182, user={'id':1234}), settings, now)
    transport = LoseFirstAcknowledgement()
    api, store = Accounts(settings, transport), Store(settings)
    try:
        api.ready()
        store.ingest([event])
        deliver(store, settings, api, now)
        assert store.status() == {'retry':1}, store.status()
        store.close()
        store = Store(settings)
        store.ingest([event])
        deliver(store, settings, api, now + 4000)
        assert store.status() == {'confirmed':1}, store.status()
        assert transport.bodies[0] == transport.bodies[1]
        ack = json.loads(store.db.execute('select acknowledgement from events').fetchone()[0])
        assert ack['balance'] == 250 and not ack['applied'] and ack['awarded'] == 0
        print('Real AccountService committed 250 LC once; exact replay after a lost acknowledgement and worker restart confirmed.')
    finally:
        store.close()


if __name__ == '__main__':
    main()
