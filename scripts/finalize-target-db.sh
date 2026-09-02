#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ ! -f .env.vps ]]; then
  echo "ERROR: .env.vps not found in $ROOT_DIR" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
source .env.vps
set +a

: "${LOYALTY_DB_PASSWORD:?LOYALY_DB_PASSWORD is required in .env.vps}"

DB_CONTAINER="${LOYALTY_DB_CONTAINER:-loyalty-postgres}"
DB_NAME="${LOYALTY_DB_NAME:-loyalty}"
DB_USER="${LOYALTY_DB_USER:-loyalty_app}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
PRE_AUDIT="/root/loyalty-audit-pre-harden-${STAMP}.log"
POST_AUDIT="/root/loyalty-audit-post-harden-${STAMP}.log"
BACKUP="/root/loyalty-post-repair-pre-harden-${STAMP}.dump"

psql_exec() {
  docker exec \
    -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
    -i "$DB_CONTAINER" \
    psql \
      -h 127.0.0.1 \
      -U "$DB_USER" \
      -d "$DB_NAME" \
      -v ON_ERROR_STOP=1 \
      "$@"
}

echo "==> [1/6] Checking target database/container..."
docker inspect "$DB_CONTAINER" >/dev/null
psql_exec -Atqc 'SELECT 1' >/dev/null

echo "==> [2/6] Running pre-hardening audit..."
psql_exec < scripts/audit-legacy-data.sql | tee "$PRE_AUDIT"

echo "==> [3/6] Validating critical invariants (fail-fast)..."
psql_exec <<'SQL'
DO $$
DECLARE
  n bigint;
BEGIN
  IF to_regclass('auth.users') IS NOT NULL THEN
    RAISE EXCEPTION 'REFUSING FINALIZE: auth.users exists; this does not look like the standalone target';
  END IF;

  IF to_regclass('public."LegacyAuthUserPassword"') IS NULL THEN
    RAISE EXCEPTION 'REFUSING FINALIZE: LegacyAuthUserPassword target marker is missing';
  END IF;

  SELECT COUNT(*) INTO n
  FROM "MemberCard" mc
  JOIN "Card" c ON c.id = mc."cardId"
  WHERE mc."currentStamps" < 0
     OR c."requiredStamps" <= 0
     OR (mc."isActive" AND mc."currentStamps" > c."requiredStamps");
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % invalid MemberCard/Card stamp row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM (
    SELECT "businessId", "memberId"
    FROM "MemberCard"
    WHERE "isActive" = true
    GROUP BY "businessId", "memberId"
    HAVING COUNT(*) > 1
  ) d;
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % member/business pair(s) have duplicate active cards', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "MemberCard" mc
  JOIN "Member" m ON m.id = mc."memberId"
  JOIN "Card" c ON c.id = mc."cardId"
  WHERE mc."businessId" <> m."businessId"
     OR mc."businessId" <> c."businessId";
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % cross-tenant MemberCard row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "MemberReward" mr
  JOIN "Member" m ON m.id = mr."memberId"
  JOIN "Reward" r ON r.id = mr."rewardId"
  LEFT JOIN "MemberCard" mc ON mc.id = mr."memberCardId"
  WHERE mr."businessId" <> m."businessId"
     OR mr."businessId" <> r."businessId"
     OR (mc.id IS NOT NULL AND mr."businessId" <> mc."businessId");
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % cross-tenant MemberReward row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "RewardToken" rt
  LEFT JOIN "MemberReward" mr ON mr.id = rt."memberRewardId"
  LEFT JOIN "Member" m ON m.id = rt."memberId"
  LEFT JOIN "MemberCard" mc ON mc.id = rt."memberCardId"
  WHERE (mr.id IS NOT NULL AND rt."businessId" <> mr."businessId")
     OR (m.id IS NOT NULL AND rt."businessId" <> m."businessId")
     OR (mc.id IS NOT NULL AND rt."businessId" <> mc."businessId");
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % cross-tenant RewardToken row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "Transaction" t
  JOIN "Member" m ON m.id = t."memberId"
  JOIN "MemberCard" mc ON mc.id = t."memberCardId"
  JOIN "Card" c ON c.id = t."cardId"
  JOIN "Outlet" o ON o.id = t."outletId"
  JOIN "AdminUser" a ON a.id = t."staffId"
  LEFT JOIN "Reward" r ON r.id = t."rewardId"
  WHERE t."businessId" <> m."businessId"
     OR t."businessId" <> mc."businessId"
     OR t."businessId" <> c."businessId"
     OR t."businessId" <> o."businessId"
     OR t."businessId" <> a."businessId"
     OR (r.id IS NOT NULL AND t."businessId" <> r."businessId");
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % cross-tenant Transaction row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "CardMilestone" cm
  JOIN "Card" c ON c.id = cm."cardId"
  LEFT JOIN "Reward" r ON r.id = cm."rewardId"
  WHERE cm."businessId" <> c."businessId"
     OR (r.id IS NOT NULL AND cm."businessId" <> r."businessId");
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % cross-tenant CardMilestone row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "MemberReward"
  WHERE (status = 'REDEEMED' AND "redeemedAt" IS NULL)
     OR (status <> 'REDEEMED' AND "redeemedAt" IS NOT NULL)
     OR ("expiresAt" IS NOT NULL AND "expiresAt" < "issuedAt");
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % MemberReward lifecycle inconsistency row(s)', n;
  END IF;

  SELECT COUNT(*) INTO n
  FROM "RewardToken"
  WHERE status = 'ACTIVE'
    AND "usedAt" IS NULL
    AND "expiresAt" IS NOT NULL
    AND "expiresAt" <= now();
  IF n > 0 THEN
    RAISE EXCEPTION 'PRE-HARDEN VALIDATION FAILED: % expired unused ACTIVE RewardToken row(s)', n;
  END IF;
