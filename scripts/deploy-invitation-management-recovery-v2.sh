#!/usr/bin/env bash
set -euo pipefail

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${ENV_FILE:-.env.vps}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.vps.yml}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
API_CONTAINER="${API_CONTAINER:-backend-loyalty}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
set -a; source "$ENV_FILE"; set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-invite-management-${STAMP}.dump"
TMP="$(mktemp -d /tmp/loyalty-invite-v2.XXXXXX)"; chmod 700 "$TMP"

FIXTURE_PASSWORD='SmokeOnly-Refresh-2026!'
FIXTURE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
OWNER_ID="team-mgmt-owner-${STAMP}"; OWNER_AUTH="$(python3 -c 'import uuid;print(uuid.uuid4())')"; OWNER_EMAIL="team-mgmt-owner-${STAMP}@example.invalid"
ADMIN_ID="team-mgmt-admin-${STAMP}"; ADMIN_AUTH="$(python3 -c 'import uuid;print(uuid.uuid4())')"; ADMIN_EMAIL="team-mgmt-admin-${STAMP}@example.invalid"
DENIED_ID="team-mgmt-denied-${STAMP}"; DENIED_AUTH="$(python3 -c 'import uuid;print(uuid.uuid4())')"; DENIED_EMAIL="team-mgmt-denied-${STAMP}@example.invalid"
INVITED_EMAIL="team-mgmt-invited-${STAMP}@example.invalid"; INVITE_TOKEN="SmokeOnly-TeamInvite-${STAMP}"
LEGACY_ID="legacyrecovery${STAMP//-/}"; LEGACY_EMAIL="legacy-recovery-${STAMP}@example.invalid"; LEGACY_PASSWORD='SmokeOnly-Legacy-Recovered-2026!'; RESET_TOKEN="SmokeOnly-LegacyReset-${STAMP}"

psql_exec(){ docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"; }

cleanup_fixture(){
  set +e
  psql_exec -q -v oa="$OWNER_AUTH" -v aa="$ADMIN_AUTH" -v da="$DENIED_AUTH" -v lid="$LEGACY_ID" -v oe="$OWNER_EMAIL" -v ae="$ADMIN_EMAIL" -v de="$DENIED_EMAIL" -v ie="$INVITED_EMAIL" -v le="$LEGACY_EMAIL" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" IN (:'oa',:'aa',:'da',:'lid');
DELETE FROM "AuthPasswordReset" WHERE "userId" IN (:'oa',:'aa',:'da',:'lid');
DELETE FROM "BusinessInvitation" WHERE email IN (:'oe',:'ae',:'de',:'ie',:'le');
DELETE FROM "BusinessUser" WHERE email IN (:'oe',:'ae',:'de',:'le');
SQL
}
cleanup(){ rc=$?; cleanup_fixture; rm -rf "$TMP"; exit "$rc"; }
trap cleanup EXIT

diagnostics(){
  rc=$?; set +e
  echo; echo "=== backend-loyalty logs (last 180 lines) ===" >&2
  docker logs --tail 180 "$API_CONTAINER" 2>&1 >&2 || true
  echo "FAIL: invitation management/recovery v2 stopped with exit code $rc." >&2
  exit "$rc"
}
trap diagnostics ERR

json(){ MODE="$1" V1="${3:-}" V2="${4:-}" python3 - "$2" <<'PY'
import json,os,sys
m=os.environ['MODE']; a=os.environ.get('V1',''); b=os.environ.get('V2','')
d={'login':{'email':a,'password':b},'invite':{'email':a,'role':b},'revoke':{'inviteId':a},'reset':{'token':a,'newPassword':b}}[m]
with open(sys.argv[1],'w') as f: json.dump(d,f)
PY
chmod 600 "$2"; }
post(){ ep="$1" body="$2" out="$3" bearer="${4:-}"; if [[ -n "$bearer" ]]; then curl -sS -o "$out" -w '%{http_code}' -H 'Content-Type: application/json' -H "Authorization: Bearer $bearer" --data-binary "@$body" "$BASE_URL$ep"; else curl -sS -o "$out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary "@$body" "$BASE_URL$ep"; fi; }
field(){ python3 - "$1" "$2" <<'PY'
import json,sys
with open(sys.argv[1]) as f:d=json.load(f)
v=(d.get('data') or {}).get(sys.argv[2]); assert v is not None,sys.argv[2]+' missing'; print(v)
PY
}
login(){ json login "$TMP/$3.login.json" "$1" "$2"; s="$(post /api/business/auth/login "$TMP/$3.login.json" "$TMP/$3.login.out.json")"; [[ "$s" == 200 ]] || { echo "ERROR: $3 login got $s" >&2; cat "$TMP/$3.login.out.json" >&2; return 1; }; field "$TMP/$3.login.out.json" accessToken; }

echo "==> [1/9] Schema check + backup..."
docker inspect "$DB_CONTAINER" >/dev/null; psql_exec -Atqc 'SELECT 1' >/dev/null
HAS_PERMS="$(psql_exec -qAt <<'SQL'
SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='BusinessUser' AND column_name='permissions' AND udt_name='jsonb');
SQL
)"
[[ "$HAS_PERMS" == t ]] || { echo "ERROR: BusinessUser.permissions jsonb missing" >&2; exit 1; }
docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"; test -s "$BACKUP"; echo "BACKUP OK: $BACKUP"

