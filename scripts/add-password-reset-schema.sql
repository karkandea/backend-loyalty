-- Target-only standalone password reset state.
-- Stores only SHA-256 hashes of reset tokens; raw reset tokens are never persisted.

BEGIN;

DO $$
BEGIN
  IF to_regclass('auth.users') IS NOT NULL THEN
    RAISE EXCEPTION 'REFUSING CHANGE: auth.users exists; run only on standalone target';
  END IF;

  IF to_regclass('public."LegacyAuthUserPassword"') IS NULL THEN
    RAISE EXCEPTION 'REFUSING CHANGE: standalone migration marker LegacyAuthUserPassword is missing';
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS "AuthPasswordReset" (
  id uuid PRIMARY KEY,
  "userId" text NOT NULL,
  "authKind" text NOT NULL,
  email text NOT NULL,
  "tokenHash" text NOT NULL,
  "expiresAt" timestamptz NOT NULL,
  "usedAt" timestamptz NULL,
  ip text NULL,
  "userAgent" text NULL,
  "createdAt" timestamptz NOT NULL,
  CONSTRAINT "AuthPasswordReset_tokenHash_uk" UNIQUE ("tokenHash"),
  CONSTRAINT "AuthPasswordReset_authKind_ck" CHECK ("authKind" IN ('admin', 'business')),
  CONSTRAINT "AuthPasswordReset_tokenHash_sha256_ck" CHECK (length("tokenHash") = 64),
  CONSTRAINT "AuthPasswordReset_expiry_ck" CHECK ("expiresAt" > "createdAt")
);

CREATE INDEX IF NOT EXISTS "AuthPasswordReset_user_auth_idx"
  ON "AuthPasswordReset" ("userId", "authKind");
CREATE INDEX IF NOT EXISTS "AuthPasswordReset_expires_idx"
  ON "AuthPasswordReset" ("expiresAt");
CREATE INDEX IF NOT EXISTS "AuthPasswordReset_active_idx"
  ON "AuthPasswordReset" ("userId", "authKind", "expiresAt")
  WHERE "usedAt" IS NULL;

COMMIT;
