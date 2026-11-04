#!/usr/bin/env python3
"""Read Boosty's paid subscription events and deliver immutable AccountService requests."""
from __future__ import annotations

import argparse
import calendar
from contextlib import contextmanager
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
import getpass
import hashlib
from http.cookies import SimpleCookie
import ipaddress
import json
import logging
import os
from pathlib import Path
import re
import signal
import sqlite3
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

API = "https://api.boosty.to"
GIFTS = {1: 0, 2: 100, 3: 250, 4: 1000}
LOG = logging.getLogger("boosty-renewals")
MAX_BODY = 4 * 1024 * 1024


class ServiceError(Exception):
    """Only a fixed, non-sensitive error code may reach logs."""


class HttpError(ServiceError):
    def __init__(self, status: int):
        self.status = status
        super().__init__(f"http_{status}")


def canonical(value):
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":"))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def positive_id(value):
    if isinstance(value, bool) or not re.fullmatch(r"[1-9][0-9]{0,19}", str(value)):
        raise ServiceError("invalid_identity")
    return str(value)


def discord_id(value):
    value = positive_id(value)
    if not 15 <= len(value) <= 20 or int(value) > 2**64 - 1:
        raise ServiceError("invalid_discord_identity")
    return value


def utc(value):
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.utcoffset() is None:
            raise ValueError()
        return parsed.astimezone(timezone.utc)
    except (AttributeError, ValueError, TypeError):
        raise ServiceError("invalid_utc_date") from None


def iso(timestamp):
    return datetime.fromtimestamp(timestamp, timezone.utc).isoformat().replace("+00:00", "Z")


def add_months(timestamp, months):
    date = datetime.fromtimestamp(timestamp, timezone.utc)
    year, month = divmod(date.year * 12 + date.month - 1 + months, 12)
    return date.replace(year=year, month=month + 1,
                        day=min(date.day, calendar.monthrange(year, month + 1)[1]))


def atomic_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temp = path.with_name(path.name + ".new")
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    fd = os.open(temp, flags, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream, ensure_ascii=True, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp, path)
        if os.name != "nt":
            os.chmod(path, 0o600)
            directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
    except Exception:
        raise ServiceError("private_file_write_failed") from None


def read_json(path):
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        if not isinstance(value, dict):
            raise ValueError()
        return value
    except (OSError, ValueError):
        raise ServiceError("invalid_json_file") from None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Http:
    def __init__(self):
        # In particular, never forward the loopback accounting Bearer key to a proxy.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, url, headers=None, body=None):
        request = urllib.request.Request(url, data=body, method=method, headers={
            "Accept": "application/json", "Accept-Encoding": "identity",
            "User-Agent": "DeepLagoon-BoostyService/1", **(headers or {})})
        try:
            with self.opener.open(request, timeout=15) as response:
                data = response.read(MAX_BODY + 1)
                if len(data) > MAX_BODY:
                    raise ServiceError("response_too_large")
                value = json.loads(data)
                if not isinstance(value, dict):
                    raise ServiceError("invalid_http_json")
                return value
        except urllib.error.HTTPError as error:
            error.close()
            raise HttpError(error.code) from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise ServiceError("transport_unavailable") from None
        except (ValueError, UnicodeError):
            raise ServiceError("invalid_http_json") from None


