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

for cmd in docker curl python3 sha256sum; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "ERROR: missing command $cmd" >&2; exit 1; }
done
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
[[ -f scripts/ensure-auth-password-policy.sql ]] || { echo 'ERROR: ensure-auth-password-policy.sql missing' >&2; exit 1; }
[[ -f scripts/create-legacy-auth-bridge.sql ]] || { echo 'ERROR: create-legacy-auth-bridge.sql missing' >&2; exit 1; }
[[ -f scripts/add-standalone-auth-identity-schema.sql ]] || { echo 'ERROR: add-standalone-auth-identity-schema.sql missing' >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-password-policy-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-password-policy.XXXXXX)"
chmod 700 "$TMP_DIR"

OLD_PASSWORD='SmokeOnly-Refresh-2026!'
OWNER_NEW_PASSWORD='SmokeOwner-New-2026!'
STAFF_NEW_PASSWORD='SmokeStaff-New-2026!'
FIXTURE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
OWNER_ID="policy-owner-${STAMP}"
OWNER_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
OWNER_EMAIL="policy-owner-${STAMP}@example.invalid"
STAFF_ID="policy-staff-${STAMP}"
STAFF_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
STAFF_EMAIL="policy-staff-${STAMP}@example.invalid"
ZERO_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
ZERO_EMAIL="policy-zero-${STAMP}@example.invalid"
BLOCKED_EMAIL="policy-blocked-${STAMP}@example.invalid"
ZERO_TOKEN="SmokeOnly-ZeroInvite-${STAMP}"
ZERO_INVITE_PERMISSIONS='["can_manage_cards","can_view_analytics"]'

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 -P pager=off "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q \
    -v owner_auth="$OWNER_AUTH" -v staff_auth="$STAFF_AUTH" -v zero_auth="$ZERO_AUTH" \
    -v owner_email="$OWNER_EMAIL" -v staff_email="$STAFF_EMAIL" -v zero_email="$ZERO_EMAIL" \
    -v blocked_email="$BLOCKED_EMAIL" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" IN (:'owner_auth', :'staff_auth', :'zero_auth');
DELETE FROM "BusinessInvitation" WHERE email IN (:'zero_email', :'blocked_email');
DELETE FROM "BusinessUser" WHERE email IN (:'owner_email', :'staff_email', :'zero_email');
DELETE FROM "AuthPasswordPolicy" WHERE "authUserId" IN (:'owner_auth', :'staff_auth', :'zero_auth');
DELETE FROM "LegacyAuthUserPassword" WHERE "authUserId" IN (:'owner_auth', :'staff_auth', :'zero_auth');
DELETE FROM "StandaloneAuthIdentity" WHERE id IN (:'zero_auth'::uuid);
SQL
}

cleanup() {
  local rc=$?
  cleanup_fixture
  rm -rf "$TMP_DIR"
  exit "$rc"
}
trap cleanup EXIT

fail_diag() {
  local rc=$?
  set +e
  echo >&2
  echo '=== backend-loyalty logs (last 240 lines) ===' >&2
  docker logs --tail 240 "$API_CONTAINER" 2>&1 >&2 || true
  echo >&2
  echo '=== password-policy / zero-invite fixture snapshot ===' >&2
  psql_exec -v owner_auth="$OWNER_AUTH" -v staff_auth="$STAFF_AUTH" -v zero_auth="$ZERO_AUTH" \
    -v owner_email="$OWNER_EMAIL" -v staff_email="$STAFF_EMAIL" -v zero_email="$ZERO_EMAIL" <<'SQL' >&2 || true
SELECT "authUserId", "requiresPassword", "graceExpiresAt", "passwordSetAt"
FROM "AuthPasswordPolicy" WHERE "authUserId" IN (:'owner_auth', :'staff_auth', :'zero_auth') ORDER BY "authUserId";
SELECT email, "businessId", "authUserId", role, permissions, "isActive"
FROM "BusinessUser" WHERE email IN (:'owner_email', :'staff_email', :'zero_email') ORDER BY email, "businessId";
SELECT email, role, permissions, "requiresPassword", "usedAt", "revokedAt"
FROM "BusinessInvitation" WHERE email=:'zero_email';
SQL
  echo "FAIL: password-policy/zero-membership invitation smoke stopped with exit code $rc." >&2
  exit "$rc"
}
trap fail_diag ERR

