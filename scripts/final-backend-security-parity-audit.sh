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

for command in git docker curl python3 sha256sum grep; do
  command -v "$command" >/dev/null 2>&1 || { echo "ERROR: missing command: $command" >&2; exit 1; }
done
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
[[ -f "$COMPOSE_FILE" ]] || { echo "ERROR: $COMPOSE_FILE not found" >&2; exit 1; }
[[ -x scripts/smoke-core-loyalty-runtime.sh || -f scripts/smoke-core-loyalty-runtime.sh ]] || {
  echo 'ERROR: scripts/smoke-core-loyalty-runtime.sh missing' >&2; exit 1;
}

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-final-backend-audit-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-final-audit.XXXXXX)"
chmod 700 "$TMP_DIR"

uuid() { python3 - <<'PY'
import uuid
print(uuid.uuid4())
PY
}

ZERO_ID="$(uuid)"
ZERO_EMAIL="final-audit-zero-${STAMP}@example.invalid"
ZERO_PASSWORD='SmokeOnly-Refresh-2026!'
ZERO_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 -P pager=off "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q -v zero_id="$ZERO_ID" -v zero_email="$ZERO_EMAIL" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" = :'zero_id';
DELETE FROM "LegacyAuthUserPassword" WHERE "authUserId" = :'zero_id';
DELETE FROM "StandaloneAuthIdentity" WHERE id = :'zero_id'::uuid OR lower(email)=lower(:'zero_email');
SQL
}

cleanup() {
  local rc=$?
  cleanup_fixture
  rm -rf "$TMP_DIR"
  exit "$rc"
}
trap cleanup EXIT

on_error() {
  local rc=$?
  set +e
  echo >&2
  echo '=== backend-loyalty logs (last 260 lines) ===' >&2
  docker logs --tail 260 "$API_CONTAINER" 2>&1 >&2 || true
  echo >&2
  echo '=== final audit DB snapshot ===' >&2
  psql_exec <<'SQL' >&2 || true
SELECT current_database(), current_user, now();
SELECT nspname FROM pg_namespace WHERE nspname='auth';
SELECT conname, convalidated FROM pg_constraint WHERE conname LIKE '%_tenant_fk' ORDER BY conname;
SELECT count(*) AS standalone_identities FROM "StandaloneAuthIdentity";
SELECT "authKind", count(*) FROM "AuthRefreshSession" GROUP BY "authKind" ORDER BY "authKind";
SQL
  echo "FAIL: final backend security/parity audit stopped with exit code $rc." >&2
  exit "$rc"
}
trap on_error ERR

echo '==> [1/8] Static runtime dependency/security checks...'
if grep -RIn --include='*.cs' -E 'auth[.]users|supabase[_-]?(url|key|jwt|secret)|service[_-]?role' src; then
  echo 'ERROR: runtime source still contains a Supabase auth dependency/reference.' >&2
  exit 1
fi
if grep -RIn --include='*.cs' -E 'Database[.](Migrate|EnsureCreated)|MigrateAsync[(]|EnsureCreatedAsync[(]' src; then
  echo 'ERROR: automatic schema migration/creation call found in runtime source.' >&2
  exit 1
fi
if grep -RIn --include='*.cs' -E 'Console[.]Write(Line)?[(]|TODO|FIXME|HACK' src; then
  echo 'ERROR: debug/TODO marker found in runtime source.' >&2
  exit 1
fi

echo 'PASS: runtime source is standalone and contains no automatic DB migration or obvious debug marker.'

echo '==> [2/8] Backing up target and rebuilding current head...'
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

echo 'PASS: current branch builds and API/database health are green.'

echo '==> [3/8] Verifying container exposure boundaries...'
DB_PORTS="$(docker inspect "$DB_CONTAINER" --format '{{json .HostConfig.PortBindings}}')"
[[ "$DB_PORTS" == 'null' || "$DB_PORTS" == '{}' ]] || {
  echo "ERROR: PostgreSQL unexpectedly has host port bindings: $DB_PORTS" >&2; exit 1;
}
API_PORT="$(docker port "$API_CONTAINER" 8080/tcp | head -n1 || true)"
[[ "$API_PORT" == "127.0.0.1:${PORT}" ]] || {
  echo "ERROR: API expected loopback binding 127.0.0.1:${PORT}, got '${API_PORT:-<none>}'" >&2; exit 1;
}
echo 'PASS: PostgreSQL has no host port and API is loopback-bound only.'

