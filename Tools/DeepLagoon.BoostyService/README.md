# Boosty paid-period rewards

Independent Python 3.11+ service, using the standard library. No game, Bot or
AccountService code/restart is required. It sends the existing `economy_payment`
contract to the already deployed AccountService. The game remains running.

The author API is **undocumented**. Confirm authenticated compatibility with
`discover` and `probe` before starting payouts. The public tier IDs below were
checked against the `iamasay` blog on 2026-11-03. Actual author events must also be
checked with an owner session; synthetic tests alone do not prove that access.

## Reward policy

Positive `subscribe` and `recurrent` accounting events grant one gift per paid
period: Искра 0, Импульс 100, Сияние 250, Сверхновая 1000 LC. Multi-month purchases
record their calendar-month duration and receive one gift per payment event.
The API, not this process, owns balances, gifts and the atomic payment journal.
Discord roles, trial/restoration events, zero payments, upgrades, post purchases
and mere passage of a month never trigger a gift. `isFeePaid` describes who pays
Boosty's fee; it is not a payment-success indicator.

`start_at` is a fixed activation boundary, in UTC. Events before it are not paid.
For a first deployment after manual compensation, set it to the activation time:
the existing ZubastiyGrisha/Dent1st grants and older paid periods are not replayed.
Do not backdate it to recover history without reconciling prior manual grants.
After activation, use the service's payment record for financial reconciliation;
independent `/lc_add` grants cannot be automatically matched to a Boosty payment.

The worker uses the linked Discord ID from the authenticated Boosty subscriber
profile, with `bindings` available for operator-verified overrides. Missing
Discord or game links remain pending and are checked again on retry. The first
request resolves and freezes the
canonical game UID; changing a Discord link or configuration cannot redirect a
queued payment. Staff requests still require the configured actor's current
game-side HOST permission, validated by AccountService on every attempt.

## Authentication and discovery

1. Sign into the author account in a separate private Firefox window. In
   Developer Tools > Network, select a request to `api.boosty.to` and copy its
   **Cookie header value** (`auth=...; _clientId=...`). Do not send it in chat.
2. On your own trusted terminal run:

   ```powershell
   py -3 Tools/DeepLagoon.BoostyService/boosty_service.py import-auth --output C:/private/boosty/auth.json
   ```

   Paste into the hidden input. Close that private window **without signing
   out**. A browser and service refreshing the same session can invalidate each
   other's rotated tokens. The importer does not read browser profile databases,
   passwords or other sites' cookies. On Windows, keep the output in a directory
   whose NTFS ACL is restricted to your account; POSIX files are created 0600.
3. Copy the example config to a private directory, set absolute paths, fixed
   `start_at`, the linked HOST Discord ID and the real loopback AccountService
   URL. Put its existing coin-scope token alone in `account_token_file`. Never
   give the service game DB credentials or the Discord Gateway token.
4. `discover --config <private-config> --output <private-discovery.json>` reads
   subscribers and 31 days of event field names. It makes **no AccountService
   requests** and saves no emails or raw financial events. Use the account IDs
   to populate `bindings` after verifying the corresponding Discord accounts.
   Its validation counts must contain `supported_paid_event` for a real paid
   event. Investigate unknown schemas before enabling any payment.
5. `probe --config <private-config>` verifies AccountService readiness and a
   complete owner-event scan, creating only local journal rows. It sends no
   financial requests. Then `once --config <private-config>` delivers pending
   events, or `run --config <private-config>` polls every five minutes.

Refresh tokens are rotated and atomically persisted (fsync + rename) before any
subsequent authenticated request. A failed write retries storage, not another
refresh. Authentication is sent only to the fixed Boosty API; redirects and
environment HTTP proxies are disabled. Accounting keys go only to a numeric
loopback HTTP address. No credential/response body is included in logs.

## Delivery and recovery

The SQLite journal is transport state, not another LC wallet. Retain its DB,
WAL/SHM and lock file along with the config and session. One process owns the
journal and author refresh stream. Backups must include consistent SQLite state
and the latest refresh token. Never delete the journal to recover a timeout.

