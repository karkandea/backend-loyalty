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
src = src.replace(old, new)

lines = src.splitlines()
out = []
replaced_banned = 0
replaced_unconfirmed = 0
for line in lines:
    if line.startswith('target_psql -q -v smoke_id="$SMOKE_ID" -c ') and '"bannedUntil"=now()+interval' in line:
        out.extend([
            'target_psql -q -v smoke_id="$SMOKE_ID" <<\'SQL\'',
            'UPDATE "StandaloneAuthIdentity"',
            'SET "bannedUntil" = now() + interval \'1 day\'',
            'WHERE id = :\'smoke_id\'::uuid;',
            'SQL',
        ])
        replaced_banned += 1
        continue
    if line.startswith('target_psql -q -v smoke_id="$SMOKE_ID" -c ') and '"emailConfirmedAt"=NULL' in line:
        out.extend([
            'target_psql -q -v smoke_id="$SMOKE_ID" <<\'SQL\'',
            'UPDATE "StandaloneAuthIdentity"',
            'SET "bannedUntil" = NULL,',
            '    "emailConfirmedAt" = NULL',
            'WHERE id = :\'smoke_id\'::uuid;',
            'SQL',
        ])
        replaced_unconfirmed += 1
        continue
    out.append(line)

if replaced_banned != 1 or replaced_unconfirmed != 1:
    raise SystemExit(
        f"ERROR: expected one banned and one unconfirmed update; got banned={replaced_banned} unconfirmed={replaced_unconfirmed}"
    )

Path(sys.argv[2]).write_text("\n".join(out) + "\n")
PY

chmod 700 "$TMP_SCRIPT"
bash -n "$TMP_SCRIPT"
exec bash "$TMP_SCRIPT"