echo '==> [4/8] Verifying target DB hardening/auth invariants...'
read -r AUTH_SCHEMA AUTH_FKS TENANT_FKS TENANT_VALIDATED RLS_ENABLED AUTH_TABLES ACTIVE_CARD_INDEX <<<"$(psql_exec -qAt -F ' ' <<'SQL'
SELECT
  (SELECT count(*) FROM pg_namespace WHERE nspname='auth'),
  (SELECT count(*) FROM pg_constraint c
     JOIN pg_class t ON t.oid=c.confrelid
     JOIN pg_namespace n ON n.oid=t.relnamespace
    WHERE c.contype='f' AND n.nspname='auth'),
  (SELECT count(*) FROM pg_constraint WHERE conname LIKE '%_tenant_fk'),
  (SELECT count(*) FROM pg_constraint WHERE conname LIKE '%_tenant_fk' AND convalidated),
  (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND c.relkind='r' AND c.relrowsecurity),
  (SELECT count(*) FROM information_schema.tables
    WHERE table_schema='public' AND table_name IN
      ('AuthRefreshSession','AuthPasswordReset','StandaloneAuthIdentity','BusinessInvitation','AuthPasswordPolicy','LegacyAuthUserPassword')),
  (SELECT count(*) FROM pg_indexes
    WHERE schemaname='public' AND indexname='MemberCard_one_active_per_member_key'
      AND indexdef ILIKE '%WHERE%"isActive"%true%');
SQL
)"
[[ "$AUTH_SCHEMA" == '0' ]] || { echo 'ERROR: auth schema exists on standalone target.' >&2; exit 1; }
[[ "$AUTH_FKS" == '0' ]] || { echo 'ERROR: target still has FK dependency on auth schema.' >&2; exit 1; }
[[ "$TENANT_FKS" == '21' && "$TENANT_VALIDATED" == '21' ]] || {
  echo "ERROR: tenant FKs expected 21/21 validated, got $TENANT_FKS/$TENANT_VALIDATED" >&2; exit 1;
}
[[ "$RLS_ENABLED" == '0' ]] || { echo "ERROR: target has $RLS_ENABLED public tables with RLS enabled." >&2; exit 1; }
[[ "$AUTH_TABLES" == '6' ]] || { echo "ERROR: expected 6 standalone auth lifecycle tables, got $AUTH_TABLES" >&2; exit 1; }
[[ "$ACTIVE_CARD_INDEX" == '1' ]] || { echo 'ERROR: partial one-active MemberCard index missing.' >&2; exit 1; }

echo 'PASS: no Supabase auth schema/FKs, 21 tenant FKs validated, RLS off, standalone auth tables present, active-card invariant present.'

echo '==> [5/8] Verifying bridge-only zero-membership login + refresh replay defense...'
cleanup_fixture
psql_exec -q -v zero_id="$ZERO_ID" -v zero_email="$ZERO_EMAIL" -v zero_hash="$ZERO_BCRYPT" <<'SQL'
INSERT INTO "StandaloneAuthIdentity" (
  id,email,"resolvedRole","emailConfirmedAt","deletedAt","bannedUntil",provider,"hasPassword",source,"sourceCreatedAt","sourceUpdatedAt","importedAt"
) VALUES (
  :'zero_id'::uuid, :'zero_email', 'staff', now(), NULL, NULL, 'email', true, 'final-audit', now(), now(), now()
);
INSERT INTO "LegacyAuthUserPassword" ("authUserId","email","passwordHash","importedAt")
VALUES (:'zero_id', :'zero_email', :'zero_hash', now());
SQL

python3 - "$TMP_DIR/login.json" "$ZERO_EMAIL" "$ZERO_PASSWORD" <<'PY'
import json,sys
json.dump({'email':sys.argv[2],'password':sys.argv[3]},open(sys.argv[1],'w'))
PY
STATUS="$(curl --silent --show-error -o "$TMP_DIR/login.out" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data-binary @"$TMP_DIR/login.json" \
  "$BASE_URL/api/business/auth/login")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: zero-membership login expected 200, got $STATUS" >&2; cat "$TMP_DIR/login.out" >&2; exit 1; }
