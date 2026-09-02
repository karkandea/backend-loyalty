#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.vps.yml}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
API_CONTAINER="${API_CONTAINER:-backend-loyalty}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"

for command in docker curl python3 sha256sum; do
  command -v "$command" >/dev/null 2>&1 || { echo "ERROR: missing command: $command" >&2; exit 1; }
done

docker compose version >/dev/null 2>&1 || { echo "ERROR: Docker Compose v2 is required" >&2; exit 1; }
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-password-lifecycle-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-password-smoke.XXXXXX)"
chmod 700 "$TMP_DIR"

SMOKE_OLD_PASSWORD='SmokeOnly-Refresh-2026!'
SMOKE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
SMOKE_NEW_PASSWORD='SmokeOnly-Password-New-2026!'
SMOKE_RESET_PASSWORD='SmokeOnly-Password-Reset-2026!'
SMOKE_USER_ID="password-smoke-${STAMP}"
SMOKE_EMAIL="password-smoke-${STAMP}@example.invalid"
SMOKE_AUTH_ID=""
SMOKE_BUSINESS_ID=""
FIXTURE_CREATED=0

psql_exec() {
  docker exec \
    -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
    -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

cleanup() {
  local rc=$?
  set +e

  if [[ "$FIXTURE_CREATED" == "1" && -n "$SMOKE_AUTH_ID" ]]; then
    psql_exec -q -v auth_id="$SMOKE_AUTH_ID" -v smoke_id="$SMOKE_USER_ID" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" = :'auth_id';
DELETE FROM "AuthPasswordReset" WHERE "userId" = :'auth_id';
DELETE FROM "BusinessUser" WHERE id = :'smoke_id';
SQL
  fi

  rm -rf "$TMP_DIR"
  exit "$rc"
}
trap cleanup EXIT

diagnostics() {
  local rc=$?
  set +e
  echo >&2
  echo "=== backend-loyalty logs (last 180 lines) ===" >&2
  docker logs --tail 180 "$API_CONTAINER" 2>&1 >&2 || true
  echo >&2
  echo "=== password lifecycle diagnostic snapshot (no raw tokens/passwords) ===" >&2
  if [[ -n "$SMOKE_AUTH_ID" ]]; then
    psql_exec -P pager=off -v auth_id="$SMOKE_AUTH_ID" <<'SQL' >&2 || true
SELECT "authKind", "revokeReason", ("revokedAt" IS NULL) AS active, count(*) AS rows
FROM "AuthRefreshSession"
WHERE "userId" = :'auth_id'
GROUP BY "authKind", "revokeReason", ("revokedAt" IS NULL)
ORDER BY active DESC, "revokeReason" NULLS FIRST;

SELECT
  "authKind",
  ("usedAt" IS NULL) AS active,
  count(*) AS rows,
  min("createdAt") AS oldest,
  max("expiresAt") AS latest_expiry
FROM "AuthPasswordReset"
WHERE "userId" = :'auth_id'
GROUP BY "authKind", ("usedAt" IS NULL)
ORDER BY active DESC;
SQL
  fi
  echo "FAIL: password lifecycle smoke stopped with exit code $rc." >&2
  exit "$rc"
}
trap diagnostics ERR

echo "==> [1/10] Checking database and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null

docker exec \
  -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
  "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/10] Applying auth session + password reset schemas..."
psql_exec < scripts/add-refresh-session-schema.sql
psql_exec < scripts/add-password-reset-schema.sql

SCHEMA_OK="$(psql_exec -qAt <<'SQL'
SELECT
  to_regclass('public."AuthRefreshSession"') IS NOT NULL
  AND to_regclass('public."AuthPasswordReset"') IS NOT NULL;
SQL
)"
[[ "$SCHEMA_OK" == "t" ]] || { echo "ERROR: standalone auth tables missing after schema apply" >&2; exit 1; }

echo "==> [3/10] Building and restarting Loyalty API..."
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api

for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null && curl --fail --silent "$BASE_URL/health/db" >/dev/null; then
    break
  fi
  if [[ "$attempt" -eq 30 ]]; then
    echo "ERROR: API did not become healthy" >&2
    docker logs --tail 150 "$API_CONTAINER" >&2 || true
    exit 1
  fi
  sleep 2
done

