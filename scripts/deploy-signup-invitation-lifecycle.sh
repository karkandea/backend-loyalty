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
BACKUP="/root/loyalty-pre-signup-invite-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-signup-invite.XXXXXX)"
chmod 700 "$TMP_DIR"

OWNER_EMAIL="signup-invite-smoke-${STAMP}@example.invalid"
STAFF_EMAIL="signup-invite-staff-${STAMP}@example.invalid"
OWNER_PASSWORD='SmokeOnly-Signup-2026!'
STAFF_PASSWORD='SmokeOnly-Staff-2026!'
BUSINESS_SLUG="auth-invite-smoke-${STAMP,,}"
SECOND_SLUG="auth-invite-smoke-second-${STAMP,,}"
BUSINESS_NAME="Signup Invite Smoke ${STAMP}"
SECOND_NAME="Signup Invite Second ${STAMP}"
OWNER_TOKEN="SmokeOwnerInvite-${STAMP}-A"
STAFF_TOKEN="SmokeStaffInvite-${STAMP}-B"
SECOND_OWNER_TOKEN="SmokeSecondOwner-${STAMP}-C"

psql_exec() {
  docker exec \
    -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
    -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q -v pattern='signup-invite-%@example.invalid' -v slug_pattern='auth-invite-smoke-%' >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession"
WHERE "userId" IN (
  SELECT DISTINCT "authUserId"::text
  FROM "BusinessUser"
  WHERE email LIKE :'pattern' AND "authUserId" IS NOT NULL
);
DELETE FROM "AuthPasswordReset"
WHERE "userId" IN (
  SELECT DISTINCT "authUserId"::text
  FROM "BusinessUser"
  WHERE email LIKE :'pattern' AND "authUserId" IS NOT NULL
);
DELETE FROM "Business" WHERE slug LIKE :'slug_pattern';
SQL
}

cleanup() {
  local rc=$?
  cleanup_fixture
  rm -rf "$TMP_DIR"
  exit "$rc"
}
trap cleanup EXIT

diagnostics() {
  local rc=$?
  set +e
  echo >&2
  echo "=== backend-loyalty logs (last 220 lines) ===" >&2
  docker logs --tail 220 "$API_CONTAINER" 2>&1 >&2 || true
  echo >&2
  echo "=== signup/invitation diagnostic snapshot ===" >&2
  psql_exec -P pager=off -v slug_pattern='auth-invite-smoke-%' <<'SQL' >&2 || true
SELECT id, slug, "isActive", "createdAt" FROM "Business"
WHERE slug LIKE :'slug_pattern' ORDER BY "createdAt";
SELECT bu.email, bu.role, bu."isActive", bu."authUserId"::text, b.slug
FROM "BusinessUser" bu
JOIN "Business" b ON b.id = bu."businessId"
WHERE b.slug LIKE :'slug_pattern'
ORDER BY bu.email, b.slug;
SELECT bi.email, bi.role, bi."requiresPassword", bi."usedAt" IS NOT NULL AS used,
       bi."revokedAt" IS NOT NULL AS revoked, length(bi."tokenHash") AS hash_len, b.slug
FROM "BusinessInvitation" bi
JOIN "Business" b ON b.id = bi."businessId"
WHERE b.slug LIKE :'slug_pattern'
ORDER BY bi."createdAt";
SQL
  echo "FAIL: signup/invitation lifecycle smoke stopped with exit code $rc." >&2
  exit "$rc"
}
trap diagnostics ERR

write_json() {
  local mode="$1" file="$2" v1="${3:-}" v2="${4:-}" v3="${5:-}" v4="${6:-}"
  MODE="$mode" V1="$v1" V2="$v2" V3="$v3" V4="$v4" python3 - "$file" <<'PY'
import json, os, sys
mode = os.environ['MODE']
v1, v2, v3, v4 = (os.environ.get(k, '') for k in ('V1','V2','V3','V4'))
if mode == 'signup':
    data = {'email': v1, 'fullName': v2, 'businessName': v3, 'slug': v4}
elif mode == 'register':
    data = {'token': v1, 'fullName': v2, 'password': v3}
elif mode == 'login':
    data = {'email': v1, 'password': v2}
elif mode == 'team':
    data = {'email': v1, 'role': v2}
elif mode == 'accept':
    data = {'token': v1}
else:
    raise SystemExit('unknown json mode')
with open(sys.argv[1], 'w', encoding='utf-8') as f:
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
with open(sys.argv[1], encoding='utf-8') as f:
    data = json.load(f)
value = (data.get('data') or {}).get(sys.argv[2])
if value is None:
    raise SystemExit(f'{sys.argv[2]} missing')
print(value)
PY
}

