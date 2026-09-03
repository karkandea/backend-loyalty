-- Target-only tenant-boundary hardening for the standalone Loyalty PostgreSQL database.
-- Run only after migration repair + core runtime smoke have passed.
-- This script does not repair data. Any dangling or cross-tenant relation causes
-- VALIDATE CONSTRAINT to fail and the whole transaction to roll back.

BEGIN;

-- Composite parent keys. `id` is already globally unique in the legacy schema;
-- these indexes exist so child rows can additionally bind businessId to the same
-- parent row and PostgreSQL can enforce tenant ownership at the database layer.
CREATE UNIQUE INDEX IF NOT EXISTS "Member_business_id_id_tenant_key"
  ON "Member" ("businessId", id);
CREATE UNIQUE INDEX IF NOT EXISTS "Card_business_id_id_tenant_key"
  ON "Card" ("businessId", id);
CREATE UNIQUE INDEX IF NOT EXISTS "Reward_business_id_id_tenant_key"
  ON "Reward" ("businessId", id);
CREATE UNIQUE INDEX IF NOT EXISTS "Outlet_business_id_id_tenant_key"
  ON "Outlet" ("businessId", id);
CREATE UNIQUE INDEX IF NOT EXISTS "AdminUser_business_id_id_tenant_key"
  ON "AdminUser" ("businessId", id);
CREATE UNIQUE INDEX IF NOT EXISTS "MemberCard_business_id_id_tenant_key"
  ON "MemberCard" ("businessId", id);
CREATE UNIQUE INDEX IF NOT EXISTS "MemberReward_business_id_id_tenant_key"
  ON "MemberReward" ("businessId", id);

-- Admin outlet context.
ALTER TABLE "AdminUser" DROP CONSTRAINT IF EXISTS "AdminUser_outlet_tenant_fk";
ALTER TABLE "AdminUser"
  ADD CONSTRAINT "AdminUser_outlet_tenant_fk"
  FOREIGN KEY ("businessId", "outletId")
  REFERENCES "Outlet" ("businessId", id)
  NOT VALID;

-- Member session / card ownership.
ALTER TABLE "MemberSession" DROP CONSTRAINT IF EXISTS "MemberSession_member_tenant_fk";
ALTER TABLE "MemberSession"
  ADD CONSTRAINT "MemberSession_member_tenant_fk"
  FOREIGN KEY ("businessId", "memberId")
  REFERENCES "Member" ("businessId", id)
  NOT VALID;

ALTER TABLE "MemberCard" DROP CONSTRAINT IF EXISTS "MemberCard_member_tenant_fk";
ALTER TABLE "MemberCard"
  ADD CONSTRAINT "MemberCard_member_tenant_fk"
  FOREIGN KEY ("businessId", "memberId")
  REFERENCES "Member" ("businessId", id)
  NOT VALID;

ALTER TABLE "MemberCard" DROP CONSTRAINT IF EXISTS "MemberCard_card_tenant_fk";
ALTER TABLE "MemberCard"
  ADD CONSTRAINT "MemberCard_card_tenant_fk"
  FOREIGN KEY ("businessId", "cardId")
  REFERENCES "Card" ("businessId", id)
  NOT VALID;

-- Milestone ownership.
ALTER TABLE "CardMilestone" DROP CONSTRAINT IF EXISTS "CardMilestone_card_tenant_fk";
ALTER TABLE "CardMilestone"
  ADD CONSTRAINT "CardMilestone_card_tenant_fk"
  FOREIGN KEY ("businessId", "cardId")
  REFERENCES "Card" ("businessId", id)
  NOT VALID;

ALTER TABLE "CardMilestone" DROP CONSTRAINT IF EXISTS "CardMilestone_reward_tenant_fk";
ALTER TABLE "CardMilestone"
  ADD CONSTRAINT "CardMilestone_reward_tenant_fk"
  FOREIGN KEY ("businessId", "rewardId")
  REFERENCES "Reward" ("businessId", id)
  NOT VALID;

