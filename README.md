# Backend Loyalty

ASP.NET Core backend for the Loyalty Stamp platform.

## Migration strategy

The existing Next.js application remains the reference implementation while routes are migrated incrementally. PostgreSQL stays the source of truth during parity testing and no automatic EF migration runs on startup.

Current backend scope:

- ASP.NET Core on .NET 10 LTS
- PostgreSQL via EF Core + Npgsql
- standalone staff/business JWT auth issued by this backend
- direct `member_session` cookie validation for members
- `POST /api/admin/auth/login`
- `POST /api/business/auth/login`
- `POST /api/business/auth/active-business`
- `POST /api/auth/refresh`
- `POST /api/admin/members/scan`
- `POST /api/admin/members/{memberId}/cards/{memberCardId}/add-stamp`
- `POST /api/admin/rewards/scan`
- `POST /api/member/rewards/{memberRewardId}/token`
- stamp overflow / level progression / cycle
- milestone reward creation and `MemberReward` issuance
- atomic reward redemption
- transaction + best-effort audit writes
- `GET /health` and `GET /health/db`
- Docker/VPS verification script

## Solution structure

```text
src/
├── BackendLoyalty.Api
├── BackendLoyalty.Application
├── BackendLoyalty.Domain
└── BackendLoyalty.Infrastructure
```

## Authentication

### Staff / manager / business users

The .NET backend no longer requires Supabase URL, anon key, service-role key, OIDC discovery, JWKS, or Supabase JWT secrets.

It issues its own HS256 access/refresh JWTs using a server-only signing key. Protected API routes accept only tokens carrying `token_type=access`; refresh tokens cannot be used as bearer access tokens.

Required JWT configuration:

```text
Jwt__Issuer=backend-loyalty
Jwt__Audience=loyalty-app
Jwt__SigningKey=<strong random server-only secret, minimum 32 bytes>
Jwt__AccessTokenMinutes=15
Jwt__RefreshTokenDays=7
```

### Existing password migration

The legacy schema has `BusinessUser.passwordHash` and `AdminUser.passwordHash`, but users created through Supabase Auth commonly contain the placeholder `managed-by-supabase-auth` instead of the real password hash.

For a zero-password-reset migration, the credential service can read the legacy bcrypt value from `auth.users.encrypted_password` **when that auth schema exists in the PostgreSQL database being migrated**. After a successful password verification it copies/adopts that bcrypt hash into the application-owned `BusinessUser.passwordHash` or `AdminUser.passwordHash`, so later logins no longer require the legacy auth schema.

If the application password hash is still a placeholder and `auth.users` was not migrated, login returns `PASSWORD_MIGRATION_REQUIRED`; the safe fallback is a password reset rather than pretending the placeholder is a password hash.

The auth schema is only a temporary password-migration source. No Supabase service/API call is made by the new backend.

Migration readiness can be inspected with:

```bash
psql "$DATABASE_URL" -f scripts/check-auth-migration.sql
```

### Members

Member authentication remains the existing opaque `member_session` design. The raw cookie token is SHA-256 hashed and validated directly against `MemberSession`, including expiry/revocation and member/business ownership.

The .NET service does not trust client-supplied `x-member-id` or `x-member-business-id` headers.

## Required configuration

```text
ConnectionStrings__LoyaltyDb
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
```

Optional trusted frontend origins:

```text
Cors__AllowedOrigins__0=http://localhost:5173
```

Credentialed CORS is enabled only for explicit origins so member cookies can work when frontend/API use separate trusted origins.

## Local build

```bash
dotnet restore BackendLoyalty.sln
dotnet build BackendLoyalty.sln
```

## Docker / VPS verification

The VPS does not need the .NET SDK installed on the host. From the repository root:

```bash
git checkout feat/bootstrap-dotnet-api
git pull
cp .env.example .env.vps
# edit .env.vps with real PostgreSQL + JWT settings
bash scripts/vps-verify.sh
```

The script builds through the .NET 10 Docker SDK image, starts an isolated container on `127.0.0.1:5092`, checks `/health` and `/health/db`, prints runtime stats, does not expose the API publicly, and does not apply EF migrations.

When PostgreSQL runs directly on the VPS host while the API runs in Docker, use `Host=host.docker.internal`; the verification script supplies the Linux host-gateway mapping.

## Parity notes before cutover

- `transactionNotes` is accepted but the legacy `Transaction` table has no notes column.
- Add-stamp currently preserves the legacy 15-minute milestone `MemberReward.expiresAt` behavior even though auto-created rewards default to 30 days.
- Member redemption tokens are 5-minute tokens; issuing a new token expires previous ACTIVE tokens for that reward.
- `AuditLog` remains best-effort because the legacy UUID audit columns do not perfectly match text IDs in the main schema.
- .NET redemption conditionally claims token + reward in one transaction to prevent concurrent double redemption.
- Standalone JWT refresh is currently stateless. Persistent refresh-session revocation/rotation should be added before broad production cutover.

## Database safety

Do not run EF migrations against the existing production database during parity testing. Schema changes needed for the final standalone auth/session model should be introduced explicitly after build/runtime parity is verified.
