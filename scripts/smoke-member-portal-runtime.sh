#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
COOKIE_JAR="$(mktemp /tmp/loyalty-member-cookie.XXXXXX)"
trap 'rm -f "$COOKIE_JAR"' EXIT

MEMBER_EMAIL='qa.member@loyalty.local'
MEMBER_PASSWORD='MemberSmoke-2026!'
BUSINESS_SLUG='qa-pos'
STAFF_EMAIL='qa.pos@loyalty.local'
STAFF_PASSWORD='SmokeOnly-Refresh-2026!'
MEMBER_ID='10000000-0000-0000-0000-000000000004'
MEMBER_CARD_ID='10000000-0000-0000-0000-000000000006'

for command in curl python3 docker; do
  command -v "$command" >/dev/null 2>&1 || { echo "ERROR: missing command: $command" >&2; exit 1; }
done
[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }

json_get() {
  local path="$1"
  python3 -c 'import json,sys
obj=json.load(sys.stdin)
for key in sys.argv[1].split("."):
    if isinstance(obj, list): obj=obj[int(key)]
    else: obj=obj[key]
if obj is None: print("")
elif isinstance(obj,(dict,list)): print(json.dumps(obj,separators=(",",":")))
else: print(str(obj))' "$path"
}

request_json() {
  local method="$1" url="$2" data="${3:-}" auth="${4:-}" cookie_mode="${5:-none}"
  local args=(--silent --show-error --fail-with-body -X "$method" -H 'Content-Type: application/json')
  [[ -n "$auth" ]] && args+=(-H "Authorization: Bearer $auth")
  [[ -n "$data" ]] && args+=(--data "$data")
  [[ "$cookie_mode" == "write" ]] && args+=(-c "$COOKIE_JAR")
  [[ "$cookie_mode" == "read" ]] && args+=(-b "$COOKIE_JAR")
  [[ "$cookie_mode" == "both" ]] && args+=(-b "$COOKIE_JAR" -c "$COOKIE_JAR")
  curl "${args[@]}" "$url"
}

echo "==> [1/9] Resetting isolated QA fixture..."
bash scripts/ensure-ui-qa-fixture.sh >/dev/null
echo "PASS: QA fixture reset."

echo "==> [2/9] Member login creates opaque member_session cookie..."
MEMBER_LOGIN="$(request_json POST "$BASE_URL/api/member/auth/login" "{\"email\":\"$MEMBER_EMAIL\",\"password\":\"$MEMBER_PASSWORD\",\"businessSlug\":\"$BUSINESS_SLUG\"}" '' write)"
[[ "$(printf '%s' "$MEMBER_LOGIN" | json_get success)" == "True" ]] || { echo "ERROR: member login response invalid" >&2; exit 1; }
grep -q 'member_session' "$COOKIE_JAR" || { echo "ERROR: member_session cookie missing" >&2; exit 1; }
echo "PASS: member login/session issuance works."

echo "==> [3/9] Member summary starts at QA card 0/5..."
SUMMARY="$(request_json GET "$BASE_URL/api/member/summary" '' '' read)"
SUMMARY_STATE="$(printf '%s' "$SUMMARY" | python3 -c 'import json,sys; d=json.load(sys.stdin)["data"]; c=d["activeCard"]; print("{}/{}|{}".format(c["currentStamps"],c["requiredStamps"],d["member"]["memberBarcode"]))')"
[[ "$SUMMARY_STATE" == '0/5|QA-MEMBER-001' ]] || { echo "ERROR: initial member summary invalid: $SUMMARY_STATE" >&2; exit 1; }
echo "PASS: member summary is tenant/session scoped."

echo "==> [4/9] POS login and exact-boundary +5 stamp..."
ADMIN_LOGIN="$(request_json POST "$BASE_URL/api/admin/auth/login" "{\"email\":\"$STAFF_EMAIL\",\"password\":\"$STAFF_PASSWORD\"}")"
ACCESS_TOKEN="$(printf '%s' "$ADMIN_LOGIN" | json_get data.accessToken)"
[[ -n "$ACCESS_TOKEN" ]] || { echo "ERROR: admin access token missing" >&2; exit 1; }
request_json POST "$BASE_URL/api/admin/members/$MEMBER_ID/cards/$MEMBER_CARD_ID/add-stamp" '{"stampCount":5,"transactionNotes":"member portal cross-role smoke"}' "$ACCESS_TOKEN" >/dev/null
echo "PASS: POS added exact-boundary stamps."

