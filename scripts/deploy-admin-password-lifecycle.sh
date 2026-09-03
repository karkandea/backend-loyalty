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
BACKUP="/root/loyalty-pre-admin-password-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-admin-password.XXXXXX)"
chmod 700 "$TMP_DIR"

OLD_PASSWORD='SmokeOnly-Refresh-2026!'
RESET_PASSWORD='SmokeOnly-AdminReset-2026!'
FINAL_PASSWORD='SmokeOnly-AdminFinal-2026!'
FIXTURE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
ADMIN_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
ADMIN_EMAIL="admin-password-${STAMP}@example.invalid"
UNKNOWN_EMAIL="admin-password-unknown-${STAMP}@example.invalid"
RESET_TOKEN="SmokeOnly-AdminResetToken-${STAMP}"

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 -P pager=off "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q -v admin_id="$ADMIN_ID" -v email="$ADMIN_EMAIL" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId"=:'admin_id' AND "authKind"='admin';
DELETE FROM "AuthPasswordReset" WHERE "userId"=:'admin_id' AND "authKind"='admin';
DELETE FROM "LegacyAuthUserPassword" WHERE "authUserId"=:'admin_id';
DELETE FROM "AdminUser" WHERE id=:'admin_id' OR email=:'email';
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
  echo '=== admin password fixture snapshot ===' >&2
  psql_exec -v admin_id="$ADMIN_ID" -v email="$ADMIN_EMAIL" <<'SQL' >&2 || true
SELECT id,email,"businessId","outletId",role,"isActive" FROM "AdminUser" WHERE id=:'admin_id' OR email=:'email';
SELECT id,"userId","authKind",email,"expiresAt","usedAt","createdAt" FROM "AuthPasswordReset" WHERE "userId"=:'admin_id' ORDER BY "createdAt";
SELECT id,"userId","authKind","revokedAt","revokeReason","expiresAt" FROM "AuthRefreshSession" WHERE "userId"=:'admin_id' ORDER BY "createdAt";
SQL
  echo "FAIL: admin password lifecycle smoke stopped with exit code $rc." >&2
  exit "$rc"
}
trap fail_diag ERR

write_json() {
  local mode="$1" file="$2" a="${3:-}" b="${4:-}" c="${5:-}"
  MODE="$mode" A="$a" B="$b" C="$c" python3 - "$file" <<'PY'
import json,os,sys
m=os.environ['MODE']; a=os.environ.get('A',''); b=os.environ.get('B',''); c=os.environ.get('C','')
if m=='login': data={'email':a,'password':b}
elif m=='forgot': data={'email':a,'businessId':b}
elif m=='reset': data={'token':a,'newPassword':b}
elif m=='update': data={'currentPassword':a,'newPassword':b}
elif m=='refresh': data={'refreshToken':a}
else: raise SystemExit('unknown mode')
with open(sys.argv[1],'w',encoding='utf-8') as f: json.dump(data,f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1" body="$2" output="$3" bearer="${4:-}" tenant_slug="${5:-}"
  local args=(--silent --show-error -o "$output" -w '%{http_code}' -H 'Content-Type: application/json')
  [[ -z "$bearer" ]] || args+=(-H "Authorization: Bearer $bearer")
  [[ -z "$tenant_slug" ]] || args+=(-H "x-tenant-slug: $tenant_slug")
  args+=(--data-binary "@$body" "$BASE_URL$endpoint")
  curl "${args[@]}"
}

extract_token() {
  local file="$1" key="$2"
  python3 - "$file" "$key" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); v=(x.get('data') or {}).get(sys.argv[2])
if not v: raise SystemExit(f'{sys.argv[2]} missing')
print(v)
PY
}

login() {
  local password="$1" prefix="$2"
  local body="$TMP_DIR/$prefix.login.json" out="$TMP_DIR/$prefix.login.out" status
  write_json login "$body" "$ADMIN_EMAIL" "$password"
  status="$(post_file '/api/admin/auth/login' "$body" "$out")"
  printf '%s' "$status" > "$TMP_DIR/$prefix.status"
  if [[ "$status" == '200' ]]; then
    extract_token "$out" accessToken > "$TMP_DIR/$prefix.access"
    extract_token "$out" refreshToken > "$TMP_DIR/$prefix.refresh"
  fi
}

echo '==> [1/8] Backing up target and building API...'
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null
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

