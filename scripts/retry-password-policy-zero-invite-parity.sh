#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

API_CONTAINER="${API_CONTAINER:-backend-loyalty}"

if bash scripts/deploy-password-policy-zero-invite-parity.sh; then
  exit 0
fi

rc=$?
echo >&2
echo '=== backend-loyalty diagnostic logs (last 260 lines) ===' >&2
docker logs --tail 260 "$API_CONTAINER" 2>&1 >&2 || true
exit "$rc"