@dataclass(frozen=True)
class Settings:
    blog: str
    owner_id: str
    start_at: int
    actor_discord_id: str
    levels: dict
    bindings: dict
    account_url: str
    account_token_file: Path
    auth_file: Path
    state_file: Path
    poll_seconds: int = 300
    max_pages: int = 100

    @classmethod
    def load(cls, path):
        data = read_json(path)
        try:
            account = urllib.parse.urlsplit(data["account_url"])
            if account.scheme != "http" or not ipaddress.ip_address(account.hostname).is_loopback:
                raise ValueError()
            if account.username or account.password or account.path not in ("", "/") or account.query or account.fragment:
                raise ValueError()
            if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", data["blog"]):
                raise ValueError()
            levels = {positive_id(k): v for k, v in data["levels"].items()}
            if not levels or any(type(v) is not int or v not in GIFTS for v in levels.values()):
                raise ValueError()
            bindings = {positive_id(k): discord_id(v) for k, v in data["bindings"].items()}
            if len(set(bindings.values())) != len(bindings):
                raise ValueError()
            paths = [Path(data[k]) for k in ("account_token_file", "auth_file", "state_file")]
            if not all(p.is_absolute() for p in paths) or len(set(paths)) != 3:
                raise ValueError()
            poll, pages = data.get("poll_seconds", 300), data.get("max_pages", 100)
            if type(poll) is not int or not 60 <= poll <= 3600 or type(pages) is not int or not 1 <= pages <= 1000:
                raise ValueError()
            return cls(data["blog"], positive_id(data["owner_id"]), int(utc(data["start_at"]).timestamp()),
                       discord_id(data["actor_discord_id"]), levels, bindings,
                       data["account_url"].rstrip("/"), *paths, poll, pages)
        except (KeyError, TypeError, ValueError):
            raise ServiceError("invalid_settings") from None

    def accounting_identity(self):
        # Bindings may be added later. A queued request is never rewritten by that change.
        return dict(blog=self.blog, owner=self.owner_id, start=self.start_at,
                    actor=self.actor_discord_id, levels=self.levels, account_url=self.account_url,
                    account_token_file=str(self.account_token_file), auth_file=str(self.auth_file),
                    state_file=str(self.state_file))


class Boosty:
    def __init__(self, settings, http):
        self.settings, self.http = settings, http
        self.unsaved = None

    def refresh(self, auth):
        if not auth.get("refresh_token") or not auth.get("device_id"):
            raise ServiceError("boosty_login_required")
        result = self.http.request("POST", API + "/oauth/token/", {
            "Content-Type": "application/x-www-form-urlencoded"},
            urllib.parse.urlencode(dict(device_id=auth["device_id"], device_os="web",
                                       grant_type="refresh_token", refresh_token=auth["refresh_token"])).encode())
        if not all(isinstance(result.get(k), str) and result[k] for k in ("access_token", "refresh_token")):
            raise ServiceError("invalid_refresh_response")
        seconds = result.get("expires_in")
        if type(seconds) is not int or seconds <= 0:
            raise ServiceError("invalid_refresh_response")
        self.unsaved = {"access_token": result["access_token"], "refresh_token": result["refresh_token"],
                        "device_id": auth["device_id"], "expires_at": int(time.time()) + seconds}
        # A rotated refresh token must be durable BEFORE proceeding with any reads.
        self.save_pending()

    def save_pending(self):
        if self.unsaved is not None:
            atomic_json(self.settings.auth_file, self.unsaved)
            self.unsaved = None

    def get(self, path, **params):
        self.save_pending()
        auth = read_json(self.settings.auth_file)
        if not isinstance(auth.get("access_token"), str) or not auth["access_token"]:
            raise ServiceError("boosty_login_required")
        if type(auth.get("expires_at")) is not int:
            raise ServiceError("invalid_auth_expiry")
        if auth["expires_at"] <= time.time() + 60:
            self.refresh(auth)
            auth = read_json(self.settings.auth_file)
        url = API + path + ("?" + urllib.parse.urlencode(params) if params else "")
        for attempt in range(2):
            try:
                return self.http.request("GET", url, {"Authorization": "Bearer " + auth["access_token"]})
            except HttpError as error:
                if error.status != 401 or attempt:
                    raise
                self.refresh(auth)
                auth = read_json(self.settings.auth_file)
        raise ServiceError("boosty_login_required")

    def linked_discord(self, user):
        try:
            result = self.get(f"/v1/blog/{self.settings.blog}/subscriber/{positive_id(user)}/profile")
        except HttpError:
            raise ServiceError("boosty_binding_unavailable") from None
        try:
            profile = result["data"]["profile"]["user"]
            if positive_id(profile["id"]) != user:
                raise ServiceError("boosty_binding_mismatch")
            connection = profile["externalApps"]["discord"]
            if connection["hasAccount"] is True:
                return discord_id(connection["id"])
        except (KeyError, TypeError):
            raise ServiceError("invalid_boosty_binding") from None
        raise ServiceError("binding_required")

    def events(self, until):
        started = time.monotonic()
        profile = self.get("/v1/blog/" + self.settings.blog)
        if profile.get("isOwner") is not True or positive_id(profile.get("owner", {}).get("id")) != self.settings.owner_id:
            raise ServiceError("wrong_boosty_owner")
        events, offset, seen = [], None, set()
        for _ in range(self.settings.max_pages):
            if time.monotonic() - started > 120:
                raise ServiceError("boosty_scan_deadline")
            params = dict(event_types="subscribe,recurrent", limit=100,
                          **{"from": self.settings.start_at, "to": until})
            if offset is not None:
                params["offset"] = offset
            page = self.get("/v1/blog/stat/" + self.settings.blog + "/events", **params)
            rows, extra = page.get("data"), page.get("extra")
            if not isinstance(rows, list) or not isinstance(extra, dict) or type(extra.get("isLast")) is not bool:
                raise ServiceError("unknown_boosty_pagination")
            events.extend(rows)
            if extra["isLast"]:
                return events
            offset = extra.get("offset")
            if type(offset) not in (str, int) or str(offset) in seen or not rows:
                raise ServiceError("invalid_boosty_cursor")
            seen.add(str(offset))
        raise ServiceError("boosty_page_limit")


