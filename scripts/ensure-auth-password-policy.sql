BEGIN;

CREATE TABLE IF NOT EXISTS "AuthPasswordPolicy" (
    "authUserId" text PRIMARY KEY,
    "requiresPassword" boolean NOT NULL DEFAULT true,
    "graceExpiresAt" timestamptz NOT NULL,
    "passwordSetAt" timestamptz,
    "createdAt" timestamptz NOT NULL DEFAULT now(),
    "updatedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS "AuthPasswordPolicy_required_idx"
    ON "AuthPasswordPolicy" ("requiresPassword", "graceExpiresAt");

REVOKE ALL ON TABLE "AuthPasswordPolicy" FROM PUBLIC;

COMMIT;
