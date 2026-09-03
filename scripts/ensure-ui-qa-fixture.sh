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

STAFF_EMAIL='qa.pos@loyalty.local'
STAFF_PASSWORD='SmokeOnly-Refresh-2026!'
STAFF_BCRYPT='$2y$10$uelGbUGlkEenIW0KuLdQhe48XrVAAP0TtBEsccu34TLv6q2koWYfu'
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
  -v member_id="$MEMBER_ID" \
  -v barcode="$MEMBER_BARCODE" \
  -v card1="$CARD_ONE_ID" \
  -v card2="$CARD_TWO_ID" \
  -v member_card="$MEMBER_CARD_ID" \
  -v reward_id="$REWARD_ID" \
  -v milestone_id="$MILESTONE_ID" <<'SQL'
BEGIN;

DELETE FROM "AuthRefreshSession" WHERE "userId" = :'staff_id';
DELETE FROM "AuditLog" WHERE "businessId" = :'business_id'::uuid;
DELETE FROM "RewardToken" WHERE "businessId" = :'business_id';
DELETE FROM "Transaction" WHERE "businessId" = :'business_id';
DELETE FROM "MemberReward" WHERE "businessId" = :'business_id';
DELETE FROM "CardMilestone" WHERE "businessId" = :'business_id';
DELETE FROM "MemberCard" WHERE "businessId" = :'business_id';
DELETE FROM "MemberSession" WHERE "businessId" = :'business_id';
DELETE FROM "Member" WHERE "businessId" = :'business_id';
DELETE FROM "Reward" WHERE "businessId" = :'business_id';
DELETE FROM "Card" WHERE "businessId" = :'business_id';
DELETE FROM "AdminUser" WHERE "businessId" = :'business_id';
DELETE FROM "Outlet" WHERE "businessId" = :'business_id';
DELETE FROM "Business" WHERE id = :'business_id';

INSERT INTO "Business" (id,name,slug,tier,"isActive","hasCompletedOnboarding","createdAt","updatedAt")
VALUES (:'business_id','Loyalty QA Store','qa-pos','FREE',true,true,now(),now());

INSERT INTO "Outlet" (id,"businessId",name,address,city,phone,"isActive","createdAt","updatedAt")
VALUES (:'outlet_id',:'business_id','QA Outlet','UI test fixture only','Jakarta',NULL,true,now(),now());

INSERT INTO "AdminUser" (id,"businessId","outletId",email,"passwordHash","fullName",role,"isActive","lastLoginAt","createdAt","updatedAt")
VALUES (:'staff_id',:'business_id',:'outlet_id',:'staff_email',:'staff_hash','QA POS Staff','STAFF',true,NULL,now(),now());

INSERT INTO "Card" (id,"businessId",name,description,level,"requiredStamps",status,"termsAndConditions","isDeleted","howItWorksSteps","createdAt","updatedAt") VALUES
(:'card1',:'business_id','QA Starter Card','UI test card',1,5,'ACTIVE',NULL,false,'[]'::jsonb,now(),now()),
(:'card2',:'business_id','QA Level 2','UI test level-up card',2,3,'ACTIVE',NULL,false,'[]'::jsonb,now()+interval '1 millisecond',now());

INSERT INTO "Reward" (id,"businessId",name,description,"sourceType","defaultExpiryDays","createdAt","updatedAt")
VALUES (:'reward_id',:'business_id','QA Free Drink','UI test milestone reward','MILESTONE',30,now(),now());

INSERT INTO "CardMilestone" (id,"businessId","cardId","stampCount","sortOrder","rewardId","rewardType","rewardValue",title,description,"createdAt","updatedAt")
VALUES (:'milestone_id',:'business_id',:'card1',5,1,:'reward_id','REWARD',NULL,'QA Reward','Issued at 5 stamps',now(),now());

INSERT INTO "Member" (id,"businessId",name,email,phone,"memberBarcode","totalStamps","dateJoined","createdAt","updatedAt")
VALUES (:'member_id',:'business_id','QA Member','qa.member@loyalty.local',NULL,:'barcode',0,now(),now(),now());

INSERT INTO "MemberCard" (id,"businessId","memberId","cardId","currentStamps","isActive","startedAt","completedAt","createdAt","updatedAt")
VALUES (:'member_card',:'business_id',:'member_id',:'card1',0,true,now(),NULL,now(),now());

COMMIT;
SQL

echo "PASS: persistent UI QA fixture is ready."
echo "Login URL: http://103.175.207.127:8088/login/admin"
echo "Email: ${STAFF_EMAIL}"
echo "Password: ${STAFF_PASSWORD}"
echo "Member barcode: ${MEMBER_BARCODE}"
echo "Business slug (forgot-password dev/IP flow): qa-pos"
echo "Reset this fixture anytime by rerunning this script."