echo '==> [2/8] Creating isolated active POS STAFF fixture...'
cleanup_fixture
ROW="$(psql_exec -qAtF '|' <<'SQL'
SELECT b.id,b.slug,o.id
FROM "Business" b
JOIN "Outlet" o ON o."businessId"=b.id AND o."isActive"=true
WHERE b."isActive"=true
ORDER BY b."createdAt",b.id,o."createdAt",o.id
LIMIT 1;
SQL
)"
IFS='|' read -r BUSINESS_ID BUSINESS_SLUG OUTLET_ID <<< "$ROW"
[[ -n "${BUSINESS_ID:-}" && -n "${BUSINESS_SLUG:-}" && -n "${OUTLET_ID:-}" ]] || { echo 'ERROR: active business+outlet fixture is required' >&2; exit 1; }
psql_exec -q -v admin_id="$ADMIN_ID" -v business_id="$BUSINESS_ID" -v outlet_id="$OUTLET_ID" -v email="$ADMIN_EMAIL" -v fixture_hash="$FIXTURE_BCRYPT" <<'SQL'
INSERT INTO "AdminUser" (id,"businessId","outletId",email,"passwordHash","fullName",role,"isActive","lastLoginAt","createdAt","updatedAt")
VALUES (:'admin_id',:'business_id',:'outlet_id',:'email',:'fixture_hash','Admin Password Smoke','STAFF',true,NULL,now(),now());
SQL
login "$OLD_PASSWORD" initial
[[ "$(cat "$TMP_DIR/initial.status")" == '200' ]] || { echo 'ERROR: initial admin login failed' >&2; cat "$TMP_DIR/initial.login.out" >&2; exit 1; }
INITIAL_ACCESS="$(cat "$TMP_DIR/initial.access")"
INITIAL_REFRESH="$(cat "$TMP_DIR/initial.refresh")"
echo 'PASS: isolated POS STAFF fixture authenticates with active business+outlet context.'

echo '==> [3/8] Verifying tenant-aware forgot-password + anti-enumeration...'
write_json forgot "$TMP_DIR/forgot.json" "$ADMIN_EMAIL" "$BUSINESS_ID"
STATUS="$(post_file '/api/admin/auth/forgot-password' "$TMP_DIR/forgot.json" "$TMP_DIR/forgot.out")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: forgot expected 200, got $STATUS" >&2; cat "$TMP_DIR/forgot.out" >&2; exit 1; }
write_json forgot "$TMP_DIR/forgot-unknown.json" "$UNKNOWN_EMAIL" "$BUSINESS_ID"
STATUS="$(post_file '/api/admin/auth/forgot-password' "$TMP_DIR/forgot-unknown.json" "$TMP_DIR/forgot-unknown.out")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: unknown forgot expected generic 200, got $STATUS" >&2; exit 1; }
RESET_ROWS="$(psql_exec -qAt -v admin_id="$ADMIN_ID" <<'SQL'
SELECT count(*) FROM "AuthPasswordReset" WHERE "userId"=:'admin_id' AND "authKind"='admin' AND "usedAt" IS NULL;
SQL
)"
[[ "$RESET_ROWS" == '1' ]] || { echo "ERROR: expected exactly one active admin reset, got $RESET_ROWS" >&2; exit 1; }
echo 'PASS: forgot-password is tenant-aware and preserves anti-enumeration behavior.'

echo '==> [4/8] Pinning reset token and verifying cross-tenant rejection...'
RESET_HASH="$(printf '%s' "$RESET_TOKEN" | sha256sum | awk '{print $1}')"
psql_exec -q -v admin_id="$ADMIN_ID" -v token_hash="$RESET_HASH" <<'SQL'
UPDATE "AuthPasswordReset" SET "tokenHash"=:'token_hash'
WHERE "userId"=:'admin_id' AND "authKind"='admin' AND "usedAt" IS NULL;
SQL
write_json reset "$TMP_DIR/reset.json" "$RESET_TOKEN" "$RESET_PASSWORD"
STATUS="$(post_file '/api/admin/auth/reset-password' "$TMP_DIR/reset.json" "$TMP_DIR/reset-wrong-tenant.out" '' 'definitely-wrong-smoke-tenant')"
[[ "$STATUS" == '400' ]] || { echo "ERROR: wrong-tenant reset expected 400, got $STATUS" >&2; cat "$TMP_DIR/reset-wrong-tenant.out" >&2; exit 1; }
STILL_UNUSED="$(psql_exec -qAt -v admin_id="$ADMIN_ID" <<'SQL'
SELECT count(*) FROM "AuthPasswordReset" WHERE "userId"=:'admin_id' AND "authKind"='admin' AND "usedAt" IS NULL;
SQL
)"
[[ "$STILL_UNUSED" == '1' ]] || { echo 'ERROR: wrong-tenant attempt consumed reset token' >&2; exit 1; }
echo 'PASS: reset token cannot cross tenant slug and remains unused after rejection.'

