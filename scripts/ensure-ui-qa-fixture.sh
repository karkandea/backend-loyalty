#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="${ENV_FILE:-.env.vps}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"
DB_USER="${DB_USER:-loyalty_app}"
DB_NAME="${DB_NAME:-loyalty}"

[[ -f "$ENV_FILE" ]] || { echo "ERROR: $ENV_FILE not found" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
: "${LOYALTY_DB_PASSWORD:?LOYALTY_DB_PASSWORD is required}"

BUSINESS_ID='10000000-0000-0000-0000-000000000001'
OUTLET_ID='10000000-0000-0000-0000-000000000002'
STAFF_ID='10000000-0000-0000-0000-000000000003'
MEMBER_ID='10000000-0000-0000-0000-000000000004'
CARD_ONE_ID='10000000-0000-0000-0000-000000000005'
MEMBER_CARD_ID='10000000-0000-0000-0000-000000000006'
REWARD_ID='10000000-0000-0000-0000-000000000007'
MILESTONE_ID='10000000-0000-0000-0000-000000000008'
CARD_TWO_ID='10000000-0000-0000-0000-000000000009'
MEMBER_IDENTITY_ID='10000000-0000-0000-0000-000000000010'
OWNER_ID='10000000-0000-0000-0000-000000000011'

STAFF_EMAIL='qa.pos@loyalty.local'
STAFF_PASSWORD='SmokeOnly-Refresh-2026!'
STAFF_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
OWNER_EMAIL='qa.owner@loyalty.local'
OWNER_PASSWORD='SmokeOnly-Refresh-2026!'
OWNER_BCRYPT="$STAFF_BCRYPT"
MEMBER_EMAIL='qa.member@loyalty.local'
MEMBER_PASSWORD='MemberSmoke-2026!'
MEMBER_SCRYPT='scrypt$00112233445566778899aabbccddeeff$3bb6b61f3a29b277aec7ba087a5ff733c56ae9925ab929dd27c4b32fbac01774c6d99d3b953eba2a58d30e6e674aa8d1cc89c828c96f38475ad92093839806ba'
MEMBER_BARCODE='QA-MEMBER-001'

psql_exec() {
  docker exec -e PGPASSWORD="$LOYALTY_DB_PASSWORD" -i "$DB_CONTAINER" \
    psql -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
}

echo "==> Resetting isolated UI QA fixture..."
psql_exec -q \
  -v business_id="$BUSINESS_ID" \
  -v outlet_id="$OUTLET_ID" \
  -v staff_id="$STAFF_ID" \
  -v staff_email="$STAFF_EMAIL" \
  -v staff_hash="$STAFF_BCRYPT" \
  -v owner_id="$OWNER_ID" \
  -v owner_email="$OWNER_EMAIL" \
  -v owner_hash="$OWNER_BCRYPT" \
  -v member_id="$MEMBER_ID" \
  -v member_identity_id="$MEMBER_IDENTITY_ID" \
  -v member_email="$MEMBER_EMAIL" \
  -v member_hash="$MEMBER_SCRYPT" \
  -v barcode="$MEMBER_BARCODE" \
  -v card1="$CARD_ONE_ID" \
  -v card2="$CARD_TWO_ID" \
  -v member_card="$MEMBER_CARD_ID" \
  -v reward_id="$REWARD_ID" \
  -v milestone_id="$MILESTONE_ID" <<'SQL'
BEGIN;

DELETE FROM "AuthRefreshSession" WHERE "userId" IN (:'staff_id', :'owner_id');
DELETE FROM "BusinessInvitation" WHERE "businessId" = :'business_id';
DELETE FROM "AuditLog" WHERE "businessId" = :'business_id'::uuid;
DELETE FROM "RewardToken" WHERE "businessId" = :'business_id';
DELETE FROM "Transaction" WHERE "businessId" = :'business_id';
DELETE FROM "MemberReward" WHERE "businessId" = :'business_id';
DELETE FROM "CardMilestone" WHERE "businessId" = :'business_id';
DELETE FROM "MemberCard" WHERE "businessId" = :'business_id';
DELETE FROM "MemberSession" WHERE "businessId" = :'business_id';
DELETE FROM "MemberIdentity" WHERE "businessId" = :'business_id';
DELETE FROM "Member" WHERE "businessId" = :'business_id';
DELETE FROM "Reward" WHERE "businessId" = :'business_id';
DELETE FROM "Card" WHERE "businessId" = :'business_id';
DELETE FROM "AdminUser" WHERE "businessId" = :'business_id';
DELETE FROM "BusinessUser" WHERE "businessId" = :'business_id';
DELETE FROM "Outlet" WHERE "businessId" = :'business_id';
DELETE FROM "Business" WHERE id = :'business_id';

INSERT INTO "Business" (id,name,slug,tier,"isActive","hasCompletedOnboarding","createdAt","updatedAt")
VALUES (:'business_id','Loyalty QA Store','qa-pos','FREE',true,false,now(),now());

INSERT INTO "Outlet" (id,"businessId",name,address,city,phone,"isActive","createdAt","updatedAt")
VALUES (:'outlet_id',:'business_id','QA Outlet','UI test fixture only','Jakarta',NULL,true,now(),now());

INSERT INTO "AdminUser" (id,"businessId","outletId",email,"passwordHash","fullName",role,"isActive","lastLoginAt","createdAt","updatedAt")
VALUES (:'staff_id',:'business_id',:'outlet_id',:'staff_email',:'staff_hash','QA POS Staff','STAFF',true,NULL,now(),now());

INSERT INTO "BusinessUser" (id,"businessId","authUserId",email,"passwordHash","fullName",role,permissions,"isActive","lastLoginAt","createdAt","updatedAt")
VALUES (:'owner_id',:'business_id',NULL,:'owner_email',:'owner_hash','QA Business Owner','OWNER','[]'::jsonb,true,NULL,now(),now());

INSERT INTO "Card" (id,"businessId",name,description,level,"requiredStamps",status,"termsAndConditions","isDeleted","howItWorksSteps","createdAt","updatedAt") VALUES
(:'card1',:'business_id','QA Starter Card','UI test card',1,5,'ACTIVE',NULL,false,'[]'::jsonb,now(),now()),
(:'card2',:'business_id','QA Level 2','UI test level-up card',2,3,'ACTIVE',NULL,false,'[]'::jsonb,now()+interval '1 millisecond',now());

INSERT INTO "Reward" (id,"businessId",name,description,"sourceType","defaultExpiryDays","createdAt","updatedAt")
VALUES (:'reward_id',:'business_id','QA Free Drink','UI test milestone reward','MILESTONE',30,now(),now());

INSERT INTO "CardMilestone" (id,"businessId","cardId","stampCount","sortOrder","rewardId","rewardType","rewardValue",title,description,"createdAt","updatedAt")
VALUES (:'milestone_id',:'business_id',:'card1',5,1,:'reward_id','REWARD',NULL,'QA Reward','Issued at 5 stamps',now(),now());

INSERT INTO "Member" (id,"businessId",name,email,phone,"memberBarcode","totalStamps","dateJoined","createdAt","updatedAt")
VALUES (:'member_id',:'business_id','QA Member',:'member_email',NULL,:'barcode',0,now(),now(),now());

INSERT INTO "MemberIdentity" (id,"businessId","memberId",email,"passwordHash","verifiedAt","lastLoginAt","failedLoginCount","lockedAt","createdAt","updatedAt")
VALUES (:'member_identity_id',:'business_id',:'member_id',:'member_email',:'member_hash',now(),NULL,0,NULL,now(),now());

INSERT INTO "MemberCard" (id,"businessId","memberId","cardId","currentStamps","isActive","startedAt","completedAt","createdAt","updatedAt")
VALUES (:'member_card',:'business_id',:'member_id',:'card1',0,true,now(),NULL,now(),now());

COMMIT;
SQL

echo "PASS: persistent UI QA fixture is ready."
echo "POS URL: http://103.175.207.127:8088/login/admin"
echo "POS email: ${STAFF_EMAIL}"
echo "POS password: ${STAFF_PASSWORD}"
echo "Member URL: http://103.175.207.127:8088/login/member"
echo "Member email: ${MEMBER_EMAIL}"
echo "Member password: ${MEMBER_PASSWORD}"
echo "Member barcode: ${MEMBER_BARCODE}"
echo "Business URL: http://103.175.207.127:8088/login/business"
echo "Business email: ${OWNER_EMAIL}"
echo "Business password: ${OWNER_PASSWORD}"
echo "Business slug: qa-pos"
echo "Reset this fixture anytime by rerunning this script."