echo "==> [2/9] Build + restart API..."
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api
for i in $(seq 1 30); do curl --fail -s "$BASE_URL/health" >/dev/null && curl --fail -s "$BASE_URL/health/db" >/dev/null && break; [[ "$i" -lt 30 ]] || exit 1; sleep 2; done

echo "==> [3/9] Create permission fixtures..."
cleanup_fixture
BUSINESS_ID="$(psql_exec -qAtc 'SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt",id LIMIT 1')"; [[ -n "$BUSINESS_ID" ]] || exit 1
psql_exec -q -v bid="$BUSINESS_ID" -v oi="$OWNER_ID" -v oa="$OWNER_AUTH" -v oe="$OWNER_EMAIL" -v ai="$ADMIN_ID" -v aa="$ADMIN_AUTH" -v ae="$ADMIN_EMAIL" -v di="$DENIED_ID" -v da="$DENIED_AUTH" -v de="$DENIED_EMAIL" -v h="$FIXTURE_BCRYPT" <<'SQL'
INSERT INTO "BusinessUser" (id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,"isActive","lastLoginAt","createdAt","updatedAt") VALUES
(:'oi',:'bid',:'oa'::uuid,:'oe',:'h','Invite Mgmt Owner','OWNER',NULL,true,NULL,now(),now()),
(:'ai',:'bid',:'aa'::uuid,:'ae',:'h','Invite Mgmt Admin','ADMIN','["can_manage_team"]'::jsonb,true,NULL,now(),now()),
(:'di',:'bid',:'da'::uuid,:'de',:'h','Invite Mgmt Denied','ADMIN','[]'::jsonb,true,NULL,now(),now());
SQL
OWNER_ACCESS="$(login "$OWNER_EMAIL" "$FIXTURE_PASSWORD" owner)"; ADMIN_ACCESS="$(login "$ADMIN_EMAIL" "$FIXTURE_PASSWORD" admin)"; DENIED_ACCESS="$(login "$DENIED_EMAIL" "$FIXTURE_PASSWORD" denied)"

echo "==> [4/9] List + permission semantics..."
json invite "$TMP/invite.json" "$INVITED_EMAIL" STAFF
S="$(post /api/business/team/invitations "$TMP/invite.json" "$TMP/invite.out" "$OWNER_ACCESS")"; [[ "$S" == 200 ]] || { cat "$TMP/invite.out"; exit 1; }
INVITE_ID="$(psql_exec -qAt -v bid="$BUSINESS_ID" -v e="$INVITED_EMAIL" <<'SQL'
SELECT id::text FROM "BusinessInvitation" WHERE "businessId"=:'bid' AND email=:'e' AND "usedAt" IS NULL AND "revokedAt" IS NULL ORDER BY "createdAt" DESC LIMIT 1;
SQL
)"; [[ -n "$INVITE_ID" ]] || exit 1
H="$(printf '%s' "$INVITE_TOKEN"|sha256sum|awk '{print $1}')"; psql_exec -q -v id="$INVITE_ID" -v h="$H" <<'SQL'
UPDATE "BusinessInvitation" SET "tokenHash"=:'h',"updatedAt"=now() WHERE id=:'id'::uuid;
SQL
S="$(curl -sS -o "$TMP/list-owner" -w '%{http_code}' -H "Authorization: Bearer $OWNER_ACCESS" "$BASE_URL/api/business/team/invitations")"; [[ "$S" == 200 ]] || exit 1
python3 - "$TMP/list-owner" "$INVITED_EMAIL" <<'PY'
import json,sys
with open(sys.argv[1]) as f:d=json.load(f)
assert any(x.get('email')==sys.argv[2] for x in (d.get('data') or []))
PY
S="$(curl -sS -o "$TMP/list-admin" -w '%{http_code}' -H "Authorization: Bearer $ADMIN_ACCESS" "$BASE_URL/api/business/team/invitations")"; [[ "$S" == 200 ]] || exit 1
S="$(curl -sS -o "$TMP/list-denied" -w '%{http_code}' -H "Authorization: Bearer $DENIED_ACCESS" "$BASE_URL/api/business/team/invitations")"; [[ "$S" == 403 ]] || { echo "ERROR: explicit [] admin expected 403 got $S" >&2; cat "$TMP/list-denied" >&2; exit 1; }
echo "PASS: invitation list honors OWNER and can_manage_team permissions."

