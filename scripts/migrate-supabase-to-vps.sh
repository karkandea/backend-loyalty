#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${ENV_FILE:-.env.vps}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.vps.yml}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
TARGET_DB="loyalty"
TARGET_APP_USER="loyalty_app"
WORK_DIR="$(mktemp -d /tmp/loyalty-migration.XXXXXX)"

cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT
chmod 700 "$WORK_DIR"

for command in docker openssl grep cut awk diff; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "ERROR: required command not found: $command"
    exit 1
  fi
done

if ! docker compose version >/dev/null 2>&1; then
  echo "ERROR: Docker Compose v2 is required (docker compose ...)"
  exit 1
fi

if [[ ! -f "$COMPOSE_FILE" ]]; then
  echo "ERROR: $COMPOSE_FILE not found; run this script from the backend-loyalty repository root"
  exit 1
fi

get_env_value() {
  local key="$1"
  grep -m1 "^${key}=" "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true
}

ensure_secret() {
  local key="$1"
  local bytes="$2"
  local current
  current="$(get_env_value "$key")"

  if [[ -z "$current" ]]; then
    current="$(openssl rand -hex "$bytes")"
    printf '%s=%s\n' "$key" "$current" >> "$ENV_FILE"
  fi

  printf '%s' "$current"
}

if [[ ! -f "$ENV_FILE" ]]; then
  umask 077
  cat > "$ENV_FILE" <<'EOF'
ASPNETCORE_ENVIRONMENT=Production
Jwt__Issuer=backend-loyalty
Jwt__Audience=loyalty-app
Jwt__AccessTokenMinutes=15
Jwt__RefreshTokenDays=7
Cors__AllowedOrigins__0=http://localhost:5173
LOYALTY_API_PORT=5092
EOF
  chmod 600 "$ENV_FILE"
fi

DB_ADMIN_PASSWORD="$(ensure_secret LOYALTY_DB_ADMIN_PASSWORD 32)"
DB_APP_PASSWORD="$(ensure_secret LOYALTY_DB_PASSWORD 32)"
JWT_SIGNING_KEY="$(ensure_secret Jwt__SigningKey 48)"
export LOYALTY_DB_ADMIN_PASSWORD="$DB_ADMIN_PASSWORD"
export LOYALTY_DB_PASSWORD="$DB_APP_PASSWORD"
export Jwt__SigningKey="$JWT_SIGNING_KEY"

printf 'Paste the Supabase PostgreSQL direct/session connection URL. It will not be printed or saved.\n'
read -rsp 'SOURCE_DB_URL: ' SOURCE_DB_URL
echo
if [[ -z "$SOURCE_DB_URL" ]]; then
  echo "ERROR: source database URL is required"
  exit 1
fi

export SOURCE_DB_URL

echo "==> Starting isolated PostgreSQL 17 target (no host port exposed)"
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d db

for attempt in $(seq 1 30); do
  health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$DB_CONTAINER" 2>/dev/null || true)"
  if [[ "$health" == "healthy" ]]; then
    break
  fi
  if [[ "$attempt" -eq 30 ]]; then
    echo "ERROR: target PostgreSQL did not become healthy"
    docker logs --tail 100 "$DB_CONTAINER" || true
    exit 1
  fi
  sleep 2
done

echo "==> Refusing destructive overwrite if a Loyalty DB already exists"
TARGET_EXISTS="$(docker exec "$DB_CONTAINER" psql -U postgres -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '${TARGET_DB}'" || true)"
if [[ "$TARGET_EXISTS" == "1" ]]; then
  echo "ERROR: target database '${TARGET_DB}' already exists. Nothing was changed."
  echo "If this is a failed first migration, inspect it before explicitly dropping/retrying."
  exit 1
fi

