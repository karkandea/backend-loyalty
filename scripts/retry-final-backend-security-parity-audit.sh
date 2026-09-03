#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

TMP_LOG="$(mktemp /tmp/loyalty-final-retry.XXXXXX.log)"
cleanup() { rm -f "$TMP_LOG"; }
trap cleanup EXIT

echo '==> [retry preflight] Running core loyalty smoke with concise diagnostics...'
set +e
bash scripts/smoke-core-loyalty-runtime.sh >"$TMP_LOG" 2>&1
RC=$?
set -e

if [[ "$RC" -ne 0 ]]; then
  echo "ERROR: core loyalty smoke failed with exit code $RC." >&2
  echo '=== concise root cause ===' >&2
  grep -E '(^|[[:space:]])(ERROR:|FAIL:)' "$TMP_LOG" | tail -n 20 >&2 || true
  echo '=== last 30 smoke lines ===' >&2
  tail -n 30 "$TMP_LOG" >&2 || true
  exit "$RC"
fi

grep -E '^(PASS:|==>)' "$TMP_LOG" | tail -n 30 || true

echo 'PASS: core loyalty smoke preflight is green.'
echo '==> [retry final] Running complete final backend security/parity audit...'
exec bash scripts/final-backend-security-parity-audit.sh
