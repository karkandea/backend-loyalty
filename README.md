# Backend Loyalty

ASP.NET Core backend for the Loyalty Stamp platform.

## Migration strategy

The existing Next.js application remains the reference implementation while backend routes are migrated incrementally to this service. Existing PostgreSQL data and API contracts are preserved during the migration.

Current first slice:

- ASP.NET Core on .NET 10 LTS
- PostgreSQL via EF Core + Npgsql
- Supabase JWT verification remains the authentication source for now
- Existing database schema is mapped read-only first; no automatic EF migrations run on startup
- `POST /api/admin/members/scan` migrated from Next.js
- Health endpoints: `GET /health` and `GET /health/db`
- Docker build included for VPS deployment

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

## Database safety

Do not generate or apply EF migrations against the existing production database during the parity phase. The current Prisma/PostgreSQL schema remains the source of truth until the backend migration has been validated.
