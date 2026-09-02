#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
API_CONTAINER="${API_CONTAINER:-backend-loyalty}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"

for command in docker curl python3 sha256sum; do
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
BACKUP="/root/loyalty-pre-core-runtime-${STAMP}.dump"
TMP_DIR="$(mktemp -d /tmp/loyalty-core-runtime.XXXXXX)"
chmod 700 "$TMP_DIR"

uuid() { python3 - <<'PY'
import uuid
print(uuid.uuid4())
PY
}

BUSINESS_ID="$(uuid)"
OUTLET_ID="$(uuid)"
STAFF_ID="$(uuid)"
MEMBER_ID="$(uuid)"
MEMBER_CARD_ID="$(uuid)"
CARD_ONE_ID="$(uuid)"
CARD_TWO_ID="$(uuid)"
REWARD_ID="$(uuid)"
MILESTONE_ID="$(uuid)"
SESSION_ID="$(uuid)"
BUSINESS_SLUG="core-runtime-${STAMP,,}"
STAFF_EMAIL="core-runtime-staff-${STAMP}@example.invalid"
MEMBER_EMAIL="core-runtime-member-${STAMP}@example.invalid"
MEMBER_BARCODE="CORE-${STAMP}"
STAFF_PASSWORD='SmokeOnly-Refresh-2026!'
STAFF_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
MEMBER_SESSION_RAW="member-session-core-${STAMP}-$(uuid)"
MEMBER_SESSION_HASH="$(printf '%s' "$MEMBER_SESSION_RAW" | sha256sum | awk '{print $1}')"

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

cleanup_fixture() {
  set +e
  psql_exec -q -v business_id="$BUSINESS_ID" -v staff_id="$STAFF_ID" >/dev/null 2>&1 <<'SQL'
DELETE FROM "AuthRefreshSession" WHERE "userId" = :'staff_id';
DELETE FROM "AuditLog" WHERE "businessId" = :'business_id'::uuid;
DELETE FROM "RewardToken" WHERE "businessId" = :'business_id';
DELETE FROM "Transaction" WHERE "businessId" = :'business_id';
DELETE FROM "MemberReward" WHERE "businessId" = :'business_id';
DELETE FROM "CardMilestone" WHERE "businessId" = :'business_id';
DELETE FROM "MemberCard" WHERE "businessId" = :'business_id';
DELETE FROM "MemberSession" WHERE "businessId" = :'business_id';
DELETE FROM "Member" WHERE "businessId" = :'business_id';
DELETE FROM "Reward" WHERE "businessId" = :'business_id';
DELETE FROM "Card" WHERE "businessId" = :'business_id';
DELETE FROM "AdminUser" WHERE "businessId" = :'business_id';
DELETE FROM "Outlet" WHERE "businessId" = :'business_id';
DELETE FROM "Business" WHERE id = :'business_id';
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
  echo "=== backend-loyalty logs (last 240 lines) ===" >&2
  docker logs --tail 240 "$API_CONTAINER" 2>&1 >&2 || true
  echo >&2
  echo "=== isolated core runtime snapshot ===" >&2
  psql_exec -P pager=off -v business_id="$BUSINESS_ID" <<'SQL' >&2 || true
SELECT id, name, slug, "isActive" FROM "Business" WHERE id=:'business_id';
SELECT mc.id, mc."cardId", mc."currentStamps", mc."isActive", mc."completedAt", c.level, c."requiredStamps"
FROM "MemberCard" mc JOIN "Card" c ON c.id=mc."cardId"
WHERE mc."businessId"=:'business_id' ORDER BY mc."createdAt", mc.id;
SELECT id, "memberRewardId", status, "usedAt", "usedByStaffId", "usedAtOutletId", "expiresAt"
FROM "RewardToken" WHERE "businessId"=:'business_id' ORDER BY "createdAt";
SELECT id, status, "memberCardId", "redeemedAt" FROM "MemberReward"
WHERE "businessId"=:'business_id' ORDER BY "createdAt";
SELECT type, count(*) AS rows, sum(stamps_added) AS stamp_sum
FROM "Transaction" WHERE "businessId"=:'business_id' GROUP BY type ORDER BY type;
SQL
  echo "FAIL: core loyalty runtime smoke stopped with exit code $rc." >&2
  exit "$rc"
}
trap diagnostics ERR

