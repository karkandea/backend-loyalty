-- Read-only integrity audit for the legacy Loyalty PostgreSQL database.
-- Safe to run against the Supabase source or a restored VPS copy.
-- This script intentionally does not mutate data.

\echo '=== Loyalty legacy data integrity audit ==='

\echo '\n[1] MemberCard uniqueness / lifecycle'
SELECT
  "businessId",
  "memberId",
  COUNT(*) FILTER (WHERE "isActive") AS active_cards,
  COUNT(*) FILTER (WHERE NOT "isActive") AS inactive_cards,
  COUNT(*) AS total_cards
FROM "MemberCard"
GROUP BY "businessId", "memberId"
HAVING COUNT(*) FILTER (WHERE "isActive") > 1
    OR COUNT(*) FILTER (WHERE NOT "isActive") > 1
ORDER BY total_cards DESC;

\echo '\n[2] Invalid MemberCard stamp counts'
SELECT mc.id, mc."businessId", mc."memberId", mc."cardId", mc."currentStamps", c."requiredStamps", mc."isActive"
FROM "MemberCard" mc
JOIN "Card" c ON c.id = mc."cardId"
WHERE mc."currentStamps" < 0
   OR c."requiredStamps" <= 0
   OR (mc."isActive" AND mc."currentStamps" > c."requiredStamps")
ORDER BY mc."businessId", mc."memberId";

\echo '\n[3] Cross-tenant MemberCard links'
SELECT mc.id, mc."businessId" AS row_business, m."businessId" AS member_business, c."businessId" AS card_business
FROM "MemberCard" mc
JOIN "Member" m ON m.id = mc."memberId"
JOIN "Card" c ON c.id = mc."cardId"
WHERE mc."businessId" <> m."businessId"
   OR mc."businessId" <> c."businessId";

\echo '\n[4] Cross-tenant MemberReward links'
SELECT mr.id, mr."businessId" AS row_business,
       m."businessId" AS member_business,
       r."businessId" AS reward_business,
       mc."businessId" AS member_card_business
FROM "MemberReward" mr
JOIN "Member" m ON m.id = mr."memberId"
JOIN "Reward" r ON r.id = mr."rewardId"
LEFT JOIN "MemberCard" mc ON mc.id = mr."memberCardId"
WHERE mr."businessId" <> m."businessId"
   OR mr."businessId" <> r."businessId"
   OR (mc.id IS NOT NULL AND mr."businessId" <> mc."businessId");

\echo '\n[5] Cross-tenant RewardToken links'
SELECT rt.id, rt."businessId" AS row_business,
       mr."businessId" AS reward_business,
       m."businessId" AS member_business,
       mc."businessId" AS member_card_business
FROM "RewardToken" rt
LEFT JOIN "MemberReward" mr ON mr.id = rt."memberRewardId"
LEFT JOIN "Member" m ON m.id = rt."memberId"
LEFT JOIN "MemberCard" mc ON mc.id = rt."memberCardId"
WHERE (mr.id IS NOT NULL AND rt."businessId" <> mr."businessId")
   OR (m.id IS NOT NULL AND rt."businessId" <> m."businessId")
   OR (mc.id IS NOT NULL AND rt."businessId" <> mc."businessId");

\echo '\n[6] Cross-tenant Transaction links'
SELECT t.id, t."businessId" AS row_business,
       m."businessId" AS member_business,
       mc."businessId" AS member_card_business,
       c."businessId" AS card_business,
       o."businessId" AS outlet_business,
       a."businessId" AS staff_business,
       r."businessId" AS reward_business
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

\echo '\n[7] Cross-tenant CardMilestone links'
SELECT cm.id, cm."businessId" AS row_business,
       c."businessId" AS card_business,
       r."businessId" AS reward_business
FROM "CardMilestone" cm
JOIN "Card" c ON c.id = cm."cardId"
LEFT JOIN "Reward" r ON r.id = cm."rewardId"
WHERE cm."businessId" <> c."businessId"
   OR (r.id IS NOT NULL AND cm."businessId" <> r."businessId");

\echo '\n[8] MemberReward lifecycle inconsistencies'
SELECT id, "businessId", status, "issuedAt", "expiresAt", "redeemedAt"
FROM "MemberReward"
WHERE (status = 'REDEEMED' AND "redeemedAt" IS NULL)
   OR (status <> 'REDEEMED' AND "redeemedAt" IS NOT NULL)
   OR ("expiresAt" IS NOT NULL AND "expiresAt" < "issuedAt")
ORDER BY "createdAt";

\echo '\n[9] RewardToken lifecycle inconsistencies'
SELECT id, "businessId", status, "expiresAt", "usedAt", "usedByStaffId", "usedAtOutletId"
FROM "RewardToken"
WHERE (status = 'USED' AND "usedAt" IS NULL)
   OR (status <> 'USED' AND "usedAt" IS NOT NULL)
   OR ("usedAt" IS NOT NULL AND "usedByStaffId" IS NULL)
