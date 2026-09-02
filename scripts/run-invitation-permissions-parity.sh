#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

SOURCE_SCRIPT="$ROOT_DIR/scripts/deploy-invitation-permissions-parity.sh"
[[ -f "$SOURCE_SCRIPT" ]] || { echo "ERROR: $SOURCE_SCRIPT missing" >&2; exit 1; }

TMP_SCRIPT="$(mktemp "$ROOT_DIR/scripts/.invite-permissions.XXXXXX.sh")"
cleanup() { rm -f "$TMP_SCRIPT"; }
trap cleanup EXIT

python3 - "$SOURCE_SCRIPT" "$TMP_SCRIPT" <<'PY'
from pathlib import Path
import sys

src = Path(sys.argv[1]).read_text()
old = '''read -r TARGET_BUSINESS SOURCE_BUSINESS < <(psql_exec -qAtF ' ' <<'SQL'
SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt", id LIMIT 2;
SQL
)
if [[ -z "${TARGET_BUSINESS:-}" || -z "${SOURCE_BUSINESS:-}" || "$TARGET_BUSINESS" == "$SOURCE_BUSINESS" ]]; then
  echo 'ERROR: at least two active businesses are required for isolated invitation permission smoke' >&2
  exit 1
fi'''
new = '''mapfile -t BUSINESS_IDS < <(psql_exec -qAt <<'SQL'
SELECT id FROM "Business" WHERE "isActive"=true ORDER BY "createdAt", id LIMIT 2;
SQL
)
[[ "${#BUSINESS_IDS[@]}" -eq 2 ]] || {
  echo 'ERROR: at least two active businesses are required for isolated invitation permission smoke' >&2
  exit 1
}
TARGET_BUSINESS="${BUSINESS_IDS[0]}"
SOURCE_BUSINESS="${BUSINESS_IDS[1]}"
[[ "$TARGET_BUSINESS" != "$SOURCE_BUSINESS" ]] || {
  echo 'ERROR: invitation permission smoke requires two distinct active businesses' >&2
  exit 1
}'''
count = src.count(old)
if count != 1:
    raise SystemExit(f'ERROR: expected one business-selection block to patch, found {count}')
Path(sys.argv[2]).write_text(src.replace(old, new))
PY

chmod 700 "$TMP_SCRIPT"
bash -n "$TMP_SCRIPT"
exec bash "$TMP_SCRIPT"
