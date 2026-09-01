-- Guarded, target-only repair for confirmed legacy Loyalty anomalies.
--
-- IMPORTANT:
--   1. Run ONLY on the standalone VPS copy, never on Supabase source.
--   2. Take a pg_dump backup immediately before running this script.
--   3. This script is intentionally conservative and idempotent.
--
-- Repairs performed:
--   - Move the known POS demo member back to its correct POS Demo Biz tenant,
--     including any business-scoped child rows for that member.
--   - Normalize three known overflowed active MemberCard progress values using
--     modulo(requiredStamps). This repairs current state only; transaction history
--     is intentionally preserved and no fake historical cycles are invented.
--   - Mark only UNUSED, expired ACTIVE RewardTokens as EXPIRED.
--     Consumed legacy tokens may legitimately remain ACTIVE with usedAt populated.
--   - Normalize BusinessUser/AdminUser role casing without changing semantics.
--
-- Deliberately NOT repaired here:
--   - BusinessUser placeholder hashes missing from the legacy auth bridge.
--   - Historical REVOKED RewardToken rows with usedAt but no redemption attribution.
--   - Expired MemberSession rows; expiration alone is sufficient invalidation.

BEGIN;

DO $$
DECLARE
  fixture_count bigint;
  duplicate_barcode_count bigint;
BEGIN
  -- Hard stop if this looks like the Supabase source rather than the standalone copy.
  IF to_regclass('auth.users') IS NOT NULL THEN
    RAISE EXCEPTION 'REFUSING REPAIR: auth.users exists; this script must not run on the Supabase source';
  END IF;

  IF to_regclass('public."LegacyAuthUserPassword"') IS NULL THEN
    RAISE EXCEPTION 'REFUSING REPAIR: LegacyAuthUserPassword is missing; target migration marker not found';
  END IF;

  -- Exact identity checks for the known cross-tenant POS demo fixture.
  SELECT COUNT(*) INTO fixture_count
  FROM "Business"
  WHERE id = '22222222-3333-4444-5555-666666666666'
    AND name = 'POS Demo Biz'
    AND slug = 'pos-demo';

  IF fixture_count <> 1 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: expected POS Demo Biz fixture was not found exactly once';
  END IF;

  SELECT COUNT(*) INTO fixture_count
  FROM "Member"
  WHERE id = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
    AND "businessId" IN (
      '0147f1b3-d0dd-4810-a3aa-70b879447ebe',
      '22222222-3333-4444-5555-666666666666'
    )
    AND name = 'POS Member'
    AND email = 'member+pos@example.com'
    AND "memberBarcode" = 'MB-POS-DEMO'
    AND "PreferredOutletId" = '33333333-4444-5555-6666-777777777777';

  IF fixture_count <> 1 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: expected POS Member fixture was not found exactly once';
  END IF;

  SELECT COUNT(*) INTO fixture_count
  FROM "Outlet"
  WHERE id = '33333333-4444-5555-6666-777777777777'
    AND "businessId" = '22222222-3333-4444-5555-666666666666'
    AND name = 'Main Outlet';

  IF fixture_count <> 1 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: expected POS Demo outlet was not found exactly once';
  END IF;

  SELECT COUNT(*) INTO duplicate_barcode_count
  FROM "Member"
  WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
    AND "memberBarcode" = 'MB-POS-DEMO'
    AND id <> '77777777-8888-9999-aaaa-bbbbbbbbbbbb';

  IF duplicate_barcode_count > 0 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: moving POS Member would collide with an existing POS Demo barcode';
  END IF;

  -- Exact guards for the three overflow rows. Allow either original or already-repaired
  -- value so rerunning this script is safe.
  SELECT COUNT(*) INTO fixture_count
  FROM "MemberCard"
  WHERE id = '88888888-9999-aaaa-bbbb-cccccccccccc'
    AND "businessId" = '22222222-3333-4444-5555-666666666666'
    AND "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
    AND "cardId" = '44444444-5555-6666-7777-888888888888'
    AND "isActive" = true
    AND "currentStamps" IN (6, 1);

  IF fixture_count <> 1 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: POS Demo overflow MemberCard no longer matches expected state';
  END IF;

  SELECT COUNT(*) INTO fixture_count
  FROM "MemberCard"
  WHERE id = 'a166ecb5-751a-4624-ae8c-8f1974faab8f'
    AND "businessId" = 'e27a939a-6001-41e7-b5e5-636e70aad849'
    AND "memberId" = '0381b6ee-315d-4000-b764-d625cf2021f6'
    AND "cardId" = '4f8fc846-540c-434c-bada-7d62b7858b36'
    AND "isActive" = true
    AND "currentStamps" IN (10, 0);

  IF fixture_count <> 1 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: Lungo 10/5 MemberCard no longer matches expected state';
  END IF;

  SELECT COUNT(*) INTO fixture_count
  FROM "MemberCard"
  WHERE id = 'c8135cd1-8c85-441b-abe4-ac044564d26b'
    AND "businessId" = 'e27a939a-6001-41e7-b5e5-636e70aad849'
    AND "memberId" = '5915bc69-7914-4307-abe6-ca8018b02ffc'
    AND "cardId" = '4f8fc846-540c-434c-bada-7d62b7858b36'
    AND "isActive" = true
    AND "currentStamps" IN (6, 1);

  IF fixture_count <> 1 THEN
    RAISE EXCEPTION 'REFUSING REPAIR: Lungo 6/5 MemberCard no longer matches expected state';
  END IF;