python3 - "$TMP_DIR/login.out" "$TMP_DIR/access" "$TMP_DIR/refresh" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
assert d.get('membershipCount') == 0, d
assert d.get('businessId') is None, d
assert d.get('accessToken') and d.get('refreshToken'), d
open(sys.argv[2],'w').write(d['accessToken'])
open(sys.argv[3],'w').write(d['refreshToken'])
PY
REFRESH1="$(cat "$TMP_DIR/refresh")"
python3 - "$TMP_DIR/refresh1.json" "$REFRESH1" <<'PY'
import json,sys
json.dump({'refreshToken':sys.argv[2]},open(sys.argv[1],'w'))
PY
STATUS="$(curl --silent --show-error -o "$TMP_DIR/refresh1.out" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data-binary @"$TMP_DIR/refresh1.json" "$BASE_URL/api/auth/refresh")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: first refresh expected 200, got $STATUS" >&2; cat "$TMP_DIR/refresh1.out" >&2; exit 1; }
REFRESH2="$(python3 - "$TMP_DIR/refresh1.out" <<'PY'
import json,sys
print((json.load(open(sys.argv[1])).get('data') or {})['refreshToken'])
PY
)"
STATUS="$(curl --silent --show-error -o "$TMP_DIR/replay.out" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data-binary @"$TMP_DIR/refresh1.json" "$BASE_URL/api/auth/refresh")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: refresh replay expected 401, got $STATUS" >&2; cat "$TMP_DIR/replay.out" >&2; exit 1; }
python3 - "$TMP_DIR/refresh2.json" "$REFRESH2" <<'PY'
import json,sys
json.dump({'refreshToken':sys.argv[2]},open(sys.argv[1],'w'))
PY
STATUS="$(curl --silent --show-error -o "$TMP_DIR/family.out" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data-binary @"$TMP_DIR/refresh2.json" "$BASE_URL/api/auth/refresh")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: replayed family successor expected 401, got $STATUS" >&2; cat "$TMP_DIR/family.out" >&2; exit 1; }
echo 'PASS: app-owned bridge authenticates zero-membership identity and refresh replay revokes the token family.'
cleanup_fixture

echo '==> [6/8] Re-running full core loyalty runtime smoke on final auth/security head...'
bash scripts/smoke-core-loyalty-runtime.sh
echo 'PASS: core member/stamp/reward/redeem runtime remains green.'

echo '==> [7/8] Final target data sanity checks...'
BAD_REQUIRED="$(psql_exec -qAt <<'SQL'
SELECT
  (SELECT count(*) FROM "MemberCard" WHERE "currentStamps" < 0) +
  (SELECT count(*) FROM "Card" WHERE "requiredStamps" <= 0) +
  (SELECT count(*) FROM "MemberReward" WHERE "expiresAt" <= "issuedAt");
SQL
)"
[[ "$BAD_REQUIRED" == '0' ]] || { echo "ERROR: final target sanity violations=$BAD_REQUIRED" >&2; exit 1; }
DUP_ACTIVE="$(psql_exec -qAt <<'SQL'
SELECT count(*) FROM (
  SELECT "businessId","memberId" FROM "MemberCard"
  WHERE "isActive"=true GROUP BY "businessId","memberId" HAVING count(*) > 1
) x;
SQL
)"
[[ "$DUP_ACTIVE" == '0' ]] || { echo "ERROR: duplicate active MemberCard groups=$DUP_ACTIVE" >&2; exit 1; }
echo 'PASS: critical stamp/card/reward target invariants remain clean.'

echo '==> [8/8] Final health + git summary...'
curl --fail --silent --show-error "$BASE_URL/health"; echo
curl --fail --silent --show-error "$BASE_URL/health/db"; echo
HEAD="$(git rev-parse --short=12 HEAD)"
BRANCH="$(git branch --show-current)"
echo "PASS: FINAL BACKEND SECURITY/PARITY AUDIT GREEN on ${BRANCH}@${HEAD}."
echo "Backup: $BACKUP"
