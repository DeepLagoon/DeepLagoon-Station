# Independent account service

Runs WL add/remove and LC add/remove/history without connecting to the game
process or its status port. It reads current Discord-to-UID mappings from the
existing identity SQLite database and writes the actual game database.
It never copies balances, creates a substitute wallet, or edits the link database.
It holds a read-only identity connection for its lifetime so SQLite WAL/SHM
remain available after the game's last writer closes. Identity files and their
directory remain read-only for the service account.

The Bot still checks the configured guild, registrar/host roles and WL ticket
context. LC remains restricted to its exact existing LC management role.
The service also requires a linked, active game HOST for LC/history, privileged
economy operations, and host-authorized WL removal. A claimed `host_authorized`
value alone is insufficient. Suspended/deadminned actors are rejected. Independent
WL, coin and supporter Bearer keys cannot authorize each other's routes.
`economy_supporter` requires `ACCOUNT_SUPPORTER_TOKEN`, matching the Bot's separate
supporter key; a coin key cannot assert a paid tier. Missing mappings,
pending account merges, changed mappings and invalid requests fail closed.

`bug_report_reward` is a narrowly scoped exception to general HOST-only LC grants.
It accepts the coin key, a reporter, a linked confirmer, a publication ID and one
of `bug:code`, `bug:translation`, `bug:sprite`, `bug:map`, `bug:wiki`, with exactly
10 LC. The authenticated Bot attests a durable confirmation by the assigned,
category-matched developer and rechecks their current Discord roles before dispatch.
Self-confirmation additionally requires a real active game HOST. This route does
not authorize manual `lc_add` or payment for merely creating/taking a report.
Install this service version before the matching Bot update. No game DB migration,
new grants or game restart is required for the route.

The health endpoints advertise `bug_report_reward_api_version: 1`. Check it on
the account URL configured in Bot, including any non-default port, after deployment.
Updating the game or Bot alone does not replace `/opt/deeplagoon-accounts`.

Legacy confirmations may have stored `category: null`. Only their original
`host_authorized: true` request from a linked, active game HOST can use that
compatibility path; missing categories never grant ordinary developers HOST
authority. The receipt canonicalizes the category to `bug:code`. The original
interaction acknowledgement and publication-scoped ledger key stay unchanged,
so replaying either the legacy or canonical request cannot credit twice.
Unknown non-null categories are still rejected.

For an existing installation, publish this project in Release as below, replace
the complete service application directory while `deeplagoon-accounts.service`
is stopped, then start it and check health. Preserve its protected environment,
identity links, receipt database and PostgreSQL grants. Update Bot afterwards.
Previously rejected reporter operations require an explicit host command
`/lc_operation operation_id:ORIGINAL_ID retry:true`; do not recreate confirmations,
edit queued payloads, clear receipts, or issue compensating manual LC grants.

## Transaction and retry guarantees

LC uses `AccountCurrencyOperations.AwardLagoonCoins` from the same database
assembly as the game. Every writer locks the preference row before reading
the balance. Balance and ledger/outbox insert commit atomically. Insufficient
funds and overflow roll back. Money is untouched. The account lock uses a
no-op update of `lagoon_coins`, allowing a column-scoped PostgreSQL login.

`discord:<interaction_id>` remains the ledger key. A private durable receipt
database binds each interaction ID to its original account, actor and request
fingerprint BEFORE the wallet transaction. A retry after a dropped response or
restart applies at most once; changing amount, direction, reason or target
returns a conflict. A crash after receipt creation but before wallet commit
leaves a safe retry, not a second wallet. Back up and retain receipts alongside
the game database; never reset the receipt store to resolve a timeout. The Bot
retries only with the original request ID. A newly invoked Discord command has
a new ID and is a new operation: after an ambiguous failure, check the ledger.

The receipt store is an idempotency binding, not a second balance or success
record. A returned success always follows confirmation from the game database.
Existing transactional webhook records remain pending for the existing delivery
worker if the game process is stopped. Webhook delivery is not a prerequisite
for a confirmed balance change.

The fixed reporter bounty binds its receipt to the publication ID and uses
`discord-bug-report:<publication_id>` in the native LC ledger. Changing Discord
interaction IDs cannot award it twice. Reporter, confirmer, category and original
account mappings stay bound; conflicting retries fail closed. Repair payment
and any approved bonus keep their own separate interaction IDs and ledger entries.

