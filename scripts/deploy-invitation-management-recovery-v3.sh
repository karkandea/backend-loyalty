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
BACKUP="/root/loyalty-pre-invite-management-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-invite-management-v3.XXXXXX)"
chmod 700 "$TMP_DIR"

FIXTURE_PASSWORD='SmokeOnly-Refresh-2026!'
FIXTURE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
OWNER_ID="team-mgmt-owner-${STAMP}"
OWNER_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
OWNER_EMAIL="team-mgmt-owner-${STAMP}@example.invalid"
ADMIN_ID="team-mgmt-admin-${STAMP}"
ADMIN_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
ADMIN_EMAIL="team-mgmt-admin-${STAMP}@example.invalid"
DENIED_ID="team-mgmt-denied-${STAMP}"
DENIED_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
DENIED_EMAIL="team-mgmt-denied-${STAMP}@example.invalid"
INVITED_EMAIL="team-mgmt-invited-${STAMP}@example.invalid"
INVITE_TOKEN="SmokeOnly-TeamInvite-${STAMP}"
LEGACY_ID="legacyrecovery${STAMP//-/}"
LEGACY_EMAIL="legacy-recovery-${STAMP}@example.invalid"
LEGACY_PASSWORD='SmokeOnly-Legacy-Recovered-2026!'
RESET_TOKEN="SmokeOnly-LegacyReset-${STAMP}"

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q \
    -v owner_auth="$OWNER_AUTH" \
    -v admin_auth="$ADMIN_AUTH" \
    -v denied_auth="$DENIED_AUTH" \
    -v legacy_id="$LEGACY_ID" \
    -v owner_email="$OWNER_EMAIL" \
    -v admin_email="$ADMIN_EMAIL" \
    -v denied_email="$DENIED_EMAIL" \
    -v invited_email="$INVITED_EMAIL" \
    -v legacy_email="$LEGACY_EMAIL" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession"
WHERE "userId" IN (:'owner_auth', :'admin_auth', :'denied_auth', :'legacy_id');
DELETE FROM "AuthPasswordReset"
WHERE "userId" IN (:'owner_auth', :'admin_auth', :'denied_auth', :'legacy_id');
DELETE FROM "BusinessInvitation"
WHERE email IN (:'owner_email', :'admin_email', :'denied_email', :'invited_email', :'legacy_email');
DELETE FROM "BusinessUser"
WHERE email IN (:'owner_email', :'admin_email', :'denied_email', :'legacy_email');
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
  echo "=== invitation/recovery fixture snapshot ===" >&2
  psql_exec -P pager=off \
    -v owner_email="$OWNER_EMAIL" \
    -v admin_email="$ADMIN_EMAIL" \
    -v denied_email="$DENIED_EMAIL" \
    -v invited_email="$INVITED_EMAIL" \
    -v legacy_email="$LEGACY_EMAIL" <<'SQL' >&2 || true
SELECT email, role, permissions, "authUserId"::text,
       CASE WHEN "passwordHash" LIKE '$2%' THEN 'bcrypt' ELSE "passwordHash" END AS password_state
FROM "BusinessUser"
WHERE email IN (:'owner_email', :'admin_email', :'denied_email', :'legacy_email')
ORDER BY email;
SELECT id::text, email, role, "usedAt" IS NOT NULL AS used,
       "revokedAt" IS NOT NULL AS revoked, length("tokenHash") AS hash_len
FROM "BusinessInvitation"
WHERE email = :'invited_email';
SQL
  echo "FAIL: invitation management/recovery v3 stopped with exit code $rc." >&2
  exit "$rc"
}
trap diagnostics ERR