-- Member reward ownership.
ALTER TABLE "MemberReward" DROP CONSTRAINT IF EXISTS "MemberReward_member_tenant_fk";
ALTER TABLE "MemberReward"
  ADD CONSTRAINT "MemberReward_member_tenant_fk"
  FOREIGN KEY ("businessId", "memberId")
  REFERENCES "Member" ("businessId", id)
  NOT VALID;

ALTER TABLE "MemberReward" DROP CONSTRAINT IF EXISTS "MemberReward_reward_tenant_fk";
ALTER TABLE "MemberReward"
  ADD CONSTRAINT "MemberReward_reward_tenant_fk"
  FOREIGN KEY ("businessId", "rewardId")
  REFERENCES "Reward" ("businessId", id)
  NOT VALID;

ALTER TABLE "MemberReward" DROP CONSTRAINT IF EXISTS "MemberReward_member_card_tenant_fk";
ALTER TABLE "MemberReward"
  ADD CONSTRAINT "MemberReward_member_card_tenant_fk"
  FOREIGN KEY ("businessId", "memberCardId")
  REFERENCES "MemberCard" ("businessId", id)
  NOT VALID;

-- Reward token ownership and redemption attribution.
ALTER TABLE "RewardToken" DROP CONSTRAINT IF EXISTS "RewardToken_member_reward_tenant_fk";
ALTER TABLE "RewardToken"
  ADD CONSTRAINT "RewardToken_member_reward_tenant_fk"
  FOREIGN KEY ("businessId", "memberRewardId")
  REFERENCES "MemberReward" ("businessId", id)
  NOT VALID;

ALTER TABLE "RewardToken" DROP CONSTRAINT IF EXISTS "RewardToken_member_tenant_fk";
ALTER TABLE "RewardToken"
  ADD CONSTRAINT "RewardToken_member_tenant_fk"
  FOREIGN KEY ("businessId", "memberId")
  REFERENCES "Member" ("businessId", id)
  NOT VALID;

ALTER TABLE "RewardToken" DROP CONSTRAINT IF EXISTS "RewardToken_member_card_tenant_fk";
ALTER TABLE "RewardToken"
  ADD CONSTRAINT "RewardToken_member_card_tenant_fk"
  FOREIGN KEY ("businessId", "memberCardId")
  REFERENCES "MemberCard" ("businessId", id)
  NOT VALID;

ALTER TABLE "RewardToken" DROP CONSTRAINT IF EXISTS "RewardToken_issuing_outlet_tenant_fk";
ALTER TABLE "RewardToken"
  ADD CONSTRAINT "RewardToken_issuing_outlet_tenant_fk"
  FOREIGN KEY ("businessId", "outletId")
  REFERENCES "Outlet" ("businessId", id)
  NOT VALID;

ALTER TABLE "RewardToken" DROP CONSTRAINT IF EXISTS "RewardToken_redeem_outlet_tenant_fk";
ALTER TABLE "RewardToken"
  ADD CONSTRAINT "RewardToken_redeem_outlet_tenant_fk"
  FOREIGN KEY ("businessId", "usedAtOutletId")
  REFERENCES "Outlet" ("businessId", id)
  NOT VALID;

ALTER TABLE "RewardToken" DROP CONSTRAINT IF EXISTS "RewardToken_staff_tenant_fk";
ALTER TABLE "RewardToken"
  ADD CONSTRAINT "RewardToken_staff_tenant_fk"
  FOREIGN KEY ("businessId", "usedByStaffId")
  REFERENCES "AdminUser" ("businessId", id)
  NOT VALID;

-- Transaction ownership and attribution.
ALTER TABLE "Transaction" DROP CONSTRAINT IF EXISTS "Transaction_member_tenant_fk";
ALTER TABLE "Transaction"
  ADD CONSTRAINT "Transaction_member_tenant_fk"
  FOREIGN KEY ("businessId", "memberId")
  REFERENCES "Member" ("businessId", id)
  NOT VALID;

ALTER TABLE "Transaction" DROP CONSTRAINT IF EXISTS "Transaction_member_card_tenant_fk";
ALTER TABLE "Transaction"
  ADD CONSTRAINT "Transaction_member_card_tenant_fk"
  FOREIGN KEY ("businessId", "memberCardId")
  REFERENCES "MemberCard" ("businessId", id)
  NOT VALID;

