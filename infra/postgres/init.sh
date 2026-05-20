#!/bin/sh
# Creates per-service databases on first postgres start.
# All databases are owned by the single 'edc' superuser created via POSTGRES_USER.

set -e

for db in cp_a ih_a dp_a cp_b ih_b dp_b issuerservice keycloak; do
  echo "Creating database: $db"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "postgres" <<-SQL
    CREATE DATABASE $db OWNER $POSTGRES_USER;
SQL
done
