-- Minimal, app-owned auth migration bridge for the standalone Loyalty backend.
-- The target VPS database does not need the Supabase auth schema.
-- Import only auth.users.id + email + encrypted_password into this table.
-- Drop this table after all required legacy accounts have adopted credentials into
-- the standalone auth model / business-admin membership records.

CREATE TABLE IF NOT EXISTS "LegacyAuthUserPassword" (
    "authUserId" text PRIMARY KEY,
    "email" text,
    "passwordHash" text NOT NULL,
    "importedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "LegacyAuthUserPassword_passwordHash_not_empty_ck"
        CHECK (length(trim("passwordHash")) > 0)
);

ALTER TABLE "LegacyAuthUserPassword"
    ADD COLUMN IF NOT EXISTS "email" text;

CREATE UNIQUE INDEX IF NOT EXISTS "LegacyAuthUserPassword_email_ci_key"
    ON "LegacyAuthUserPassword" (lower(trim("email")))
    WHERE "email" IS NOT NULL AND length(trim("email")) > 0;

COMMENT ON TABLE "LegacyAuthUserPassword" IS
    'Temporary one-time bridge containing legacy auth IDs, normalized emails, and password hashes; safe to drop after standalone credential adoption is complete.';

REVOKE ALL ON TABLE "LegacyAuthUserPassword" FROM PUBLIC;