write_json() {
  local mode="$1" file="$2" v1="${3:-}" v2="${4:-}"
  MODE="$mode" V1="$v1" V2="$v2" python3 - "$file" <<'PY'
import json, os, sys
mode=os.environ['MODE']; v1=os.environ.get('V1',''); v2=os.environ.get('V2','')
if mode == 'login': data={'email':v1,'password':v2}
elif mode == 'scan_member': data={'memberBarcode':v1}
elif mode == 'stamp': data={'stampCount':int(v1),'transactionNotes':v2 or None}
elif mode == 'scan_reward': data={'rewardToken':v1}
else: raise SystemExit('unknown mode')
with open(sys.argv[1],'w',encoding='utf-8') as f: json.dump(data,f)
PY
  chmod 600 "$file"
}

post_file() {
  local endpoint="$1" body="$2" output="$3" bearer="${4:-}" cookie="${5:-}"
  local args=(--silent --show-error -o "$output" -w '%{http_code}' -H 'Content-Type: application/json')
  [[ -z "$bearer" ]] || args+=(-H "Authorization: Bearer $bearer")
  [[ -z "$cookie" ]] || args+=(-H "Cookie: $cookie")
  args+=(--data-binary "@$body" "$BASE_URL$endpoint")
  curl "${args[@]}"
}

extract_path() {
  local file="$1" path="$2"
  python3 - "$file" "$path" <<'PY'
import json, sys
with open(sys.argv[1],encoding='utf-8') as f: cur=json.load(f)
for p in sys.argv[2].split('.'):
    if isinstance(cur, dict): cur=cur.get(p)
    else: cur=None
    if cur is None: raise SystemExit(f'missing path {sys.argv[2]}')
if isinstance(cur,(dict,list)): print(json.dumps(cur))
else: print(cur)
PY
}

echo "==> [1/10] Checking target and creating safety backup..."
docker inspect "$DB_CONTAINER" >/dev/null
curl --fail --silent "$BASE_URL/health" >/dev/null
curl --fail --silent "$BASE_URL/health/db" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null

docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" "$DB_CONTAINER" \
  pg_dump -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -Fc > "$BACKUP"
test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"

echo "==> [2/10] Creating isolated tenant/card/member/admin fixtures..."
cleanup_fixture
psql_exec -q \
  -v business_id="$BUSINESS_ID" -v slug="$BUSINESS_SLUG" \
  -v outlet_id="$OUTLET_ID" -v staff_id="$STAFF_ID" -v staff_email="$STAFF_EMAIL" -v staff_hash="$STAFF_BCRYPT" \
  -v member_id="$MEMBER_ID" -v member_email="$MEMBER_EMAIL" -v barcode="$MEMBER_BARCODE" \
  -v card1="$CARD_ONE_ID" -v card2="$CARD_TWO_ID" -v member_card="$MEMBER_CARD_ID" \
  -v reward_id="$REWARD_ID" -v milestone_id="$MILESTONE_ID" \
  -v session_id="$SESSION_ID" -v session_hash="$MEMBER_SESSION_HASH" <<'SQL'
INSERT INTO "Business" (id,name,slug,tier,"isActive","hasCompletedOnboarding","createdAt","updatedAt")
VALUES (:'business_id','Core Runtime Smoke',:'slug','FREE',true,false,now(),now());

INSERT INTO "Outlet" (id,"businessId",name,address,city,phone,"isActive","createdAt","updatedAt")
VALUES (:'outlet_id',:'business_id','Core Smoke Outlet','Smoke only','Jakarta',NULL,true,now(),now());

INSERT INTO "AdminUser" (id,"businessId","outletId",email,"passwordHash","fullName",role,"isActive","lastLoginAt","createdAt","updatedAt")
VALUES (:'staff_id',:'business_id',:'outlet_id',:'staff_email',:'staff_hash','Core Runtime Staff','STAFF',true,NULL,now(),now());

