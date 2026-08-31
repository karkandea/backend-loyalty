SELECT to_regclass('auth.users') IS NOT NULL AS legacy_auth_users_present;

DO $$
DECLARE
  legacy_hash_count bigint;
BEGIN
  IF to_regclass('auth.users') IS NULL THEN
    RAISE NOTICE 'auth.users is not present; accounts with placeholder hashes will need password reset/migration.';
  ELSE
    EXECUTE 'SELECT COUNT(*) FROM auth.users WHERE encrypted_password IS NOT NULL AND encrypted_password <> '''''
      INTO legacy_hash_count;
    RAISE NOTICE 'legacy password hashes available: %', legacy_hash_count;
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
