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

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-invite-permissions-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-invite-permissions.XXXXXX)"
chmod 700 "$TMP_DIR"

FIXTURE_PASSWORD='SmokeOnly-Refresh-2026!'
FIXTURE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
OWNER_ID="invite-perm-owner-${STAMP}"
OWNER_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
OWNER_EMAIL="invite-perm-owner-${STAMP}@example.invalid"
EXISTING_ID="invite-perm-existing-${STAMP}"
EXISTING_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
EXISTING_EMAIL="invite-perm-existing-${STAMP}@example.invalid"
NEW_EMAIL="invite-perm-new-${STAMP}@example.invalid"
NEW_TOKEN="SmokeOnly-NewInvite-${STAMP}"
EXISTING_TOKEN="SmokeOnly-ExistingInvite-${STAMP}"
NEW_PERMISSIONS='["can_manage_cards","can_view_analytics"]'
EXISTING_PERMISSIONS='["can_manage_team","can_manage_outlets"]'

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 -P pager=off "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q \
    -v owner_auth="$OWNER_AUTH" -v existing_auth="$EXISTING_AUTH" \
    -v owner_email="$OWNER_EMAIL" -v existing_email="$EXISTING_EMAIL" -v new_email="$NEW_EMAIL" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" IN (:'owner_auth', :'existing_auth');
DELETE FROM "BusinessInvitation" WHERE email IN (:'existing_email', :'new_email');
DELETE FROM "BusinessUser" WHERE email IN (:'owner_email', :'existing_email', :'new_email');
DELETE FROM "LegacyAuthUserPassword" WHERE "authUserId" IN (:'owner_auth', :'existing_auth');
DELETE FROM "StandaloneAuthIdentity" WHERE id IN (:'owner_auth'::uuid, :'existing_auth'::uuid);
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
  echo '=== backend-loyalty logs (last 220 lines) ===' >&2
  docker logs --tail 220 "$API_CONTAINER" 2>&1 >&2 || true
  echo >&2
  echo '=== invitation permission fixture snapshot ===' >&2
  psql_exec -v owner_email="$OWNER_EMAIL" -v existing_email="$EXISTING_EMAIL" -v new_email="$NEW_EMAIL" <<'SQL' >&2 || true
SELECT email, role, permissions, "usedAt", "revokedAt" FROM "BusinessInvitation"
WHERE email IN (:'existing_email', :'new_email') ORDER BY email;
SELECT email, "businessId", role, permissions, "isActive" FROM "BusinessUser"
WHERE email IN (:'owner_email', :'existing_email', :'new_email') ORDER BY email, "businessId";
SQL
  echo "FAIL: invitation permissions parity smoke stopped with exit code $rc." >&2
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
import json, os, sys
m=os.environ['MODE']; a=os.environ.get('VALUE1',''); b=os.environ.get('VALUE2',''); c=os.environ.get('VALUE3','')
if m == 'login': data={'email':a,'password':b}
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

extract_access() {
  python3 - "$1" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); v=(x.get('data') or {}).get('accessToken')
if not v: raise SystemExit('accessToken missing')
print(v)
PY
}

login() {
  local email="$1"
  local prefix="$2"
  local body="$TMP_DIR/$prefix.login.json"
  local out="$TMP_DIR/$prefix.login.out"
  local status
  write_json login "$body" "$email" "$FIXTURE_PASSWORD"
  status="$(post_file '/api/business/auth/login' "$body" "$out")"
  [[ "$status" == '200' ]] || { echo "ERROR: $prefix login expected 200, got $status" >&2; cat "$out" >&2; return 1; }
  extract_access "$out"
}

assert_permissions_response() {
  local file="$1"
  local email="$2"
  local expected_json="$3"
  local mode="$4"
  python3 - "$file" "$email" "$expected_json" "$mode" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); expected=json.loads(sys.argv[3]); mode=sys.argv[4]
data=x.get('data')
if mode == 'resolve':
    assert isinstance(data,dict), data
    assert data.get('email') == sys.argv[2], data
    assert data.get('permissions') == expected, (data.get('permissions'), expected)
elif mode == 'list':
    assert isinstance(data,list), data
    rows=[r for r in data if r.get('email') == sys.argv[2]]
    assert len(rows) == 1, rows
    assert rows[0].get('permissions') == expected, (rows[0].get('permissions'), expected)
else: raise SystemExit('bad mode')
PY
}

echo '==> [1/8] Checking schema, backing up target, and building API...'
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
HAS_COLUMNS="$(psql_exec -qAt <<'SQL'
SELECT count(*) FROM information_schema.columns
WHERE table_schema='public' AND column_name='permissions'
  AND table_name IN ('BusinessInvitation','BusinessUser') AND udt_name='jsonb';