set_invite_token() {
  local slug="$1" role="$2" raw="$3"
  local hash
  hash="$(printf '%s' "$raw" | sha256sum | awk '{print $1}')"
  psql_exec -q -v slug="$slug" -v role="$role" -v hash="$hash" <<'SQL'
UPDATE "BusinessInvitation" bi
SET "tokenHash" = :'hash', "updatedAt" = now()
FROM "Business" b
WHERE bi."businessId" = b.id
  AND b.slug = :'slug'
  AND upper(bi.role) = upper(:'role')
  AND bi."usedAt" IS NULL
  AND bi."revokedAt" IS NULL;
SQL
}

echo "==> [1/11] Checking target database and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
TABLE_OK="$(psql_exec -qAt <<'SQL'
SELECT to_regclass('public."BusinessInvitation"') IS NOT NULL
   AND to_regclass('public."BusinessUser"') IS NOT NULL
   AND to_regclass('public."Business"') IS NOT NULL;
SQL
)"
[[ "$TABLE_OK" == "t" ]] || { echo "ERROR: migrated business invitation tables are missing" >&2; exit 1; }

docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/11] Building and restarting Loyalty API..."
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api
for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null && curl --fail --silent "$BASE_URL/health/db" >/dev/null; then
    break
  fi
  if [[ "$attempt" -eq 30 ]]; then
    echo "ERROR: API did not become healthy" >&2
    exit 1
  fi
  sleep 2
done

echo "==> [3/11] Cleaning stale isolated smoke fixtures..."
cleanup_fixture

echo "==> [4/11] Testing new-owner signup stays inactive until email token is consumed..."
SIGNUP_BODY="$TMP_DIR/signup.json"
SIGNUP_OUT="$TMP_DIR/signup.out.json"
write_json signup "$SIGNUP_BODY" "$OWNER_EMAIL" 'Signup Owner Smoke' "$BUSINESS_NAME" "$BUSINESS_SLUG"
STATUS="$(post_file '/api/business/auth/signup' "$SIGNUP_BODY" "$SIGNUP_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: owner signup expected 200, got $STATUS" >&2; cat "$SIGNUP_OUT" >&2; exit 1; }
BUSINESS_ID="$(extract_field "$SIGNUP_OUT" businessId)"

PENDING_STATE="$(psql_exec -qAt -v business_id="$BUSINESS_ID" <<'SQL'
SELECT concat_ws('|', b."isActive", count(bu.id), count(bi.id), min(length(bi."tokenHash")))
FROM "Business" b
LEFT JOIN "BusinessUser" bu ON bu."businessId" = b.id
LEFT JOIN "BusinessInvitation" bi ON bi."businessId" = b.id
  AND upper(bi.role) = 'OWNER' AND bi."usedAt" IS NULL AND bi."revokedAt" IS NULL
WHERE b.id = :'business_id'
GROUP BY b."isActive";
SQL
)"
[[ "$PENDING_STATE" == "f|0|1|64" ]] || { echo "ERROR: unexpected pending signup state: $PENDING_STATE" >&2; exit 1; }
echo "PASS: pending signup creates an inactive tenant and hash-only owner invitation."

echo "==> [5/11] Testing public invite resolve + owner registration + single-use token..."
set_invite_token "$BUSINESS_SLUG" OWNER "$OWNER_TOKEN"
STATUS="$(curl --silent --show-error -o "$TMP_DIR/resolve-owner.json" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$OWNER_TOKEN")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: owner invite resolve expected 200, got $STATUS" >&2; cat "$TMP_DIR/resolve-owner.json" >&2; exit 1; }
[[ "$(extract_field "$TMP_DIR/resolve-owner.json" requiresPassword)" == "True" || "$(extract_field "$TMP_DIR/resolve-owner.json" requiresPassword)" == "true" ]] || { echo "ERROR: first owner invite should require a password" >&2; exit 1; }