write_json() {
  local mode="$1"
  local file="$2"
  local value1="${3:-}"
  local value2="${4:-}"
  local value3="${5:-}"
  MODE="$mode" VALUE1="$value1" VALUE2="$value2" VALUE3="$value3" python3 - "$file" <<'PY'
import json,os,sys
m=os.environ['MODE']; a=os.environ.get('VALUE1',''); b=os.environ.get('VALUE2',''); c=os.environ.get('VALUE3','')
if m == 'login': data={'email':a,'password':b}
elif m == 'set': data={'newPassword':a}
elif m == 'refresh': data={'refreshToken':a}
elif m == 'invite': data={'email':a,'role':b,'permissions':json.loads(c)}
elif m == 'register': data={'token':a,'fullName':b,'password':c}
elif m == 'accept': data={'token':a}
else: raise SystemExit('unknown json mode')
with open(sys.argv[1],'w',encoding='utf-8') as f: json.dump(data,f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1"
  local body="$2"
  local output="$3"
  local bearer="${4:-}"
  local args=(--silent --show-error -o "$output" -w '%{http_code}' -H 'Content-Type: application/json')
  [[ -z "$bearer" ]] || args+=(-H "Authorization: Bearer $bearer")
  args+=(--data-binary "@$body" "$BASE_URL$endpoint")
  curl "${args[@]}"
}

login_to_files() {
  local email="$1"
  local password="$2"
  local prefix="$3"
  local body="$TMP_DIR/$prefix.login.json"
  local out="$TMP_DIR/$prefix.login.out"
  local status
  write_json login "$body" "$email" "$password"
  status="$(post_file '/api/business/auth/login' "$body" "$out")"
  [[ "$status" == '200' ]] || { echo "ERROR: $prefix login expected 200, got $status" >&2; cat "$out" >&2; return 1; }
  python3 - "$out" "$TMP_DIR/$prefix.access" "$TMP_DIR/$prefix.refresh" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
a=d.get('accessToken'); r=d.get('refreshToken')
if not a or not r: raise SystemExit('tokens missing')
open(sys.argv[2],'w').write(a); open(sys.argv[3],'w').write(r)
PY
}

assert_policy() {
  local file="$1"
  local requires="$2"
  local expired="$3"
  python3 - "$file" "$requires" "$expired" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
assert d.get('requiresPassword') == (sys.argv[2]=='true'), d
assert d.get('isExpired') == (sys.argv[3]=='true'), d
if sys.argv[2]=='false': assert d.get('passwordSetAt') is not None, d
PY
}

assert_error_code() {
  python3 - "$1" "$2" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); actual=(x.get('error') or {}).get('code')
assert actual == sys.argv[2], (actual,sys.argv[2],x)
PY
}

echo '==> [1/10] Ensuring target schemas, backing up, and building API...'
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
psql_exec -q < scripts/create-legacy-auth-bridge.sql
psql_exec -q < scripts/add-standalone-auth-identity-schema.sql
psql_exec -q < scripts/ensure-auth-password-policy.sql

docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api
for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null && curl --fail --silent "$BASE_URL/health/db" >/dev/null; then break; fi
  [[ "$attempt" -lt 30 ]] || { echo 'ERROR: API did not become healthy' >&2; exit 1; }
  sleep 2
done

echo '==> [2/10] Creating isolated OWNER / STAFF / zero-membership fixtures...'
cleanup_fixture
BUSINESS_ID="$(psql_exec -qAt <<'SQL'
SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt", id LIMIT 1;
SQL
)"
[[ -n "$BUSINESS_ID" ]] || { echo 'ERROR: no active business available' >&2; exit 1; }

psql_exec -q \
  -v business_id="$BUSINESS_ID" -v fixture_hash="$FIXTURE_BCRYPT" \
  -v owner_id="$OWNER_ID" -v owner_auth="$OWNER_AUTH" -v owner_email="$OWNER_EMAIL" \
  -v staff_id="$STAFF_ID" -v staff_auth="$STAFF_AUTH" -v staff_email="$STAFF_EMAIL" \
  -v zero_auth="$ZERO_AUTH" -v zero_email="$ZERO_EMAIL" <<'SQL'
INSERT INTO "BusinessUser" (
  id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,
  "isActive","lastLoginAt","createdAt","updatedAt"
) VALUES
(:'owner_id', :'business_id', :'owner_auth'::uuid, :'owner_email', :'fixture_hash', 'Policy Smoke Owner', 'OWNER', NULL, true, NULL, now(), now()),
(:'staff_id', :'business_id', :'staff_auth'::uuid, :'staff_email', :'fixture_hash', 'Policy Smoke Staff', 'STAFF', '["can_manage_cards"]'::jsonb, true, NULL, now(), now());