write_json() {
  local mode="$1" file="$2" value1="${3:-}" value2="${4:-}"
  MODE="$mode" VALUE1="$value1" VALUE2="$value2" python3 - "$file" <<'PY'
import json, os, sys
m=os.environ['MODE']; a=os.environ.get('VALUE1',''); b=os.environ.get('VALUE2','')
if m == 'login': data={'email':a,'password':b}
elif m == 'invite': data={'email':a,'role':b}
elif m == 'revoke': data={'inviteId':a}
elif m == 'reset': data={'token':a,'newPassword':b}
else: raise SystemExit('unknown json mode')
with open(sys.argv[1],'w',encoding='utf-8') as f: json.dump(data,f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1" body="$2" output="$3" bearer="${4:-}"
  if [[ -n "$bearer" ]]; then
    curl --silent --show-error -o "$output" -w '%{http_code}' \
      -H 'Content-Type: application/json' \
      -H "Authorization: Bearer $bearer" \
      --data-binary "@$body" "$BASE_URL$endpoint"
  else
    curl --silent --show-error -o "$output" -w '%{http_code}' \
      -H 'Content-Type: application/json' \
      --data-binary "@$body" "$BASE_URL$endpoint"
  fi
}

extract_field() {
  python3 - "$1" "$2" <<'PY'
import json,sys
with open(sys.argv[1],encoding='utf-8') as f: data=json.load(f)
value=(data.get('data') or {}).get(sys.argv[2])
if value is None: raise SystemExit(sys.argv[2]+' missing')
print(value)
PY
}

login() {
  local email="$1" password="$2" prefix="$3"
  local body="$TMP_DIR/${prefix}.login.json" output="$TMP_DIR/${prefix}.login.out.json" status
  write_json login "$body" "$email" "$password"
  status="$(post_file '/api/business/auth/login' "$body" "$output")"
  [[ "$status" == "200" ]] || {
    echo "ERROR: $prefix login expected 200, got $status" >&2
    cat "$output" >&2
    return 1
  }
  extract_field "$output" accessToken
}

echo "==> [1/9] Checking target schema and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
HAS_PERMISSIONS="$(psql_exec -qAt <<'SQL'
SELECT EXISTS (
  SELECT 1 FROM information_schema.columns
  WHERE table_schema='public'
    AND table_name='BusinessUser'
    AND column_name='permissions'
    AND udt_name='jsonb'
);
SQL
)"
[[ "$HAS_PERMISSIONS" == "t" ]] || { echo "ERROR: BusinessUser.permissions jsonb column is required" >&2; exit 1; }

docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/9] Building and restarting Loyalty API..."
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api
for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null && curl --fail --silent "$BASE_URL/health/db" >/dev/null; then
    break
  fi
  [[ "$attempt" -lt 30 ]] || { echo "ERROR: API did not become healthy" >&2; exit 1; }
  sleep 2
done

echo "==> [3/9] Creating isolated permission fixtures..."
cleanup_fixture
BUSINESS_ID="$(psql_exec -qAt <<'SQL'
SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt", id LIMIT 1;
SQL
)"
[[ -n "$BUSINESS_ID" ]] || { echo "ERROR: no active business available" >&2; exit 1; }

psql_exec -q \
  -v business_id="$BUSINESS_ID" \
  -v owner_id="$OWNER_ID" -v owner_auth="$OWNER_AUTH" -v owner_email="$OWNER_EMAIL" \
  -v admin_id="$ADMIN_ID" -v admin_auth="$ADMIN_AUTH" -v admin_email="$ADMIN_EMAIL" \
  -v denied_id="$DENIED_ID" -v denied_auth="$DENIED_AUTH" -v denied_email="$DENIED_EMAIL" \
  -v fixture_hash="$FIXTURE_BCRYPT" <<'SQL'
INSERT INTO "BusinessUser" (
  id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,
  "isActive","lastLoginAt","createdAt","updatedAt"
) VALUES
(:'owner_id', :'business_id', :'owner_auth'::uuid, :'owner_email', :'fixture_hash', 'Invite Mgmt Owner', 'OWNER', NULL, true, NULL, now(), now()),
(:'admin_id', :'business_id', :'admin_auth'::uuid, :'admin_email', :'fixture_hash', 'Invite Mgmt Admin', 'ADMIN', '["can_manage_team"]'::jsonb, true, NULL, now(), now()),
(:'denied_id', :'business_id', :'denied_auth'::uuid, :'denied_email', :'fixture_hash', 'Invite Mgmt Denied', 'ADMIN', '[]'::jsonb, true, NULL, now(), now());
SQL