REGISTER_BODY="$TMP_DIR/register-owner.json"
REGISTER_OUT="$TMP_DIR/register-owner.out.json"
write_json register "$REGISTER_BODY" "$OWNER_TOKEN" 'Signup Owner Smoke' "$OWNER_PASSWORD"
STATUS="$(post_file '/api/business/team/invitations/register' "$REGISTER_BODY" "$REGISTER_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: owner invite registration expected 200, got $STATUS" >&2; cat "$REGISTER_OUT" >&2; exit 1; }
OWNER_AUTH_ID="$(extract_field "$REGISTER_OUT" userId)"

STATUS="$(post_file '/api/business/team/invitations/register' "$REGISTER_BODY" "$TMP_DIR/register-owner-replay.json")"
[[ "$STATUS" == "410" ]] || { echo "ERROR: owner invite replay expected 410, got $STATUS" >&2; cat "$TMP_DIR/register-owner-replay.json" >&2; exit 1; }

OWNER_STATE="$(psql_exec -qAt -v business_id="$BUSINESS_ID" <<'SQL'
SELECT concat_ws('|', b."isActive", bu.role, bu."isActive", bu."authUserId" IS NOT NULL, left(bu."passwordHash", 2))
FROM "Business" b
JOIN "BusinessUser" bu ON bu."businessId" = b.id
WHERE b.id = :'business_id' AND upper(bu.role) = 'OWNER';
SQL
)"
[[ "$OWNER_STATE" == "t|OWNER|t|t|\$2" ]] || { echo "ERROR: owner registration state invalid: $OWNER_STATE" >&2; exit 1; }
echo "PASS: owner token registered once, activated tenant, and persisted bcrypt locally."

echo "==> [6/11] Testing owner login from standalone credentials..."
LOGIN_BODY="$TMP_DIR/login-owner.json"
LOGIN_OUT="$TMP_DIR/login-owner.out.json"
write_json login "$LOGIN_BODY" "$OWNER_EMAIL" "$OWNER_PASSWORD"
STATUS="$(post_file '/api/business/auth/login' "$LOGIN_BODY" "$LOGIN_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: owner login expected 200, got $STATUS" >&2; cat "$LOGIN_OUT" >&2; exit 1; }
OWNER_ACCESS="$(extract_field "$LOGIN_OUT" accessToken)"
[[ "$(extract_field "$LOGIN_OUT" membershipCount)" == "1" ]] || { echo "ERROR: owner should initially have one membership" >&2; exit 1; }
echo "PASS: newly registered owner logs in through .NET + PostgreSQL only."

echo "==> [7/11] Testing authenticated team invite + new staff registration..."
TEAM_BODY="$TMP_DIR/team-invite.json"
TEAM_OUT="$TMP_DIR/team-invite.out.json"
write_json team "$TEAM_BODY" "$STAFF_EMAIL" STAFF
STATUS="$(post_file '/api/business/team/invitations' "$TEAM_BODY" "$TEAM_OUT" "$OWNER_ACCESS")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: team invite expected 200, got $STATUS" >&2; cat "$TEAM_OUT" >&2; exit 1; }
set_invite_token "$BUSINESS_SLUG" STAFF "$STAFF_TOKEN"

STAFF_REGISTER_BODY="$TMP_DIR/register-staff.json"
STAFF_REGISTER_OUT="$TMP_DIR/register-staff.out.json"
write_json register "$STAFF_REGISTER_BODY" "$STAFF_TOKEN" 'Signup Staff Smoke' "$STAFF_PASSWORD"
STATUS="$(post_file '/api/business/team/invitations/register' "$STAFF_REGISTER_BODY" "$STAFF_REGISTER_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: staff registration expected 200, got $STATUS" >&2; cat "$STAFF_REGISTER_OUT" >&2; exit 1; }

