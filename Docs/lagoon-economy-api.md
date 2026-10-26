# Lagoon economy API, contract v1

Ownership: Station implements game, database and `Tools/DeepLagoon.AccountService`.
The Discord chat implements only `DeepLagoon_Bot`. Do not store a second LC balance.
This contract is in implementation; the acceptance tests must pass before deployment.

## Transport and identities

All routes below are POST `/deeplagoon/discord/<action>` on the existing independent
AccountService, loopback-only, using the **coin** Bearer token. JSON fields use snake_case.
Required common fields: `discord_id` (recipient), `actor_discord_id` (requesting member),
`request_id` (original Discord interaction snowflake). Each identity must be linked.
The bot checks the configured guild and permissions before making a request.
Staff operations additionally require `host_authorized: true`, meaning the bot verified
the existing LC-management role `1554164947804487770`; this is not an assertion from a user option.
Self-service requests require actor == recipient unless staff-authorized.

`external_tier` is consumed ONLY by `economy_supporter`, never by financial retries or reads. It is 0..4 and computed by the trusted bot from freshly fetched Boosty roles and native booster status (active boost provides at least tier 2):
0 none; 1 Spark; 2 Impulse; 3 Shine; 4 Supernova. Never expose it as a caller-editable command option.
The game supporter API retains `boosty_tier: 0..3` for Impulse/Shine/Supernova and adds
`spark: boolean`. The three existing role IDs retain their existing game tiers.

An interaction ID is durably bound to its original action, body and canonical identities.
Retries must use the **same** ID and body after timeouts. Task/payment IDs also persist
across *new* interactions: a new click does not create a new payment or task completion.

## Actions

| Action | Additional fields | Permission / meaning |
|---|---|---|
| `economy_state` | None | Self; balance, purchases, LC-period and referral state |
| `economy_pass` | `tier` 1..4 | Self; buy/renew 30 days, or prorated upgrade until current expiry |
| `economy_color` | `color` as `#RRGGBB` | Self; requires Spark or higher |
| `economy_referral` | `referral_code` | Self; bind once while eligible |
| `economy_supporters` | None | Self; permanent list of confirmed paid supporters |
| `economy_task_create` | `task_id`, `category`, `amount`, `reason` | Staff; declare a fixed recipient and agreed work |
| `economy_task_approve` | `task_id` | Staff; actor must differ from executor; repeat does not pay twice |
| `economy_payment` | `receipt`, `tier`, `paid_at` ISO-8601 UTC, `historical` | Staff; verified actual paid period only |
| `economy_onboarding` | `newcomer_discord_id`, `category`, `reason` | Staff; category interview/training/return; recipient is helper |

Payment `receipt` is a global stable platform payment/period identifier, e.g.
`boosty:original-period-id`. It is **not** a newly generated interaction ID. The service
does not verify Boosty payment data itself. Only a trusted reviewer may confirm a real
receipt. Historical imports add credits history but give **zero** LC. Paid confirmation
does not fabricate a live Boosty role or an LC-period. Never confirm payment from a role-sync event.

Task categories: `admin-duty` 40; `admin-case` 40..120;
`code-tiny` 50..100, `code-small` 150..300, `code-medium` 400..800, `code-large` 1000..2000;
`map-room` 100..200, `map-apartment` 250..500, `map-poi` 600..1200, `map-large` 1500..3000;
`sprite-icon` 40..80, `sprite-item` 120..250, `sprite-set` 300..600, `sprite-large` 800..1500.
Onboarding uses its dedicated operation so one novice/stage cannot be paid via new task IDs.
One onboarding stage currently has one paid executor; split a chain across helpers by stages.

## Responses and errors

Normal response: `uid`, `request_id`, `balance`, `applied`, `awarded`, `state`.
`applied`/`awarded` are meaningful for task/payment/onboarding grants and may be null for
state/purchase actions. Replayed purchases return current authoritative state and balance.
`state` uses camelCase: `passTier`, `passTicks`, `passAccountedAt`, `externalTier`,
`furniture`, `apartmentCredit`, `apartments`, `oocColor`, `referralCode`, `referralStage`.
Ticks use .NET ticks (10,000,000 per second). LC duration is paused while an equal/higher
external entitlement overlaps it. Do not create a second timer in the bot; request fresh state.
Supporters response: `request_id`, `names` (display names only).

