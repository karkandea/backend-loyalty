-- Target-only schema for persistent JWT refresh-token rotation.
-- Stores only SHA-256 hashes of refresh tokens; raw refresh JWTs are never persisted.

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

CREATE TABLE IF NOT EXISTS "AuthRefreshSession" (
  id uuid PRIMARY KEY,
  "userId" text NOT NULL,
  "authKind" text NOT NULL,
  "tokenHash" text NOT NULL,
  "familyId" uuid NOT NULL,
  "parentSessionId" uuid NULL,
  role text NULL,
  "businessId" text NULL,
  "outletId" text NULL,
  "expiresAt" timestamptz NOT NULL,
  "revokedAt" timestamptz NULL,
  "revokeReason" text NULL,
  "replacedBySessionId" uuid NULL,
  "createdAt" timestamptz NOT NULL,
  CONSTRAINT "AuthRefreshSession_tokenHash_uk" UNIQUE ("tokenHash"),
  CONSTRAINT "AuthRefreshSession_authKind_ck" CHECK ("authKind" IN ('admin', 'business')),
  CONSTRAINT "AuthRefreshSession_tokenHash_sha256_ck" CHECK (length("tokenHash") = 64),
  CONSTRAINT "AuthRefreshSession_expiry_ck" CHECK ("expiresAt" > "createdAt"),
  CONSTRAINT "AuthRefreshSession_parent_fkey" FOREIGN KEY ("parentSessionId")
    REFERENCES "AuthRefreshSession"(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS "AuthRefreshSession_user_auth_idx"
  ON "AuthRefreshSession" ("userId", "authKind");
CREATE INDEX IF NOT EXISTS "AuthRefreshSession_family_idx"
  ON "AuthRefreshSession" ("familyId");
CREATE INDEX IF NOT EXISTS "AuthRefreshSession_expires_idx"
  ON "AuthRefreshSession" ("expiresAt");
CREATE INDEX IF NOT EXISTS "AuthRefreshSession_active_idx"
  ON "AuthRefreshSession" ("userId", "authKind", "expiresAt")
  WHERE "revokedAt" IS NULL;

COMMIT;
