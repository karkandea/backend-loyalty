#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-$ROOT_DIR/.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"
PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-tenant-boundary-${STAMP}.dump"

for command in docker curl; do
  command -v "$command" >/dev/null 2>&1 || { echo "ERROR: missing command: $command" >&2; exit 1; }
done
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
[[ -f scripts/add-tenant-boundary-fks.sql ]] || { echo "ERROR: tenant hardening SQL missing" >&2; exit 1; }
[[ -f scripts/retry-core-loyalty-runtime.sh ]] || { echo "ERROR: core runtime retry missing" >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

diagnostics() {
  local rc=$?
  set +e
  echo >&2
  echo "=== tenant-boundary constraint status ===" >&2
  psql_exec -P pager=off <<'SQL' >&2 || true
SELECT conrelid::regclass::text AS table_name,
       conname,
       contype,
       convalidated
FROM pg_constraint
WHERE conname LIKE '%_tenant_fk'
ORDER BY table_name, conname;
SQL
  echo "FAIL: tenant-boundary hardening stopped with exit code $rc." >&2
  exit "$rc"
}
trap diagnostics ERR

echo "==> [1/5] Checking target database and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
curl --fail --silent "$BASE_URL/health" >/dev/null
curl --fail --silent "$BASE_URL/health/db" >/dev/null

docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/5] Applying composite tenant-boundary foreign keys..."
psql_exec < scripts/add-tenant-boundary-fks.sql

echo "==> [3/5] Verifying all tenant constraints are present and validated..."
EXPECTED=21
TOTAL="$(psql_exec -qAt <<'SQL'
SELECT count(*)
FROM pg_constraint
WHERE conname LIKE '%_tenant_fk';
SQL
)"
VALIDATED="$(psql_exec -qAt <<'SQL'
SELECT count(*)
FROM pg_constraint
WHERE conname LIKE '%_tenant_fk' AND convalidated;
SQL
)"
[[ "$TOTAL" == "$EXPECTED" ]] || { echo "ERROR: expected $EXPECTED tenant FKs, found $TOTAL" >&2; exit 1; }
[[ "$VALIDATED" == "$EXPECTED" ]] || { echo "ERROR: expected $EXPECTED validated tenant FKs, found $VALIDATED" >&2; exit 1; }
echo "PASS: all $EXPECTED tenant-boundary foreign keys are validated."

echo "==> [4/5] Re-running isolated core loyalty runtime against hardened schema..."
ENV_FILE="$ENV_FILE" bash scripts/retry-core-loyalty-runtime.sh

echo "==> [5/5] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo "PASS: tenant-boundary DB hardening deployed and core runtime remains green."
echo "Backup: $BACKUP"
