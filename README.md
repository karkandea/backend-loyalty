# Backend Loyalty

**ASP.NET Core 10 loyalty API** for stamp accrual, milestone rewards, and
redeeming a reward once. The backend uses PostgreSQL and separates API,
application contracts, domain entities, and infrastructure implementations.

**Status:** Incremental migration from an existing Next.js implementation.
The default-branch code is not presented as a completed production cutover.
Read the [architecture, authentication, and redemption notes](docs/architecture.md).

| Engineering area | Evidence |
| --- | --- |
| Layered solution | `BackendLoyalty.Api`, `.Application`, `.Domain`, `.Infrastructure` |
| Authentication | Self-issued JWT for staff/business users; cookie-based member sessions |
| Session safety | Persisted hashed refresh-session records, rotation and reuse handling |
| Transaction safety | Conditional token/reward updates in a PostgreSQL transaction |
| Migration | Password-hash adoption with explicit reset fallback |
| Verification | [Backend CI](.github/workflows/backend-ci.yml), [source](src/), [runtime script](scripts/vps-verify.sh) |

## Migration strategy

The existing Next.js application remains the reference implementation while routes are migrated incrementally. PostgreSQL stays the source of truth during parity testing and no automatic EF migration runs on startup.

Current backend scope:

- ASP.NET Core on .NET 10 LTS
- PostgreSQL via EF Core + Npgsql
- standalone staff/business JWT auth issued by this backend
- persisted refresh-session rotation and replay-family revocation in the
  `AuthRefreshSession` store
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

The .NET backend no longer requires Supabase URL, anon key, service-role key, OIDC discovery, JWKS, or Supabase JWT secrets for staff/business authentication.

It issues its own HS256 access/refresh JWTs using a server-only signing key. Protected API routes accept only tokens carrying `token_type=access`; refresh tokens cannot be used as bearer access tokens. A hashed refresh-token session is persisted and rotated at refresh; reused rotated tokens can trigger family revocation. This requires the corresponding auth-session database schema.

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

Requires the .NET 10 SDK. Build does not require a production database.

```bash
dotnet restore BackendLoyalty.sln
dotnet build BackendLoyalty.sln --configuration Release --no-restore
```

The default branch has a GitHub Actions restore/build/Docker-build workflow,
but **no dedicated automated test project** in the solution snapshot. Build
success is not evidence of transactional correctness on live PostgreSQL.

## Docker / VPS verification

The Docker host does not need a .NET SDK. Use only a disposable or explicitly
approved test database; do not run this against the legacy production database.
From the repository root:

```bash
cp .env.example .env.vps
# edit .env.vps with isolated PostgreSQL + unique JWT settings
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
- Refresh sessions now have persistent hashed records and rotation logic.
  Migration and concurrency behavior still need runtime verification against
  the target PostgreSQL schema before cutover.

## Database safety

Do not run EF migrations against the existing production database during parity testing. Schema changes needed for the final standalone auth/session model should be introduced explicitly after build/runtime parity is verified.

## Important trade-offs and current limits

- Two authentication models coexist to preserve member sessions during the
  migration; authorization must remain explicit at every endpoint.
- A conditional database update claims both the reward token and reward,
  with a single transaction to avoid double redemption. See
  [`RewardRedemptionService.cs`](src/BackendLoyalty.Infrastructure/Rewards/RewardRedemptionService.cs).
- Member reward expiry and the legacy audit schema retain compatibility rules
  noted above; these should be revisited before final cutover.
- No validated production cutover, comprehensive automated test suite, or
  current end-to-end transaction evidence is asserted here.

Individual authorship is traceable through commit and pull-request history;
this README does not claim sole implementation of every component.
