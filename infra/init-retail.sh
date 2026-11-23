#!/bin/sh
set -eu
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=retail_password="$RETAIL_DB_PASSWORD" <<'SQL'
CREATE ROLE retail_app LOGIN PASSWORD :'retail_password';
REVOKE CONNECT ON DATABASE retail FROM PUBLIC;
ALTER DATABASE retail OWNER TO retail_app;
GRANT USAGE, CREATE ON SCHEMA public TO retail_app;
SQL
