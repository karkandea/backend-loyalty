#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"
API_CONTAINER="${API_CONTAINER:-backend-loyalty}"

[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

psql_exec() {
  docker exec \
    -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
    -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

echo "==> Retrying standalone signup/invitation lifecycle..."
set +e
bash scripts/deploy-signup-invitation-lifecycle.sh
rc=$?
set -e

if [[ "$rc" -eq 0 ]]; then
  echo "PASS: diagnostic retry completed successfully."
  exit 0
fi

echo >&2
echo "=== backend-loyalty exception lines ===" >&2
docker logs --tail 300 "$API_CONTAINER" 2>&1 \
  | grep -E -A18 -B8 'DbUpdateException|PostgresException|Standalone owner signup|fail:|Exception|SQLSTATE|constraint' >&2 \
  || docker logs --tail 120 "$API_CONTAINER" 2>&1 >&2 \
  || true

echo >&2
echo "=== target Business / BusinessInvitation column contract ===" >&2
psql_exec -P pager=off <<'SQL' >&2 || true
SELECT table_name, ordinal_position, column_name, data_type, udt_name, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN ('Business', 'BusinessInvitation')
ORDER BY table_name, ordinal_position;

SELECT
  conrelid::regclass::text AS table_name,
  conname,
  contype,
  pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE conrelid IN ('"Business"'::regclass, '"BusinessInvitation"'::regclass)
ORDER BY table_name, conname;
SQL

echo >&2
echo "=== leftover smoke rows after automatic cleanup ===" >&2
psql_exec -P pager=off <<'SQL' >&2 || true
SELECT id, slug, "isActive", "createdAt"
FROM "Business"
WHERE slug LIKE 'auth-invite-smoke-%'
ORDER BY "createdAt" DESC;

SELECT bi.id, bi.email, bi.role, length(bi."tokenHash") AS token_hash_len,
       bi."usedAt" IS NOT NULL AS used, bi."revokedAt" IS NOT NULL AS revoked
FROM "BusinessInvitation" bi
JOIN "Business" b ON b.id = bi."businessId"
WHERE b.slug LIKE 'auth-invite-smoke-%'
ORDER BY bi."createdAt" DESC;
SQL

echo "FAIL: signup/invitation lifecycle retry stopped with exit code $rc." >&2
exit "$rc"
