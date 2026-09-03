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

for command in docker curl python3 psql; do
  command -v "$command" >/dev/null 2>&1 || { echo "ERROR: missing command: $command" >&2; exit 1; }
done
docker compose version >/dev/null 2>&1 || { echo 'ERROR: Docker Compose v2 is required' >&2; exit 1; }
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

if [[ -z "${SOURCE_DB_URL:-}" ]]; then
  read -r -s -p 'SOURCE_DB_URL (hidden): ' SOURCE_DB_URL
  echo
fi
SOURCE_DB_URL="${SOURCE_DB_URL#${SOURCE_DB_URL%%[![:space:]]*}}"
SOURCE_DB_URL="${SOURCE_DB_URL%${SOURCE_DB_URL##*[![:space:]]}}"
case "$SOURCE_DB_URL" in
  postgres://*|postgresql://*) ;;
  *) echo 'ERROR: SOURCE_DB_URL must be a PostgreSQL connection URL' >&2; exit 1 ;;
esac

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/loyalty-pre-zero-membership-auth-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-zero-membership.XXXXXX)"
chmod 700 "$TMP_DIR"

SMOKE_ID="$(python3 - <<'PY'
import uuid
print(uuid.uuid4())
PY
)"
SMOKE_EMAIL="zero-membership-smoke-${STAMP}@example.invalid"
SMOKE_PASSWORD='SmokeOnly-Refresh-2026!'
SMOKE_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
FIXTURE_CREATED=0

source_psql() {
  psql "$SOURCE_DB_URL" -X -v ON_ERROR_STOP=1 -P pager=off "$@"
}

target_psql() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 -P pager=off "$@"
}

cleanup_staging() {
  set +e
  target_psql -q >/dev/null 2>&1 <<'SQL'
DROP TABLE IF EXISTS "_StandaloneAuthIdentityImport";
DROP TABLE IF EXISTS "_LegacyAuthPasswordImport";
SQL
}

cleanup_fixture() {
  set +e
  if [[ "$FIXTURE_CREATED" == "1" ]]; then
    target_psql -q -v smoke_id="$SMOKE_ID" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" = :'smoke_id';
DELETE FROM "LegacyAuthUserPassword" WHERE "authUserId" = :'smoke_id';
DELETE FROM "StandaloneAuthIdentity" WHERE id = :'smoke_id'::uuid;
SQL
  fi
}

cleanup() {
  local rc=$?
  cleanup_fixture
  cleanup_staging
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
  echo '=== standalone auth snapshot counts ===' >&2
  target_psql -P pager=off <<'SQL' >&2 || true
SELECT count(*) AS identities,
       count(*) FILTER (WHERE "hasPassword") AS with_password,
       count(*) FILTER (WHERE "emailConfirmedAt" IS NULL) AS unconfirmed,
       count(*) FILTER (WHERE "deletedAt" IS NOT NULL) AS deleted,
       count(*) FILTER (WHERE "bannedUntil" IS NOT NULL AND "bannedUntil" > now()) AS currently_banned
FROM "StandaloneAuthIdentity";
SQL
  echo "FAIL: zero-membership auth parity deploy stopped with exit code $rc." >&2
  exit "$rc"
}
trap fail_diag ERR

echo '==> [1/9] Verifying READ-ONLY source and backing up target...'
AUTH_OK="$(source_psql -qAtc "SELECT to_regclass('auth.users') IS NOT NULL;")"
BUSINESS_OK="$(source_psql -qAtc "SELECT to_regclass('public.\"BusinessUser\"') IS NOT NULL;")"
[[ "$AUTH_OK" == 't' && "$BUSINESS_OK" == 't' ]] || { echo 'ERROR: unexpected source database' >&2; exit 1; }

SOURCE_TOTAL="$(source_psql -qAtc 'SELECT count(*) FROM auth.users;')"
SOURCE_HASHES="$(source_psql -qAtc "SELECT count(*) FROM auth.users WHERE COALESCE(encrypted_password,'') <> ''; ")"
SOURCE_ZERO="$(source_psql -qAtc 'SELECT count(*) FROM auth.users u WHERE NOT EXISTS (SELECT 1 FROM public."BusinessUser" b WHERE b."isActive"=true AND (b."authUserId"=u.id OR (b."authUserId" IS NULL AND b.id=u.id::text)));')"
SOURCE_ZERO_ELIGIBLE="$(source_psql -qAtc 'SELECT count(*) FROM auth.users u WHERE COALESCE(u.encrypted_password,'''') <> '''' AND u.email_confirmed_at IS NOT NULL AND u.deleted_at IS NULL AND (u.banned_until IS NULL OR u.banned_until <= now()) AND NOT EXISTS (SELECT 1 FROM public."BusinessUser" b WHERE b."isActive"=true AND (b."authUserId"=u.id OR (b."authUserId" IS NULL AND b.id=u.id::text)));')"

