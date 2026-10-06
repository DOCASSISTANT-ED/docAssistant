#!/bin/bash
# Uygulamanın bağlandığı kısıtlı kullanıcıyı oluşturur (docs/decisions.md #19).
# Süper kullanıcı ve tablo sahibi RLS'ten muaf olduğu için uygulama POSTGRES_USER ile bağlanmaz.
# Tablo yetkileri ve RLS politikaları burada değil, migration'larda verilir.
set -e

: "${APP_DB_PASSWORD:?APP_DB_PASSWORD is not set; add it to .env (see .env.example)}"

psql -v ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname "$POSTGRES_DB" \
    -v app_password="$APP_DB_PASSWORD" <<'EOSQL'
CREATE ROLE docassistant_app LOGIN PASSWORD :'app_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
EOSQL