INSERT INTO "AuthPasswordPolicy" (
  "authUserId","requiresPassword","graceExpiresAt","passwordSetAt","createdAt","updatedAt"
) VALUES
(:'owner_auth', true, now() - interval '1 minute', NULL, now(), now()),
(:'staff_auth', true, now() - interval '1 minute', NULL, now(), now())
ON CONFLICT ("authUserId") DO UPDATE SET
  "requiresPassword"=true,"graceExpiresAt"=EXCLUDED."graceExpiresAt","passwordSetAt"=NULL,"updatedAt"=now();

INSERT INTO "StandaloneAuthIdentity" (
  id,email,"resolvedRole","emailConfirmedAt","deletedAt","bannedUntil",provider,"hasPassword",source,"sourceCreatedAt","sourceUpdatedAt","importedAt"
) VALUES
(:'zero_auth'::uuid, :'zero_email', 'staff', now(), NULL, NULL, 'email', true, 'smoke', now(), now(), now());

INSERT INTO "LegacyAuthUserPassword" ("authUserId","email","passwordHash","importedAt")
VALUES (:'zero_auth', :'zero_email', :'fixture_hash', now())
ON CONFLICT ("authUserId") DO UPDATE SET "email"=EXCLUDED."email","passwordHash"=EXCLUDED."passwordHash","importedAt"=now();
SQL

login_to_files "$OWNER_EMAIL" "$OLD_PASSWORD" owner
OWNER_ACCESS="$(cat "$TMP_DIR/owner.access")"
OWNER_REFRESH="$(cat "$TMP_DIR/owner.refresh")"
login_to_files "$STAFF_EMAIL" "$OLD_PASSWORD" staff
STAFF_ACCESS="$(cat "$TMP_DIR/staff.access")"
login_to_files "$ZERO_EMAIL" "$OLD_PASSWORD" zero
ZERO_ACCESS="$(cat "$TMP_DIR/zero.access")"
python3 - "$TMP_DIR/zero.login.out" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
assert d.get('membershipCount') == 0, d
assert d.get('businessId') is None, d
PY
echo 'PASS: isolated owner/staff authenticate and standalone identity logs in with membershipCount=0.'

echo '==> [3/10] Verifying expired password policy blocks team invitation...'
STATUS="$(curl --silent --show-error -o "$TMP_DIR/owner-policy-before.out" -w '%{http_code}' -H "Authorization: Bearer $OWNER_ACCESS" "$BASE_URL/api/business/auth/password-policy")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: owner policy expected 200, got $STATUS" >&2; cat "$TMP_DIR/owner-policy-before.out" >&2; exit 1; }
assert_policy "$TMP_DIR/owner-policy-before.out" true true
write_json invite "$TMP_DIR/blocked-invite.json" "$BLOCKED_EMAIL" STAFF '[]'
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/blocked-invite.json" "$TMP_DIR/blocked-invite.out" "$OWNER_ACCESS")"
[[ "$STATUS" == '403' ]] || { echo "ERROR: expired-policy invite expected 403, got $STATUS" >&2; cat "$TMP_DIR/blocked-invite.out" >&2; exit 1; }
assert_error_code "$TMP_DIR/blocked-invite.out" FORBIDDEN
BLOCKED_ROWS="$(psql_exec -qAt -v email="$BLOCKED_EMAIL" <<'SQL'
SELECT count(*) FROM "BusinessInvitation" WHERE email=:'email';
SQL
)"
[[ "$BLOCKED_ROWS" == '0' ]] || { echo 'ERROR: expired-policy request created invitation row' >&2; exit 1; }
echo 'PASS: expired password policy blocks team invitation without persisting an invite.'