ALTER TABLE "Transaction" DROP CONSTRAINT IF EXISTS "Transaction_card_tenant_fk";
ALTER TABLE "Transaction"
  ADD CONSTRAINT "Transaction_card_tenant_fk"
  FOREIGN KEY ("businessId", "cardId")
  REFERENCES "Card" ("businessId", id)
  NOT VALID;

ALTER TABLE "Transaction" DROP CONSTRAINT IF EXISTS "Transaction_reward_tenant_fk";
ALTER TABLE "Transaction"
  ADD CONSTRAINT "Transaction_reward_tenant_fk"
  FOREIGN KEY ("businessId", "rewardId")
  REFERENCES "Reward" ("businessId", id)
  NOT VALID;

ALTER TABLE "Transaction" DROP CONSTRAINT IF EXISTS "Transaction_outlet_tenant_fk";
ALTER TABLE "Transaction"
  ADD CONSTRAINT "Transaction_outlet_tenant_fk"
  FOREIGN KEY ("businessId", "outletId")
  REFERENCES "Outlet" ("businessId", id)
  NOT VALID;

ALTER TABLE "Transaction" DROP CONSTRAINT IF EXISTS "Transaction_staff_tenant_fk";
ALTER TABLE "Transaction"
  ADD CONSTRAINT "Transaction_staff_tenant_fk"
  FOREIGN KEY ("businessId", "staffId")
  REFERENCES "AdminUser" ("businessId", id)
  NOT VALID;

-- Validate every newly-added relation against all historical rows. Because this
-- runs inside one transaction, any mismatch leaves the target schema unchanged.
ALTER TABLE "AdminUser" VALIDATE CONSTRAINT "AdminUser_outlet_tenant_fk";
ALTER TABLE "MemberSession" VALIDATE CONSTRAINT "MemberSession_member_tenant_fk";
ALTER TABLE "MemberCard" VALIDATE CONSTRAINT "MemberCard_member_tenant_fk";
ALTER TABLE "MemberCard" VALIDATE CONSTRAINT "MemberCard_card_tenant_fk";
ALTER TABLE "CardMilestone" VALIDATE CONSTRAINT "CardMilestone_card_tenant_fk";
ALTER TABLE "CardMilestone" VALIDATE CONSTRAINT "CardMilestone_reward_tenant_fk";
ALTER TABLE "MemberReward" VALIDATE CONSTRAINT "MemberReward_member_tenant_fk";
ALTER TABLE "MemberReward" VALIDATE CONSTRAINT "MemberReward_reward_tenant_fk";
ALTER TABLE "MemberReward" VALIDATE CONSTRAINT "MemberReward_member_card_tenant_fk";
ALTER TABLE "RewardToken" VALIDATE CONSTRAINT "RewardToken_member_reward_tenant_fk";
ALTER TABLE "RewardToken" VALIDATE CONSTRAINT "RewardToken_member_tenant_fk";
ALTER TABLE "RewardToken" VALIDATE CONSTRAINT "RewardToken_member_card_tenant_fk";
ALTER TABLE "RewardToken" VALIDATE CONSTRAINT "RewardToken_issuing_outlet_tenant_fk";
ALTER TABLE "RewardToken" VALIDATE CONSTRAINT "RewardToken_redeem_outlet_tenant_fk";
ALTER TABLE "RewardToken" VALIDATE CONSTRAINT "RewardToken_staff_tenant_fk";
ALTER TABLE "Transaction" VALIDATE CONSTRAINT "Transaction_member_tenant_fk";
ALTER TABLE "Transaction" VALIDATE CONSTRAINT "Transaction_member_card_tenant_fk";
ALTER TABLE "Transaction" VALIDATE CONSTRAINT "Transaction_card_tenant_fk";
ALTER TABLE "Transaction" VALIDATE CONSTRAINT "Transaction_reward_tenant_fk";
ALTER TABLE "Transaction" VALIDATE CONSTRAINT "Transaction_outlet_tenant_fk";
ALTER TABLE "Transaction" VALIDATE CONSTRAINT "Transaction_staff_tenant_fk";

COMMIT;
