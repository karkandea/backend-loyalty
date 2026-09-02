#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
API_CONTAINER="${API_CONTAINER:-backend-loyalty}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"

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

diagnostics() {
  set +e
  echo
  echo "=== backend-loyalty logs (last 180 lines) ===" >&2
  docker logs --tail 180 "$API_CONTAINER" 2>&1 >&2 || true

  echo >&2
  echo "=== refresh-session diagnostic snapshot (no raw tokens) ===" >&2
  psql_exec -P pager=off <<'SQL' >&2 || true
SELECT
  "authKind",
  "revokeReason",
  ("revokedAt" IS NULL) AS active,
  count(*) AS rows
FROM "AuthRefreshSession"
GROUP BY "authKind", "revokeReason", ("revokedAt" IS NULL)
ORDER BY "authKind", active DESC, "revokeReason" NULLS FIRST;

SELECT
  id,
  "authKind",
  left("tokenHash", 10) || '...' AS token_hash_prefix,
  "familyId",
  "parentSessionId",
  "replacedBySessionId",
  "revokeReason",
  "createdAt",
  "expiresAt",
  "revokedAt"
FROM "AuthRefreshSession"
ORDER BY "createdAt" DESC
LIMIT 8;
SQL
}

trap diagnostics ERR

echo "==> Removing obsolete replacement FK if an earlier partial attempt created it..."
psql_exec <<'SQL'
DO $$
BEGIN
  IF to_regclass('public."AuthRefreshSession"') IS NOT NULL THEN
    ALTER TABLE "AuthRefreshSession"
      DROP CONSTRAINT IF EXISTS "AuthRefreshSession_replaced_fkey";
  END IF;
END $$;
SQL

echo "==> Running refresh hardening deployment + end-to-end smoke test..."
bash scripts/deploy-auth-refresh-hardening.sh

trap - ERR

echo
echo "PASS: diagnostic retry completed successfully."