echo '==> [4/10] Setting required OWNER password and verifying policy/session lifecycle...'
write_json set "$TMP_DIR/owner-set.json" "$OWNER_NEW_PASSWORD"
STATUS="$(post_file '/api/business/auth/set-password' "$TMP_DIR/owner-set.json" "$TMP_DIR/owner-set.out" "$OWNER_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: owner set-password expected 200, got $STATUS" >&2; cat "$TMP_DIR/owner-set.out" >&2; exit 1; }
STATUS="$(curl --silent --show-error -o "$TMP_DIR/owner-policy-after.out" -w '%{http_code}' -H "Authorization: Bearer $OWNER_ACCESS" "$BASE_URL/api/business/auth/password-policy")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: owner policy after set expected 200, got $STATUS" >&2; exit 1; }
assert_policy "$TMP_DIR/owner-policy-after.out" false false
write_json refresh "$TMP_DIR/old-refresh.json" "$OWNER_REFRESH"
STATUS="$(post_file '/api/auth/refresh' "$TMP_DIR/old-refresh.json" "$TMP_DIR/old-refresh.out")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: old refresh expected 401 after set-password, got $STATUS" >&2; cat "$TMP_DIR/old-refresh.out" >&2; exit 1; }
write_json login "$TMP_DIR/owner-old.json" "$OWNER_EMAIL" "$OLD_PASSWORD"
STATUS="$(post_file '/api/business/auth/login' "$TMP_DIR/owner-old.json" "$TMP_DIR/owner-old.out")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: old owner password expected 401, got $STATUS" >&2; cat "$TMP_DIR/owner-old.out" >&2; exit 1; }
login_to_files "$OWNER_EMAIL" "$OWNER_NEW_PASSWORD" owner-new
OWNER_ACCESS_NEW="$(cat "$TMP_DIR/owner-new.access")"
STATUS="$(post_file '/api/business/auth/set-password' "$TMP_DIR/owner-set.json" "$TMP_DIR/owner-set-again.out" "$OWNER_ACCESS_NEW")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: idempotent set-password expected 200, got $STATUS" >&2; cat "$TMP_DIR/owner-set-again.out" >&2; exit 1; }
echo 'PASS: set-password clears policy, revokes old refresh, kills old password, enables new password, and is idempotent.'

echo '==> [5/10] Verifying STAFF can complete required-password onboarding...'
STATUS="$(curl --silent --show-error -o "$TMP_DIR/staff-policy-before.out" -w '%{http_code}' -H "Authorization: Bearer $STAFF_ACCESS" "$BASE_URL/api/business/auth/password-policy")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: staff policy expected 200, got $STATUS" >&2; exit 1; }
assert_policy "$TMP_DIR/staff-policy-before.out" true true
write_json set "$TMP_DIR/staff-set.json" "$STAFF_NEW_PASSWORD"
STATUS="$(post_file '/api/business/auth/set-password' "$TMP_DIR/staff-set.json" "$TMP_DIR/staff-set.out" "$STAFF_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: staff set-password expected 200, got $STATUS" >&2; cat "$TMP_DIR/staff-set.out" >&2; exit 1; }
write_json login "$TMP_DIR/staff-old.json" "$STAFF_EMAIL" "$OLD_PASSWORD"
STATUS="$(post_file '/api/business/auth/login' "$TMP_DIR/staff-old.json" "$TMP_DIR/staff-old.out")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: old staff password expected 401, got $STATUS" >&2; exit 1; }
login_to_files "$STAFF_EMAIL" "$STAFF_NEW_PASSWORD" staff-new
STATUS="$(curl --silent --show-error -o "$TMP_DIR/staff-policy-after.out" -w '%{http_code}' -H "Authorization: Bearer $(cat "$TMP_DIR/staff-new.access")" "$BASE_URL/api/business/auth/password-policy")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: staff policy after expected 200, got $STATUS" >&2; exit 1; }
assert_policy "$TMP_DIR/staff-policy-after.out" false false
echo 'PASS: STAFF required-password onboarding matches legacy role semantics.'

echo '==> [6/10] Creating invitation for a real zero-membership standalone identity...'
write_json invite "$TMP_DIR/zero-invite.json" "$ZERO_EMAIL" ADMIN "$ZERO_INVITE_PERMISSIONS"
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/zero-invite.json" "$TMP_DIR/zero-invite.out" "$OWNER_ACCESS_NEW")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: zero identity invite expected 200, got $STATUS" >&2; cat "$TMP_DIR/zero-invite.out" >&2; exit 1; }
ZERO_HASH="$(printf '%s' "$ZERO_TOKEN" | sha256sum | awk '{print $1}')"
psql_exec -q -v email="$ZERO_EMAIL" -v token_hash="$ZERO_HASH" <<'SQL'
UPDATE "BusinessInvitation" SET "tokenHash"=:'token_hash', "updatedAt"=now()
WHERE email=:'email' AND "usedAt" IS NULL AND "revokedAt" IS NULL;
SQL
STATUS="$(curl --silent --show-error -o "$TMP_DIR/zero-resolve.out" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$ZERO_TOKEN")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: zero resolve expected 200, got $STATUS" >&2; cat "$TMP_DIR/zero-resolve.out" >&2; exit 1; }
python3 - "$TMP_DIR/zero-resolve.out" "$ZERO_EMAIL" "$ZERO_INVITE_PERMISSIONS" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
assert d.get('email') == sys.argv[2], d
assert d.get('requiresPassword') is False, d
assert d.get('permissions') == json.loads(sys.argv[3]), d
PY
echo 'PASS: existing zero-membership identity is recognized as existing and invite requiresPassword=false.'

