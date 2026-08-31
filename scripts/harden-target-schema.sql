-- Target-only schema hardening for the standalone Loyalty PostgreSQL database.
-- Run AFTER restoring the legacy database into the VPS and AFTER audit-legacy-data.sql.
-- Do NOT run this against the legacy Supabase production database.
--
-- This script intentionally fails instead of silently repairing conflicting data.

BEGIN;

DO $$
DECLARE
  duplicate_active_count bigint;
  invalid_member_card_count bigint;
  invalid_card_count bigint;
BEGIN
  SELECT COUNT(*) INTO duplicate_active_count
  FROM (
    SELECT "businessId", "memberId"
    FROM "MemberCard"
    WHERE "isActive" = true
    GROUP BY "businessId", "memberId"
    HAVING COUNT(*) > 1
  ) x;

  IF duplicate_active_count > 0 THEN
    RAISE EXCEPTION 'Cannot harden MemberCard: % member(s) have more than one active card', duplicate_active_count;
  END IF;

  SELECT COUNT(*) INTO invalid_member_card_count
  FROM "MemberCard"
  WHERE "currentStamps" < 0;

  IF invalid_member_card_count > 0 THEN
    RAISE EXCEPTION 'Cannot harden MemberCard: % row(s) have negative currentStamps', invalid_member_card_count;
  END IF;

  SELECT COUNT(*) INTO invalid_card_count
  FROM "Card"
  WHERE "requiredStamps" <= 0;

  IF invalid_card_count > 0 THEN
    RAISE EXCEPTION 'Cannot harden Card: % row(s) have requiredStamps <= 0', invalid_card_count;
  END IF;
END $$;

-- Legacy Prisma used UNIQUE (businessId, memberId, isActive).
-- That accidentally allows at most one historical inactive card, which breaks
-- repeated completion / level-up cycles. The actual invariant is only:
-- "one ACTIVE card per business/member".
ALTER TABLE "MemberCard"
  DROP CONSTRAINT IF EXISTS "MemberCard_businessId_memberId_isActive_key";

DROP INDEX IF EXISTS "MemberCard_businessId_memberId_isActive_key";

CREATE UNIQUE INDEX IF NOT EXISTS "MemberCard_one_active_per_member_key"
  ON "MemberCard" ("businessId", "memberId")
  WHERE "isActive" = true;

ALTER TABLE "MemberCard"
  DROP CONSTRAINT IF EXISTS "MemberCard_currentStamps_nonnegative_ck";
ALTER TABLE "MemberCard"
  ADD CONSTRAINT "MemberCard_currentStamps_nonnegative_ck"
  CHECK ("currentStamps" >= 0);

ALTER TABLE "Card"
  DROP CONSTRAINT IF EXISTS "Card_requiredStamps_positive_ck";
ALTER TABLE "Card"
  ADD CONSTRAINT "Card_requiredStamps_positive_ck"
  CHECK ("requiredStamps" > 0);

ALTER TABLE "Reward"
  DROP CONSTRAINT IF EXISTS "Reward_defaultExpiryDays_positive_ck";
ALTER TABLE "Reward"
  ADD CONSTRAINT "Reward_defaultExpiryDays_positive_ck"
  CHECK ("defaultExpiryDays" IS NULL OR "defaultExpiryDays" > 0);

ALTER TABLE "MemberReward"
  DROP CONSTRAINT IF EXISTS "MemberReward_expiry_after_issue_ck";
ALTER TABLE "MemberReward"
  ADD CONSTRAINT "MemberReward_expiry_after_issue_ck"
  CHECK ("expiresAt" IS NULL OR "expiresAt" >= "issuedAt");

COMMIT;