echo "==> Verifying source database identity"
docker run --rm \
  -e SOURCE_DB_URL="$SOURCE_DB_URL" \
  postgres:17 \
  sh -ceu '
    found=$(psql "$SOURCE_DB_URL" -Atv ON_ERROR_STOP=1 -c "SELECT count(*) FROM pg_tables WHERE schemaname = '\''public'\'' AND tablename IN ('\''Business'\'', '\''BusinessUser'\'', '\''Member'\'', '\''MemberCard'\'')")
    if [ "$found" -ne 4 ]; then
      echo "ERROR: source does not look like the Loyalty database (expected 4 core tables, found $found)" >&2
      exit 1
    fi
    auth_found=$(psql "$SOURCE_DB_URL" -Atv ON_ERROR_STOP=1 -c "SELECT to_regclass('\''auth.users'\'') IS NOT NULL")
    if [ "$auth_found" != "t" ]; then
      echo "ERROR: auth.users is missing; legacy password bridge cannot be exported" >&2
      exit 1
    fi
  '

echo "==> Creating non-superuser application role and empty target database"
docker exec -i "$DB_CONTAINER" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
  -v "app_password=$DB_APP_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE loyalty_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'loyalty_app')
\gexec
SELECT format('ALTER ROLE loyalty_app PASSWORD %L', :'app_password')
\gexec
CREATE DATABASE loyalty OWNER loyalty_app;
SQL

echo "==> Dumping public application schema + data from Supabase"
docker run --rm \
  -e SOURCE_DB_URL="$SOURCE_DB_URL" \
  -v "$WORK_DIR:/work" \
  postgres:17 \
  sh -ceu '
    pg_dump \
      --dbname="$SOURCE_DB_URL" \
      --format=custom \
      --schema=public \
      --no-owner \
      --no-privileges \
      --file=/work/public.dump
    pg_restore --list /work/public.dump > /work/restore.list
  '

# Supabase RLS policies/ROW SECURITY entries can depend on auth functions and API roles.
# The standalone target is API-only PostgreSQL, so do not carry those runtime policies over.
awk '!/ (POLICY|ROW SECURITY|ACL|DEFAULT ACL) /' "$WORK_DIR/restore.list" > "$WORK_DIR/restore.filtered.list"

echo "==> Exporting only legacy auth IDs + bcrypt hashes (not the Supabase auth schema)"
docker run --rm \
  -e SOURCE_DB_URL="$SOURCE_DB_URL" \
  -v "$WORK_DIR:/work" \
  postgres:17 \
  sh -ceu "psql \"\$SOURCE_DB_URL\" -v ON_ERROR_STOP=1 -c \"COPY (SELECT id::text, encrypted_password FROM auth.users WHERE encrypted_password IS NOT NULL AND length(encrypted_password) > 0) TO STDOUT WITH (FORMAT csv)\" > /work/legacy-auth.csv"

echo "==> Restoring application schema/data into isolated VPS PostgreSQL 17"
docker run --rm \
  --network loyalty-internal \
  -e PGPASSWORD="$DB_APP_PASSWORD" \
  -v "$WORK_DIR:/work" \
  postgres:17 \
  pg_restore \
    --host=loyalty-postgres \
    --port=5432 \
    --username=loyalty_app \
    --dbname=loyalty \
    --no-owner \
    --no-privileges \
    --clean \
    --if-exists \
    --exit-on-error \
    --use-list=/work/restore.filtered.list \
    /work/public.dump

echo "==> Creating minimal app-owned legacy password bridge"
docker exec -e PGPASSWORD="$DB_APP_PASSWORD" -i "$DB_CONTAINER" \
  psql -h 127.0.0.1 -U "$TARGET_APP_USER" -d "$TARGET_DB" -v ON_ERROR_STOP=1 \
  < scripts/create-legacy-auth-bridge.sql

if [[ -s "$WORK_DIR/legacy-auth.csv" ]]; then
  docker exec -e PGPASSWORD="$DB_APP_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$TARGET_APP_USER" -d "$TARGET_DB" -v ON_ERROR_STOP=1 \
    -c 'COPY "LegacyAuthUserPassword" ("authUserId", "passwordHash") FROM STDIN WITH (FORMAT csv)' \
    < "$WORK_DIR/legacy-auth.csv"
fi

