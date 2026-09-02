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
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-rate-limit-team-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-rate-limit-team.XXXXXX)"
chmod 700 "$TMP_DIR"

FIXTURE_PASSWORD='SmokeOnly-Refresh-2026!'
FIXTURE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
OWNER_ID="rate-owner-${STAMP}"
OWNER_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
OWNER_EMAIL="rate-owner-${STAMP}@example.invalid"
ADMIN_ID="rate-admin-${STAMP}"
ADMIN_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
ADMIN_EMAIL="rate-admin-${STAMP}@example.invalid"
DENIED_ID="rate-denied-${STAMP}"
DENIED_AUTH="$(python3 -c 'import uuid; print(uuid.uuid4())')"
DENIED_EMAIL="rate-denied-${STAMP}@example.invalid"
OWNER_INVITE="rate-owner-invite-${STAMP}@example.invalid"
ADMIN_INVITE="rate-admin-invite-${STAMP}@example.invalid"
DENIED_INVITE="rate-denied-invite-${STAMP}@example.invalid"
RATE_EMAIL="rate-limit-${STAMP}@example.invalid"
RATE_SLUG="rate-limit-${STAMP}"

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q \
    -v owner_auth="$OWNER_AUTH" -v admin_auth="$ADMIN_AUTH" -v denied_auth="$DENIED_AUTH" \
    -v owner_email="$OWNER_EMAIL" -v admin_email="$ADMIN_EMAIL" -v denied_email="$DENIED_EMAIL" \
    -v owner_invite="$OWNER_INVITE" -v admin_invite="$ADMIN_INVITE" -v denied_invite="$DENIED_INVITE" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" IN (:'owner_auth', :'admin_auth', :'denied_auth');
DELETE FROM "BusinessInvitation" WHERE email IN (:'owner_invite', :'admin_invite', :'denied_invite');
DELETE FROM "BusinessUser" WHERE email IN (:'owner_email', :'admin_email', :'denied_email');
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
  echo '=== permission fixture snapshot ===' >&2
  psql_exec -P pager=off -v owner_email="$OWNER_EMAIL" -v admin_email="$ADMIN_EMAIL" -v denied_email="$DENIED_EMAIL" \
    -v owner_invite="$OWNER_INVITE" -v admin_invite="$ADMIN_INVITE" -v denied_invite="$DENIED_INVITE" <<'SQL' >&2 || true
SELECT email, role, permissions, "isActive" FROM "BusinessUser"
WHERE email IN (:'owner_email', :'admin_email', :'denied_email') ORDER BY email;
SELECT email, role, "usedAt", "revokedAt" FROM "BusinessInvitation"
WHERE email IN (:'owner_invite', :'admin_invite', :'denied_invite') ORDER BY email;
SQL
  echo "FAIL: rate-limit/team-permission smoke stopped with exit code $rc." >&2
  exit "$rc"
}
trap fail_diag ERR

write_json() {
  local mode="$1" file="$2" value1="${3:-}" value2="${4:-}"
  MODE="$mode" VALUE1="$value1" VALUE2="$value2" python3 - "$file" <<'PY'
import json, os, sys
m=os.environ['MODE']; a=os.environ.get('VALUE1',''); b=os.environ.get('VALUE2','')
if m == 'login': data={'email':a,'password':b}
elif m == 'invite': data={'email':a,'role':b}
elif m == 'resend': data={'email':a,'slug':b}
else: raise SystemExit('unknown json mode')
with open(sys.argv[1],'w',encoding='utf-8') as f: json.dump(data,f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1" body="$2" output="$3" bearer="${4:-}"
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

assert_error_code() {
  python3 - "$1" "$2" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); actual=(x.get('error') or {}).get('code')
assert actual == sys.argv[2], f'expected error.code={sys.argv[2]}, got {actual}'
PY
}

echo '==> [1/7] Backing up target and building API...'
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api
for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null && curl --fail --silent "$BASE_URL/health/db" >/dev/null; then
    break
  fi
  [[ "$attempt" -lt 30 ]] || { echo 'ERROR: API did not become healthy' >&2; exit 1; }
  sleep 2
done

echo '==> [2/7] Creating isolated OWNER / allowed ADMIN / denied ADMIN fixtures...'
cleanup_fixture
BUSINESS_ID="$(psql_exec -qAt <<'SQL'
SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt", id LIMIT 1;
SQL
)"
[[ -n "$BUSINESS_ID" ]] || { echo 'ERROR: no active business available' >&2; exit 1; }

psql_exec -q \
  -v business_id="$BUSINESS_ID" -v fixture_hash="$FIXTURE_BCRYPT" \
  -v owner_id="$OWNER_ID" -v owner_auth="$OWNER_AUTH" -v owner_email="$OWNER_EMAIL" \
  -v admin_id="$ADMIN_ID" -v admin_auth="$ADMIN_AUTH" -v admin_email="$ADMIN_EMAIL" \
  -v denied_id="$DENIED_ID" -v denied_auth="$DENIED_AUTH" -v denied_email="$DENIED_EMAIL" <<'SQL'
INSERT INTO "BusinessUser" (
  id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,
  "isActive","lastLoginAt","createdAt","updatedAt"
) VALUES
(:'owner_id', :'business_id', :'owner_auth'::uuid, :'owner_email', :'fixture_hash', 'Rate Smoke Owner', 'OWNER', NULL, true, NULL, now(), now()),
(:'admin_id', :'business_id', :'admin_auth'::uuid, :'admin_email', :'fixture_hash', 'Rate Smoke Admin', 'ADMIN', '["can_manage_team"]'::jsonb, true, NULL, now(), now()),
(:'denied_id', :'business_id', :'denied_auth'::uuid, :'denied_email', :'fixture_hash', 'Rate Smoke Denied', 'ADMIN', '[]'::jsonb, true, NULL, now(), now());
SQL

