SELECT
  to_regclass('public."LegacyAuthUserPassword"') IS NOT NULL AS legacy_password_bridge_present,
  to_regclass('auth.users') IS NOT NULL AS transitional_supabase_auth_present;

DO $$
DECLARE
  legacy_hash_count bigint := 0;
BEGIN
  IF to_regclass('public."LegacyAuthUserPassword"') IS NOT NULL THEN
    EXECUTE $sql$
      SELECT COUNT(*)
      FROM "LegacyAuthUserPassword"
      WHERE "passwordHash" IS NOT NULL
        AND "passwordHash" <> ''
    $sql$ INTO legacy_hash_count;

    RAISE NOTICE 'app-owned legacy password hashes available: %', legacy_hash_count;
  ELSIF to_regclass('auth.users') IS NOT NULL THEN
    EXECUTE $sql$
      SELECT COUNT(*)
      FROM auth.users
      WHERE encrypted_password IS NOT NULL
        AND encrypted_password <> ''
    $sql$ INTO legacy_hash_count;

    RAISE NOTICE 'transitional auth.users password hashes available: %', legacy_hash_count;
  ELSE
    RAISE NOTICE 'No legacy password source is present; placeholder accounts will require password reset/import.';
  END IF;
END $$;

SELECT
  COUNT(*) FILTER (WHERE "passwordHash" = 'managed-by-supabase-auth') AS business_users_pending_password_adoption,
  COUNT(*) FILTER (WHERE "passwordHash" LIKE '$2%') AS business_users_with_bcrypt
FROM "BusinessUser";

SELECT
  COUNT(*) FILTER (WHERE "passwordHash" = 'managed-by-supabase-auth') AS admin_users_pending_password_adoption,
  COUNT(*) FILTER (WHERE "passwordHash" LIKE '$2%') AS admin_users_with_bcrypt
FROM "AdminUser";
