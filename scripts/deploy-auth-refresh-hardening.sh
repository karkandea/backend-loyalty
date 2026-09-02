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
: "${Jwt__Issuer:?Jwt__Issuer is required}"
: "${Jwt__Audience:?Jwt__Audience is required}"
: "${Jwt__SigningKey:?Jwt__SigningKey is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-refresh-hardening-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-refresh-smoke.XXXXXX)"
chmod 700 "$TMP_DIR"
FAMILY_ONE=""
FAMILY_TWO=""

psql_exec() {
  docker exec \
    -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
    -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

cleanup() {
  set +e
  if [[ -n "$FAMILY_ONE" && -n "$FAMILY_TWO" ]]; then
    psql_exec -q -v f1="$FAMILY_ONE" -v f2="$FAMILY_TWO" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession"
WHERE "familyId" IN (:'f1'::uuid, :'f2'::uuid);
SQL
  elif [[ -n "$FAMILY_ONE" ]]; then
    psql_exec -q -v f1="$FAMILY_ONE" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "familyId" = :'f1'::uuid;
SQL
  elif [[ -n "$FAMILY_TWO" ]]; then
    psql_exec -q -v f2="$FAMILY_TWO" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "familyId" = :'f2'::uuid;
SQL
  fi
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

echo "==> [1/8] Checking current database and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null

docker exec \
  -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
  "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/8] Applying persistent refresh-session schema..."
psql_exec < scripts/add-refresh-session-schema.sql

TABLE_OK="$(psql_exec -qAt <<'SQL'
SELECT to_regclass('public."AuthRefreshSession"') IS NOT NULL;
SQL
)"
[[ "$TABLE_OK" == "t" ]] || { echo "ERROR: AuthRefreshSession table missing after schema apply" >&2; exit 1; }

echo "==> [3/8] Building and restarting Loyalty API..."
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

echo "==> [4/8] Selecting isolated smoke-test identity..."
IDENTITY_ROW="$(psql_exec -qAtF '|' <<'SQL'
SELECT id, lower(role), "businessId", COALESCE("outletId", '')
FROM "AdminUser"
WHERE "isActive" = true
ORDER BY "createdAt", id
LIMIT 1;
SQL
)"
IFS='|' read -r TEST_USER_ID TEST_ROLE TEST_BUSINESS_ID TEST_OUTLET_ID <<< "$IDENTITY_ROW"
[[ -n "$TEST_USER_ID" ]] || { echo "ERROR: no active AdminUser available for smoke test" >&2; exit 1; }

make_refresh_token() {
  local jti="$1"
  python3 - "$TEST_USER_ID" "$TEST_ROLE" "$TEST_BUSINESS_ID" "$TEST_OUTLET_ID" "$jti" <<'PY'
import base64, hashlib, hmac, json, os, sys, time
user_id, role, business_id, outlet_id, jti = sys.argv[1:]
now = int(time.time())
payload = {
    "sub": user_id,
    "auth_kind": "admin",
    "role": role,
    "business_id": business_id,
    "token_type": "refresh",
    "jti": jti,
    "nbf": now - 1,
    "exp": now + 600,
    "iss": os.environ["Jwt__Issuer"],
    "aud": os.environ["Jwt__Audience"],
}
if outlet_id:
    payload["outlet_id"] = outlet_id
header = {"alg": "HS256", "typ": "JWT"}
def enc(value):
    raw = json.dumps(value, separators=(",", ":")).encode()
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()
body = f"{enc(header)}.{enc(payload)}"
sig = hmac.new(os.environ["Jwt__SigningKey"].encode(), body.encode(), hashlib.sha256).digest()
print(body + "." + base64.urlsafe_b64encode(sig).rstrip(b"=").decode())
PY
}