echo '==> [7/10] Verifying register refuses to duplicate the standalone identity...'
write_json register "$TMP_DIR/zero-register.json" "$ZERO_TOKEN" 'Should Not Duplicate' 'Duplicate-New-2026!'
STATUS="$(post_file '/api/business/team/invitations/register' "$TMP_DIR/zero-register.json" "$TMP_DIR/zero-register.out")"
[[ "$STATUS" == '409' ]] || { echo "ERROR: existing zero identity register expected 409, got $STATUS" >&2; cat "$TMP_DIR/zero-register.out" >&2; exit 1; }
assert_error_code "$TMP_DIR/zero-register.out" USER_EXISTS
ZERO_MEMBERS_BEFORE="$(psql_exec -qAt -v email="$ZERO_EMAIL" <<'SQL'
SELECT count(*) FROM "BusinessUser" WHERE email=:'email';
SQL
)"
[[ "$ZERO_MEMBERS_BEFORE" == '0' ]] || { echo 'ERROR: register duplicate guard still created BusinessUser' >&2; exit 1; }
echo 'PASS: register path returns USER_EXISTS and creates no duplicate membership/identity.'

echo '==> [8/10] Accepting invitation with zero-membership access token...'
write_json accept "$TMP_DIR/zero-accept.json" "$ZERO_TOKEN"
STATUS="$(post_file '/api/business/team/invitations/accept' "$TMP_DIR/zero-accept.json" "$TMP_DIR/zero-accept.out" "$ZERO_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: zero identity accept expected 200, got $STATUS" >&2; cat "$TMP_DIR/zero-accept.out" >&2; exit 1; }
ZERO_STATE="$(psql_exec -qAtF '|' -v email="$ZERO_EMAIL" -v auth="$ZERO_AUTH" -v business="$BUSINESS_ID" <<'SQL'
SELECT concat_ws('|', role, permissions::text, "authUserId"::text, "businessId")
FROM "BusinessUser" WHERE email=:'email' AND "isActive"=true;
SQL
)"
python3 - "$ZERO_STATE" "$ZERO_INVITE_PERMISSIONS" "$ZERO_AUTH" "$BUSINESS_ID" <<'PY'
import json,sys
role,perms,auth,business=sys.argv[1].split('|',3)
assert role.upper() == 'ADMIN', role
assert json.loads(perms) == json.loads(sys.argv[2]), (perms,sys.argv[2])
assert auth.lower() == sys.argv[3].lower(), (auth,sys.argv[3])
assert business == sys.argv[4], (business,sys.argv[4])
PY
python3 - "$TMP_DIR/zero-accept.out" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
assert d.get('membershipCount') == 1, d
assert d.get('requiresRelogin') is False, d
PY
echo 'PASS: zero-membership identity accepts invite into the same auth UUID with exact role/permissions.'

echo '==> [9/10] Verifying invitation remains single-use after zero-membership acceptance...'
STATUS="$(post_file '/api/business/team/invitations/accept' "$TMP_DIR/zero-accept.json" "$TMP_DIR/zero-accept-replay.out" "$ZERO_ACCESS")"
[[ "$STATUS" == '410' ]] || { echo "ERROR: accepted invite replay expected 410, got $STATUS" >&2; cat "$TMP_DIR/zero-accept-replay.out" >&2; exit 1; }
CONSUMED="$(psql_exec -qAt -v email="$ZERO_EMAIL" <<'SQL'
SELECT count(*) FROM "BusinessInvitation" WHERE email=:'email' AND "usedAt" IS NOT NULL AND "revokedAt" IS NULL;
SQL
)"
[[ "$CONSUMED" == '1' ]] || { echo "ERROR: expected one consumed zero invite, got $CONSUMED" >&2; exit 1; }
echo 'PASS: zero-membership invitation remains single-use.'

echo '==> [10/10] Final health checks...'
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo 'PASS: password-policy onboarding + zero-membership invitation parity deployed and smoke-tested end-to-end.'
echo "Backup: $BACKUP"