@dataclass(frozen=True)
class PaidEvent:
    key: str
    user: str
    tier: int
    paid_at: int
    until: str
    kind: str
    amount: str
    months: int


def paid_event(raw, settings, now):
    if not isinstance(raw, dict):
        raise ServiceError("invalid_boosty_event")
    kind = raw.get("eventType")
    if kind not in ("subscribe", "recurrent"):
        return None
    try:
        amount = Decimal(str(raw["amount"]))
        if not amount.is_finite():
            raise ValueError()
        # isFeePaid means the subscriber covers the platform fee, NOT payment success.
        promo = raw.get("promo") or {}
        if amount <= 0 or raw.get("isRestore") is True or promo.get("trialDays", 0):
            return None
        stamp = raw["createdAt"]
        if type(stamp) is not int or stamp <= 0 or stamp > now + 300:
            raise ValueError()
        if stamp < settings.start_at:
            return None
        user = positive_id(raw["user"]["id"])
        level = positive_id(raw["levelId"])
        if level not in settings.levels:
            raise ServiceError("unmapped_boosty_level")
        months = raw.get("period", 1)
        if type(months) is not int or not 1 <= months <= 12:
            raise ValueError()
        # Events may not expose a transaction ID. This is explicitly an event
        # fingerprint, NOT an invented Boosty payment identifier. Monetary changes
        # for the same identity conflict instead of generating a second payout.
        identity = dict(blog=settings.owner_id, user=user, kind=kind, at=stamp)
        if raw.get("id") is not None:
            identity = dict(blog=settings.owner_id, event=str(raw["id"]))
        return PaidEvent(digest(identity), user, settings.levels[level], stamp,
                         add_months(stamp, months).isoformat().replace("+00:00", "Z"),
                         kind, str(amount.normalize()), months)
    except (KeyError, TypeError, ValueError, InvalidOperation, AttributeError):
        raise ServiceError("unknown_boosty_paid_event") from None


