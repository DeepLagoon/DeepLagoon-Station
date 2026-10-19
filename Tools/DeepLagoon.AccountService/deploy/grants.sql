-- Run in the ACTUAL game database as its owner, after creating a dedicated
-- LOGIN role lagoon_accounts with a generated password (do not commit it).
-- The service does not create schemas or run game migrations.
GRANT USAGE ON SCHEMA public TO lagoon_accounts;
GRANT SELECT ON preference, lagoon_coin_operations TO lagoon_accounts;
GRANT UPDATE (lagoon_coins, lagoon_coin_played_ticks, lagoon_coin_bonus_ticks)
    ON preference TO lagoon_accounts;
GRANT INSERT ON lagoon_coin_operations TO lagoon_accounts;
GRANT SELECT, INSERT, DELETE ON whitelist TO lagoon_accounts;
GRANT SELECT (user_id) ON admin TO lagoon_accounts;
ALTER ROLE lagoon_accounts SET statement_timeout = '8s';
ALTER ROLE lagoon_accounts SET lock_timeout = '5s';
ALTER ROLE lagoon_accounts SET idle_in_transaction_session_timeout = '10s';
-- No profile, Money UPDATE, admin UPDATE, owner, superuser, createdb or DDL grants.