docker inspect "$DB_CONTAINER" >/dev/null
target_psql -Atqc 'SELECT 1' >/dev/null
docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo '==> [2/9] Creating target identity schema and staging tables...'
target_psql < scripts/add-standalone-auth-identity-schema.sql
target_psql -q <<'SQL'
DROP TABLE IF EXISTS "_StandaloneAuthIdentityImport";
CREATE UNLOGGED TABLE "_StandaloneAuthIdentityImport" (
  id uuid PRIMARY KEY,
  email text NOT NULL,
  "resolvedRole" text,
  "emailConfirmedAt" timestamptz,
  "deletedAt" timestamptz,
  "bannedUntil" timestamptz,
  provider text,
  "hasPassword" boolean NOT NULL,
  "sourceCreatedAt" timestamptz,
  "sourceUpdatedAt" timestamptz
);
DROP TABLE IF EXISTS "_LegacyAuthPasswordImport";
CREATE UNLOGGED TABLE "_LegacyAuthPasswordImport" (
  id uuid PRIMARY KEY,
  email text,
  "passwordHash" text NOT NULL
);
SQL

echo '==> [3/9] Streaming auth state + password hashes from source into target staging...'
source_psql -q -c "COPY (
  SELECT id,
         email,
         lower(COALESCE(NULLIF(raw_user_meta_data ->> 'role',''), NULLIF(raw_app_meta_data ->> 'role',''), NULLIF(role,''))) AS resolved_role,
         email_confirmed_at,
         deleted_at,
         banned_until,
         raw_app_meta_data ->> 'provider' AS provider,
         COALESCE(encrypted_password,'') <> '' AS has_password,
         created_at,
         updated_at
  FROM auth.users
  WHERE email IS NOT NULL AND trim(email) <> ''
) TO STDOUT WITH (FORMAT csv)" \
| target_psql -q -c 'COPY "_StandaloneAuthIdentityImport" (id,email,"resolvedRole","emailConfirmedAt","deletedAt","bannedUntil",provider,"hasPassword","sourceCreatedAt","sourceUpdatedAt") FROM STDIN WITH (FORMAT csv)'

source_psql -q -c "COPY (
  SELECT id, email, encrypted_password
  FROM auth.users
  WHERE COALESCE(encrypted_password,'') <> ''
) TO STDOUT WITH (FORMAT csv)" \
| target_psql -q -c 'COPY "_LegacyAuthPasswordImport" (id,email,"passwordHash") FROM STDIN WITH (FORMAT csv)'

echo '==> [4/9] Atomically promoting source snapshot into app-owned target tables...'
target_psql -q <<'SQL'
BEGIN;

INSERT INTO "StandaloneAuthIdentity" (
  id,email,"resolvedRole","emailConfirmedAt","deletedAt","bannedUntil",provider,
  "hasPassword",source,"sourceCreatedAt","sourceUpdatedAt","importedAt"
)
SELECT id,email,"resolvedRole","emailConfirmedAt","deletedAt","bannedUntil",provider,
       "hasPassword",'supabase',"sourceCreatedAt","sourceUpdatedAt",now()
FROM "_StandaloneAuthIdentityImport"
ON CONFLICT (id) DO UPDATE SET
  email=EXCLUDED.email,
  "resolvedRole"=EXCLUDED."resolvedRole",
  "emailConfirmedAt"=EXCLUDED."emailConfirmedAt",
  "deletedAt"=EXCLUDED."deletedAt",
  "bannedUntil"=EXCLUDED."bannedUntil",
  provider=EXCLUDED.provider,
  "hasPassword"=EXCLUDED."hasPassword",
  source='supabase',
  "sourceCreatedAt"=EXCLUDED."sourceCreatedAt",
  "sourceUpdatedAt"=EXCLUDED."sourceUpdatedAt",
  "importedAt"=now();

DELETE FROM "StandaloneAuthIdentity" s
WHERE s.source='supabase'
  AND NOT EXISTS (SELECT 1 FROM "_StandaloneAuthIdentityImport" i WHERE i.id=s.id);