END $$;
SQL

echo "==> [4/6] Creating post-repair/pre-hardening backup..."
docker exec \
  -e PGPASSWORD="$LOYALTY_DB_PASSWORD" \
  "$DB_CONTAINER" \
  pg_dump \
    -h 127.0.0.1 \
    -U "$DB_USER" \
    -d "$DB_NAME" \
    -Fc \
  > "$BACKUP"

test -s "$BACKUP"
echo "BACKUP OK: $BACKUP"
ls -lh "$BACKUP"

echo "==> [5/6] Applying target schema hardening..."
psql_exec < scripts/harden-target-schema.sql

psql_exec <<'SQL'
DO $$
DECLARE
  n bigint;
BEGIN
  SELECT COUNT(*) INTO n
  FROM pg_class i
  JOIN pg_namespace ns ON ns.oid = i.relnamespace
  JOIN pg_index ix ON ix.indexrelid = i.oid
  JOIN pg_class t ON t.oid = ix.indrelid
  WHERE ns.nspname = 'public'
    AND t.relname = 'MemberCard'
    AND i.relname = 'MemberCard_one_active_per_member_key'
    AND ix.indisunique = true
    AND ix.indpred IS NOT NULL;
  IF n <> 1 THEN
    RAISE EXCEPTION 'POST-HARDEN VALIDATION FAILED: active-only partial unique MemberCard index missing or unexpected';
  END IF;

  SELECT COUNT(*) INTO n
  FROM pg_constraint
  WHERE conname IN (
    'MemberCard_currentStamps_nonnegative_ck',
    'Card_requiredStamps_positive_ck',
    'Reward_defaultExpiryDays_positive_ck',
    'MemberReward_expiry_after_issue_ck'
  );
  IF n <> 4 THEN
    RAISE EXCEPTION 'POST-HARDEN VALIDATION FAILED: expected 4 hardening CHECK constraints, found %', n;
  END IF;
END $$;
SQL

echo "==> [6/6] Running post-hardening audit..."
psql_exec < scripts/audit-legacy-data.sql | tee "$POST_AUDIT"

echo
echo "PASS: target DB repaired, validated, backed up, and hardened."
echo "Pre-hardening audit:  $PRE_AUDIT"
echo "Post-hardening audit: $POST_AUDIT"
echo "Backup:               $BACKUP"
