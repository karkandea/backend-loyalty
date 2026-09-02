#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

SOURCE_SCRIPT="$ROOT_DIR/scripts/deploy-zero-membership-auth-parity.sh"
[[ -f "$SOURCE_SCRIPT" ]] || { echo "ERROR: $SOURCE_SCRIPT missing" >&2; exit 1; }

TMP_SCRIPT="$(mktemp "$ROOT_DIR/scripts/.zero-membership-auth.XXXXXX.sh")"
cleanup() { rm -f "$TMP_SCRIPT"; }
trap cleanup EXIT

python3 - "$SOURCE_SCRIPT" "$TMP_SCRIPT" <<'PY'
from pathlib import Path
import sys

src = Path(sys.argv[1]).read_text()
old = "SOURCE_ZERO_ELIGIBLE=\"$(source_psql -qAtc 'SELECT count(*) FROM auth.users u WHERE COALESCE(u.encrypted_password,'''') <> '''' AND u.email_confirmed_at IS NOT NULL AND u.deleted_at IS NULL AND (u.banned_until IS NULL OR u.banned_until <= now()) AND NOT EXISTS (SELECT 1 FROM public.\"BusinessUser\" b WHERE b.\"isActive\"=true AND (b.\"authUserId\"=u.id OR (b.\"authUserId\" IS NULL AND b.id=u.id::text)));')\""
new = '''SOURCE_ZERO_ELIGIBLE="$(source_psql -qAt <<'SQL'
SELECT count(*)
FROM auth.users u
WHERE COALESCE(u.encrypted_password, '') <> ''
  AND u.email_confirmed_at IS NOT NULL
  AND u.deleted_at IS NULL
  AND (u.banned_until IS NULL OR u.banned_until <= now())
  AND NOT EXISTS (
    SELECT 1
    FROM public."BusinessUser" b
    WHERE b."isActive" = true
      AND (b."authUserId" = u.id OR (b."authUserId" IS NULL AND b.id = u.id::text))
  );
SQL
)"'''
count = src.count(old)
if count != 1:
    raise SystemExit(f"ERROR: expected exactly one eligible-query line to patch, found {count}")
Path(sys.argv[2]).write_text(src.replace(old, new))
PY

chmod 700 "$TMP_SCRIPT"
bash -n "$TMP_SCRIPT"
exec bash "$TMP_SCRIPT"