echo "==> [5/9] Revoke + token invalidation..."
json revoke "$TMP/revoke.json" "$INVITE_ID"
S="$(post /api/business/team/invitations/revoke "$TMP/revoke.json" "$TMP/revoke.out" "$OWNER_ACCESS")"; [[ "$S" == 200 ]] || { cat "$TMP/revoke.out"; exit 1; }
S="$(curl -sS -o "$TMP/resolve" -w '%{http_code}' "$BASE_URL/api/business/team/invitations/resolve?token=$INVITE_TOKEN")"; [[ "$S" == 410 ]] || { echo "ERROR: revoked resolve expected 410 got $S" >&2; exit 1; }
echo "PASS: revoked invite is invalid immediately."

echo "==> [6/9] Create missing-bridge CUID fixture..."
psql_exec -q -v lid="$LEGACY_ID" -v bid="$BUSINESS_ID" -v le="$LEGACY_EMAIL" <<'SQL'
INSERT INTO "BusinessUser" (id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,"isActive","lastLoginAt","createdAt","updatedAt") VALUES
(:'lid',:'bid',NULL,:'le','managed-by-supabase-auth','Legacy Recovery Smoke','OWNER',NULL,true,NULL,now(),now());
SQL
C="$(psql_exec -qAt -v lid="$LEGACY_ID" -c 'SELECT count(*) FROM "LegacyAuthUserPassword" WHERE "authUserId"=:'"'"'lid'"'"'')"; [[ "$C" == 0 ]] || exit 1

echo "==> [7/9] Standalone reset recovers missing bridge..."
RH="$(printf '%s' "$RESET_TOKEN"|sha256sum|awk '{print $1}')"
psql_exec -q -v lid="$LEGACY_ID" -v le="$LEGACY_EMAIL" -v rh="$RH" <<'SQL'
INSERT INTO "AuthPasswordReset" (id,"userId","authKind",email,"tokenHash","expiresAt","usedAt",ip,"userAgent","createdAt") VALUES
(extensions.uuid_generate_v4(),:'lid','business',:'le',:'rh',now()+interval '10 minutes',NULL,NULL,'invite-management-recovery-v2',now());
SQL
json reset "$TMP/reset.json" "$RESET_TOKEN" "$LEGACY_PASSWORD"
S="$(post /api/business/auth/reset-password "$TMP/reset.json" "$TMP/reset.out")"; [[ "$S" == 200 ]] || { cat "$TMP/reset.out"; exit 1; }
LEGACY_ACCESS="$(login "$LEGACY_EMAIL" "$LEGACY_PASSWORD" legacy)"; [[ -n "$LEGACY_ACCESS" ]] || exit 1
OK="$(psql_exec -qAt -v lid="$LEGACY_ID" -c 'SELECT "passwordHash" LIKE '"'"'$2%'"'"' FROM "BusinessUser" WHERE id=:'"'"'lid'"'"'')"; [[ "$OK" == t ]] || exit 1
echo "PASS: missing-bridge CUID account resets and logs in standalone."

echo "==> [8/9] Reset replay + no fabricated bridge..."
S="$(post /api/business/auth/reset-password "$TMP/reset.json" "$TMP/reset-replay.out")"; [[ "$S" == 400 ]] || exit 1
C="$(psql_exec -qAt -v lid="$LEGACY_ID" -c 'SELECT count(*) FROM "LegacyAuthUserPassword" WHERE "authUserId"=:'"'"'lid'"'"'')"; [[ "$C" == 0 ]] || exit 1
echo "PASS: recovery is single-use and does not fabricate bridge credentials."

echo "==> [9/9] Final health..."
curl --fail -sS "$BASE_URL/health"; echo; curl --fail -sS "$BASE_URL/health/db"; echo
trap - ERR
echo "PASS: invitation management + legacy missing-bridge recovery deployed and smoke-tested."
echo "Backup: $BACKUP"