OWNER_ACCESS="$(login "$OWNER_EMAIL" "$FIXTURE_PASSWORD" owner)"
ADMIN_ACCESS="$(login "$ADMIN_EMAIL" "$FIXTURE_PASSWORD" admin)"
DENIED_ACCESS="$(login "$DENIED_EMAIL" "$FIXTURE_PASSWORD" denied)"

echo "==> [4/9] Testing invitation list and permission semantics..."
write_json invite "$TMP_DIR/invite.json" "$INVITED_EMAIL" STAFF
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/invite.json" "$TMP_DIR/invite.out.json" "$OWNER_ACCESS")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: invite create got $STATUS" >&2; cat "$TMP_DIR/invite.out.json" >&2; exit 1; }

INVITE_ID="$(psql_exec -qAt -v business_id="$BUSINESS_ID" -v invited_email="$INVITED_EMAIL" <<'SQL'
SELECT id::text
FROM "BusinessInvitation"
WHERE "businessId"=:'business_id'
  AND email=:'invited_email'
  AND "usedAt" IS NULL
  AND "revokedAt" IS NULL
ORDER BY "createdAt" DESC
LIMIT 1;
SQL
)"
[[ -n "$INVITE_ID" ]] || { echo "ERROR: created invitation row not found" >&2; exit 1; }
INVITE_HASH="$(printf '%s' "$INVITE_TOKEN" | sha256sum | awk '{print $1}')"
psql_exec -q -v invite_id="$INVITE_ID" -v invite_hash="$INVITE_HASH" <<'SQL'
UPDATE "BusinessInvitation"
SET "tokenHash"=:'invite_hash', "updatedAt"=now()
WHERE id=:'invite_id'::uuid;
SQL

STATUS="$(curl --silent --show-error -o "$TMP_DIR/list-owner.json" -w '%{http_code}' -H "Authorization: Bearer $OWNER_ACCESS" "$BASE_URL/api/business/team/invitations")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: owner list got $STATUS" >&2; cat "$TMP_DIR/list-owner.json" >&2; exit 1; }
python3 - "$TMP_DIR/list-owner.json" "$INVITED_EMAIL" <<'PY'
import json,sys
with open(sys.argv[1]) as f: data=json.load(f)
assert any(row.get('email') == sys.argv[2] for row in (data.get('data') or [])), 'invite missing from owner list'
PY

STATUS="$(curl --silent --show-error -o "$TMP_DIR/list-admin.json" -w '%{http_code}' -H "Authorization: Bearer $ADMIN_ACCESS" "$BASE_URL/api/business/team/invitations")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: allowed admin list got $STATUS" >&2; cat "$TMP_DIR/list-admin.json" >&2; exit 1; }
STATUS="$(curl --silent --show-error -o "$TMP_DIR/list-denied.json" -w '%{http_code}' -H "Authorization: Bearer $DENIED_ACCESS" "$BASE_URL/api/business/team/invitations")"
[[ "$STATUS" == "403" ]] || { echo "ERROR: explicit [] admin expected 403, got $STATUS" >&2; cat "$TMP_DIR/list-denied.json" >&2; exit 1; }
echo "PASS: invitation list honors OWNER and can_manage_team permission semantics."

echo "==> [5/9] Testing revoke and immediate token invalidation..."
write_json revoke "$TMP_DIR/revoke.json" "$INVITE_ID"
STATUS="$(post_file '/api/business/team/invitations/revoke' "$TMP_DIR/revoke.json" "$TMP_DIR/revoke.out.json" "$OWNER_ACCESS")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: revoke got $STATUS" >&2; cat "$TMP_DIR/revoke.out.json" >&2; exit 1; }
STATUS="$(curl --silent --show-error -o "$TMP_DIR/resolve-revoked.json" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$INVITE_TOKEN")"
[[ "$STATUS" == "410" ]] || { echo "ERROR: revoked token expected 410, got $STATUS" >&2; cat "$TMP_DIR/resolve-revoked.json" >&2; exit 1; }
echo "PASS: revoked invitation is immediately unusable."