INSERT INTO "LegacyAuthUserPassword" ("authUserId",email,"passwordHash","importedAt")
SELECT id::text,email,"passwordHash",now()
FROM "_LegacyAuthPasswordImport"
ON CONFLICT ("authUserId") DO UPDATE SET
  email=EXCLUDED.email,
  "passwordHash"=EXCLUDED."passwordHash",
  "importedAt"=now();

DROP TABLE "_StandaloneAuthIdentityImport";
DROP TABLE "_LegacyAuthPasswordImport";
COMMIT;
SQL

echo '==> [5/9] Verifying snapshot cardinality and password-bridge coverage...'
TARGET_TOTAL="$(target_psql -qAtc "SELECT count(*) FROM \"StandaloneAuthIdentity\" WHERE source='supabase';")"
TARGET_HASHES="$(target_psql -qAtc 'SELECT count(*) FROM "LegacyAuthUserPassword" l JOIN "StandaloneAuthIdentity" s ON s.id::text=l."authUserId" WHERE s.source='"'"'supabase'"'"' AND s."hasPassword";')"
TARGET_ZERO="$(target_psql -qAtc 'SELECT count(*) FROM "StandaloneAuthIdentity" s WHERE s.source='"'"'supabase'"'"' AND NOT EXISTS (SELECT 1 FROM "BusinessUser" b WHERE b."isActive"=true AND (b."authUserId"=s.id OR (b."authUserId" IS NULL AND b.id=s.id::text)));')"
TARGET_ZERO_ELIGIBLE="$(target_psql -qAtc 'SELECT count(*) FROM "StandaloneAuthIdentity" s WHERE s.source='"'"'supabase'"'"' AND s."hasPassword" AND s."emailConfirmedAt" IS NOT NULL AND s."deletedAt" IS NULL AND (s."bannedUntil" IS NULL OR s."bannedUntil" <= now()) AND NOT EXISTS (SELECT 1 FROM "BusinessUser" b WHERE b."isActive"=true AND (b."authUserId"=s.id OR (b."authUserId" IS NULL AND b.id=s.id::text)));')"
[[ "$TARGET_TOTAL" == "$SOURCE_TOTAL" ]] || { echo "ERROR: identity count mismatch source=$SOURCE_TOTAL target=$TARGET_TOTAL" >&2; exit 1; }
[[ "$TARGET_HASHES" == "$SOURCE_HASHES" ]] || { echo "ERROR: bridge coverage mismatch source=$SOURCE_HASHES target=$TARGET_HASHES" >&2; exit 1; }
[[ "$TARGET_ZERO" == "$SOURCE_ZERO" ]] || { echo "ERROR: zero-membership count mismatch source=$SOURCE_ZERO target=$TARGET_ZERO" >&2; exit 1; }
[[ "$TARGET_ZERO_ELIGIBLE" == "$SOURCE_ZERO_ELIGIBLE" ]] || { echo "ERROR: eligible zero-membership mismatch source=$SOURCE_ZERO_ELIGIBLE target=$TARGET_ZERO_ELIGIBLE" >&2; exit 1; }
echo "PASS: imported $TARGET_TOTAL auth identities; zero-membership=$TARGET_ZERO, password-login-eligible=$TARGET_ZERO_ELIGIBLE."

echo '==> [6/9] Building and restarting Loyalty API...'
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api
for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null && curl --fail --silent "$BASE_URL/health/db" >/dev/null; then
    break
  fi
  if [[ "$attempt" -eq 30 ]]; then
    echo 'ERROR: API did not become healthy' >&2
    exit 1
  fi
  sleep 2
done

echo '==> [7/9] Testing real zero-membership login + refresh on isolated target identity...'
target_psql -q -v smoke_id="$SMOKE_ID" -v smoke_email="$SMOKE_EMAIL" -v smoke_hash="$SMOKE_BCRYPT" <<'SQL'
INSERT INTO "StandaloneAuthIdentity" (
  id,email,"resolvedRole","emailConfirmedAt","deletedAt","bannedUntil",provider,
  "hasPassword",source,"sourceCreatedAt","sourceUpdatedAt","importedAt"
) VALUES (
  :'smoke_id'::uuid, :'smoke_email', 'staff', now(), NULL, NULL, 'email',
  true, 'smoke', now(), now(), now()
);
INSERT INTO "LegacyAuthUserPassword" ("authUserId",email,"passwordHash","importedAt")
VALUES (:'smoke_id',:'smoke_email',:'smoke_hash',now());
SQL
FIXTURE_CREATED=1