write_json login "$TMP_DIR/login-staff.json" "$STAFF_EMAIL" "$STAFF_PASSWORD"
STATUS="$(post_file '/api/business/auth/login' "$TMP_DIR/login-staff.json" "$TMP_DIR/login-staff.out.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: staff business login expected 200, got $STATUS" >&2; cat "$TMP_DIR/login-staff.out.json" >&2; exit 1; }
echo "PASS: owner invitation created a standalone STAFF membership with its own password."

echo "==> [8/11] Testing same identity can claim a second owner business via authenticated accept..."
SECOND_SIGNUP_BODY="$TMP_DIR/signup-second.json"
SECOND_SIGNUP_OUT="$TMP_DIR/signup-second.out.json"
write_json signup "$SECOND_SIGNUP_BODY" "$OWNER_EMAIL" 'Signup Owner Smoke' "$SECOND_NAME" "$SECOND_SLUG"
STATUS="$(post_file '/api/business/auth/signup' "$SECOND_SIGNUP_BODY" "$SECOND_SIGNUP_OUT")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: second business signup expected 200, got $STATUS" >&2; cat "$SECOND_SIGNUP_OUT" >&2; exit 1; }
SECOND_BUSINESS_ID="$(extract_field "$SECOND_SIGNUP_OUT" businessId)"
set_invite_token "$SECOND_SLUG" OWNER "$SECOND_OWNER_TOKEN"

ACCEPT_BODY="$TMP_DIR/accept-second.json"
ACCEPT_OUT="$TMP_DIR/accept-second.out.json"
write_json accept "$ACCEPT_BODY" "$SECOND_OWNER_TOKEN"
STATUS="$(post_file '/api/business/team/invitations/accept' "$ACCEPT_BODY" "$ACCEPT_OUT" "$OWNER_ACCESS")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: existing identity accept expected 200, got $STATUS" >&2; cat "$ACCEPT_OUT" >&2; exit 1; }
[[ "$(extract_field "$ACCEPT_OUT" membershipCount)" == "2" ]] || { echo "ERROR: owner membership count should become 2" >&2; exit 1; }
[[ "$(extract_field "$ACCEPT_OUT" requiresRelogin)" == "False" || "$(extract_field "$ACCEPT_OUT" requiresRelogin)" == "false" ]] || { echo "ERROR: UUID standalone identity should not require relogin" >&2; exit 1; }

SAME_ID_COUNT="$(psql_exec -qAt -v auth_id="$OWNER_AUTH_ID" <<'SQL'
SELECT count(*) FROM "BusinessUser"
WHERE "authUserId"::text = :'auth_id' AND "isActive" = true AND upper(role) = 'OWNER';
SQL
)"
[[ "$SAME_ID_COUNT" == "2" ]] || { echo "ERROR: expected two active OWNER memberships on same identity, found $SAME_ID_COUNT" >&2; exit 1; }
echo "PASS: existing identity accepted a second owner membership without duplicating credentials."

echo "==> [9/11] Verifying consumed invitations cannot resolve..."
STATUS="$(curl --silent --show-error -o "$TMP_DIR/resolve-consumed.json" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$SECOND_OWNER_TOKEN")"
[[ "$STATUS" == "410" ]] || { echo "ERROR: consumed invite resolve expected 410, got $STATUS" >&2; exit 1; }
echo "PASS: consumed invitation tokens are unusable."

echo "==> [10/11] Verifying invitation tokens are hash-only at rest..."
BAD_HASH_ROWS="$(psql_exec -qAt -v slug_pattern='auth-invite-smoke-%' <<'SQL'
SELECT count(*)
FROM "BusinessInvitation" bi
JOIN "Business" b ON b.id = bi."businessId"
WHERE b.slug LIKE :'slug_pattern'
  AND (length(bi."tokenHash") <> 64 OR bi."tokenHash" LIKE 'Smoke%');
SQL
)"
[[ "$BAD_HASH_ROWS" == "0" ]] || { echo "ERROR: invitation storage contains unexpected raw-token representation" >&2; exit 1; }
echo "PASS: only SHA-256 invitation hashes are persisted."

echo "==> [11/11] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"
echo
curl --fail --show-error --silent "$BASE_URL/health/db"
echo

trap - ERR

echo
echo "PASS: standalone signup + invitation lifecycle deployed and smoke-tested."
echo "Backup: $BACKUP"
