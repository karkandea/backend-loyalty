#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-$ROOT_DIR/.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"
SOURCE_SCRIPT="$ROOT_DIR/scripts/smoke-core-loyalty-runtime.sh"

[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
[[ -f "$SOURCE_SCRIPT" ]] || { echo "ERROR: $SOURCE_SCRIPT not found" >&2; exit 1; }

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

COLUMN_UDT="$(psql_exec -qAt <<'SQL'
SELECT udt_name
FROM information_schema.columns
WHERE table_schema='public'
  AND table_name='Card'
  AND column_name='howItWorksSteps';
SQL
)"

# Keep the generated script under scripts/ so its own ROOT_DIR calculation still
# resolves to the repository root rather than / when executed from /tmp.
TMP_SCRIPT="$(mktemp "$ROOT_DIR/scripts/.core-runtime-schema-aware.XXXXXX.sh")"
cleanup() { rm -f "$TMP_SCRIPT"; }
trap cleanup EXIT

case "$COLUMN_UDT" in
  jsonb)
    python3 - "$SOURCE_SCRIPT" "$TMP_SCRIPT" <<'PY'
from pathlib import Path
import sys
src = Path(sys.argv[1]).read_text()
needle = 'ARRAY[]::text[]'
count = src.count(needle)
if count != 2:
    raise SystemExit(f'ERROR: expected exactly 2 legacy howItWorksSteps array literals, found {count}')
Path(sys.argv[2]).write_text(src.replace(needle, "'[]'::jsonb"))
PY
    echo "INFO: Card.howItWorksSteps is jsonb; using JSON empty-array fixture values."
    ;;
  _text)
    cp "$SOURCE_SCRIPT" "$TMP_SCRIPT"
    echo "INFO: Card.howItWorksSteps is text[]; using legacy array fixture values."
    ;;
  *)
    echo "ERROR: unsupported Card.howItWorksSteps type: ${COLUMN_UDT:-<missing>}" >&2
    exit 1
    ;;
esac

# PostgreSQL text rendering of booleans is f/t, not false/true. Normalize the
# smoke assertion to the actual psql output so a correct runtime state does not
# fail the harness.
python3 - "$TMP_SCRIPT" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
src = path.read_text()
old = '5:false:true|0:true|1|1'
new = '5:f:t|0:t|1|1'
count = src.count(old)
if count != 1:
    raise SystemExit(f'ERROR: expected exactly 1 legacy boolean assertion, found {count}')
path.write_text(src.replace(old, new))
PY

echo "INFO: PostgreSQL boolean assertion normalized to f/t output."
chmod 700 "$TMP_SCRIPT"
bash -n "$TMP_SCRIPT"
ENV_FILE="$ENV_FILE" bash "$TMP_SCRIPT"

echo "PASS: schema-aware core loyalty runtime retry completed successfully."