echo "==> [4/10] Cleaning stale password smoke fixtures and creating isolated BusinessUser..."
psql_exec -q <<'SQL'
DELETE FROM "AuthRefreshSession"
WHERE "userId" IN (
  SELECT "authUserId"::text FROM "BusinessUser"
  WHERE email LIKE 'password-smoke-%@example.invalid' AND "authUserId" IS NOT NULL
);
DELETE FROM "AuthPasswordReset"
WHERE "userId" IN (
  SELECT "authUserId"::text FROM "BusinessUser"
  WHERE email LIKE 'password-smoke-%@example.invalid' AND "authUserId" IS NOT NULL
);
DELETE FROM "BusinessUser" WHERE email LIKE 'password-smoke-%@example.invalid';
SQL

SMOKE_BUSINESS_ID="$(psql_exec -qAt <<'SQL'
SELECT id
FROM "Business"
WHERE "isActive" = true
ORDER BY "createdAt", id
LIMIT 1;
SQL
)"
[[ -n "$SMOKE_BUSINESS_ID" ]] || { echo "ERROR: no active business available for smoke fixture" >&2; exit 1; }

SMOKE_AUTH_ID="$(psql_exec -qAt \
  -v smoke_id="$SMOKE_USER_ID" \
  -v business_id="$SMOKE_BUSINESS_ID" \
  -v smoke_email="$SMOKE_EMAIL" \
  -v smoke_hash="$SMOKE_BCRYPT" <<'SQL'
INSERT INTO "BusinessUser" (
  id, "businessId", "authUserId", email, "passwordHash", "fullName", role,
  "isActive", "lastLoginAt", "createdAt", "updatedAt"
)
VALUES (
  :'smoke_id', :'business_id', extensions.uuid_generate_v4(), :'smoke_email', :'smoke_hash',
  'Password Lifecycle Smoke Fixture', 'OWNER', true, NULL, now(), now()
)
RETURNING "authUserId"::text;
SQL
)"
[[ -n "$SMOKE_AUTH_ID" ]] || { echo "ERROR: failed to create smoke auth identity" >&2; exit 1; }
FIXTURE_CREATED=1

write_json() {
  local mode="$1" file="$2" value1="${3:-}" value2="${4:-}"
  MODE="$mode" VALUE1="$value1" VALUE2="$value2" python3 - "$file" <<'PY'
import json, os, sys
mode = os.environ["MODE"]
v1 = os.environ.get("VALUE1", "")
v2 = os.environ.get("VALUE2", "")
if mode == "login":
    data = {"email": v1, "password": v2}
elif mode == "update":
    data = {"currentPassword": v1, "newPassword": v2}
elif mode == "forgot":
    data = {"email": v1}
elif mode == "reset":
    data = {"token": v1, "newPassword": v2}
elif mode == "refresh":
    data = {"refreshToken": v1}
else:
    raise SystemExit("unknown JSON mode")
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump(data, f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1" body="$2" output="$3" bearer="${4:-}"
  if [[ -n "$bearer" ]]; then
    curl --silent --show-error -o "$output" -w '%{http_code}' \
      -H 'Content-Type: application/json' \
      -H "Authorization: Bearer $bearer" \
      --data-binary "@$body" \
      "$BASE_URL$endpoint"
  else
    curl --silent --show-error -o "$output" -w '%{http_code}' \
      -H 'Content-Type: application/json' \
      --data-binary "@$body" \
      "$BASE_URL$endpoint"
  fi
}

extract_field() {
  local file="$1" field="$2"
  python3 - "$file" "$field" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
value = (data.get("data") or {}).get(sys.argv[2])
if not value:
    raise SystemExit(f"{sys.argv[2]} missing from successful response")
print(value)
PY
}

login() {
  local password="$1" prefix="$2"
  local body="$TMP_DIR/${prefix}-login.request.json"
  local output="$TMP_DIR/${prefix}-login.response.json"
  local status
  write_json login "$body" "$SMOKE_EMAIL" "$password"
  status="$(post_file '/api/business/auth/login' "$body" "$output")"
  printf '%s|%s' "$status" "$output"
}

echo "==> [5/10] Testing authenticated update-password + refresh revocation..."
LOGIN_RESULT="$(login "$SMOKE_OLD_PASSWORD" 'old')"
LOGIN_STATUS="${LOGIN_RESULT%%|*}"
LOGIN_FILE="${LOGIN_RESULT#*|}"
[[ "$LOGIN_STATUS" == "200" ]] || { echo "ERROR: initial business login expected 200, got $LOGIN_STATUS" >&2; cat "$LOGIN_FILE" >&2; exit 1; }
ACCESS_OLD="$(extract_field "$LOGIN_FILE" accessToken)"
REFRESH_OLD="$(extract_field "$LOGIN_FILE" refreshToken)"

UPDATE_BODY="$TMP_DIR/update.request.json"
UPDATE_OUT="$TMP_DIR/update.response.json"
write_json update "$UPDATE_BODY" "$SMOKE_OLD_PASSWORD" "$SMOKE_NEW_PASSWORD"
STATUS="$(post_file '/api/business/auth/update-password' "$UPDATE_BODY" "$UPDATE_OUT" "$ACCESS_OLD")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: update-password expected 200, got $STATUS" >&2; cat "$UPDATE_OUT" >&2; exit 1; }