INSERT INTO "Card" (id,"businessId",name,description,level,"requiredStamps",status,"termsAndConditions","isDeleted","howItWorksSteps","createdAt","updatedAt") VALUES
(:'card1',:'business_id','Core Level 1','Smoke exact-boundary card',1,5,'ACTIVE',NULL,false,ARRAY[]::text[],now(),now()),
(:'card2',:'business_id','Core Level 2','Smoke overflow cycle card',2,3,'ACTIVE',NULL,false,ARRAY[]::text[],now()+interval '1 millisecond',now());

INSERT INTO "Reward" (id,"businessId",name,description,"sourceType","defaultExpiryDays","createdAt","updatedAt")
VALUES (:'reward_id',:'business_id','Core Smoke Reward','Milestone runtime smoke','MILESTONE',30,now(),now());

INSERT INTO "CardMilestone" (id,"businessId","cardId","stampCount","sortOrder","rewardId","rewardType","rewardValue",title,description,"createdAt","updatedAt")
VALUES (:'milestone_id',:'business_id',:'card1',5,1,:'reward_id','REWARD',NULL,'Complete Level 1','Runtime smoke milestone',now(),now());

INSERT INTO "Member" (id,"businessId",name,email,phone,"memberBarcode","totalStamps","dateJoined","createdAt","updatedAt")
VALUES (:'member_id',:'business_id','Core Runtime Member',:'member_email',NULL,:'barcode',0,now(),now(),now());

INSERT INTO "MemberCard" (id,"businessId","memberId","cardId","currentStamps","isActive","startedAt","completedAt","createdAt","updatedAt")
VALUES (:'member_card',:'business_id',:'member_id',:'card1',4,true,now(),NULL,now(),now());

INSERT INTO "MemberSession" (id,"businessId","memberId","sessionTokenHash","expiresAt","revokedAt",ip,"userAgent","createdAt","updatedAt")
VALUES (:'session_id',:'business_id',:'member_id',:'session_hash',now()+interval '1 hour',NULL,NULL,'core-runtime-smoke',now(),now());
SQL

echo "==> [3/10] Logging in through real admin auth and scanning member..."
write_json login "$TMP_DIR/login.json" "$STAFF_EMAIL" "$STAFF_PASSWORD"
STATUS="$(post_file '/api/admin/auth/login' "$TMP_DIR/login.json" "$TMP_DIR/login.out.json")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: admin login expected 200, got $STATUS" >&2; cat "$TMP_DIR/login.out.json" >&2; exit 1; }
ACCESS_TOKEN="$(extract_path "$TMP_DIR/login.out.json" 'data.accessToken')"

write_json scan_member "$TMP_DIR/member-scan.json" "$MEMBER_BARCODE"
STATUS="$(post_file '/api/admin/members/scan' "$TMP_DIR/member-scan.json" "$TMP_DIR/member-scan.out.json" "$ACCESS_TOKEN")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: member scan expected 200, got $STATUS" >&2; cat "$TMP_DIR/member-scan.out.json" >&2; exit 1; }
echo "PASS: real admin JWT scans the isolated member in tenant context."

echo "==> [4/10] Testing exact 4/5 +1 completion and level-up..."
write_json stamp "$TMP_DIR/stamp-one.json" 1 'exact boundary smoke'
STATUS="$(post_file "/api/admin/members/$MEMBER_ID/cards/$MEMBER_CARD_ID/add-stamp" "$TMP_DIR/stamp-one.json" "$TMP_DIR/stamp-one.out.json" "$ACCESS_TOKEN")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: exact-boundary add-stamp expected 200, got $STATUS" >&2; cat "$TMP_DIR/stamp-one.out.json" >&2; exit 1; }

EXACT_STATE="$(psql_exec -qAtF '|' -v business_id="$BUSINESS_ID" -v member_id="$MEMBER_ID" -v card1="$CARD_ONE_ID" -v card2="$CARD_TWO_ID" <<'SQL'
SELECT concat_ws('|',
  (SELECT concat("currentStamps",':',"isActive",':',"completedAt" IS NOT NULL) FROM "MemberCard" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND "cardId"=:'card1' ORDER BY "createdAt" LIMIT 1),
  (SELECT concat("currentStamps",':',"isActive") FROM "MemberCard" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND "cardId"=:'card2' AND "isActive"=true LIMIT 1),
  (SELECT count(*) FROM "MemberReward" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND status='AVAILABLE'),
  (SELECT coalesce(sum(stamps_added),0) FROM "Transaction" WHERE "businessId"=:'business_id' AND type='STAMP_ADDED')
);
SQL
)"
[[ "$EXACT_STATE" == "5:false:true|0:true|1|1" ]] || { echo "ERROR: exact-boundary state invalid: $EXACT_STATE" >&2; exit 1; }
echo "PASS: exact completion deactivates 5/5 card, creates level-2 0/3 card, reward, and transaction."

