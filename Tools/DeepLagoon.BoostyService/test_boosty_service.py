"""Temporary local databases and HTTP fixtures; no provider or production mutations."""
from dataclasses import replace
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

from boosty_service import (Accounts, Boosty, HttpError, PaidEvent, ServiceError, Settings, Store,
                            acknowledgement, atomic_json, canonical, deliver, exclusive, import_auth,
                            paid_event, prepare, request_id, run, scan)

TARGET, ACTOR = "1554164947804487771", "1554164947804487770"
UID = str(uuid.uuid4())
NOW = int(datetime(2026, 11, 3, 18, tzinfo=timezone.utc).timestamp())


def source(**changes):
    return dict(eventType="recurrent", createdAt=NOW, amount=500, period=1,
                levelId=4125182, user={"id": 1234, "email": "must-not-be-stored@example.org"}, **changes)


class FakeAccounts:
    def __init__(self):
        self.uid, self.balance, self.receipts = UID, 0, {}
        self.calls, self.drop, self.reply, self.failure = [], False, None, None

    def call(self, action, body):
        self.calls.append((action, canonical(body)))
        if self.failure:
            raise self.failure
        if action == "economy_state":
            return dict(uid=self.uid, request_id=body["request_id"])
        if body["expected_uid"] != self.uid:
            raise HttpError(409)
        existing = self.receipts.get(body["receipt"])
        if existing and existing != canonical(body):
            raise HttpError(409)
        applied = existing is None
        awarded = {1: 0, 2: 100, 3: 250, 4: 1000}[body["tier"]] if applied else 0
        self.balance += awarded
        self.receipts[body["receipt"]] = canonical(body)
        if self.drop:
            self.drop = False
            raise ServiceError("transport_unavailable")
        return self.reply or dict(uid=self.uid, request_id=body["request_id"], balance=self.balance,
                                  applied=applied, awarded=awarded)