REFRESH_BODY="$TMP_DIR/old-refresh.request.json"
REFRESH_OUT="$TMP_DIR/old-refresh.response.json"
write_json refresh "$REFRESH_BODY" "$REFRESH_OLD"
STATUS="$(post_file '/api/auth/refresh' "$REFRESH_BODY" "$REFRESH_OUT")"
[[ "$STATUS" == "401" ]] || { echo "ERROR: pre-change refresh token should be revoked; got $STATUS" >&2; cat "$REFRESH_OUT" >&2; exit 1; }

LOGIN_RESULT="$(login "$SMOKE_OLD_PASSWORD" 'old-after-update')"
[[ "${LOGIN_RESULT%%|*}" == "401" ]] || { echo "ERROR: old password should fail after update" >&2; cat "${LOGIN_RESULT#*|}" >&2; exit 1; }
LOGIN_RESULT="$(login "$SMOKE_NEW_PASSWORD" 'new-after-update')"
[[ "${LOGIN_RESULT%%|*}" == "200" ]] || { echo "ERROR: new password should login after update" >&2; cat "${LOGIN_RESULT#*|}" >&2; exit 1; }
LOGIN_NEW_FILE="${LOGIN_RESULT#*|}"
REFRESH_BEFORE_RESET="$(extract_field "$LOGIN_NEW_FILE" refreshToken)"
echo "PASS: authenticated password update replaced bcrypt and revoked prior refresh sessions."

echo "==> [6/10] Testing forgot-password anti-enumeration + cooldown..."
UNKNOWN_BODY="$TMP_DIR/forgot-unknown.request.json"
UNKNOWN_OUT="$TMP_DIR/forgot-unknown.response.json"
write_json forgot "$UNKNOWN_BODY" "missing-${STAMP}@example.invalid"
STATUS="$(post_file '/api/business/auth/forgot-password' "$UNKNOWN_BODY" "$UNKNOWN_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: unknown forgot-password must return generic 200, got $STATUS" >&2; cat "$UNKNOWN_OUT" >&2; exit 1; }

FORGOT_BODY="$TMP_DIR/forgot-known.request.json"
FORGOT_OUT="$TMP_DIR/forgot-known.response.json"
write_json forgot "$FORGOT_BODY" "$SMOKE_EMAIL"
STATUS="$(post_file '/api/business/auth/forgot-password' "$FORGOT_BODY" "$FORGOT_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: known forgot-password expected 200, got $STATUS" >&2; cat "$FORGOT_OUT" >&2; exit 1; }

RESET_COUNT="$(psql_exec -qAt -v auth_id="$SMOKE_AUTH_ID" <<'SQL'
SELECT count(*) FROM "AuthPasswordReset"
WHERE "userId" = :'auth_id' AND "authKind" = 'business' AND "usedAt" IS NULL;
SQL
)"
[[ "$RESET_COUNT" == "1" ]] || { echo "ERROR: forgot-password should create exactly 1 active reset row, found $RESET_COUNT" >&2; exit 1; }

STATUS="$(post_file '/api/business/auth/forgot-password' "$FORGOT_BODY" "$TMP_DIR/forgot-known-repeat.response.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: repeated forgot-password expected generic 200, got $STATUS" >&2; exit 1; }
RESET_COUNT="$(psql_exec -qAt -v auth_id="$SMOKE_AUTH_ID" <<'SQL'
SELECT count(*) FROM "AuthPasswordReset"
WHERE "userId" = :'auth_id' AND "authKind" = 'business' AND "usedAt" IS NULL;
SQL
)"
[[ "$RESET_COUNT" == "1" ]] || { echo "ERROR: reset cooldown should prevent duplicate active token, found $RESET_COUNT" >&2; exit 1; }
echo "PASS: forgot-password preserves anti-enumeration and reset cooldown."