SQL
)"
[[ "$HAS_COLUMNS" == '2' ]] || { echo "ERROR: BusinessInvitation/BusinessUser permissions jsonb columns are required" >&2; exit 1; }

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

echo '==> [2/8] Creating isolated owner + existing-identity fixtures...'
cleanup_fixture
read -r TARGET_BUSINESS SOURCE_BUSINESS < <(psql_exec -qAtF ' ' <<'SQL'
SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt", id LIMIT 2;
SQL
)
if [[ -z "${TARGET_BUSINESS:-}" || -z "${SOURCE_BUSINESS:-}" || "$TARGET_BUSINESS" == "$SOURCE_BUSINESS" ]]; then
  echo 'ERROR: at least two active businesses are required for isolated invitation permission smoke' >&2
  exit 1
fi

psql_exec -q \
  -v target_business="$TARGET_BUSINESS" -v source_business="$SOURCE_BUSINESS" -v fixture_hash="$FIXTURE_BCRYPT" \
  -v owner_id="$OWNER_ID" -v owner_auth="$OWNER_AUTH" -v owner_email="$OWNER_EMAIL" \
  -v existing_id="$EXISTING_ID" -v existing_auth="$EXISTING_AUTH" -v existing_email="$EXISTING_EMAIL" <<'SQL'
INSERT INTO "BusinessUser" (
  id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,
  "isActive","lastLoginAt","createdAt","updatedAt"
) VALUES
(:'owner_id', :'target_business', :'owner_auth'::uuid, :'owner_email', :'fixture_hash', 'Permission Smoke Owner', 'OWNER', NULL, true, NULL, now(), now()),
(:'existing_id', :'source_business', :'existing_auth'::uuid, :'existing_email', :'fixture_hash', 'Permission Smoke Existing', 'STAFF', '["can_manage_cards"]'::jsonb, true, NULL, now(), now());
SQL

OWNER_ACCESS="$(login "$OWNER_EMAIL" owner)"
EXISTING_ACCESS="$(login "$EXISTING_EMAIL" existing)"

echo '==> [3/8] Creating custom-permission invitations and pinning test tokens...'
write_json invite "$TMP_DIR/new-invite.json" "$NEW_EMAIL" STAFF "$NEW_PERMISSIONS"
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/new-invite.json" "$TMP_DIR/new-invite.out" "$OWNER_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: new-user invite expected 200, got $STATUS" >&2; cat "$TMP_DIR/new-invite.out" >&2; exit 1; }

write_json invite "$TMP_DIR/existing-invite.json" "$EXISTING_EMAIL" ADMIN "$EXISTING_PERMISSIONS"
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/existing-invite.json" "$TMP_DIR/existing-invite.out" "$OWNER_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: existing-user invite expected 200, got $STATUS" >&2; cat "$TMP_DIR/existing-invite.out" >&2; exit 1; }

NEW_HASH="$(printf '%s' "$NEW_TOKEN" | sha256sum | awk '{print $1}')"
EXISTING_HASH="$(printf '%s' "$EXISTING_TOKEN" | sha256sum | awk '{print $1}')"
psql_exec -q -v new_email="$NEW_EMAIL" -v existing_email="$EXISTING_EMAIL" -v new_hash="$NEW_HASH" -v existing_hash="$EXISTING_HASH" <<'SQL'
UPDATE "BusinessInvitation" SET "tokenHash"=:'new_hash', "updatedAt"=now()
WHERE email=:'new_email' AND "usedAt" IS NULL AND "revokedAt" IS NULL;
UPDATE "BusinessInvitation" SET "tokenHash"=:'existing_hash', "updatedAt"=now()
WHERE email=:'existing_email' AND "usedAt" IS NULL AND "revokedAt" IS NULL;
SQL

DB_PERMISSIONS="$(psql_exec -qAtF '|' -v new_email="$NEW_EMAIL" -v existing_email="$EXISTING_EMAIL" <<'SQL'
SELECT concat_ws('|',
  (SELECT permissions::text FROM "BusinessInvitation" WHERE email=:'new_email' AND "usedAt" IS NULL AND "revokedAt" IS NULL),
  (SELECT permissions::text FROM "BusinessInvitation" WHERE email=:'existing_email' AND "usedAt" IS NULL AND "revokedAt" IS NULL)
);
SQL
)"
python3 - "$DB_PERMISSIONS" "$NEW_PERMISSIONS" "$EXISTING_PERMISSIONS" <<'PY'
import json,sys
left,right=sys.argv[1].split('|',1)
assert json.loads(left)==json.loads(sys.argv[2]), (left,sys.argv[2])
assert json.loads(right)==json.loads(sys.argv[3]), (right,sys.argv[3])
PY
echo 'PASS: create stores exact custom permissions on both invitation rows.'