Errors: 401 unauthorized token; 403 `forbidden`/`actor_not_linked`; 404 `not_linked` or
unknown route; 400 `invalid_request`; 409 `insufficient_lc`, `economy_conflict`,
or identity/receipt conflicts from existing binding; 503 `unavailable`.
For 503/timeouts the result may already be committed: replay the original operation,
never compensate with a fresh `/lc_add`. Discord delivery failure never rolls back a grant.

## Independent processes and deployment

Keep Discord and AccountService as separate processes/services. No Requires, PartOf,
BindsTo or startup wait for Discord Gateway. The existing account service has no ECS,
game-status HTTP request or Discord Gateway dependency. Game and Discord restarts do
not remove its receipts or LC records. Use bounded per-request deadlines and concurrency.
The Discord process owns its delivery queue only, not financial truth.

Run game DB migration `20261026010000_LagoonEconomy` as the game DB owner, then apply the
updated `Tools/DeepLagoon.AccountService/deploy/grants.sql`. Back up the game DB, identity
store, receipt DB and apartment layouts. The narrow API login does not run migrations.
Source and executable checks are separate from production deployment proof.

Configured Boosty roles:
Spark `1564094723646427307`; Impulse `1557387840906858557`;
Shine `1557387960146468874`; Supernova `1557388057836261477`.
Separate ordinary LC-access/color role IDs remain deployment settings, never inferred.

## Fresh external lease and status lookup

`economy_supporter`: common identities plus `external_tier` and `observed_at` UTC ISO-8601.
The bot must fetch Discord membership freshly, serialize sync per member, and send a current
observation (at most 2 minutes old; at most 30 seconds in the future). The server stores a
15-minute lease ending at observed_at + 15 minutes. Older/equal observations do not overwrite
newer ones. This non-financial heartbeat intentionally does not bind a financial receipt.
Financial replay never refreshes a lease. Send sync to AccountService independently of the
game supporter sync; a stopped game does not prevent the independent API from receiving it.

`economy_status`: common fields and exactly one of `task_id`, `receipt`, `operation_id`.
Read-only. Returns `id`, `status` (not_found/declared/committed) and original `record` for
tasks/payments, or amount/balance_after/reason/actor/at for ledger operations. Record fields
inside the original persisted JSON retain PascalCase; the normal `state` response uses
camelCase through ASP.NET JSON. Use `spend:discord:<original request_id>` for LC purchases.
Original financial receipts must be retained across new Discord interactions/restarts.

Health: `/health/live` reports process liveness and economy_api_version 1 without DB access.
`/health/ready` and legacy `/health` check schema/identity stores with a 3-second deadline.
Readiness has a separate one-slot gate; financial HTTP requests have a 16-slot fail-fast gate
(429 busy) and a 10-second deadline. Starting the process does not wait for DB or Discord.

The read-only identity WAL lease opens lazily on readiness or the first identity lookup.
A successful readiness check establishes it before the last game writer stops.
An omitted/default payment date is invalid; status lookup requires exactly one selector.

Dates accept ISO-8601 offsets (including Python's +00:00) and are normalized to UTC
through DateTimeOffset before validation and persistence. externalExpiresAt is the
optional UTC expiry of the external observation that paused an LC period; elapsed
time after expiry is charged even when no request was made during the gap.

Cross-repository test: set ACCOUNT_TEST_BOT_PATH to DeepLagoon_Bot and
ACCOUNT_TEST_PYTHON to its configured Python executable, then run the AccountService
test project. bot_contract_probe.py imports the real EconomyApi, AccountDispatcher,
OperationQueue and LagoonCoinApi and calls the actual temporary AccountService.

An active native Discord server boost (fresh Member.premium_since, not a reward role)
grants the Impulse effective level (product tier 2) while active. Higher Boosty/LC
tiers take precedence. It grants 4 LC/hour, 5% decor discount, OOC/Discord color and
Impulse feature/loadout access. A purchased Spark/Impulse LC period pauses during
the boost; higher LC periods keep running. Events and periodic reconciliation revoke
the grant when boosting ends; the existing 15-minute lease bounds an unavailable Bot.
Boost synchronization creates no paid Boosty receipt, paid-period gift or permanent
paid-supporter credit and never assigns a Boosty input role.