echo "==> Comparing core source/target row counts"
CORE_COUNT_SQL='SELECT '\''Business'\'', count(*) FROM "Business"
UNION ALL SELECT '\''BusinessUser'\'', count(*) FROM "BusinessUser"
UNION ALL SELECT '\''AdminUser'\'', count(*) FROM "AdminUser"
UNION ALL SELECT '\''Outlet'\'', count(*) FROM "Outlet"
UNION ALL SELECT '\''Card'\'', count(*) FROM "Card"
UNION ALL SELECT '\''Member'\'', count(*) FROM "Member"
UNION ALL SELECT '\''MemberCard'\'', count(*) FROM "MemberCard"
UNION ALL SELECT '\''MemberReward'\'', count(*) FROM "MemberReward"
UNION ALL SELECT '\''RewardToken'\'', count(*) FROM "RewardToken"
UNION ALL SELECT '\''Transaction'\'', count(*) FROM "Transaction"
UNION ALL SELECT '\''MemberSession'\'', count(*) FROM "MemberSession"
ORDER BY 1;'

docker run --rm -e SOURCE_DB_URL="$SOURCE_DB_URL" -e CORE_COUNT_SQL="$CORE_COUNT_SQL" postgres:17 \
  sh -ceu 'psql "$SOURCE_DB_URL" -At -F "|" -v ON_ERROR_STOP=1 -c "$CORE_COUNT_SQL"' \
  > "$WORK_DIR/source-counts.txt"

docker exec -e PGPASSWORD="$DB_APP_PASSWORD" -e CORE_COUNT_SQL="$CORE_COUNT_SQL" "$DB_CONTAINER" \
  sh -ceu 'psql -h 127.0.0.1 -U loyalty_app -d loyalty -At -F "|" -v ON_ERROR_STOP=1 -c "$CORE_COUNT_SQL"' \
  > "$WORK_DIR/target-counts.txt"

cat "$WORK_DIR/target-counts.txt"
if ! diff -u "$WORK_DIR/source-counts.txt" "$WORK_DIR/target-counts.txt"; then
  echo "ERROR: core row counts differ; do not continue to schema hardening"
  exit 1
fi

SOURCE_AUTH_COUNT="$(docker run --rm -e SOURCE_DB_URL="$SOURCE_DB_URL" postgres:17 sh -ceu 'psql "$SOURCE_DB_URL" -Atv ON_ERROR_STOP=1 -c "SELECT count(*) FROM auth.users WHERE encrypted_password IS NOT NULL AND length(encrypted_password) > 0"')"
TARGET_AUTH_COUNT="$(docker exec -e PGPASSWORD="$DB_APP_PASSWORD" "$DB_CONTAINER" psql -h 127.0.0.1 -U "$TARGET_APP_USER" -d "$TARGET_DB" -Atv ON_ERROR_STOP=1 -c 'SELECT count(*) FROM "LegacyAuthUserPassword"')"

if [[ "$SOURCE_AUTH_COUNT" != "$TARGET_AUTH_COUNT" ]]; then
  echo "ERROR: legacy auth bridge count mismatch: source=$SOURCE_AUTH_COUNT target=$TARGET_AUTH_COUNT"
  exit 1
fi

echo "Legacy password hashes imported: $TARGET_AUTH_COUNT"

echo
echo "==> Running READ-ONLY legacy data audit on restored VPS copy"
docker exec -e PGPASSWORD="$DB_APP_PASSWORD" -i "$DB_CONTAINER" \
  psql -h 127.0.0.1 -U "$TARGET_APP_USER" -d "$TARGET_DB" -v ON_ERROR_STOP=1 \
  < scripts/audit-legacy-data.sql

unset SOURCE_DB_URL

echo
echo "PASS: source data was copied to isolated VPS PostgreSQL 17 and core counts match."
echo "No source data was modified. Supabase auth schema was NOT restored."
echo "Do NOT run harden-target-schema.sql until the audit output has been reviewed."
echo "Server secrets are stored only in $ENV_FILE (mode 600)."