OWNER_ACCESS="$(login "$OWNER_EMAIL" owner)"
ADMIN_ACCESS="$(login "$ADMIN_EMAIL" admin)"
DENIED_ACCESS="$(login "$DENIED_EMAIL" denied)"

echo '==> [3/7] Verifying create-invite uses can_manage_team semantics...'
write_json invite "$TMP_DIR/denied-invite.json" "$DENIED_INVITE" STAFF
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/denied-invite.json" "$TMP_DIR/denied-invite.out" "$DENIED_ACCESS")"
[[ "$STATUS" == '403' ]] || { echo "ERROR: explicit [] ADMIN create expected 403, got $STATUS" >&2; cat "$TMP_DIR/denied-invite.out" >&2; exit 1; }
DENIED_ROWS="$(psql_exec -qAt -v email="$DENIED_INVITE" <<'SQL'
SELECT count(*) FROM "BusinessInvitation" WHERE email=:'email';
SQL
)"
[[ "$DENIED_ROWS" == '0' ]] || { echo 'ERROR: denied ADMIN still created an invitation row' >&2; exit 1; }

write_json invite "$TMP_DIR/owner-invite.json" "$OWNER_INVITE" STAFF
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/owner-invite.json" "$TMP_DIR/owner-invite.out" "$OWNER_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: OWNER create expected 200, got $STATUS" >&2; cat "$TMP_DIR/owner-invite.out" >&2; exit 1; }

write_json invite "$TMP_DIR/admin-invite.json" "$ADMIN_INVITE" STAFF
STATUS="$(post_file '/api/business/team/invitations' "$TMP_DIR/admin-invite.json" "$TMP_DIR/admin-invite.out" "$ADMIN_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: allowed ADMIN create expected 200, got $STATUS" >&2; cat "$TMP_DIR/admin-invite.out" >&2; exit 1; }
echo 'PASS: invitation create now matches OWNER/admin can_manage_team semantics.'

echo '==> [4/7] Verifying email-action rate limiter and 429 envelope...'
write_json resend "$TMP_DIR/resend.json" "$RATE_EMAIL" "$RATE_SLUG"
for attempt in $(seq 1 5); do
  STATUS="$(post_file '/api/business/auth/resend-verification' "$TMP_DIR/resend.json" "$TMP_DIR/resend-$attempt.out")"
  [[ "$STATUS" == '200' ]] || { echo "ERROR: resend attempt $attempt expected 200, got $STATUS" >&2; cat "$TMP_DIR/resend-$attempt.out" >&2; exit 1; }
done
STATUS="$(post_file '/api/business/auth/resend-verification' "$TMP_DIR/resend.json" "$TMP_DIR/resend-6.out")"
[[ "$STATUS" == '429' ]] || { echo "ERROR: sixth resend expected 429, got $STATUS" >&2; cat "$TMP_DIR/resend-6.out" >&2; exit 1; }
assert_error_code "$TMP_DIR/resend-6.out" TOO_MANY_REQUESTS
echo 'PASS: email-action limiter allows 5 and rejects the 6th request with structured 429.'

echo '==> [5/7] Verifying shared business-login rate limiter...'
write_json login "$TMP_DIR/bad-login.json" "missing-$STAMP@example.invalid" 'definitely-wrong'
# Three successful fixture logins already consumed 3 of the 10 requests in this fixed window.
for attempt in $(seq 1 7); do
  STATUS="$(post_file '/api/business/auth/login' "$TMP_DIR/bad-login.json" "$TMP_DIR/bad-login-$attempt.out")"
  [[ "$STATUS" == '401' ]] || { echo "ERROR: bad login attempt $attempt expected 401, got $STATUS" >&2; cat "$TMP_DIR/bad-login-$attempt.out" >&2; exit 1; }
done
STATUS="$(post_file '/api/business/auth/login' "$TMP_DIR/bad-login.json" "$TMP_DIR/bad-login-8.out")"
[[ "$STATUS" == '429' ]] || { echo "ERROR: login limiter expected 429 after 10 total requests, got $STATUS" >&2; cat "$TMP_DIR/bad-login-8.out" >&2; exit 1; }
assert_error_code "$TMP_DIR/bad-login-8.out" TOO_MANY_REQUESTS
echo 'PASS: business login limiter rejects requests beyond 10/minute per IP.'

echo '==> [6/7] Verifying invitations persisted only for authorized callers...'
COUNTS="$(psql_exec -qAtF '|' -v owner_invite="$OWNER_INVITE" -v admin_invite="$ADMIN_INVITE" -v denied_invite="$DENIED_INVITE" <<'SQL'
SELECT concat_ws('|',
  (SELECT count(*) FROM "BusinessInvitation" WHERE email=:'owner_invite'),
  (SELECT count(*) FROM "BusinessInvitation" WHERE email=:'admin_invite'),
  (SELECT count(*) FROM "BusinessInvitation" WHERE email=:'denied_invite')
);
SQL
)"
[[ "$COUNTS" == '1|1|0' ]] || { echo "ERROR: invitation persistence mismatch: $COUNTS" >&2; exit 1; }
echo 'PASS: authorized invite rows=2, denied invite rows=0.'

echo '==> [7/7] Final health checks...'
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo 'PASS: native rate limiting + team invitation permission parity deployed and smoke-tested.'
echo "Backup: $BACKUP"
