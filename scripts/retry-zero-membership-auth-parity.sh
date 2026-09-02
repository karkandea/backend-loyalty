#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"

[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
[[ -f scripts/create-legacy-auth-bridge.sql ]] || { echo 'ERROR: create-legacy-auth-bridge.sql missing' >&2; exit 1; }
[[ -f scripts/run-zero-membership-auth-parity.sh ]] || { echo 'ERROR: zero-membership runner missing' >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

TARGET_PSQL=(
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER"
  psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 -P pager=off
)

echo '==> Preflight: upgrading app-owned legacy auth bridge schema...'
"${TARGET_PSQL[@]}" < scripts/create-legacy-auth-bridge.sql

EMAIL_COLUMN="$("${TARGET_PSQL[@]}" -qAt <<'SQL'
SELECT count(*)
FROM information_schema.columns
WHERE table_schema='public'
  AND table_name='LegacyAuthUserPassword'
  AND column_name='email';
SQL
)"
[[ "$EMAIL_COLUMN" == '1' ]] || { echo 'ERROR: LegacyAuthUserPassword.email is still missing' >&2; exit 1; }

echo 'PASS: LegacyAuthUserPassword.email exists; retrying zero-membership auth parity.'
exec bash scripts/run-zero-membership-auth-parity.sh
