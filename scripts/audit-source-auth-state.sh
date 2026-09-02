#!/usr/bin/env bash
set -euo pipefail

# READ-ONLY audit against the legacy Supabase PostgreSQL source.
# Never writes to source. Never prints encrypted_password or connection credentials.
# The source URL is read silently from stdin unless SOURCE_DB_URL is already set.

if [[ -z "${SOURCE_DB_URL:-}" ]]; then
  read -r -s -p 'SOURCE_DB_URL (hidden): ' SOURCE_DB_URL
  echo
fi

[[ -n "${SOURCE_DB_URL:-}" ]] || { echo 'ERROR: SOURCE_DB_URL is required' >&2; exit 1; }
command -v psql >/dev/null 2>&1 || { echo 'ERROR: psql is required' >&2; exit 1; }

PSQL=(psql "$SOURCE_DB_URL" -v ON_ERROR_STOP=1 -X -P pager=off)

AUTH_USERS_OK="$("${PSQL[@]}" -qAtc "SELECT to_regclass('auth.users') IS NOT NULL;")"
BUSINESS_USER_OK="$("${PSQL[@]}" -qAtc "SELECT to_regclass('public.\"BusinessUser\"') IS NOT NULL;")"
[[ "$AUTH_USERS_OK" == "t" ]] || { echo 'ERROR: auth.users missing; refusing to audit unexpected database' >&2; exit 1; }
[[ "$BUSINESS_USER_OK" == "t" ]] || { echo 'ERROR: public.BusinessUser missing; refusing to audit unexpected database' >&2; exit 1; }

echo '=== READ-ONLY legacy Supabase auth-state audit ==='

"${PSQL[@]}" <<'SQL'
BEGIN READ ONLY;

\echo '\n[1] Overall auth.users state'
SELECT
  COUNT(*) AS total_auth_users,
  COUNT(*) FILTER (WHERE COALESCE(encrypted_password, '') <> '') AS with_password_hash,
  COUNT(*) FILTER (WHERE email_confirmed_at IS NOT NULL) AS email_confirmed,
  COUNT(*) FILTER (WHERE email_confirmed_at IS NULL) AS email_unconfirmed,
  COUNT(*) FILTER (WHERE deleted_at IS NOT NULL) AS deleted,
  COUNT(*) FILTER (WHERE banned_until IS NOT NULL AND banned_until > now()) AS currently_banned
FROM auth.users;

\echo '\n[2] Active business-membership cardinality per auth identity'
WITH membership_counts AS (
  SELECT
    u.id,
    COUNT(b.id) FILTER (WHERE b."isActive" = true) AS active_memberships
  FROM auth.users u
  LEFT JOIN public."BusinessUser" b
    ON b."authUserId" = u.id
       OR (b."authUserId" IS NULL AND b.id = u.id::text)
  GROUP BY u.id
)
SELECT
  COUNT(*) FILTER (WHERE active_memberships = 0) AS zero_membership,
  COUNT(*) FILTER (WHERE active_memberships = 1) AS one_membership,
  COUNT(*) FILTER (WHERE active_memberships > 1) AS multi_membership,
  MAX(active_memberships) AS max_memberships
FROM membership_counts;

\echo '\n[3] Zero-membership auth-state matrix'
WITH zero_users AS (
  SELECT u.*
  FROM auth.users u
  WHERE NOT EXISTS (
    SELECT 1
    FROM public."BusinessUser" b
    WHERE b."isActive" = true
      AND (b."authUserId" = u.id OR (b."authUserId" IS NULL AND b.id = u.id::text))
  )
)
SELECT
  COUNT(*) AS zero_membership_total,
  COUNT(*) FILTER (
    WHERE COALESCE(encrypted_password, '') <> ''
      AND email_confirmed_at IS NOT NULL
      AND deleted_at IS NULL
      AND (banned_until IS NULL OR banned_until <= now())
  ) AS password_login_eligible,
  COUNT(*) FILTER (WHERE COALESCE(encrypted_password, '') = '') AS no_password,
  COUNT(*) FILTER (WHERE email_confirmed_at IS NULL) AS unconfirmed,
  COUNT(*) FILTER (WHERE deleted_at IS NOT NULL) AS deleted,
  COUNT(*) FILTER (WHERE banned_until IS NOT NULL AND banned_until > now()) AS currently_banned
