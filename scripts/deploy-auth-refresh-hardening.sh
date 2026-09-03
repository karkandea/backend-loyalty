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

for command in docker curl python3; do
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
BACKUP="/root/loyalty-pre-refresh-hardening-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-refresh-smoke.XXXXXX)"
chmod 700 "$TMP_DIR"

# This password/hash pair exists only for the ephemeral smoke AdminUser below.
# The fixture uses @example.invalid and is deleted by the EXIT trap.
SMOKE_PASSWORD='SmokeOnly-Refresh-2026!'
SMOKE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
SMOKE_USER_ID="refresh-smoke-${STAMP}"
SMOKE_EMAIL="refresh-smoke-${STAMP}@example.invalid"
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

  if [[ "$FIXTURE_CREATED" == "1" ]]; then
    psql_exec -q -v smoke_id="$SMOKE_USER_ID" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" = :'smoke_id';
DELETE FROM "AdminUser" WHERE id = :'smoke_id';
SQL
  fi

  rm -rf "$TMP_DIR"
  exit "$rc"
}
trap cleanup EXIT

echo "==> [1/9] Checking current database and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null

docker exec \
  -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
  "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/9] Applying persistent refresh-session schema..."
psql_exec < scripts/add-refresh-session-schema.sql

TABLE_OK="$(psql_exec -qAt <<'SQL'
SELECT to_regclass('public."AuthRefreshSession"') IS NOT NULL;
SQL
)"
[[ "$TABLE_OK" == "t" ]] || { echo "ERROR: AuthRefreshSession table missing after schema apply" >&2; exit 1; }

echo "==> [3/9] Building and restarting Loyalty API..."
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

echo "==> [4/9] Cleaning stale smoke fixtures and creating isolated admin fixture..."
psql_exec -q <<'SQL'
DELETE FROM "AuthRefreshSession"
WHERE "userId" IN (
  SELECT id FROM "AdminUser" WHERE email LIKE 'refresh-smoke-%@example.invalid'
);
DELETE FROM "AdminUser" WHERE email LIKE 'refresh-smoke-%@example.invalid';
SQL

TARGET_ROW="$(psql_exec -qAtF '|' <<'SQL'
SELECT o."businessId", o.id
FROM "Outlet" o
JOIN "Business" b ON b.id = o."businessId"
WHERE o."isActive" = true
  AND b."isActive" = true
ORDER BY o."createdAt", o.id
LIMIT 1;
SQL
)"
IFS='|' read -r SMOKE_BUSINESS_ID SMOKE_OUTLET_ID <<< "$TARGET_ROW"
[[ -n "$SMOKE_BUSINESS_ID" && -n "$SMOKE_OUTLET_ID" ]] || {
  echo "ERROR: no active business/outlet pair available for smoke fixture" >&2
  exit 1
}

psql_exec -q \
  -v smoke_id="$SMOKE_USER_ID" \
  -v business_id="$SMOKE_BUSINESS_ID" \
  -v outlet_id="$SMOKE_OUTLET_ID" \
  -v smoke_email="$SMOKE_EMAIL" \
  -v smoke_hash="$SMOKE_BCRYPT" <<'SQL'
INSERT INTO "AdminUser" (
  id, "businessId", "outletId", email, "passwordHash", "fullName", role,
  "isActive", "lastLoginAt", "createdAt", "updatedAt"
)
VALUES (
  :'smoke_id', :'business_id', :'outlet_id', :'smoke_email', :'smoke_hash',
  'Refresh Smoke Fixture', 'STAFF', true, NULL, now(), now()
);
SQL
FIXTURE_CREATED=1