Durable safety reservations share the receipt database. LC add/remove defaults:
6000 per operation, 12000 per rolling hour, 24000 per rolling day, globally.
Configure `ACCOUNT_MAX_GRANT`, `ACCOUNT_HOURLY_GRANT_LIMIT`, `ACCOUNT_DAILY_GRANT_LIMIT`.
Staff economy operations have a separate count budget of 30/hour and 100/day.
Whitelist mutations have their own 30/hour and 100/day budget; no-op retries do not consume it.
Retries of the original ID do not charge twice. Failed or uncertain operations
retain reservations; reserve state is not proof of a committed payment.

## Installation and migration

1. Back up the active game DB and bot configuration. Verify the active database
   name, server data root and `discord_link.database_file`; do not guess them
   from a checkout. PostgreSQL is the production backend. SQLite is supported
   for local verification; the supplied systemd sandbox intentionally grants no
   game database file writes.
2. Publish on a compatible build host using:

   ```powershell
   dotnet publish Tools/DeepLagoon.AccountService/DeepLagoon.AccountService.csproj -c Release -o artifacts/accounts
   ```

   Install the complete publish directory, including native SQLite libraries,
   root-owned and read-only to the runtime user, at `/opt/deeplagoon-accounts`.
   The host needs the .NET 10 ASP.NET Core runtime. Do not use a Debug build:
   the game database models enable sensitive diagnostics in Debug.
3. Create the dedicated system account `lagoon-accounts` and a separate PostgreSQL
   LOGIN `lagoon_accounts` without elevated attributes. In the actual game DB,
   apply `deploy/grants.sql` and grant CONNECT to that database if not already
   available. Do not put DB credentials or the server configuration into Bot.
   The game's existing LC migrations must already be applied. This service only
   verifies schema availability and never runs migrations.
4. Give `lagoon-accounts` traverse access only to the parents of the actual
   dedicated identity directory, read/traverse on that directory, read on its
   existing DB/WAL/SHM files, and a default read ACL on that directory for newly
   recreated files. Do not give it write permission, or broad read access to
   runtime data, game configs or other users' secrets. Confirm WAL/SHM access
   using the service account. Do not use SQLite `immutable=1` on a live database.
5. Create root-owned `/etc/deeplagoon-accounts.env` mode 0600 from the example
   with the dedicated DB login and two independently generated keys. Install
   `deploy/deeplagoon-accounts.service`, run `systemctl daemon-reload`, then
   `systemctl enable --now deeplagoon-accounts.service`. StateDirectory creates
   `/var/lib/deeplagoon-accounts`; its receipt files must remain private.
6. Update Bot and set its protected EnvironmentFile:

   ```dotenv
   SS14_ACCOUNT_API_URL=http://127.0.0.1:1213
   SS14_WHITELIST_API_TOKEN=<ACCOUNT_WHITELIST_TOKEN>
   SS14_COIN_API_TOKEN=<ACCOUNT_COIN_TOKEN>
   ```

   Retain the existing game identity/moderation/supporter keys and game URL.
   There is deliberately no fallback to the game when the account service is
   unavailable. Stop using the old scoped-key migration for coin after this
   cutover; the updated migration skips that scope when external accounts are
   configured. Restart only the Discord Bot and verify service routes before
   moving further.
7. Deploy the accompanying Station changes and enable
   `discord_link.external_accounts=true` in the active server config. This makes
   old game WL/LC routes return HTTP 410 and enables periodic external-WL refresh.
   Restart the game normally once to activate it. Connected players' WL cache
   and admission update within the next refresh (about 3 seconds plus DB work);
   removal revokes admission and disconnects the player. While the game is
   frozen, DB/Discord results are still confirmed; live gameplay effects apply
   when its thread resumes. New connections read the actual DB as before.
8. Verify missing/wrong keys and cross-scope keys are rejected. With the game
   stopped on a test instance, add/remove WL and LC and query the DB/history.
   Retry exactly the same LC payload/ID, then repeat it after a service restart:
   the second response must have `applied=false`. Check the protected admin case,
   insufficient balance, and unchanged Money. Resume the game and verify live
   admission and displayed balance separately.

