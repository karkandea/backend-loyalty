-- Read-only inspection for legacy Loyalty repair candidates.
-- Run only against the restored VPS copy. This script does not mutate data.

\echo '=== Loyalty repair candidate inspection (READ ONLY) ==='

\echo '\n[1] Placeholder BusinessUser rows without a legacy password bridge match'
SELECT
    b.id,
    b."businessId",
    biz.name AS business_name,
    biz.slug AS business_slug,
    b."authUserId"::text AS auth_user_id,
    b.email,
    b."fullName",
    b.role,
    b."isActive",
    b."createdAt",
    b."updatedAt"
FROM "BusinessUser" b
JOIN "Business" biz ON biz.id = b."businessId"
LEFT JOIN "LegacyAuthUserPassword" l
    ON l."authUserId" = COALESCE(b."authUserId"::text, b.id)
WHERE b."passwordHash" = 'managed-by-supabase-auth'
  AND l."authUserId" IS NULL
ORDER BY b."businessId", b.email;

\echo '\n[2] Patterned IDs involved in the cross-tenant anomaly'
SELECT id, name, slug, tier, "isActive", "createdAt", "updatedAt"
FROM "Business"
WHERE id IN (
    '22222222-3333-4444-5555-666666666666',
    '0147f1b3-d0dd-4810-a3aa-70b879447ebe'
)
ORDER BY id;

SELECT *
FROM "Member"
WHERE id = '77777777-8888-9999-aaaa-bbbbbbbbbbbb';

SELECT *
FROM "Card"
WHERE id = '44444444-5555-6666-7777-888888888888';

SELECT *
FROM "MemberCard"
WHERE id = '88888888-9999-aaaa-bbbb-cccccccccccc';

SELECT *
FROM "MemberReward"
WHERE id = 'ba63e13b-773f-468e-b9fb-fdf95414262e';

\echo '\n[2b] Related row counts for patterned business 2222...'
SELECT 'AdminUser' AS entity, COUNT(*) AS rows FROM "AdminUser" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'BusinessUser', COUNT(*) FROM "BusinessUser" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'Outlet', COUNT(*) FROM "Outlet" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'Card', COUNT(*) FROM "Card" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'Member', COUNT(*) FROM "Member" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'MemberCard', COUNT(*) FROM "MemberCard" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'MemberReward', COUNT(*) FROM "MemberReward" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'RewardToken', COUNT(*) FROM "RewardToken" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
UNION ALL SELECT 'Transaction', COUNT(*) FROM "Transaction" WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
ORDER BY entity;

\echo '\n[2c] Related identities/outlets/admins for patterned business 2222...'
SELECT id, email, "fullName", role, "isActive", "authUserId"::text AS auth_user_id, "createdAt"
FROM "BusinessUser"
WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
ORDER BY "createdAt";

SELECT id, email, "fullName", role, "isActive", "outletId", "createdAt"
FROM "AdminUser"
WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
ORDER BY "createdAt";

SELECT id, name, address, city, "isActive", "createdAt"
FROM "Outlet"
WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
ORDER BY "createdAt";

\echo '\n[2d] Transactions touching the patterned member/card/business'
SELECT *
FROM "Transaction"
WHERE "businessId" = '22222222-3333-4444-5555-666666666666'
   OR "memberId" = '77777777-8888-9999-aaaa-bbbbbbbbbbbb'
   OR "memberCardId" = '88888888-9999-aaaa-bbbb-cccccccccccc'
ORDER BY "createdAt";

\echo '\n[3] Overflow MemberCards that look like real legacy rows'
SELECT
    mc.id AS member_card_id,
    mc."businessId",
    biz.name AS business_name,
    mc."memberId",
    m.name AS member_name,
    m.email AS member_email,
    m."memberBarcode",
    m."totalStamps",
    mc."cardId",
    c.name AS card_name,
    c.level AS card_level,
    c."requiredStamps",
    mc."currentStamps",
    mc."isActive",
    mc."startedAt",
    mc."completedAt",
    mc."createdAt",
    mc."updatedAt"
FROM "MemberCard" mc
JOIN "Business" biz ON biz.id = mc."businessId"
JOIN "Member" m ON m.id = mc."memberId"
JOIN "Card" c ON c.id = mc."cardId"
WHERE mc.id IN (
    'a166ecb5-751a-4624-ae8c-8f1974faab8f',
    'c8135cd1-8c85-441b-abe4-ac044564d26b'
)
ORDER BY mc.id;

\echo '\n[3b] Stamp/redeem transaction history for overflow members'
SELECT *
FROM "Transaction"
WHERE "memberId" IN (
    '0381b6ee-315d-4000-b764-d625cf2021f6',
    '5915bc69-7914-4307-abe6-ca8018b02ffc'
)
ORDER BY "memberId", "createdAt";

\echo '\n[3c] Rewards/tokens for overflow members'
SELECT *
FROM "MemberReward"
WHERE "memberId" IN (
    '0381b6ee-315d-4000-b764-d625cf2021f6',
    '5915bc69-7914-4307-abe6-ca8018b02ffc'
)
ORDER BY "memberId", "issuedAt";

SELECT *
FROM "RewardToken"
WHERE "memberId" IN (
    '0381b6ee-315d-4000-b764-d625cf2021f6',
    '5915bc69-7914-4307-abe6-ca8018b02ffc'
)
ORDER BY "memberId", "createdAt";

\echo '\n[4] RewardToken lifecycle candidates with related reward state'
SELECT
    rt.id,
    rt."businessId",
    rt."memberRewardId",
    mr.status AS member_reward_status,
    mr."redeemedAt" AS member_reward_redeemed_at,
    rt.status AS token_status,
    rt."expiresAt",
    rt."usedAt",
    rt."usedByStaffId",
    rt."usedAtOutletId",
    rt."createdAt",
    rt."updatedAt"
FROM "RewardToken" rt
LEFT JOIN "MemberReward" mr ON mr.id = rt."memberRewardId"
WHERE (rt.status = 'USED' AND rt."usedAt" IS NULL)
   OR (rt.status <> 'USED' AND rt."usedAt" IS NOT NULL)
   OR (rt."usedAt" IS NOT NULL AND rt."usedByStaffId" IS NULL)
   OR (rt.status = 'ACTIVE' AND rt."expiresAt" IS NOT NULL AND rt."expiresAt" <= now())
ORDER BY rt."createdAt";

\echo '\n[5] Lowercase/mixed-case role rows before normalization decision'
SELECT 'BusinessUser' AS source, id, "businessId", email, role, "isActive", "createdAt"
FROM "BusinessUser"
WHERE role <> upper(role)
UNION ALL
SELECT 'AdminUser', id, "businessId", email, role, "isActive", "createdAt"
FROM "AdminUser"
WHERE role <> upper(role)
ORDER BY source, "businessId", email;

\echo '\n=== End repair candidate inspection; no data was changed ==='