echo '==> [4/8] Verifying resolve + list expose exact permissions...'
STATUS="$(curl --silent --show-error -o "$TMP_DIR/new-resolve.out" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$NEW_TOKEN")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: new resolve expected 200, got $STATUS" >&2; cat "$TMP_DIR/new-resolve.out" >&2; exit 1; }
assert_permissions_response "$TMP_DIR/new-resolve.out" "$NEW_EMAIL" "$NEW_PERMISSIONS" resolve
STATUS="$(curl --silent --show-error -o "$TMP_DIR/existing-resolve.out" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$EXISTING_TOKEN")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: existing resolve expected 200, got $STATUS" >&2; cat "$TMP_DIR/existing-resolve.out" >&2; exit 1; }
assert_permissions_response "$TMP_DIR/existing-resolve.out" "$EXISTING_EMAIL" "$EXISTING_PERMISSIONS" resolve
STATUS="$(curl --silent --show-error -o "$TMP_DIR/list.out" -w '%{http_code}' -H "Authorization: Bearer $OWNER_ACCESS" "$BASE_URL/api/business/team/invitations")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: invite list expected 200, got $STATUS" >&2; cat "$TMP_DIR/list.out" >&2; exit 1; }
assert_permissions_response "$TMP_DIR/list.out" "$NEW_EMAIL" "$NEW_PERMISSIONS" list
assert_permissions_response "$TMP_DIR/list.out" "$EXISTING_EMAIL" "$EXISTING_PERMISSIONS" list
echo 'PASS: resolve/list contracts expose exact invitation permissions.'

echo '==> [5/8] Registering new identity and verifying permissions copied to membership...'
write_json register "$TMP_DIR/register.json" "$NEW_TOKEN" 'Permission Smoke New' "$FIXTURE_PASSWORD"
STATUS="$(post_file '/api/business/team/invitations/register' "$TMP_DIR/register.json" "$TMP_DIR/register.out")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: register expected 200, got $STATUS" >&2; cat "$TMP_DIR/register.out" >&2; exit 1; }
NEW_MEM_PERMS="$(psql_exec -qAt -v business_id="$TARGET_BUSINESS" -v email="$NEW_EMAIL" <<'SQL'
SELECT permissions::text FROM "BusinessUser" WHERE "businessId"=:'business_id' AND email=:'email' AND "isActive"=true;
SQL
)"
python3 - "$NEW_MEM_PERMS" "$NEW_PERMISSIONS" <<'PY'
import json,sys
assert json.loads(sys.argv[1]) == json.loads(sys.argv[2]), (sys.argv[1],sys.argv[2])
PY
echo 'PASS: register copies exact invitation permissions to new BusinessUser.'

echo '==> [6/8] Accepting as existing identity and verifying target membership permissions...'
write_json accept "$TMP_DIR/accept.json" "$EXISTING_TOKEN"
STATUS="$(post_file '/api/business/team/invitations/accept' "$TMP_DIR/accept.json" "$TMP_DIR/accept.out" "$EXISTING_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: accept existing expected 200, got $STATUS" >&2; cat "$TMP_DIR/accept.out" >&2; exit 1; }
EXISTING_TARGET_STATE="$(psql_exec -qAtF '|' -v business_id="$TARGET_BUSINESS" -v email="$EXISTING_EMAIL" <<'SQL'
SELECT concat_ws('|', role, permissions::text)
FROM "BusinessUser" WHERE "businessId"=:'business_id' AND email=:'email' AND "isActive"=true;
SQL
)"
python3 - "$EXISTING_TARGET_STATE" "$EXISTING_PERMISSIONS" <<'PY'
import json,sys
role,perms=sys.argv[1].split('|',1)
assert role.upper()=='ADMIN', role
assert json.loads(perms)==json.loads(sys.argv[2]), (perms,sys.argv[2])
PY
echo 'PASS: existing identity accept copies role + exact invitation permissions to target membership.'

echo '==> [7/8] Verifying both invitations were consumed exactly once...'
CONSUMED="$(psql_exec -qAt -v new_email="$NEW_EMAIL" -v existing_email="$EXISTING_EMAIL" <<'SQL'
SELECT count(*) FROM "BusinessInvitation"
WHERE email IN (:'new_email', :'existing_email') AND "usedAt" IS NOT NULL AND "revokedAt" IS NULL;
SQL
)"
[[ "$CONSUMED" == '2' ]] || { echo "ERROR: expected 2 consumed invites, got $CONSUMED" >&2; exit 1; }
echo 'PASS: both custom-permission invitations are single-use consumed.'

echo '==> [8/8] Final health checks...'
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo 'PASS: custom invitation permission parity deployed and smoke-tested end-to-end.'
echo "Backup: $BACKUP"
