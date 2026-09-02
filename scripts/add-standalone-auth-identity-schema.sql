-- App-owned auth-state snapshot used after removing Supabase Auth runtime dependency.
-- Target database only. Source Supabase remains read-only.

BEGIN;

CREATE TABLE IF NOT EXISTS "StandaloneAuthIdentity" (
    id uuid PRIMARY KEY,
    email text NOT NULL,
    "resolvedRole" text,
    "emailConfirmedAt" timestamptz,
    "deletedAt" timestamptz,
    "bannedUntil" timestamptz,
    provider text,
    "hasPassword" boolean NOT NULL DEFAULT false,
    source text NOT NULL DEFAULT 'supabase',
    "sourceCreatedAt" timestamptz,
    "sourceUpdatedAt" timestamptz,
    "importedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS "StandaloneAuthIdentity_email_ci_key"
    ON "StandaloneAuthIdentity" (lower(trim(email)));

CREATE INDEX IF NOT EXISTS "StandaloneAuthIdentity_login_state_idx"
    ON "StandaloneAuthIdentity" ("emailConfirmedAt", "deletedAt", "bannedUntil", "hasPassword");

ALTER TABLE "StandaloneAuthIdentity"
    DROP CONSTRAINT IF EXISTS "StandaloneAuthIdentity_email_nonempty_ck";
ALTER TABLE "StandaloneAuthIdentity"
    ADD CONSTRAINT "StandaloneAuthIdentity_email_nonempty_ck"
    CHECK (length(trim(email)) > 0);

COMMENT ON TABLE "StandaloneAuthIdentity" IS
    'App-owned snapshot of legacy auth identity state; contains no raw tokens and no password hashes.';

REVOKE ALL ON TABLE "StandaloneAuthIdentity" FROM PUBLIC;

COMMIT;
