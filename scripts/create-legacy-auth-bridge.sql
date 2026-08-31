-- Minimal, app-owned password migration bridge for the standalone Loyalty backend.
-- The target VPS database does not need the Supabase auth schema.
-- Import only auth.users.id + encrypted_password into this table.
-- Drop this table after all required legacy accounts have adopted their bcrypt hash
-- into BusinessUser.passwordHash / AdminUser.passwordHash.

CREATE TABLE IF NOT EXISTS "LegacyAuthUserPassword" (
    "authUserId" text PRIMARY KEY,
    "passwordHash" text NOT NULL,
    "importedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "LegacyAuthUserPassword_passwordHash_not_empty_ck"
        CHECK (length(trim("passwordHash")) > 0)
);

COMMENT ON TABLE "LegacyAuthUserPassword" IS
    'Temporary one-time bridge containing only legacy auth IDs and password hashes; safe to drop after password adoption is complete.';

REVOKE ALL ON TABLE "LegacyAuthUserPassword" FROM PUBLIC;
