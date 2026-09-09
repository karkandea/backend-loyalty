#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BASE_URL="${BASE_URL:-http://127.0.0.1:5092}"
COOKIE_JAR="$(mktemp /tmp/loyalty-phone-guard.XXXXXX)"
trap 'rm -f "$COOKIE_JAR" /tmp/loyalty-phone-guard.json' EXIT

curl --silent --show-error --fail-with-body   -c "$COOKIE_JAR"   -H 'Content-Type: application/json'   --data '{"email":"qa.member@loyalty.local","password":"MemberSmoke-2026!","businessSlug":"qa-pos"}'   "$BASE_URL/api/member/auth/login" >/dev/null

STATUS="$(curl --silent --show-error   --output /tmp/loyalty-phone-guard.json   --write-out '%{http_code}'   -b "$COOKIE_JAR"   -X PATCH   -H 'Content-Type: application/json'   --data '{"phone":"+6285550009901"}'   "$BASE_URL/api/member/account")"

[[ "$STATUS" == "409" ]] || {
  echo "ERROR: expected generic phone PATCH to return 409, got $STATUS" >&2
  cat /tmp/loyalty-phone-guard.json >&2
  exit 1
}

ME="$(curl --silent --show-error --fail-with-body -b "$COOKIE_JAR" "$BASE_URL/api/member/me")"
printf '%s' "$ME" | python3 -c '
import json,sys
d=json.load(sys.stdin)["data"]["member"]
assert d["phone"] is None, d
'
echo "PASS: generic member profile PATCH cannot bypass WhatsApp phone verification."
