#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

PORT="${LOYALTY_API_PORT:-5092}"
BASE_URL="http://127.0.0.1:${PORT}"
OWNER_EMAIL='qa.owner@loyalty.local'
OWNER_PASSWORD='SmokeOnly-Refresh-2026!'
BUSINESS_ID='10000000-0000-0000-0000-000000000001'
INVITE_EMAIL='qa.invitee@loyalty.local'

json_get() {
  local path="$1"
  python3 -c 'import json,sys
obj=json.load(sys.stdin)
for key in sys.argv[1].split("."):
    if isinstance(obj,list): obj=obj[int(key)]
    else: obj=obj[key]
if obj is None: print("")
elif isinstance(obj,(dict,list)): print(json.dumps(obj,separators=(",",":")))
else: print(str(obj))' "$path"
}

request_json() {
  local method="$1" url="$2" data="${3:-}" auth="${4:-}"
  local args=(--silent --show-error --fail-with-body -X "$method" -H 'Content-Type: application/json')
  [[ -n "$auth" ]] && args+=(-H "Authorization: Bearer $auth")
  [[ -n "$data" ]] && args+=(--data "$data")
  curl "${args[@]}" "$url"
}

for command in curl python3; do
  command -v "$command" >/dev/null 2>&1 || { echo "ERROR: missing command: $command" >&2; exit 1; }
done

echo "==> [1/8] Resetting isolated QA fixture..."
bash scripts/ensure-ui-qa-fixture.sh >/dev/null
echo "PASS: QA fixture reset."

echo "==> [2/8] Owner login selects the single active business..."
LOGIN="$(request_json POST "$BASE_URL/api/business/auth/login" "{\"email\":\"$OWNER_EMAIL\",\"password\":\"$OWNER_PASSWORD\"}")"
ACCESS="$(printf '%s' "$LOGIN" | json_get data.accessToken)"
REFRESH="$(printf '%s' "$LOGIN" | json_get data.refreshToken)"
LOGIN_STATE="$(printf '%s' "$LOGIN" | python3 -c 'import json,sys; d=json.load(sys.stdin)["data"]; print(f"{d[\"businessId\"]}|{d[\"membershipCount\"]}|{d[\"role\"]}")')"
[[ "$LOGIN_STATE" == "$BUSINESS_ID|1|OWNER" ]] || { echo "ERROR: owner login state invalid: $LOGIN_STATE" >&2; exit 1; }
echo "PASS: owner login business context works."

echo "==> [3/8] Membership list and refresh rotation preserve business access..."
MEMBERSHIPS="$(request_json GET "$BASE_URL/api/business/portal/memberships" '' "$ACCESS")"
MEMBERSHIP_STATE="$(printf '%s' "$MEMBERSHIPS" | python3 -c 'import json,sys; rows=json.load(sys.stdin)["data"]["memberships"]; print(f"{len(rows)}|{rows[0][\"businessId\"]}|{rows[0][\"role\"]}")')"
[[ "$MEMBERSHIP_STATE" == "1|$BUSINESS_ID|owner" ]] || { echo "ERROR: membership state invalid: $MEMBERSHIP_STATE" >&2; exit 1; }
ROTATED="$(request_json POST "$BASE_URL/api/auth/refresh" "{\"refreshToken\":\"$REFRESH\"}")"
ACCESS="$(printf '%s' "$ROTATED" | json_get data.accessToken)"
REFRESH="$(printf '%s' "$ROTATED" | json_get data.refreshToken)"
[[ -n "$ACCESS" && -n "$REFRESH" ]] || { echo "ERROR: owner refresh rotation failed" >&2; exit 1; }
echo "PASS: membership list and refresh rotation work."

echo "==> [4/8] Owner summary reads tenant-scoped operational counts..."
SUMMARY="$(request_json GET "$BASE_URL/api/business/portal/summary" '' "$ACCESS")"
SUMMARY_STATE="$(printf '%s' "$SUMMARY" | python3 -c 'import json,sys; d=json.load(sys.stdin)["data"]; print(f"{d[\"hasCompletedOnboarding\"]}|{d[\"memberCount\"]}|{d[\"activeCardCount\"]}|{d[\"rewardCount\"]}|{d[\"outletCount\"]}|{d[\"teamMemberCount\"]}")')"
[[ "$SUMMARY_STATE" == 'False|1|2|1|1|1' ]] || { echo "ERROR: owner summary invalid: $SUMMARY_STATE" >&2; exit 1; }
echo "PASS: business dashboard summary is correct."

echo "==> [5/8] Owner completes onboarding and state persists..."
request_json POST "$BASE_URL/api/business/portal/onboarding/complete" '{}' "$ACCESS" >/dev/null
SUMMARY_AFTER="$(request_json GET "$BASE_URL/api/business/portal/summary" '' "$ACCESS")"
[[ "$(printf '%s' "$SUMMARY_AFTER" | json_get data.hasCompletedOnboarding)" == 'True' ]] || { echo "ERROR: onboarding completion did not persist" >&2; exit 1; }
echo "PASS: onboarding completion persists."

echo "==> [6/8] Owner creates and lists team invitation..."
request_json POST "$BASE_URL/api/business/team/invitations" "{\"email\":\"$INVITE_EMAIL\",\"role\":\"STAFF\",\"permissions\":[]}" "$ACCESS" >/dev/null
INVITES="$(request_json GET "$BASE_URL/api/business/team/invitations" '' "$ACCESS")"
INVITE_ID="$(printf '%s' "$INVITES" | python3 -c 'import json,sys; rows=json.load(sys.stdin)["data"]; print(next((x["invitationId"] for x in rows if x["email"]==sys.argv[1]),""))' "$INVITE_EMAIL")"
[[ -n "$INVITE_ID" ]] || { echo "ERROR: created invitation not found in list" >&2; exit 1; }
echo "PASS: owner team invitation create/list works."

echo "==> [7/8] Owner revokes team invitation..."
request_json POST "$BASE_URL/api/business/team/invitations/revoke" "{\"inviteId\":\"$INVITE_ID\"}" "$ACCESS" >/dev/null
INVITES_AFTER="$(request_json GET "$BASE_URL/api/business/team/invitations" '' "$ACCESS")"
REMAINING="$(printf '%s' "$INVITES_AFTER" | python3 -c 'import json,sys; rows=json.load(sys.stdin)["data"]; print(sum(1 for x in rows if x["email"]==sys.argv[1]))' "$INVITE_EMAIL")"
[[ "$REMAINING" == '0' ]] || { echo "ERROR: revoked invitation still active" >&2; exit 1; }
echo "PASS: invitation revoke works."

echo "==> [8/8] Owner logout revokes rotated refresh token..."
request_json POST "$BASE_URL/api/auth/logout" "{\"refreshToken\":\"$REFRESH\"}" >/dev/null
STATUS="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' --data "{\"refreshToken\":\"$REFRESH\"}" "$BASE_URL/api/auth/refresh")"
[[ "$STATUS" == '401' ]] || { echo "ERROR: expected revoked owner refresh token to return 401, got $STATUS" >&2; exit 1; }
echo "PASS: owner logout/revocation works."

echo "PASS: BUSINESS OWNER PORTAL RUNTIME SMOKE GREEN."