echo "==> [5/9] Member sees level-up card and available reward..."
SUMMARY_AFTER="$(request_json GET "$BASE_URL/api/member/summary" '' '' read)"
AFTER_STATE="$(printf '%s' "$SUMMARY_AFTER" | python3 -c 'import json,sys; d=json.load(sys.stdin)["data"]; c=d["activeCard"]; s=d["stats"]; print("{}/{}|{}|{}".format(c["currentStamps"],c["requiredStamps"],c["level"]["value"],s["totalVouchersAvailable"]))')"
[[ "$AFTER_STATE" == '0/3|2|1' ]] || { echo "ERROR: post-stamp member state invalid: $AFTER_STATE" >&2; exit 1; }
REWARDS="$(request_json GET "$BASE_URL/api/member/rewards?status=AVAILABLE" '' '' read)"
MEMBER_REWARD_ID="$(printf '%s' "$REWARDS" | json_get data.rewards.0.id)"
[[ -n "$MEMBER_REWARD_ID" ]] || { echo "ERROR: available member reward missing" >&2; exit 1; }
echo "PASS: customer view reflects POS level-up/reward state."

echo "==> [6/9] Member generates short-lived reward token..."
TOKEN_RESPONSE="$(request_json POST "$BASE_URL/api/member/rewards/$MEMBER_REWARD_ID/token" '{}' '' read)"
REWARD_TOKEN="$(printf '%s' "$TOKEN_RESPONSE" | json_get data.token.token)"
[[ -n "$REWARD_TOKEN" ]] || { echo "ERROR: reward token missing" >&2; exit 1; }
echo "PASS: member reward token issuance works."

echo "==> [7/9] POS redeems member token..."
request_json POST "$BASE_URL/api/admin/rewards/scan" "{\"rewardToken\":\"$REWARD_TOKEN\"}" "$ACCESS_TOKEN" >/dev/null
REWARDS_AFTER="$(request_json GET "$BASE_URL/api/member/rewards" '' '' read)"
REWARD_STATUS="$(printf '%s' "$REWARDS_AFTER" | python3 -c 'import json,sys; rows=json.load(sys.stdin)["data"]["rewards"]; print(next(x["status"] for x in rows if x["id"]==sys.argv[1]))' "$MEMBER_REWARD_ID")"
[[ "$REWARD_STATUS" == 'REDEEMED' ]] || { echo "ERROR: reward status after POS redeem is $REWARD_STATUS" >&2; exit 1; }
echo "PASS: POS redemption is visible to member portal."

echo "==> [8/9] Member transaction history contains earn + redeem..."
TRANSACTIONS="$(request_json GET "$BASE_URL/api/member/transactions?limit=10" '' '' read)"
TX_STATE="$(printf '%s' "$TRANSACTIONS" | python3 -c 'import json,sys; t=[x["type"] for x in json.load(sys.stdin)["data"]["transactions"]]; print("{}|{}".format(int("EARN_STAMP" in t),int("REDEEM_REWARD" in t)))')"
[[ "$TX_STATE" == '1|1' ]] || { echo "ERROR: member transaction parity invalid: $TX_STATE" >&2; exit 1; }
echo "PASS: member history includes earn and redeem."

echo "==> [9/9] Member logout revokes cookie-backed session..."
request_json POST "$BASE_URL/api/member/auth/logout" '{}' '' both >/dev/null
STATUS="$(curl --silent --output /dev/null --write-out '%{http_code}' -b "$COOKIE_JAR" "$BASE_URL/api/member/summary")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: expected summary 401 after logout, got $STATUS" >&2; exit 1; }
echo "PASS: member logout/session revocation works."

echo "PASS: MEMBER PORTAL CROSS-ROLE RUNTIME SMOKE GREEN."
