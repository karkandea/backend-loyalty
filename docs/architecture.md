# Backend Loyalty — architecture and critical flows

This is an implementation-oriented view of the default branch, not a
production-readiness certification.

## Layers and persistence

```mermaid
flowchart TD
    Clients["Staff, business, member clients"] --> Api["ASP.NET Core API and auth policies"]
    Api --> App["Application contracts and use cases"]
    Api --> Infra["Infrastructure services"]
    App --> Infra
    Infra --> Domain["Domain entities"]
    Infra --> Db[(PostgreSQL via EF Core)]
```

`BackendLoyalty.Api` provides controllers, authentication and dependency
injection; `Application` defines service contracts; `Domain` contains the
entities; `Infrastructure` implements database operations and external
integrations. `LoyaltyDbContext` maps legacy loyalty data, while
`StandaloneAuthDbContext` supports the application-owned authentication model.

## Authentication paths

```mermaid
flowchart TD
    Login["Staff / business login"] --> Verify["Verify application password hash"]
    Verify --> Legacy{"Legacy placeholder?"}
    Legacy -->|"Yes, migrated auth schema exists"| Adopt["Verify and adopt bcrypt hash"]
    Legacy -->|"No usable password hash"| Reset["Explicit password reset required"]
    Legacy -->|"No"| Issue["Issue access and refresh JWT"]
    Adopt --> Issue
    Issue --> Session["Store hashed refresh session"]
    Session --> Rotate["Rotate on refresh; detect reuse"]
```

Staff/business access tokens require `token_type=access`. Refresh-token
records are persisted as hashes, and the refresh endpoint uses transactional
rotation. This is distinct from member authentication: `member_session` is an
opaque cookie, hashed before database lookup and checked for expiry,
revocation and the owning member/business context. See the
[`AuthController`](../src/BackendLoyalty.Api/Controllers/AuthController.cs),
[`RefreshTokenSessionService`](../src/BackendLoyalty.Infrastructure/Auth/RefreshTokenSessionService.cs),
and [`MemberSessionResolver`](../src/BackendLoyalty.Infrastructure/Members/MemberSessionResolver.cs).

## Redemption consistency

```mermaid
sequenceDiagram
    participant Staff as Authorized staff
    participant API as Redemption API
    participant DB as PostgreSQL
    Staff->>API: Redeem a reward token
    API->>DB: Begin transaction; read token and reward
    API->>API: Check business, expiry, status and ownership
    API->>DB: Conditionally claim unused token
    API->>DB: Conditionally mark reward redeemed
    API->>DB: Insert loyalty transaction
    API->>DB: Commit
    API-->>Staff: Redemption result
```

The code uses EF Core `ExecuteUpdateAsync` with status/ownership guards and
checks the number of affected rows before completing a transaction. A
concurrent second redemption should fail its conditional claim instead of
creating another successful redemption. See
[`RewardRedemptionService.cs`](../src/BackendLoyalty.Infrastructure/Rewards/RewardRedemptionService.cs).

## Verification boundary

The CI file covers .NET restore/build and a Docker image build, not a database
integration test suite. Before migration or release, provision a disposable
PostgreSQL database, apply schema changes deliberately, exercise auth migration,
refresh reuse, member session isolation, and concurrent redemption, then
record the exact commit and result. Never point the test script at production.
