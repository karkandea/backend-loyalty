# Backend Loyalty

ASP.NET Core backend for the Loyalty Stamp platform.

## Migration strategy

The existing Next.js application remains the reference implementation while backend routes are migrated incrementally to this service. Existing PostgreSQL data and API contracts are preserved during the migration.

Current migrated backend scope:

- ASP.NET Core on .NET 10 LTS
- PostgreSQL via EF Core + Npgsql
- Supabase remains the staff/business authentication source for now
- existing `member_session` opaque cookie validation is handled directly by .NET for migrated member routes
- Existing database schema is mapped directly; no automatic EF migrations run on startup
- `POST /api/admin/members/scan`
- `POST /api/admin/members/{memberId}/cards/{memberCardId}/add-stamp`
- `POST /api/admin/rewards/scan`
- `POST /api/member/rewards/{memberRewardId}/token`
- add-stamp overflow across card levels / cycles
- optimistic stamp concurrency guard
- milestone detection
- milestone reward auto-creation and linking
- `MemberReward` issuance
- short-lived member reward token issuance
- atomic cashier reward redemption
- `Transaction` creation
- best-effort `AuditLog` write
- health endpoints: `GET /health` and `GET /health/db`
- Docker build included for VPS deployment

The .NET add-stamp and reward-redemption implementations use database transactions. This improves consistency compared with legacy sequences of separate REST writes while preserving the external API behavior.

## Solution structure

```text
src/
├── BackendLoyalty.Api
├── BackendLoyalty.Application
├── BackendLoyalty.Domain
└── BackendLoyalty.Infrastructure
```

This is a modular monolith: one deployable backend service, split into projects for separation of responsibilities.

## Authentication during migration

### Staff / manager / business users

Supabase remains the token issuer. Authorization trusts signed `app_metadata` / signed claims only; it does not use user-editable `user_metadata` for authorization.

The preferred mode is asymmetric Supabase signing keys. Leave this unset and ASP.NET Core validates through Supabase OIDC/JWKS:

```text
Supabase__JwtSecret=
```

If the existing Loyalty Supabase project still uses legacy HS256 signing, set the existing project JWT secret only in the server runtime environment:

```text
Supabase__JwtSecret=<legacy-project-jwt-secret>
```

Never expose this secret to the frontend or commit it to Git.

### Members

Legacy member authentication uses an opaque `member_session` cookie. The raw token is never stored in PostgreSQL; the existing `MemberSession.sessionTokenHash` SHA-256 value is validated by the .NET API together with expiry/revocation and the linked member/business.

Migrated member endpoints therefore do **not** trust client-supplied `x-member-id` or `x-member-business-id` headers. Those headers were safe in the Next.js application only because `proxy.ts` validated the cookie first and overwrote them before forwarding the request.

## Required configuration

Copy `.env.example` values into your runtime environment. Do not commit real secrets.

Required values:

```text
ConnectionStrings__LoyaltyDb
Supabase__Url
```

Optional frontend origins can be configured with:

```text
Cors__AllowedOrigins__0=http://localhost:5173
```

## Local build

```bash
dotnet restore BackendLoyalty.sln
dotnet build BackendLoyalty.sln
```

Run the API after setting the required environment variables:

```bash
dotnet run --project src/BackendLoyalty.Api
```

## Docker

```bash
docker build -t backend-loyalty .
docker run --rm -p 8080:8080 --env-file .env backend-loyalty
```

## Parity notes to validate before cutover

- `transactionNotes` is accepted by the legacy add-stamp contract but the existing `Transaction` table has no notes column, so it is currently not persisted.
- The legacy add-stamp flow issues milestone `MemberReward.expiresAt` using a 15-minute TTL even though auto-created `Reward.defaultExpiryDays` is 30. The .NET parity implementation currently preserves that behavior pending an explicit business-rule decision.
- Member reward redemption tokens are a separate 5-minute token generated on demand by `POST /api/member/rewards/{memberRewardId}/token`; generating a new one expires previous ACTIVE tokens for that reward.
- The legacy `AuditLog` table uses UUID columns while the main entity IDs are stored as text. Current signup flows generate UUID-shaped IDs, but audit remains best-effort and never blocks the business transaction.
- Reward redemption in .NET conditionally claims both the token and `MemberReward` within one transaction, closing the legacy concurrent double-redemption race.

## Database safety

Do not generate or apply EF migrations against the existing production database during the parity phase. The current Prisma/PostgreSQL schema remains the source of truth until the backend migration has been validated.