echo '==> [5/8] Resetting password and verifying single-use + session revocation...'
STATUS="$(post_file '/api/admin/auth/reset-password' "$TMP_DIR/reset.json" "$TMP_DIR/reset.out" '' "$BUSINESS_SLUG")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: valid reset expected 200, got $STATUS" >&2; cat "$TMP_DIR/reset.out" >&2; exit 1; }
write_json refresh "$TMP_DIR/initial-refresh.json" "$INITIAL_REFRESH"
STATUS="$(post_file '/api/auth/refresh' "$TMP_DIR/initial-refresh.json" "$TMP_DIR/initial-refresh.out")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: pre-reset refresh expected 401, got $STATUS" >&2; exit 1; }
login "$OLD_PASSWORD" old-after-reset
[[ "$(cat "$TMP_DIR/old-after-reset.status")" == '401' ]] || { echo 'ERROR: old admin password still works after reset' >&2; exit 1; }
login "$RESET_PASSWORD" reset-pass
[[ "$(cat "$TMP_DIR/reset-pass.status")" == '200' ]] || { echo 'ERROR: reset admin password does not login' >&2; cat "$TMP_DIR/reset-pass.login.out" >&2; exit 1; }
RESET_ACCESS="$(cat "$TMP_DIR/reset-pass.access")"
RESET_REFRESH="$(cat "$TMP_DIR/reset-pass.refresh")"
STATUS="$(post_file '/api/admin/auth/reset-password' "$TMP_DIR/reset.json" "$TMP_DIR/reset-replay.out" '' "$BUSINESS_SLUG")"
[[ "$STATUS" == '400' ]] || { echo "ERROR: reset replay expected 400, got $STATUS" >&2; exit 1; }
echo 'PASS: admin reset changes password, revokes old refresh, and is single-use.'

echo '==> [6/8] Verifying authenticated update-password current-password semantics...'
write_json update "$TMP_DIR/update-wrong.json" 'definitely-wrong-current' "$FINAL_PASSWORD"
STATUS="$(post_file '/api/admin/auth/update-password' "$TMP_DIR/update-wrong.json" "$TMP_DIR/update-wrong.out" "$RESET_ACCESS")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: wrong current password expected 401, got $STATUS" >&2; cat "$TMP_DIR/update-wrong.out" >&2; exit 1; }
write_json update "$TMP_DIR/update.json" "$RESET_PASSWORD" "$FINAL_PASSWORD"
STATUS="$(post_file '/api/admin/auth/update-password' "$TMP_DIR/update.json" "$TMP_DIR/update.out" "$RESET_ACCESS")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: valid update expected 200, got $STATUS" >&2; cat "$TMP_DIR/update.out" >&2; exit 1; }
write_json refresh "$TMP_DIR/reset-refresh.json" "$RESET_REFRESH"
STATUS="$(post_file '/api/auth/refresh' "$TMP_DIR/reset-refresh.json" "$TMP_DIR/reset-refresh.out")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: pre-update refresh expected 401, got $STATUS" >&2; exit 1; }
login "$RESET_PASSWORD" reset-after-update
[[ "$(cat "$TMP_DIR/reset-after-update.status")" == '401' ]] || { echo 'ERROR: pre-update password still works' >&2; exit 1; }
login "$FINAL_PASSWORD" final
[[ "$(cat "$TMP_DIR/final.status")" == '200' ]] || { echo 'ERROR: final admin password does not login' >&2; cat "$TMP_DIR/final.login.out" >&2; exit 1; }
echo 'PASS: update-password reauthenticates current password, changes it, and revokes existing admin refresh sessions.'

echo '==> [7/8] Verifying reset state and bridge cleanup...'
STATE="$(psql_exec -qAtF '|' -v admin_id="$ADMIN_ID" <<'SQL'
SELECT
  (SELECT count(*) FROM "AuthPasswordReset" WHERE "userId"=:'admin_id' AND "authKind"='admin' AND "usedAt" IS NULL),
  (SELECT count(*) FROM "LegacyAuthUserPassword" WHERE "authUserId"=:'admin_id');
SQL
)"
[[ "$STATE" == '0|0' ]] || { echo "ERROR: expected no live resets/legacy bridge, got $STATE" >&2; exit 1; }
echo 'PASS: no live admin reset token or legacy credential bridge remains for the adopted fixture.'

echo '==> [8/8] Final health checks...'
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo 'PASS: standalone admin password lifecycle deployed and smoke-tested end-to-end.'
echo "Backup: $BACKUP"
