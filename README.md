# Backend Loyalty

ASP.NET Core backend for the Loyalty Stamp platform.

## Migration strategy

The existing Next.js application remains the reference implementation while backend routes are migrated incrementally to this service. Existing PostgreSQL data and API contracts are preserved during the migration.

Current migrated backend scope:

- ASP.NET Core on .NET 10 LTS
- PostgreSQL via EF Core + Npgsql
- Supabase remains the authentication source for now
- Existing database schema is mapped directly; no automatic EF migrations run on startup
- `POST /api/admin/members/scan`
- `POST /api/admin/members/{memberId}/cards/{memberCardId}/add-stamp`
- add-stamp overflow across card levels / cycles
- optimistic stamp concurrency guard
- milestone detection
- milestone reward auto-creation and linking
- `MemberReward` issuance
- `Transaction` creation
- best-effort `AuditLog` write
- health endpoints: `GET /health` and `GET /health/db`
- Docker build included for VPS deployment

The .NET add-stamp implementation performs the write flow inside a database transaction. This improves consistency compared with the legacy sequence of separate REST writes while preserving the external API behavior.

## Solution structure

```text
src/
├── BackendLoyalty.Api
├── BackendLoyalty.Application
├── BackendLoyalty.Domain
└── BackendLoyalty.Infrastructure
```

This is a modular monolith: one deployable backend service, split into projects for separation of responsibilities.

## Required configuration

Copy `.env.example` values into your runtime environment. Do not commit real secrets.

Required values:

```text
ConnectionStrings__LoyaltyDb
Supabase__Url
```

### Supabase JWT signing mode

The preferred mode is asymmetric Supabase signing keys. Leave this unset and ASP.NET Core will validate through Supabase OIDC/JWKS:

```text
Supabase__JwtSecret=
```

If the existing Loyalty Supabase project still uses legacy HS256 signing, set the existing project JWT secret only in the server runtime environment:

```text
Supabase__JwtSecret=<legacy-project-jwt-secret>
```

Never expose this secret to the frontend or commit it to Git.

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
- The legacy `AuditLog` table uses UUID columns while the main entity IDs are stored as text. Current signup flows generate UUID-shaped IDs, but audit remains best-effort and never blocks the stamp transaction.

## Database safety

Do not generate or apply EF migrations against the existing production database during the parity phase. The current Prisma/PostgreSQL schema remains the source of truth until the backend migration has been validated.