echo "==> [5/10] Testing +7 overflow across repeated level-2 cycles..."
ACTIVE_CARD_TWO="$(psql_exec -qAt -v business_id="$BUSINESS_ID" -v member_id="$MEMBER_ID" <<'SQL'
SELECT id FROM "MemberCard" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND "isActive"=true LIMIT 1;
SQL
)"
write_json stamp "$TMP_DIR/stamp-overflow.json" 7 'overflow smoke'
STATUS="$(post_file "/api/admin/members/$MEMBER_ID/cards/$ACTIVE_CARD_TWO/add-stamp" "$TMP_DIR/stamp-overflow.json" "$TMP_DIR/stamp-overflow.out.json" "$ACCESS_TOKEN")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: overflow add-stamp expected 200, got $STATUS" >&2; cat "$TMP_DIR/stamp-overflow.out.json" >&2; exit 1; }

OVERFLOW_STATE="$(psql_exec -qAtF '|' -v business_id="$BUSINESS_ID" -v member_id="$MEMBER_ID" -v card2="$CARD_TWO_ID" <<'SQL'
SELECT concat_ws('|',
 (SELECT count(*) FROM "MemberCard" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND "cardId"=:'card2' AND "isActive"=false AND "currentStamps"=3),
 (SELECT "currentStamps" FROM "MemberCard" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND "cardId"=:'card2' AND "isActive"=true LIMIT 1),
 (SELECT count(*) FROM "Transaction" WHERE "businessId"=:'business_id' AND type='STAMP_ADDED'),
 (SELECT coalesce(sum(stamps_added),0) FROM "Transaction" WHERE "businessId"=:'business_id' AND type='STAMP_ADDED')
);
SQL
)"
[[ "$OVERFLOW_STATE" == "2|1|4|8" ]] || { echo "ERROR: overflow state invalid: $OVERFLOW_STATE" >&2; exit 1; }
echo "PASS: +7 overflow completed two 3-stamp cycles and left the next active card at 1/3."

echo "==> [6/10] Issuing reward token through direct member_session verification..."
MEMBER_REWARD_ID="$(psql_exec -qAt -v business_id="$BUSINESS_ID" -v member_id="$MEMBER_ID" <<'SQL'
SELECT id FROM "MemberReward" WHERE "businessId"=:'business_id' AND "memberId"=:'member_id' AND status='AVAILABLE' ORDER BY "createdAt" LIMIT 1;
SQL
)"
[[ -n "$MEMBER_REWARD_ID" ]] || { echo "ERROR: milestone member reward missing" >&2; exit 1; }
printf '{}' > "$TMP_DIR/empty.json"
STATUS="$(post_file "/api/member/rewards/$MEMBER_REWARD_ID/token" "$TMP_DIR/empty.json" "$TMP_DIR/token.out.json" '' "member_session=$MEMBER_SESSION_RAW")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: member reward token issue expected 200, got $STATUS" >&2; cat "$TMP_DIR/token.out.json" >&2; exit 1; }
REWARD_TOKEN="$(extract_path "$TMP_DIR/token.out.json" 'data.token.token')"
[[ -n "$REWARD_TOKEN" ]] || { echo "ERROR: reward token missing" >&2; exit 1; }
echo "PASS: member_session SHA-256 verification issues an ACTIVE reward token."

