-- Run in the ACTUAL game database as its owner, after creating a dedicated
-- LOGIN role lagoon_accounts with a generated password (do not commit it).
-- The service does not create schemas or run game migrations.
GRANT USAGE ON SCHEMA public TO lagoon_accounts;
GRANT SELECT ON preference, lagoon_coin_operations TO lagoon_accounts;
GRANT UPDATE (lagoon_coins, lagoon_coin_played_ticks, lagoon_coin_bonus_ticks)
    ON preference TO lagoon_accounts;
GRANT INSERT ON lagoon_coin_operations TO lagoon_accounts;
GRANT SELECT, INSERT, UPDATE ON lagoon_economy_records TO lagoon_accounts;
GRANT SELECT (user_id, first_seen_time, last_seen_user_name) ON player TO lagoon_accounts;
GRANT SELECT (player_id, tracker, time_spent) ON play_time TO lagoon_accounts;
GRANT SELECT, INSERT, DELETE ON whitelist TO lagoon_accounts;
GRANT SELECT ON admin, admin_flag, admin_rank, admin_rank_flag TO lagoon_accounts;
ALTER ROLE lagoon_accounts SET statement_timeout = '8s';
ALTER ROLE lagoon_accounts SET lock_timeout = '5s';
ALTER ROLE lagoon_accounts SET idle_in_transaction_session_timeout = '10s';
-- No profile, Money UPDATE, admin UPDATE, owner, superuser, createdb or DDL grants.