A complete paginated source scan is validated before new events are journalled.
Scans reread all events since the fixed cutoff, including after downtime; no
timestamp cursor can silently skip a late event. A repeated pagination cursor,
unknown level, unknown paid schema or changed accounting facts aborts the scan.
Previously verified queued payments can still recover while Boosty is down.

An event's stable ID is used when present. Otherwise an explicitly labelled
**event fingerprint** uses owner, subscriber ID, event kind and exact creation
second. It is not a fabricated platform transaction ID. Two conflicting rows
with that identity stop the scan for inspection; they do not pay twice. This
fallback's ambiguity must be reviewed against actual author data before enablement.
The immutable API receipt is `boosty:event:<owner-id>:<fingerprint>`.

Persist the canonical UID and exact body **before sending**. A timeout, service
restart or lost acknowledgement retries that same body and receipt, with capped
exponential backoff. Verified acknowledgements alone mark delivery confirmed.
HTTP 400/401/403/409 remains `rejected` for operator review. After fixing the cause:

```text
python3 boosty_service.py status --config /private/config.json
python3 boosty_service.py retry --config /private/config.json --event FULL_EVENT_KEY
python3 boosty_service.py health --config /private/config.json
```

Stop the worker before `status`/`retry`/`discover`, since they use its exclusive
lock. `retry` preserves the original body and cannot create a replacement grant.
`health` is concurrent/read-only and fails on stale scans or unresolved payments.
The `.health.json` contains only timestamps, counts and mode; no credentials.

## Linux installation without touching the round

On hosts where an existing service account already has the AccountService coin
key and a persistent user manager (`loginctl show-user -p Linger`), the user
unit is also supported. Install `boosty_service.py` under
`~/.local/lib/deeplagoon-boosty`, keep configuration and credentials in
`~/.local/state/deeplagoon-boosty` (0700 directory, 0600 files), and install
`deploy/deeplagoon-boosty.user.service` as
`~/.config/systemd/user/deeplagoon-boosty.service`. Verify `discover` and `probe`,
then run `systemctl --user daemon-reload` and
`systemctl --user enable --now deeplagoon-boosty.service`.
Run a single instance across user and system units.

Use `deploy/install.py` with `--source`, `--config` and `--auth` pointing to a
staged reviewed bundle/config/session. It reads the existing AccountService
environment **as root**, copies only the coin key into the new service state,
sets owner-only permissions, runs authenticated discovery and a financial-free
probe, and installs `deeplagoon-boosty.service` **disabled**. Review the discovery,
complete bindings, then enable only this service:

```sh
sudo python3 /staging/install.py --source /staging/boosty-service \
  --config /staging/config.json --auth /staging/auth.json
sudo systemctl enable --now deeplagoon-boosty.service
```

No other unit is restarted or stopped. Rollback is
`sudo systemctl disable --now deeplagoon-boosty.service`; preserve journal and
rotated session to resume. Already committed LC operations stay committed.
Watch service logs and run `health` after start. A successful local fixture is
not proof of a real new Boosty charge or live in-game notification.

## Validation

```text
py -3 -m unittest discover -s Tools/DeepLagoon.BoostyService -p test_boosty_service.py -v
```

The tests cover renewal vs free/upgrade/restore events, cutoff protection, tier
gifts, calendar months, full scans, source conflicts, missing and changed links,
lost commit responses, restart, exact replays, session rotation and exclusive
ownership. The optional AccountService cross-process test uses this actual Python
delivery code and the actual .NET API/SQLite ledger, with a dropped success response.
Set `ACCOUNT_TEST_BOOSTY_PATH` to this directory and `ACCOUNT_TEST_PYTHON` to a
Python executable when running `BoostyWorkerPaysRealAccountServiceExactlyOnce`.

The read endpoints and event enum were checked in Boosty's own web client:
[author frontend](https://static.boosty.to/js/app.CxmiyAra.js),
[public tier catalogue](https://api.boosty.to/v1/blog/iamasay/subscription_level/).