class Store:
    def __init__(self, settings):
        settings.state_file.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.db = sqlite3.connect(settings.state_file, timeout=5)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
                key TEXT PRIMARY KEY, source TEXT NOT NULL, payload TEXT,
                state TEXT NOT NULL DEFAULT 'waiting', attempts INTEGER NOT NULL DEFAULT 0,
                due REAL NOT NULL DEFAULT 0, error TEXT, acknowledgement TEXT);
        """)
        identity = canonical(settings.accounting_identity())
        prior = self.db.execute("SELECT value FROM metadata WHERE key='identity'").fetchone()
        if prior and prior[0] != identity:
            self.db.close()
            raise ServiceError("state_configuration_conflict")
        with self.db:
            self.db.execute("INSERT OR IGNORE INTO metadata VALUES('identity',?)", (identity,))

    def ingest(self, events):
        # Validate the entire scan first; no payments after an incomplete page or
        # conflicting source row. Only the minimum accounting facts are retained.
        with self.db:
            for event in events:
                source = canonical(asdict(event))
                previous = self.db.execute("SELECT source FROM events WHERE key=?", (event.key,)).fetchone()
                if previous and previous[0] != source:
                    raise ServiceError("boosty_event_changed")
                self.db.execute("INSERT OR IGNORE INTO events(key,source) VALUES(?,?)", (event.key, source))

    def change(self, key, **fields):
        allowed = {"payload", "state", "attempts", "due", "error", "acknowledgement"}
        if not fields or fields.keys() - allowed:
            raise ValueError("Invalid state update")
        with self.db:
            self.db.execute("UPDATE events SET " + ",".join(k + "=?" for k in fields) + " WHERE key=?",
                            (*fields.values(), key))

    def pending(self, now):
        return self.db.execute("SELECT * FROM events WHERE state IN ('waiting','retry','sending') AND due<=? ORDER BY key LIMIT 100", (now,)).fetchall()

    def status(self):
        return dict(self.db.execute("SELECT state,count(*) FROM events GROUP BY state").fetchall())

    def close(self):
        self.db.close()


class Accounts:
    def __init__(self, settings, http):
        self.settings, self.http = settings, http

    def call(self, action, payload):
        try:
            token = self.settings.account_token_file.read_text().strip()
        except OSError:
            raise ServiceError("account_key_unavailable") from None
        if len(token) < 32 or any(c.isspace() for c in token):
            raise ServiceError("invalid_account_key")
        return self.http.request("POST", self.settings.account_url + "/deeplagoon/discord/" + action,
                                 {"Authorization": "Bearer " + token, "Content-Type": "application/json"},
                                 canonical(payload).encode())

    def ready(self):
        result = self.http.request("GET", self.settings.account_url + "/health/ready")
        if result.get("ready") is not True or result.get("paid_subscription_api_version") != 1:
            raise ServiceError("account_upgrade_required")


def request_id(key):
    # Reserved synthetic unsigned IDs, above real Discord snowflakes' signed range.
    return str(10**19 + int(key[:16], 16) % 10**18)


def prepare(event, settings, accounts, resolve_discord=None):
    target = settings.bindings.get(event.user)
    if target is None and resolve_discord is not None:
        target = resolve_discord(event.user)
    if target is None:
        raise ServiceError("binding_required")
    state = accounts.call("economy_state", dict(discord_id=target, actor_discord_id=target,
                                               request_id=request_id(event.key)))
    try:
        uid = str(uuid.UUID(state["uid"]))
        if state.get("request_id") != request_id(event.key) or uid == str(uuid.UUID(int=0)):
            raise ValueError()
    except (KeyError, TypeError, ValueError, AttributeError):
        raise ServiceError("invalid_identity_acknowledgement") from None
    return dict(discord_id=target, actor_discord_id=settings.actor_discord_id,
                request_id=request_id(event.key), host_authorized=True, expected_uid=uid,
                receipt="boosty:event:" + settings.owner_id + ":" + event.key, tier=event.tier,
                paid_at=iso(event.paid_at), paid_until=event.until, historical=False)


def acknowledgement(reply, payload, tier):
    if (reply.get("uid") != payload["expected_uid"] or reply.get("request_id") != payload["request_id"] or
            type(reply.get("balance")) is not int or reply["balance"] < 0 or
            type(reply.get("applied")) is not bool or type(reply.get("awarded")) is not int or
            reply["awarded"] != (GIFTS[tier] if reply["applied"] else 0)):
        raise ServiceError("invalid_payment_acknowledgement")
    return {k: reply[k] for k in ("uid", "request_id", "balance", "applied", "awarded")}


def deliver(store, settings, accounts, now, resolve_discord=None):
    for row in store.pending(now):
        event = PaidEvent(**json.loads(row["source"]))
        try:
            if row["payload"] is None:
                payload = prepare(event, settings, accounts, resolve_discord)
                # Freeze canonical recipient and full body BEFORE the first payment.
                store.change(event.key, payload=canonical(payload))
            else:
                payload = json.loads(row["payload"])
            store.change(event.key, state="sending", attempts=row["attempts"] + 1, error=None)
            result = accounts.call("economy_payment", payload)
            ack = acknowledgement(result, payload, event.tier)
            store.change(event.key, state="confirmed", acknowledgement=canonical(ack), error=None)
            LOG.info("payment_confirmed event=%s awarded=%d applied=%s", event.key[:12], ack["awarded"], ack["applied"])
        except ServiceError as error:
            # Explicit account conflicts/authorization failures require attention.
            permanent = isinstance(error, HttpError) and error.status in (400, 401, 403, 409)
            attempts = row["attempts"] + 1
            state = "rejected" if permanent else "retry"
            code = str(error)
            store.change(event.key, state=state, attempts=attempts, error=code,
                         due=now + min(3600, 30 * 2 ** min(attempts, 7)))
            # No source names, email addresses, tokens or provider response bodies.
            if code != row["error"]:
                LOG.warning("payment_waiting event=%s state=%s reason=%s", event.key[:12], state, code)


@contextmanager
def exclusive(path):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with open(str(path) + ".lock", "a+b") as stream:
        if os.name == "nt":
            import msvcrt
            if os.fstat(stream.fileno()).st_size == 0:
                stream.write(b"0")
                stream.flush()
            stream.seek(0)
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError:
                raise ServiceError("already_running") from None
        else:
            import fcntl
            try:
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                raise ServiceError("already_running") from None
        yield


def scan(settings, boosty, store, now):
    raw = boosty.events(now)
    events = [event for row in raw if (event := paid_event(row, settings, now)) is not None]
    store.ingest(events)
    return len(events)


def unavailable_source(error):
    return ((isinstance(error, HttpError) and (error.status in (401, 403, 429) or error.status >= 500))
            or str(error) in ("transport_unavailable", "boosty_login_required", "private_file_write_failed"))


def import_auth(destination, cookie_file=None):
    cookie = Path(cookie_file).read_text().strip() if cookie_file else getpass.getpass("Boosty Cookie header (hidden): ")
    try:
        values = SimpleCookie()
        values.load(cookie)
        auth = json.loads(urllib.parse.unquote(values["auth"].value))
        expires = auth["expiresAt"]
        if type(expires) not in (int, float) or expires <= 0:
            raise ValueError()
        # Firefox's auth cookie stores expiry in milliseconds; API storage uses seconds.
        if expires > 10**12:
            expires /= 1000
        data = dict(access_token=auth["accessToken"], refresh_token=auth["refreshToken"],
                    device_id=values["_clientId"].value, expires_at=int(expires))
        if not all(isinstance(data[k], str) and data[k] for k in ("access_token", "refresh_token", "device_id")):
            raise ValueError()
    except (KeyError, ValueError, TypeError):
        raise ServiceError("invalid_boosty_cookie") from None
    atomic_json(destination, data)
    print("Author session saved; credentials were not printed.")


def inspect_state(config, event=None, retry=False):
    settings = Settings.load(config)
    with exclusive(settings.state_file):
        store = Store(settings)
        try:
            if retry:
                row = store.db.execute("SELECT state FROM events WHERE key=?", (event,)).fetchone()
                if row is None or row[0] not in ("rejected", "retry"):
                    raise ServiceError("event_not_retryable")
                # Keep payload, original identity, receipt and accounting history intact.
                store.change(event, state="retry", due=0, error=None)
            rows = store.db.execute("SELECT key,source,state,attempts,error FROM events ORDER BY key LIMIT 1000").fetchall()
            print(canonical(dict(states=store.status(), events=[dict(row) for row in rows])))
        finally:
            store.close()


def discover(config, output):
    """Read-only source inspection before enabling any accounting requests."""
    settings = Settings.load(config)
    with exclusive(settings.state_file):
        provider = Boosty(settings, Http())
        profile = provider.get("/v1/blog/" + settings.blog)
        if profile.get("isOwner") is not True or positive_id(profile.get("owner", {}).get("id")) != settings.owner_id:
            raise ServiceError("wrong_boosty_owner")
        subscribers, offset = [], 0
        for _ in range(settings.max_pages):
            response = provider.get("/v1/blog/" + settings.blog + "/subscribers", limit=100,
                                    offset=offset, with_follow="false")
            rows = response.get("data")
            if not isinstance(rows, list):
                raise ServiceError("unknown_subscriber_schema")
            for row in rows:
                subscribers.append(dict(boosty_id=positive_id(row.get("id")), name=row.get("name"),
                                        level_id=row.get("levelId", (row.get("level") or {}).get("id"))))
            offset += len(rows)
            total = response.get("total")
            if type(total) is not int or total < offset:
                raise ServiceError("unknown_subscriber_pagination")
            if offset == total:
                break
            if not rows:
                raise ServiceError("incomplete_subscribers")
        else:
            raise ServiceError("boosty_page_limit")
        # Save field names, not financial rows/emails, for validating API compatibility.
        now = int(time.time())
        from dataclasses import replace
        provider.settings = replace(settings, start_at=now - 31 * 86400)
        events = provider.events(now)
        schemas = sorted(set(canonical(sorted(row)) for row in events if isinstance(row, dict)))
        validation = {}
        for row in events:
            try:
                event = paid_event(row, provider.settings, now)
                key = "supported_paid_event" if event else "ignored_nonpayment"
            except ServiceError as error:
                key = str(error)
            validation[key] = validation.get(key, 0) + 1
        atomic_json(output, dict(subscribers=subscribers, event_field_sets=[json.loads(row) for row in schemas],
                                 validation=validation, read_only=True))
        print("Source discovery saved locally; no accounting requests were sent.")


def run(config, once=False, probe=False):
    settings = Settings.load(config)
    with exclusive(settings.state_file):
        http, stopped = Http(), threading.Event()
        for sig in (signal.SIGINT, signal.SIGTERM):
            signal.signal(sig, lambda *_: stopped.set())
        boosty, accounts, store = Boosty(settings, http), Accounts(settings, http), Store(settings)
        try:
            last_error = None
            while not stopped.is_set():
                try:
                    # Reload ONLY the same accounting identity; added bindings are picked up.
                    current = Settings.load(config)
                    if current.accounting_identity() != settings.accounting_identity():
                        raise ServiceError("state_configuration_conflict")
                    settings = current
                    accounts.ready()
                    count = None
                    scan_error = None
                    try:
                        count = scan(settings, boosty, store, int(time.time()))
                    except ServiceError as error:
                        scan_error = error
                        # A contradictory source scan must never release a previously
                        # queued version of that same payment. Only unavailability
                        # permits recovery of earlier verified deliveries.
                        if probe or not unavailable_source(error):
                            raise
                    # Prior verified events remain payable if Boosty is unavailable.
                    if not probe:
                        deliver(store, settings, accounts, time.time(), lambda user: boosty.linked_discord(user))
                    if scan_error is not None:
                        raise scan_error
                    atomic_json(settings.state_file.with_suffix(".health.json"), dict(
                        last_success=iso(time.time()), probe=probe, events=count, states=store.status()))
                    if once or probe:
                        print(canonical(dict(probe=probe, events=count, states=store.status())))
                        return
                    last_error = None
                except ServiceError as error:
                    if once or probe or str(error) == "state_configuration_conflict":
                        raise
                    if str(error) != last_error:
                        LOG.error("scan_failed reason=%s", error)
                        last_error = str(error)
                stopped.wait(settings.poll_seconds)
        finally:
            store.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("run", "once", "probe"):
        command = sub.add_parser(name)
        command.add_argument("--config", type=Path, required=True)
    for name in ("status", "retry", "discover", "health"):
        command = sub.add_parser(name)
        command.add_argument("--config", type=Path, required=True)
        if name == "retry":
            command.add_argument("--event", required=True)
        if name == "discover":
            command.add_argument("--output", type=Path, required=True)
    auth = sub.add_parser("import-auth")
    auth.add_argument("--output", type=Path, required=True)
    auth.add_argument("--cookie-file", type=Path)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    os.umask(0o077)
    try:
        if args.command == "import-auth":
            import_auth(args.output, args.cookie_file)
        elif args.command in ("status", "retry"):
            inspect_state(args.config, getattr(args, "event", None), retry=args.command == "retry")
        elif args.command == "discover":
            discover(args.config, args.output)
        elif args.command == "health":
            settings = Settings.load(args.config)
            health = read_json(settings.state_file.with_suffix(".health.json"))
            ready = (time.time() - utc(health["last_success"]).timestamp() <= 2 * settings.poll_seconds + 30
                     and not health.get("probe") and not any(count for state, count in health["states"].items() if state != "confirmed"))
            print(canonical(dict(ready=ready, **health)))
            return 0 if ready else 1
        else:
            run(args.config, once=args.command == "once", probe=args.command == "probe")
    except ServiceError as error:
        LOG.error("service_stopped reason=%s", error)
        return 1
    except Exception as error:
        # Never expose response bodies, secrets or exception messages in tracebacks.
        LOG.error("service_stopped type=%s", type(error).__name__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
