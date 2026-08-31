SELECT
  to_regclass('auth.users') IS NOT NULL AS legacy_auth_users_present,
  CASE
    WHEN to_regclass('auth.users') IS NULL THEN NULL
    ELSE (
      SELECT COUNT(*)
      FROM auth.users
      WHERE encrypted_password IS NOT NULL
        AND encrypted_password <> ''
    )
  END AS legacy_password_hash_count;

SELECT
  COUNT(*) FILTER (WHERE "passwordHash" = 'managed-by-supabase-auth') AS business_users_pending_password_adoption,
  COUNT(*) FILTER (WHERE "passwordHash" LIKE '$2%') AS business_users_with_bcrypt
FROM "BusinessUser";

SELECT
  COUNT(*) FILTER (WHERE "passwordHash" = 'managed-by-supabase-auth') AS admin_users_pending_password_adoption,
  COUNT(*) FILTER (WHERE "passwordHash" LIKE '$2%') AS admin_users_with_bcrypt
FROM "AdminUser";