The service has no systemd Requires/PartOf relation to the game. A game crash or
restart must not stop it. Shared PostgreSQL failure or a game transaction holding
a database lock can still prevent a write: bounded timeouts return unavailable,
and the original operation ID makes a retry safe. Full host exhaustion also
requires operating-system resource isolation; it cannot be solved by API routing.

Rollback: preserve receipts and use compatible versions of all components.
The hardened game always retires legacy WL/LC routes with HTTP 410; disabling
`external_accounts` does not restore them. Keep AccountService running for those
operations. Never reset receipts or run two Bots concurrently.

## Local validation

```powershell
dotnet test Tools/DeepLagoon.AccountService.Tests/DeepLagoon.AccountService.Tests.csproj -c Release
# For a disposable PostgreSQL database only (the tests apply game migrations):
$env:ACCOUNT_TEST_POSTGRES='Host=127.0.0.1;Port=55439;Database=account_service_test;Username=postgres;Password=<test-password>'
dotnet test Tools/DeepLagoon.AccountService.Tests/DeepLagoon.AccountService.Tests.csproj -c Release
```

Tests start only this API and its databases, never a game server. They cover
independent WL/LC, concurrent duplicates, debit protection, persistent retries,
conflicting receipt reuse, admin protection, scoped authorization, pending merges,
rollback before commit, and concurrent game/service database writers. Bot routing
tests additionally hold a game request open while WL and LC finish independently.

## Launcher shop

Optional `ACCOUNT_LAUNCHER_SHOP_TOKEN` enables only
`launcher_economy_state`, `launcher_economy_pass`, `launcher_economy_color` and
`launcher_economy_referral`. Use an independent random key of at least 32
characters, distinct from coin/WL/supporter keys. Put the same value in the
identity service's protected environment as `LAGOON_ACCOUNT_SHOP_TOKEN`, never
in a launcher binary. An unset key leaves the shop routes unauthorized.

The identity service authenticates a current Lagoon session and constructs the
account request. The account API requires matching actor/target Discord IDs,
`expected_uid`, no host assertion and no external-tier assertion. It checks the
expected UID before writing and retains the existing stable-link checks before
commit. This scope cannot authorize staff rewards, payments, WL or other API
scopes. The existing durable receipt, transaction and wallet code handles
purchases; the catalog uses the game's `LagoonEconomyRules`.

Deploy this service and updated launcher identity code together, restart both,
and verify `launcher_shop_api_version: 1` on `/health/live` at the actual account
port. No schema migration or game restart is required. Older services return
404 for these routes, and identity reports the shop unavailable. Rollback may
disable the shop key while retaining native economy data and receipt records.

Regression checks are in `LauncherShopTests.cs`; run the existing account-service
test command above. Launcher `identity/test_economy.py` verifies session/link
checks, payload allowlists and stable operation IDs. Synthetic UI validation is
in Launcher `diagnostics/ShopProbe`.

## Paid periods without a Discord badge

A non-historical confirmed `economy_payment` grants the tier through optional
`paid_until` (UTC), or one calendar month from `paid_at`. The payment's gift,
immutable expiry and account access are one transaction. Removing the Boosty
Discord role changes its short observation only; the paid period survives.
Historical imports grant neither a gift nor current access. Replaying a receipt
cannot credit again, extend its expiry or move it to another account.

Paid periods stay in `payment:<receipt>`. `external:<uid>` publishes the highest
active source with its corresponding end for existing game builds. Its separate
`DiscordTier`/`DiscordExpiresAt` fields retain the original 15-minute observation.
A lower paid tier cannot extend a higher Discord source. No LC purchase or
Discord role is fabricated. The Bot retains an existing ordinary color role
using the authoritative account tier. Health advertises
`paid_subscription_api_version: 1`; install this service before updating the Bot.

The provider payment must first be verified by the host using `/reward_payment`.
This change does not import Boosty billing data automatically. An explicitly
manual restoration uses a stable manual receipt ID and its reviewed paid dates.

`deploy/upgrade_paid_subscriptions.py` verifies a SHA-256 manifest bundle and
supports a non-mutating check without `--apply`. Applying requires interactive
sudo. It preserves the existing port, identity links, private receipts and Bot
queue, backs up the old application/env/receipts/code, installs the required
restricted database grants, and starts AccountService before restarting Bot.
It does not restart the game or migrate the game schema. Failed startup rolls
back code/config while retaining all financial receipts; never rewind a live
receipt store. Inspect the exact bundle and command before applying.