echo "==> [7/10] Testing reset-password single-use + refresh revocation..."
psql_exec -q -v auth_id="$SMOKE_AUTH_ID" <<'SQL'
DELETE FROM "AuthPasswordReset" WHERE "userId" = :'auth_id';
SQL
RAW_RESET_TOKEN="SmokeOnly-Reset-${STAMP}-$(python3 - <<'PY'
import secrets
print(secrets.token_hex(12))
PY
)"
RESET_HASH="$(printf '%s' "$RAW_RESET_TOKEN" | sha256sum | awk '{print $1}')"
psql_exec -q \
  -v auth_id="$SMOKE_AUTH_ID" \
  -v smoke_email="$SMOKE_EMAIL" \
  -v reset_hash="$RESET_HASH" <<'SQL'
INSERT INTO "AuthPasswordReset" (
  id, "userId", "authKind", email, "tokenHash", "expiresAt", "usedAt", ip, "userAgent", "createdAt"
)
VALUES (
  extensions.uuid_generate_v4(), :'auth_id', 'business', :'smoke_email', :'reset_hash',
  now() + interval '10 minutes', NULL, NULL, 'password-lifecycle-smoke', now()
);
SQL

RESET_BODY="$TMP_DIR/reset.request.json"
RESET_OUT="$TMP_DIR/reset.response.json"
write_json reset "$RESET_BODY" "$RAW_RESET_TOKEN" "$SMOKE_RESET_PASSWORD"
STATUS="$(post_file '/api/business/auth/reset-password' "$RESET_BODY" "$RESET_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: reset-password expected 200, got $STATUS" >&2; cat "$RESET_OUT" >&2; exit 1; }

STATUS="$(post_file '/api/business/auth/reset-password' "$RESET_BODY" "$TMP_DIR/reset-replay.response.json")"
[[ "$STATUS" == "400" ]] || { echo "ERROR: reset token replay expected 400, got $STATUS" >&2; cat "$TMP_DIR/reset-replay.response.json" >&2; exit 1; }

write_json refresh "$TMP_DIR/pre-reset-refresh.request.json" "$REFRESH_BEFORE_RESET"
STATUS="$(post_file '/api/auth/refresh' "$TMP_DIR/pre-reset-refresh.request.json" "$TMP_DIR/pre-reset-refresh.response.json")"
[[ "$STATUS" == "401" ]] || { echo "ERROR: pre-reset refresh token should be revoked; got $STATUS" >&2; cat "$TMP_DIR/pre-reset-refresh.response.json" >&2; exit 1; }

LOGIN_RESULT="$(login "$SMOKE_NEW_PASSWORD" 'pre-reset-password-after-reset')"
[[ "${LOGIN_RESULT%%|*}" == "401" ]] || { echo "ERROR: pre-reset password should fail after reset" >&2; cat "${LOGIN_RESULT#*|}" >&2; exit 1; }
LOGIN_RESULT="$(login "$SMOKE_RESET_PASSWORD" 'reset-password-final')"
[[ "${LOGIN_RESULT%%|*}" == "200" ]] || { echo "ERROR: reset password should login successfully" >&2; cat "${LOGIN_RESULT#*|}" >&2; exit 1; }
echo "PASS: reset token is single-use, password changed, and prior refresh sessions were revoked."

echo "==> [8/10] Verifying only reset-token hashes are persisted..."
BAD_RESET_HASH_ROWS="$(psql_exec -qAt -v auth_id="$SMOKE_AUTH_ID" <<'SQL'
SELECT count(*) FROM "AuthPasswordReset"
WHERE "userId" = :'auth_id'
  AND (length("tokenHash") <> 64 OR "tokenHash" LIKE 'SmokeOnly-%');
SQL
)"
[[ "$BAD_RESET_HASH_ROWS" == "0" ]] || { echo "ERROR: reset storage contains unexpected token representation" >&2; exit 1; }

echo "==> [9/10] Checking bridge adoption cleanup behavior..."
BRIDGE_ROWS="$(psql_exec -qAt -v auth_id="$SMOKE_AUTH_ID" <<'SQL'
SELECT count(*) FROM "LegacyAuthUserPassword" WHERE "authUserId" = :'auth_id';
SQL
)"
[[ "$BRIDGE_ROWS" == "0" ]] || { echo "ERROR: local password lifecycle should not leave a legacy bridge row" >&2; exit 1; }

echo "==> [10/10] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"
echo
curl --fail --show-error --silent "$BASE_URL/health/db"
echo

trap - ERR

echo
echo "PASS: standalone business password lifecycle deployed and smoke-tested."
echo "Backup: $BACKUP"