write_json() {
  local mode="$1" file="$2" value1="$3" value2="${4:-}"
  MODE="$mode" VALUE1="$value1" VALUE2="$value2" python3 - "$file" <<'PY'
import json, os, sys
mode = os.environ["MODE"]
if mode == "login":
    data = {"email": os.environ["VALUE1"], "password": os.environ["VALUE2"]}
elif mode == "refresh":
    data = {"refreshToken": os.environ["VALUE1"]}
else:
    raise SystemExit("unknown JSON mode")
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump(data, f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1" body="$2" output="$3"
  curl --silent --show-error -o "$output" -w '%{http_code}' \
    -H 'Content-Type: application/json' \
    --data-binary "@$body" \
    "$BASE_URL$endpoint"
}

extract_refresh_token() {
  python3 - "$1" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
value = ((data.get("data") or {}).get("refreshToken"))
if not value:
    raise SystemExit("refreshToken missing from successful response")
print(value)
PY
}

login_fixture() {
  local prefix="$1"
  local body="$TMP_DIR/${prefix}-login.request.json"
  local output="$TMP_DIR/${prefix}-login.response.json"
  local status
  write_json login "$body" "$SMOKE_EMAIL" "$SMOKE_PASSWORD"
  status="$(post_file '/api/admin/auth/login' "$body" "$output")"
  if [[ "$status" != "200" ]]; then
    echo "ERROR: smoke admin login expected 200, got $status" >&2
    cat "$output" >&2
    return 1
  fi
  extract_refresh_token "$output"
}

request_refresh_endpoint() {
  local endpoint="$1" token="$2" output="$3"
  local body="${output}.request.json"
  write_json refresh "$body" "$token"
  post_file "$endpoint" "$body" "$output"
}

echo "==> [5/9] Testing real admin login + persisted refresh registration..."
TOKEN_ONE="$(login_fixture 'rotation')"
SESSION_COUNT="$(psql_exec -qAt -v smoke_id="$SMOKE_USER_ID" <<'SQL'
SELECT count(*) FROM "AuthRefreshSession"
WHERE "userId" = :'smoke_id'
  AND "authKind" = 'admin'
  AND "revokedAt" IS NULL;
SQL
)"
[[ "$SESSION_COUNT" == "1" ]] || {
  echo "ERROR: login should register exactly 1 active refresh session, found $SESSION_COUNT" >&2
  exit 1
}
echo "PASS: real admin login registered a persisted refresh session."

echo "==> [6/9] Testing rotation + replay-family revocation..."
STATUS="$(request_refresh_endpoint '/api/auth/refresh' "$TOKEN_ONE" "$TMP_DIR/refresh1.json")"
[[ "$STATUS" == "200" ]] || {
  echo "ERROR: first refresh expected 200, got $STATUS" >&2
  cat "$TMP_DIR/refresh1.json" >&2
  exit 1
}
TOKEN_ONE_NEXT="$(extract_refresh_token "$TMP_DIR/refresh1.json")"

STATUS="$(request_refresh_endpoint '/api/auth/refresh' "$TOKEN_ONE" "$TMP_DIR/replay-old.json")"
[[ "$STATUS" == "401" ]] || {
  echo "ERROR: replayed old refresh expected 401, got $STATUS" >&2
  cat "$TMP_DIR/replay-old.json" >&2
  exit 1
}

STATUS="$(request_refresh_endpoint '/api/auth/refresh' "$TOKEN_ONE_NEXT" "$TMP_DIR/family-revoked.json")"
[[ "$STATUS" == "401" ]] || {
  echo "ERROR: refresh family should be revoked after replay; expected 401, got $STATUS" >&2
  cat "$TMP_DIR/family-revoked.json" >&2
  exit 1
}
echo "PASS: replay detection revoked the replacement token family."

echo "==> [7/9] Testing fresh login -> rotate -> logout -> reject..."
TOKEN_TWO="$(login_fixture 'logout')"
STATUS="$(request_refresh_endpoint '/api/auth/refresh' "$TOKEN_TWO" "$TMP_DIR/refresh2.json")"
[[ "$STATUS" == "200" ]] || {
  echo "ERROR: second refresh expected 200, got $STATUS" >&2
  cat "$TMP_DIR/refresh2.json" >&2
  exit 1
}
TOKEN_TWO_NEXT="$(extract_refresh_token "$TMP_DIR/refresh2.json")"

STATUS="$(request_refresh_endpoint '/api/auth/logout' "$TOKEN_TWO_NEXT" "$TMP_DIR/logout.json")"
[[ "$STATUS" == "200" ]] || {
  echo "ERROR: logout expected 200, got $STATUS" >&2
  cat "$TMP_DIR/logout.json" >&2
  exit 1
}

STATUS="$(request_refresh_endpoint '/api/auth/refresh' "$TOKEN_TWO_NEXT" "$TMP_DIR/after-logout.json")"
[[ "$STATUS" == "401" ]] || {
  echo "ERROR: refresh after logout expected 401, got $STATUS" >&2
  cat "$TMP_DIR/after-logout.json" >&2
  exit 1
}
echo "PASS: logout revoked the persisted refresh session."

echo "==> [8/9] Verifying only token hashes are persisted..."
BAD_HASH_ROWS="$(psql_exec -qAt -v smoke_id="$SMOKE_USER_ID" <<'SQL'
SELECT count(*)
FROM "AuthRefreshSession"
WHERE "userId" = :'smoke_id'
  AND (length("tokenHash") <> 64 OR "tokenHash" LIKE 'eyJ%');
SQL
)"
[[ "$BAD_HASH_ROWS" == "0" ]] || {
  echo "ERROR: refresh-session storage contains unexpected token representation" >&2
  exit 1
}

echo "==> [9/9] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"
echo
curl --fail --show-error --silent "$BASE_URL/health/db"
echo

echo
echo "PASS: persistent refresh-token rotation/revocation deployed and smoke-tested through real login."
echo "Backup: $BACKUP"
