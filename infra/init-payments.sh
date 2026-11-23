#!/bin/sh
set -eu

psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname postgres \
  --set=simulator_password="$SIMULATOR_DB_PASSWORD" <<'SQL'
SELECT pg_advisory_lock(713804592);
SELECT format('CREATE ROLE payment_simulator_app LOGIN PASSWORD %L', :'simulator_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payment_simulator_app') \gexec
ALTER ROLE payment_simulator_app PASSWORD :'simulator_password';
SELECT 'CREATE DATABASE payment_simulator'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'payment_simulator') \gexec
REVOKE CONNECT ON DATABASE payment_simulator FROM PUBLIC;
GRANT CONNECT ON DATABASE payment_simulator TO payment_simulator_app;
REVOKE CONNECT ON DATABASE retail FROM payment_simulator_app;
\connect payment_simulator
BEGIN;
SELECT pg_advisory_xact_lock(713804592);
CREATE TABLE IF NOT EXISTS payment_authorizations (
    operation_id uuid PRIMARY KEY,
    amount_minor bigint NOT NULL CHECK (amount_minor >= 0),
    currency varchar(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    decision text NOT NULL CHECK (decision IN ('approved', 'declined')),
    decided_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
GRANT USAGE ON SCHEMA public TO payment_simulator_app;
GRANT SELECT, INSERT ON TABLE payment_authorizations TO payment_simulator_app;
COMMIT;
SQL