echo "==> [6/9] Creating missing-bridge CUID fixture..."
psql_exec -q -v legacy_id="$LEGACY_ID" -v business_id="$BUSINESS_ID" -v legacy_email="$LEGACY_EMAIL" <<'SQL'
INSERT INTO "BusinessUser" (
  id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,
  "isActive","lastLoginAt","createdAt","updatedAt"
) VALUES (
  :'legacy_id', :'business_id', NULL, :'legacy_email', 'managed-by-supabase-auth',
  'Legacy Recovery Smoke', 'OWNER', NULL, true, NULL, now(), now()
);
SQL
BRIDGE_COUNT="$(psql_exec -qAt -v legacy_id="$LEGACY_ID" <<'SQL'
SELECT count(*) FROM "LegacyAuthUserPassword" WHERE "authUserId"=:'legacy_id';
SQL
)"
[[ "$BRIDGE_COUNT" == "0" ]] || { echo "ERROR: fixture unexpectedly has bridge row" >&2; exit 1; }

echo "==> [7/9] Testing standalone reset recovery for missing bridge..."
RESET_HASH="$(printf '%s' "$RESET_TOKEN" | sha256sum | awk '{print $1}')"
psql_exec -q -v legacy_id="$LEGACY_ID" -v legacy_email="$LEGACY_EMAIL" -v reset_hash="$RESET_HASH" <<'SQL'
INSERT INTO "AuthPasswordReset" (
  id,"userId","authKind",email,"tokenHash","expiresAt","usedAt",ip,"userAgent","createdAt"
) VALUES (
  extensions.uuid_generate_v4(), :'legacy_id', 'business', :'legacy_email', :'reset_hash',
  now()+interval '10 minutes', NULL, NULL, 'invite-management-recovery-v3', now()
);
SQL
write_json reset "$TMP_DIR/reset.json" "$RESET_TOKEN" "$LEGACY_PASSWORD"
STATUS="$(post_file '/api/business/auth/reset-password' "$TMP_DIR/reset.json" "$TMP_DIR/reset.out.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: missing-bridge reset got $STATUS" >&2; cat "$TMP_DIR/reset.out.json" >&2; exit 1; }
LEGACY_ACCESS="$(login "$LEGACY_EMAIL" "$LEGACY_PASSWORD" legacy-recovered)"
[[ -n "$LEGACY_ACCESS" ]] || { echo "ERROR: recovered account did not receive access token" >&2; exit 1; }
HASH_OK="$(psql_exec -qAt -v legacy_id="$LEGACY_ID" <<'SQL'
SELECT "passwordHash" LIKE '$2%' FROM "BusinessUser" WHERE id=:'legacy_id';
SQL
)"
[[ "$HASH_OK" == "t" ]] || { echo "ERROR: recovered account did not persist bcrypt" >&2; exit 1; }
echo "PASS: missing-bridge CUID account resets and logs in standalone."

echo "==> [8/9] Testing reset single-use and no fabricated bridge..."
STATUS="$(post_file '/api/business/auth/reset-password' "$TMP_DIR/reset.json" "$TMP_DIR/reset-replay.out.json")"
[[ "$STATUS" == "400" ]] || { echo "ERROR: reset replay expected 400, got $STATUS" >&2; cat "$TMP_DIR/reset-replay.out.json" >&2; exit 1; }
BRIDGE_COUNT="$(psql_exec -qAt -v legacy_id="$LEGACY_ID" <<'SQL'
SELECT count(*) FROM "LegacyAuthUserPassword" WHERE "authUserId"=:'legacy_id';
SQL
)"
[[ "$BRIDGE_COUNT" == "0" ]] || { echo "ERROR: recovery fabricated a bridge row" >&2; exit 1; }
echo "PASS: recovery is single-use and does not fabricate bridge credentials."

echo "==> [9/9] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

trap - ERR
echo
echo "PASS: invitation management + legacy missing-bridge recovery deployed and smoke-tested."
echo "Backup: $BACKUP"