END $$;

\echo 'Repairing known POS Demo tenant mismatch...'

-- The member itself was the only known wrong tenant anchor. Keep all related historical
-- POS rows and move any business-scoped member-auth/support rows with it if present.
UPDATE "Member"
SET "businessId" = '22222222-3333-4444-5555-666666666666',
    "updatedAt" = now()
WHERE id = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "MemberIdentity"
SET "businessId" = '22222222-3333-4444-5555-666666666666',
    "updatedAt" = now()
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "MemberSession"
SET "businessId" = '22222222-3333-4444-5555-666666666666',
    "updatedAt" = now()
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "MemberEmailVerification"
SET "businessId" = '22222222-3333-4444-5555-666666666666'
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "MemberPasswordReset"
SET "businessId" = '22222222-3333-4444-5555-666666666666'
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "OtpSession"
SET "businessId" = '22222222-3333-4444-5555-666666666666',
    "updatedAt" = now()
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "PhoneChangeAudit"
SET "businessId" = '22222222-3333-4444-5555-666666666666'
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "BroadcastLog"
SET "businessId" = '22222222-3333-4444-5555-666666666666'
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

UPDATE "RewardToken"
SET "businessId" = '22222222-3333-4444-5555-666666666666',
    "updatedAt" = now()
WHERE "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
  AND "businessId" = '0147f1b3-d0dd-4810-a3aa-70b879447ebe';

\echo 'Normalizing known overflow MemberCard current progress...'

-- These are state repairs, not fabricated history. All Transaction rows are preserved.
UPDATE "MemberCard"
SET "currentStamps" = 1,
    "updatedAt" = now()
WHERE id = '88888888-9999-aaaa-bbbb-cccccccccccc'
  AND "currentStamps" = 6;

UPDATE "MemberCard"
SET "currentStamps" = 0,
    "updatedAt" = now()
WHERE id = 'a166ecb5-751a-4624-ae8c-8f1974faab8f'
  AND "currentStamps" = 10;

UPDATE "MemberCard"
SET "currentStamps" = 1,
    "updatedAt" = now()
WHERE id = 'c8135cd1-8c85-441b-abe4-ac044564d26b'
  AND "currentStamps" = 6;

\echo 'Expiring only stale, unused RewardTokens...'
UPDATE "RewardToken"
SET status = 'EXPIRED',
    "updatedAt" = now()
WHERE status = 'ACTIVE'
  AND "usedAt" IS NULL
  AND "expiresAt" IS NOT NULL
  AND "expiresAt" <= now();

\echo 'Normalizing role casing...'
UPDATE "BusinessUser"
SET role = upper(trim(role)),
    "updatedAt" = now()
WHERE role <> upper(trim(role));

UPDATE "AdminUser"
SET role = upper(trim(role)),
    "updatedAt" = now()
WHERE role <> upper(trim(role));

DO $$
DECLARE
  invalid_overflow bigint;
  cross_tenant_member_card bigint;
  cross_tenant_member_reward bigint;
  cross_tenant_transaction bigint;
BEGIN
  SELECT COUNT(*) INTO invalid_overflow
  FROM "MemberCard" mc
  JOIN "Card" c ON c.id = mc."cardId"
  WHERE mc."isActive"
    AND mc."currentStamps" > c."requiredStamps";

  IF invalid_overflow > 0 THEN
    RAISE EXCEPTION 'POST-REPAIR CHECK FAILED: % active MemberCard row(s) still exceed requiredStamps', invalid_overflow;
  END IF;

  SELECT COUNT(*) INTO cross_tenant_member_card
  FROM "MemberCard" mc
  JOIN "Member" m ON m.id = mc."memberId"
  JOIN "Card" c ON c.id = mc."cardId"
  WHERE mc."businessId" <> m."businessId"
     OR mc."businessId" <> c."businessId";

  IF cross_tenant_member_card > 0 THEN
    RAISE EXCEPTION 'POST-REPAIR CHECK FAILED: % cross-tenant MemberCard row(s) remain', cross_tenant_member_card;
  END IF;

  SELECT COUNT(*) INTO cross_tenant_member_reward
  FROM "MemberReward" mr
  JOIN "Member" m ON m.id = mr."memberId"
  JOIN "Reward" r ON r.id = mr."rewardId"
  LEFT JOIN "MemberCard" mc ON mc.id = mr."memberCardId"
  WHERE mr."businessId" <> m."businessId"
     OR mr."businessId" <> r."businessId"
     OR (mc.id IS NOT NULL AND mr."businessId" <> mc."businessId");

  IF cross_tenant_member_reward > 0 THEN
    RAISE EXCEPTION 'POST-REPAIR CHECK FAILED: % cross-tenant MemberReward row(s) remain', cross_tenant_member_reward;
  END IF;

  SELECT COUNT(*) INTO cross_tenant_transaction
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

  IF cross_tenant_transaction > 0 THEN
    RAISE EXCEPTION 'POST-REPAIR CHECK FAILED: % cross-tenant Transaction row(s) remain', cross_tenant_transaction;
  END IF;
END $$;

COMMIT;

\echo 'PASS: confirmed target-only data repairs committed.'