python3 - "$TMP_DIR/login.json" "$SMOKE_EMAIL" "$SMOKE_PASSWORD" <<'PY'
import json,sys
with open(sys.argv[1],'w') as f: json.dump({'email':sys.argv[2],'password':sys.argv[3]},f)
PY
STATUS="$(curl --silent --show-error -o "$TMP_DIR/login.out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary "@$TMP_DIR/login.json" "$BASE_URL/api/business/auth/login")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: zero-membership login expected 200, got $STATUS" >&2; cat "$TMP_DIR/login.out" >&2; exit 1; }
read -r MEMBERSHIP_COUNT ROLE REFRESH_TOKEN < <(python3 - "$TMP_DIR/login.out" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); d=x.get('data') or {}
print(d.get('membershipCount'), d.get('role'), d.get('refreshToken'))
PY
)
[[ "$MEMBERSHIP_COUNT" == '0' && "$ROLE" == 'STAFF' && -n "$REFRESH_TOKEN" && "$REFRESH_TOKEN" != 'None' ]] || {
  echo "ERROR: zero-membership response parity mismatch membership=$MEMBERSHIP_COUNT role=$ROLE" >&2; exit 1;
}
python3 - "$TMP_DIR/refresh.json" "$REFRESH_TOKEN" <<'PY'
import json,sys
with open(sys.argv[1],'w') as f: json.dump({'refreshToken':sys.argv[2]},f)
PY
STATUS="$(curl --silent --show-error -o "$TMP_DIR/refresh.out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary "@$TMP_DIR/refresh.json" "$BASE_URL/api/auth/refresh")"
[[ "$STATUS" == '200' ]] || { echo "ERROR: zero-membership refresh expected 200, got $STATUS" >&2; cat "$TMP_DIR/refresh.out" >&2; exit 1; }
NEXT_REFRESH="$(python3 - "$TMP_DIR/refresh.out" <<'PY'
import json,sys
x=json.load(open(sys.argv[1])); print((x.get('data') or {}).get('refreshToken') or '')
PY
)"
[[ -n "$NEXT_REFRESH" ]] || { echo 'ERROR: rotated refresh token missing' >&2; exit 1; }
echo 'PASS: confirmed password-capable zero-membership identity logs in with membershipCount=0 and refreshes.'

echo '==> [8/9] Testing banned/unconfirmed state blocks login and existing refresh...'
target_psql -q -v smoke_id="$SMOKE_ID" -c 'UPDATE "StandaloneAuthIdentity" SET "bannedUntil"=now()+interval '"'"'1 day'"'"' WHERE id=:'"'"'smoke_id'"'"'::uuid;'
STATUS="$(curl --silent --show-error -o "$TMP_DIR/banned-login.out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary "@$TMP_DIR/login.json" "$BASE_URL/api/business/auth/login")"
[[ "$STATUS" == '403' ]] || { echo "ERROR: banned identity login expected 403, got $STATUS" >&2; cat "$TMP_DIR/banned-login.out" >&2; exit 1; }
python3 - "$TMP_DIR/banned-refresh.json" "$NEXT_REFRESH" <<'PY'
import json,sys
with open(sys.argv[1],'w') as f: json.dump({'refreshToken':sys.argv[2]},f)
PY
STATUS="$(curl --silent --show-error -o "$TMP_DIR/banned-refresh.out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary "@$TMP_DIR/banned-refresh.json" "$BASE_URL/api/auth/refresh")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: banned identity refresh expected 401, got $STATUS" >&2; cat "$TMP_DIR/banned-refresh.out" >&2; exit 1; }

target_psql -q -v smoke_id="$SMOKE_ID" -c 'UPDATE "StandaloneAuthIdentity" SET "bannedUntil"=NULL,"emailConfirmedAt"=NULL WHERE id=:'"'"'smoke_id'"'"'::uuid;'
STATUS="$(curl --silent --show-error -o "$TMP_DIR/unconfirmed.out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary "@$TMP_DIR/login.json" "$BASE_URL/api/business/auth/login")"
[[ "$STATUS" == '403' ]] || { echo "ERROR: unconfirmed identity login expected 403, got $STATUS" >&2; cat "$TMP_DIR/unconfirmed.out" >&2; exit 1; }
echo 'PASS: banned state blocks login+refresh, and unconfirmed state blocks login.'

echo '==> [9/9] Final health checks...'
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo "PASS: standalone zero-membership auth-state parity deployed and smoke-tested."
echo "Source snapshot: total=$SOURCE_TOTAL hashes=$SOURCE_HASHES zero-membership=$SOURCE_ZERO eligible=$SOURCE_ZERO_ELIGIBLE"
echo "Backup: $BACKUP"