insert_session() {
  local token="$1"
  local hash family
  hash="$(printf '%s' "$token" | sha256sum | awk '{print $1}')"
  family="$(psql_exec -qAt \
    -v token_hash="$hash" \
    -v user_id="$TEST_USER_ID" \
    -v role="$TEST_ROLE" \
    -v business_id="$TEST_BUSINESS_ID" \
    -v outlet_id="$TEST_OUTLET_ID" <<'SQL'
INSERT INTO "AuthRefreshSession" (
  id, "userId", "authKind", "tokenHash", "familyId", "parentSessionId",
  role, "businessId", "outletId", "expiresAt", "revokedAt", "revokeReason",
  "replacedBySessionId", "createdAt"
)
VALUES (
  extensions.uuid_generate_v4(), :'user_id', 'admin', :'token_hash', extensions.uuid_generate_v4(), NULL,
  NULLIF(:'role',''), NULLIF(:'business_id',''), NULLIF(:'outlet_id',''),
  now() + interval '10 minutes', NULL, NULL, NULL, now()
)
RETURNING "familyId";
SQL
)"
  printf '%s' "$family"
}

write_body() {
  local token="$1" file="$2"
  TOKEN="$token" python3 - "$file" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump({"refreshToken": os.environ["TOKEN"]}, f)
PY
  chmod 600 "$file"
}

request() {
  local endpoint="$1" token="$2" output="$3"
  local body="${output}.request"
  write_body "$token" "$body"
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

echo "==> [5/8] Testing rotation + replay detection..."
TOKEN_ONE="$(make_refresh_token "smoke-rotation-${STAMP}")"
FAMILY_ONE="$(insert_session "$TOKEN_ONE")"
STATUS="$(request '/api/auth/refresh' "$TOKEN_ONE" "$TMP_DIR/refresh1.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: first refresh expected 200, got $STATUS" >&2; cat "$TMP_DIR/refresh1.json" >&2; exit 1; }
TOKEN_ONE_NEXT="$(extract_refresh_token "$TMP_DIR/refresh1.json")"

STATUS="$(request '/api/auth/refresh' "$TOKEN_ONE" "$TMP_DIR/replay-old.json")"
[[ "$STATUS" == "401" ]] || { echo "ERROR: replayed old refresh expected 401, got $STATUS" >&2; cat "$TMP_DIR/replay-old.json" >&2; exit 1; }

STATUS="$(request '/api/auth/refresh' "$TOKEN_ONE_NEXT" "$TMP_DIR/family-revoked.json")"
[[ "$STATUS" == "401" ]] || { echo "ERROR: refresh family should be revoked after replay; expected 401, got $STATUS" >&2; cat "$TMP_DIR/family-revoked.json" >&2; exit 1; }

echo "PASS: replay detection revoked the token family."

echo "==> [6/8] Testing normal rotate -> logout -> reject flow..."
TOKEN_TWO="$(make_refresh_token "smoke-logout-${STAMP}")"
FAMILY_TWO="$(insert_session "$TOKEN_TWO")"
STATUS="$(request '/api/auth/refresh' "$TOKEN_TWO" "$TMP_DIR/refresh2.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: second refresh expected 200, got $STATUS" >&2; cat "$TMP_DIR/refresh2.json" >&2; exit 1; }
TOKEN_TWO_NEXT="$(extract_refresh_token "$TMP_DIR/refresh2.json")"

STATUS="$(request '/api/auth/logout' "$TOKEN_TWO_NEXT" "$TMP_DIR/logout.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: logout expected 200, got $STATUS" >&2; cat "$TMP_DIR/logout.json" >&2; exit 1; }

STATUS="$(request '/api/auth/refresh' "$TOKEN_TWO_NEXT" "$TMP_DIR/after-logout.json")"
[[ "$STATUS" == "401" ]] || { echo "ERROR: refresh after logout expected 401, got $STATUS" >&2; cat "$TMP_DIR/after-logout.json" >&2; exit 1; }

echo "PASS: logout revoked the persisted refresh session."

echo "==> [7/8] Verifying no raw JWTs are stored..."
BAD_HASH_ROWS="$(psql_exec -qAt <<'SQL'
SELECT count(*)
FROM "AuthRefreshSession"
WHERE length("tokenHash") <> 64
   OR "tokenHash" LIKE 'eyJ%';
SQL
)"
[[ "$BAD_HASH_ROWS" == "0" ]] || { echo "ERROR: refresh-session storage contains unexpected token representation" >&2; exit 1; }

echo "==> [8/8] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"
echo
curl --fail --show-error --silent "$BASE_URL/health/db"
echo

echo
echo "PASS: persistent refresh-token rotation/revocation deployed and smoke-tested."
echo "Backup: $BACKUP"