class Tests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        root = Path(self.directory.name)
        self.settings = Settings("iamasay", "45945494", NOW, ACTOR,
                                 {"4140151": 1, "4125180": 2, "4125182": 3, "4125184": 4},
                                 {"1234": TARGET}, "http://127.0.0.1:12345", root / "key", root / "auth", root / "state.db")
        self.store = Store(self.settings)
        self.accounts = FakeAccounts()

    def tearDown(self):
        self.store.close()
        self.directory.cleanup()

    def event(self, **fields):
        raw = source()
        raw.update(fields)
        return paid_event(raw, self.settings, NOW)

    def test_duplicate_poll_and_restart_pay_only_once(self):
        event = self.event()
        self.store.ingest([event, event])
        deliver(self.store, self.settings, self.accounts, NOW)
        self.store.close()
        self.store = Store(self.settings)
        self.store.ingest([event])
        deliver(self.store, self.settings, self.accounts, NOW + 4000)
        self.assertEqual(self.accounts.balance, 250)
        self.assertEqual(self.store.status(), {"confirmed": 1})
        self.assertNotIn("email", self.store.db.execute("select source from events").fetchone()[0])

    def test_committed_payment_with_lost_response_replays_identical_body(self):
        self.store.ingest([self.event()])
        self.accounts.drop = True
        deliver(self.store, self.settings, self.accounts, NOW)
        self.assertEqual(self.accounts.balance, 250)
        self.store.close()
        self.store = Store(self.settings)
        deliver(self.store, self.settings, self.accounts, NOW + 4000)
        payments = [body for action, body in self.accounts.calls if action == "economy_payment"]
        self.assertEqual(payments[0], payments[1])
        self.assertEqual(self.accounts.balance, 250)
        self.assertEqual(self.store.status(), {"confirmed": 1})

    def test_next_month_is_a_separate_gift(self):
        next_time = NOW + 30 * 86400
        self.store.ingest([self.event(), paid_event({**source(), "createdAt": next_time}, self.settings, next_time)])
        deliver(self.store, self.settings, self.accounts, next_time)
        self.assertEqual(self.accounts.balance, 500)
        self.assertEqual(len(self.accounts.receipts), 2)

    def test_changed_source_rolls_back_entire_scan(self):
        event = self.event()
        self.store.ingest([event])
        with self.assertRaisesRegex(ServiceError, "boosty_event_changed"):
            self.store.ingest([self.event(createdAt=NOW + 1), self.event(amount=600)])
        self.assertEqual(self.store.db.execute("select count(*) from events").fetchone()[0], 1)
        self.assertEqual(self.accounts.balance, 0)

    def test_binding_added_later_and_queued_binding_never_changes(self):
        self.store.ingest([self.event()])
        deliver(self.store, replace(self.settings, bindings={}), self.accounts, NOW)
        self.assertEqual(self.accounts.balance, 0)
        self.accounts.drop = True
        deliver(self.store, self.settings, self.accounts, NOW + 4000)
        self.accounts.uid = str(uuid.uuid4())
        changed = replace(self.settings, bindings={"1234": "1554164947804487999"})
        deliver(self.store, changed, self.accounts, NOW + 8000)
        self.assertEqual(self.accounts.balance, 250)
        self.assertEqual(self.store.status(), {"rejected": 1})
        payments = [json.loads(body) for action, body in self.accounts.calls if action == "economy_payment"]
        self.assertEqual(payments[0], payments[1])
        self.assertEqual(payments[1]["discord_id"], TARGET)

    def test_ambiguous_success_is_retried_without_new_receipt(self):
        self.store.ingest([self.event()])
        self.accounts.reply = {"balance": 250, "applied": True, "awarded": 250}
        deliver(self.store, self.settings, self.accounts, NOW)
        self.assertEqual(self.store.status(), {"retry": 1})
        self.accounts.reply = None
        deliver(self.store, self.settings, self.accounts, NOW + 4000)
        self.assertEqual(self.store.status(), {"confirmed": 1})
        self.assertEqual(self.accounts.balance, 250)

    def test_paid_at_cutoff_and_no_free_upgrade_or_role_gifts(self):
        for fields in [dict(createdAt=NOW - 1), dict(amount=0), dict(amount=-1),
                       dict(eventType="up_subscription"), dict(eventType="unsubscribe"),
                       dict(isRestore=True), dict(promo={"trialDays": 7})]:
            with self.subTest(fields=fields):
                self.assertIsNone(self.event(**fields))
        self.assertIsNotNone(self.event(isFeePaid=False, promo=None))

    def test_unknown_levels_dates_periods_stop_without_paying(self):
        for fields in [dict(levelId=9), dict(createdAt=NOW + 301), dict(period=13),
                       dict(period=True), dict(amount="NaN"), dict(user={}), dict(createdAt="123")]:
            with self.subTest(fields=fields), self.assertRaises(ServiceError):
                self.event(**fields)
        self.assertEqual(self.accounts.balance, 0)

    def test_calendar_month_and_all_gifts(self):
        jan31 = int(datetime(2027, 1, 31, 12, tzinfo=timezone.utc).timestamp())
        event = paid_event({**source(), "createdAt": jan31}, self.settings, jan31)
        self.assertEqual(event.until, "2027-02-28T12:00:00Z")
        for level, expected in [(4140151, 0), (4125180, 100), (4125182, 250), (4125184, 1000)]:
            store = Store(replace(self.settings, state_file=Path(self.directory.name) / f"tier{level}.db"))
            try:
                store.ingest([self.event(levelId=level)])
                accounts = FakeAccounts()
                deliver(store, self.settings, accounts, NOW)
                self.assertEqual(accounts.balance, expected)
                self.assertEqual(store.status(), {"confirmed": 1})
            finally:
                store.close()

    def test_pagination_complete_before_scan_is_stored(self):
        class Provider:
            def events(self, until):
                raise ServiceError("invalid_boosty_cursor")
        with self.assertRaises(ServiceError):
            scan(self.settings, Provider(), self.store, NOW)
        self.assertEqual(self.store.status(), {})
        self.assertEqual(self.accounts.balance, 0)

    def test_provider_pagination_and_owner_guard(self):
        provider = Boosty(self.settings, None)
        responses = [dict(isOwner=True, owner={"id": 45945494}),
                     dict(data=[source()], extra=dict(isLast=False, offset="cursor")),
                     dict(data=[], extra=dict(isLast=True))]
        provider.get = lambda *a, **kw: responses.pop(0)
        self.assertEqual(provider.events(NOW), [source()])
        provider.get = lambda *a, **kw: dict(isOwner=False, owner={"id": 45945494})
        with self.assertRaisesRegex(ServiceError, "wrong_boosty_owner"):
            provider.events(NOW)

    def test_repeated_or_missing_cursor_fails_closed(self):
        for extra in [dict(isLast=False, offset="repeat"), dict(isLast=False), {}]:
            provider = Boosty(self.settings, None)
            provider.get = lambda path, **kw: (dict(isOwner=True, owner={"id": 45945494}) if "/stat/" not in path
                                              else dict(data=[source()], extra=extra))
            with self.subTest(extra=extra), self.assertRaises(ServiceError):
                provider.events(NOW)

    def test_new_source_event_id_does_not_hide_tier_or_amount_conflict(self):
        self.store.ingest([self.event(id="provider-row")])
        with self.assertRaisesRegex(ServiceError, "boosty_event_changed"):
            self.store.ingest([self.event(id="provider-row", amount=1000)])

    def test_state_refuses_changed_cutoff_author_or_tiers(self):
        for changed in [replace(self.settings, start_at=NOW + 1), replace(self.settings, actor_discord_id=TARGET),
                        replace(self.settings, levels={"4125182": 4})]:
            with self.subTest(changed=changed), self.assertRaisesRegex(ServiceError, "state_configuration_conflict"):
                Store(changed)

    def test_settings_require_loopback_and_unique_explicit_bindings(self):
        root = Path(self.directory.name)
        data = dict(blog="iamasay", owner_id="45945494", start_at="2026-11-03T18:00:00Z",
                    actor_discord_id=ACTOR, levels={"4125182": 3}, bindings={"1234": TARGET},
                    account_url="http://127.0.0.1:12345", account_token_file=str(root/"key"),
                    auth_file=str(root/"auth"), state_file=str(root/"state.db"))
        config = root / "config.json"
        atomic_json(config, data)
        self.assertEqual(Settings.load(config).start_at, NOW)
        for changes in [dict(account_url="http://evil.example"), dict(account_url="http://user:pass@127.0.0.1"),
                        dict(bindings={"1234": TARGET, "5678": TARGET}), dict(levels={"4125182": True}),
                        dict(auth_file="relative"), dict(start_at="2026-11-03T18:00:00")]:
            atomic_json(config, {**data, **changes})
            with self.subTest(changes=changes), self.assertRaises(ServiceError):
                Settings.load(config)

    def test_auth_refresh_is_persisted_before_read_and_not_repeated_after_write_failure(self):
        atomic_json(self.settings.auth_file, dict(access_token="old", refresh_token="refresh",
                                                device_id="device", expires_at=1))
        class Transport:
            def __init__(self): self.calls = []
            def request(self, method, url, headers=None, body=None):
                self.calls.append(method)
                if method == "POST":
                    return dict(access_token="new", refresh_token="rotated", expires_in=3600)
                return dict(ok=True)
        http = Transport()
        provider = Boosty(self.settings, http)
        with patch("boosty_service.atomic_json", side_effect=ServiceError("private_file_write_failed")):
            with self.assertRaises(ServiceError): provider.get("/v1/blog/iamasay")
            with self.assertRaises(ServiceError): provider.get("/v1/blog/iamasay")
        self.assertEqual(http.calls, ["POST"])
        self.assertEqual(provider.get("/v1/blog/iamasay"), {"ok": True})
        self.assertEqual(http.calls, ["POST", "GET"])
        self.assertEqual(json.loads(self.settings.auth_file.read_text())["refresh_token"], "rotated")

    def test_import_cookie_does_not_require_password_or_print_secrets(self):
        from urllib.parse import quote
        cookie = "auth=" + quote(json.dumps(dict(accessToken="access", refreshToken="refresh", expiresAt=1793728800000))) + "; _clientId=device"
        path = Path(self.directory.name) / "cookie.txt"
        path.write_text(cookie)
        with patch("builtins.print") as output:
            import_auth(self.settings.auth_file, path)
        self.assertEqual(json.loads(self.settings.auth_file.read_text())["expires_at"], 1793728800)
        self.assertNotIn("refresh", str(output.call_args))

    def test_process_lock_excludes_second_queue_owner(self):
        with exclusive(self.settings.state_file):
            with self.assertRaisesRegex(ServiceError, "already_running"):
                with exclusive(self.settings.state_file):
                    self.fail("Second owner acquired the lock")

    def test_source_conflict_never_releases_previously_queued_payment(self):
        self.store.ingest([self.event()])
        provider = type("Provider", (), {"events": lambda _, now: [{**source(), "amount": 600}]})()
        self.accounts.ready = lambda: None
        with patch("boosty_service.Settings.load", return_value=self.settings), \
             patch("boosty_service.Boosty", return_value=provider), \
             patch("boosty_service.Accounts", return_value=self.accounts), \
             patch("boosty_service.signal.signal"), \
             self.assertRaisesRegex(ServiceError, "boosty_event_changed"):
            run("unused", once=True)
        self.assertEqual(self.accounts.balance, 0)
        self.assertEqual(self.store.status(), {"waiting": 1})

    def test_source_outage_still_recovers_prior_verified_queue(self):
        self.store.ingest([self.event()])
        def unavailable(*_):
            raise ServiceError("transport_unavailable")
        provider = type("Provider", (), {"events": unavailable})()
        self.accounts.ready = lambda: None
        with patch("boosty_service.Settings.load", return_value=self.settings), \
             patch("boosty_service.Boosty", return_value=provider), \
             patch("boosty_service.Accounts", return_value=self.accounts), \
             patch("boosty_service.signal.signal"), \
             self.assertRaisesRegex(ServiceError, "transport_unavailable"):
            run("unused", once=True)
        self.assertEqual(self.accounts.balance, 250)
        self.assertEqual(self.store.status(), {"confirmed": 1})


if __name__ == "__main__":
    unittest.main()