echo "==> [7/10] Testing concurrent double-scan redemption..."
write_json scan_reward "$TMP_DIR/redeem.json" "$REWARD_TOKEN"
(
  post_file '/api/admin/rewards/scan' "$TMP_DIR/redeem.json" "$TMP_DIR/redeem-a.out.json" "$ACCESS_TOKEN" > "$TMP_DIR/redeem-a.status"
) & PID_A=$!
(
  post_file '/api/admin/rewards/scan' "$TMP_DIR/redeem.json" "$TMP_DIR/redeem-b.out.json" "$ACCESS_TOKEN" > "$TMP_DIR/redeem-b.status"
) & PID_B=$!
wait "$PID_A"
wait "$PID_B"
STATUS_A="$(cat "$TMP_DIR/redeem-a.status")"
STATUS_B="$(cat "$TMP_DIR/redeem-b.status")"
SUCCESS_COUNT=0
[[ "$STATUS_A" == "200" ]] && SUCCESS_COUNT=$((SUCCESS_COUNT+1))
[[ "$STATUS_B" == "200" ]] && SUCCESS_COUNT=$((SUCCESS_COUNT+1))
[[ "$SUCCESS_COUNT" == "1" ]] || { echo "ERROR: concurrent redemption expected exactly one HTTP 200, got $STATUS_A/$STATUS_B" >&2; cat "$TMP_DIR/redeem-a.out.json" >&2; cat "$TMP_DIR/redeem-b.out.json" >&2; exit 1; }
[[ "$STATUS_A" == "400" || "$STATUS_B" == "400" ]] || { echo "ERROR: losing concurrent redemption should be 400 conflict, got $STATUS_A/$STATUS_B" >&2; exit 1; }

echo "PASS: concurrent redemption allows exactly one success and rejects the replay."

echo "==> [8/10] Verifying atomic redemption state and audit trail..."
REDEEM_STATE="$(psql_exec -qAtF '|' -v business_id="$BUSINESS_ID" -v member_reward="$MEMBER_REWARD_ID" <<'SQL'
SELECT concat_ws('|',
 (SELECT status FROM "MemberReward" WHERE id=:'member_reward'),
 (SELECT count(*) FROM "RewardToken" WHERE "businessId"=:'business_id' AND "memberRewardId"=:'member_reward' AND "usedAt" IS NOT NULL),
 (SELECT count(*) FROM "Transaction" WHERE "businessId"=:'business_id' AND type='REWARD_REDEEMED'),
 (SELECT count(*) FROM "AuditLog" WHERE "businessId"=:'business_id'::uuid AND "actionType"='pos_add_stamp'),
 (SELECT count(*) FROM "AuditLog" WHERE "businessId"=:'business_id'::uuid AND "actionType"='pos_redeem_reward')
);
SQL
)"
[[ "$REDEEM_STATE" == "REDEEMED|1|1|2|1" ]] || { echo "ERROR: redemption/audit state invalid: $REDEEM_STATE" >&2; exit 1; }
echo "PASS: one reward redemption transaction exists and operational audit events were persisted."

echo "==> [9/10] Re-scanning member after all transitions..."
STATUS="$(post_file '/api/admin/members/scan' "$TMP_DIR/member-scan.json" "$TMP_DIR/member-scan-final.out.json" "$ACCESS_TOKEN")"
[[ "$STATUS" == "200" ]] || { echo "ERROR: final member scan expected 200, got $STATUS" >&2; cat "$TMP_DIR/member-scan-final.out.json" >&2; exit 1; }
FINAL_ACTIVE="$(psql_exec -qAt -v business_id="$BUSINESS_ID" -v member_id="$MEMBER_ID" <<'SQL'
SELECT concat(c.level,':',mc."currentStamps",'/',c."requiredStamps")
FROM "MemberCard" mc JOIN "Card" c ON c.id=mc."cardId"
WHERE mc."businessId"=:'business_id' AND mc."memberId"=:'member_id' AND mc."isActive"=true;
SQL
)"
[[ "$FINAL_ACTIVE" == "2:1/3" ]] || { echo "ERROR: final active card expected 2:1/3, got $FINAL_ACTIVE" >&2; exit 1; }
echo "PASS: member remains scannable with the expected active level-2 1/3 state."

echo "==> [10/10] Final health checks..."
curl --fail --show-error --silent "$BASE_URL/health"; echo
curl --fail --show-error --silent "$BASE_URL/health/db"; echo

echo "PASS: core loyalty runtime endpoints smoke-tested end-to-end on isolated fixtures."
echo "Backup: $BACKUP"