FROM zero_users;

\echo '\n[4] Zero-membership resolved role distribution (no PII)'
WITH zero_users AS (
  SELECT u.*
  FROM auth.users u
  WHERE NOT EXISTS (
    SELECT 1
    FROM public."BusinessUser" b
    WHERE b."isActive" = true
      AND (b."authUserId" = u.id OR (b."authUserId" IS NULL AND b.id = u.id::text))
  )
), roles AS (
  SELECT lower(COALESCE(
    NULLIF(raw_user_meta_data ->> 'role', ''),
    NULLIF(raw_app_meta_data ->> 'role', ''),
    NULLIF(role, ''),
    '<null>'
  )) AS resolved_role
  FROM zero_users
)
SELECT resolved_role, COUNT(*) AS rows
FROM roles
GROUP BY resolved_role
ORDER BY rows DESC, resolved_role;

\echo '\n[5] Zero-membership provider distribution'
WITH zero_users AS (
  SELECT u.*
  FROM auth.users u
  WHERE NOT EXISTS (
    SELECT 1
    FROM public."BusinessUser" b
    WHERE b."isActive" = true
      AND (b."authUserId" = u.id OR (b."authUserId" IS NULL AND b.id = u.id::text))
  )
)
SELECT
  COALESCE(raw_app_meta_data ->> 'provider', '<null>') AS provider,
  COUNT(*) AS rows
FROM zero_users
GROUP BY provider
ORDER BY rows DESC, provider;

\echo '\n[6] Duplicate normalized auth emails'
SELECT lower(trim(email)) AS normalized_email, COUNT(*) AS rows
FROM auth.users
WHERE email IS NOT NULL AND trim(email) <> ''
GROUP BY lower(trim(email))
HAVING COUNT(*) > 1
ORDER BY rows DESC, normalized_email;

\echo '\n[7] BusinessUser auth mapping anomalies against auth.users'
SELECT
  COUNT(*) FILTER (WHERE b."authUserId" IS NOT NULL AND u_by_auth.id IS NULL) AS missing_auth_user_by_authUserId,
  COUNT(*) FILTER (WHERE b."authUserId" IS NULL AND u_by_id.id IS NULL) AS legacy_id_without_auth_user,
  COUNT(*) FILTER (WHERE b."authUserId" IS NOT NULL AND lower(trim(b.email)) <> lower(trim(u_by_auth.email))) AS email_mismatch
FROM public."BusinessUser" b
LEFT JOIN auth.users u_by_auth ON u_by_auth.id = b."authUserId"
LEFT JOIN auth.users u_by_id ON b."authUserId" IS NULL AND b.id = u_by_id.id::text;

\echo '\n[8] Auth state among active BusinessUser identities'
WITH identities AS (
  SELECT DISTINCT COALESCE(b."authUserId"::text, b.id) AS auth_id
  FROM public."BusinessUser" b
  WHERE b."isActive" = true
), users AS (
  SELECT u.*
  FROM identities i
  JOIN auth.users u ON u.id::text = i.auth_id
)
SELECT
  COUNT(*) AS mapped_identities,
  COUNT(*) FILTER (WHERE email_confirmed_at IS NULL) AS unconfirmed,
  COUNT(*) FILTER (WHERE deleted_at IS NOT NULL) AS deleted,
  COUNT(*) FILTER (WHERE banned_until IS NOT NULL AND banned_until > now()) AS currently_banned,
  COUNT(*) FILTER (WHERE COALESCE(encrypted_password, '') = '') AS no_password
FROM users;

ROLLBACK;
SQL

echo
echo 'PASS: source auth-state audit completed read-only.'