ORDER BY "createdAt";

\echo '\n[10] Expired-but-active reward tokens'
SELECT id, "businessId", status, "expiresAt", "usedAt"
FROM "RewardToken"
WHERE status = 'ACTIVE'
  AND "expiresAt" IS NOT NULL
  AND "expiresAt" <= now()
ORDER BY "expiresAt";

\echo '\n[11] Expired/revoked member sessions still present'
SELECT
  COUNT(*) FILTER (WHERE "expiresAt" <= now() AND "revokedAt" IS NULL) AS expired_not_revoked,
  COUNT(*) FILTER (WHERE "revokedAt" IS NOT NULL) AS revoked,
  COUNT(*) AS total
FROM "MemberSession";

\echo '\n[12] Business/Admin auth migration readiness'
SELECT
  'BusinessUser' AS source,
  COUNT(*) AS total,
  COUNT(*) FILTER (WHERE "passwordHash" = 'managed-by-supabase-auth') AS placeholder_hash,
  COUNT(*) FILTER (WHERE "passwordHash" LIKE '$2%') AS bcrypt_hash,
  COUNT(*) FILTER (WHERE NOT "isActive") AS inactive
FROM "BusinessUser"
UNION ALL
SELECT
  'AdminUser',
  COUNT(*),
  COUNT(*) FILTER (WHERE "passwordHash" = 'managed-by-supabase-auth'),
  COUNT(*) FILTER (WHERE "passwordHash" LIKE '$2%'),
  COUNT(*) FILTER (WHERE NOT "isActive")
FROM "AdminUser";

\echo '\n[12b] Password bridge mapping coverage (target copy only)'
DO $$
DECLARE
  missing_business bigint;
  missing_admin bigint;
BEGIN
  IF to_regclass('public."LegacyAuthUserPassword"') IS NULL THEN
    RAISE NOTICE 'LegacyAuthUserPassword is not present; bridge coverage check skipped.';
    RETURN;
  END IF;

  EXECUTE $sql$
    SELECT COUNT(*)
    FROM "BusinessUser" b
    LEFT JOIN "LegacyAuthUserPassword" l
      ON l."authUserId" = COALESCE(NULLIF(b."authUserId", ''), b.id)
    WHERE b."passwordHash" = 'managed-by-supabase-auth'
      AND l."authUserId" IS NULL
  $sql$ INTO missing_business;

  EXECUTE $sql$
    SELECT COUNT(*)
    FROM "AdminUser" a
    LEFT JOIN "LegacyAuthUserPassword" l ON l."authUserId" = a.id
    WHERE a."passwordHash" = 'managed-by-supabase-auth'
      AND l."authUserId" IS NULL
  $sql$ INTO missing_admin;

  RAISE NOTICE 'placeholder BusinessUser rows without bridge hash: %', missing_business;
  RAISE NOTICE 'placeholder AdminUser rows without bridge hash (id mapping): %', missing_admin;
END $$;

\echo '\n[13] Case-insensitive duplicate emails within the same business'
SELECT 'BusinessUser' AS source, "businessId", lower(trim(email)) AS normalized_email, COUNT(*) AS rows
FROM "BusinessUser"
GROUP BY "businessId", lower(trim(email))
HAVING COUNT(*) > 1
UNION ALL
SELECT 'AdminUser', "businessId", lower(trim(email)), COUNT(*)
FROM "AdminUser"
GROUP BY "businessId", lower(trim(email))
HAVING COUNT(*) > 1
ORDER BY source, rows DESC;

\echo '\n[14] Distinct free-form values that should eventually become constrained values'
SELECT 'BusinessUser.role' AS field, role AS value, COUNT(*) AS rows FROM "BusinessUser" GROUP BY role
UNION ALL
SELECT 'AdminUser.role', role, COUNT(*) FROM "AdminUser" GROUP BY role
UNION ALL
SELECT 'Card.status', status, COUNT(*) FROM "Card" GROUP BY status
UNION ALL
SELECT 'MemberReward.status', status, COUNT(*) FROM "MemberReward" GROUP BY status
UNION ALL
SELECT 'MemberReward.source_type', source_type, COUNT(*) FROM "MemberReward" GROUP BY source_type
UNION ALL
SELECT 'RewardToken.status', status, COUNT(*) FROM "RewardToken" GROUP BY status
UNION ALL
SELECT 'RewardToken.scope', scope, COUNT(*) FROM "RewardToken" GROUP BY scope
UNION ALL
SELECT 'Transaction.type', type, COUNT(*) FROM "Transaction" GROUP BY type
ORDER BY field, value;

\echo '\n=== End audit; no data was changed ==='
