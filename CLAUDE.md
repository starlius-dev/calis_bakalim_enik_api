# CLAUDE.md — Çalış Bakalım Enik API

.NET 9 Web API for the **Çalış Bakalım Enik** life planner (by Starlius).
Companion repo: the Flutter client (web + Android + iOS).

Read `docs/ARCHITECTURE.md` before touching structure, `docs/DATABASE.md` before
touching the schema, and `docs/SECURITY.md` before touching anything auth-shaped.

## Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 9 (`net9.0`), C# 13, nullable + implicit usings on |
| Web | ASP.NET Core Minimal APIs, grouped per feature |
| Data | EF Core 9 + Npgsql → PostgreSQL 16 |
| Cache / counters | Redis (already running on the host) via `StackExchange.Redis` |
| Auth | ASP.NET Core Identity (extended) + custom JWT issuance |
| Validation | FluentValidation |
| Mapping | Mapperly (source-generated; no runtime reflection) |
| Logging | Serilog → console + rolling file + PostgreSQL sink |
| Push | Firebase Cloud Messaging (`FirebaseAdmin`) |
| Tests | xUnit + FluentAssertions + Testcontainers (Postgres + Redis) |

## Solution layout

    src/
      CalisBakalimEnik.Domain/          entities, value objects, domain events. NO dependencies.
      CalisBakalimEnik.Application/     use cases, DTOs, validators, port interfaces. Depends on Domain.
      CalisBakalimEnik.Infrastructure/  EF Core, Redis, FCM, SMTP, SMS. Implements Application ports.
      CalisBakalimEnik.Api/             endpoints, DI wiring, middleware, auth config.
    tests/
      CalisBakalimEnik.UnitTests/
      CalisBakalimEnik.IntegrationTests/

**Dependency rule — enforced, not aspirational.** Arrows point inward only:
`Api → Infrastructure → Application → Domain`. Domain references nothing.
Application declares interfaces (`IEmailSender`, `IPushSender`, `ICurrentUser`,
`IClock`); Infrastructure implements them. If you need an `Npgsql` or
`StackExchange.Redis` using-directive outside Infrastructure, the design is wrong.

## Non-negotiable rules

1. **Every user-owned entity derives from `OwnedEntity`** and is filtered by an
   EF global query filter on `OwnerId`. Never write a query that bypasses it
   except in explicitly-named admin code paths that call `IgnoreQueryFilters()` —
   and never for the health tables, which have no admin read path at all.
2. **Never return an entity from an endpoint.** Map to a DTO. Entities carry
   `OwnerId`, row versions and audit columns that must not leak.
3. **No `DateTime.Now`, ever.** Inject `IClock`; store `timestamptz`; everything is
   UTC in the database and converted at the edge.
4. **Never log a secret.** Passwords, tokens, TOTP secrets, recovery codes and
   FCM tokens are redacted by the Serilog destructuring policy — do not
   interpolate them into a message string, which bypasses it.
5. **The client IP is `CF-Connecting-IP`,** not `HttpContext.Connection.RemoteIpAddress`.
   Traffic arrives through a Cloudflare Tunnel, so the socket address is the same
   for every user. Rate limiting and lockout read the forwarded header via
   `ForwardedHeaders` restricted to Cloudflare's published ranges.
6. **Migrations are additive and reviewed.** No destructive migration reaches `main`
   without a written backfill plan. Never edit an applied migration.
7. **Breaking an endpoint means a new API version,** not a changed one. See
   `docs/VERSIONING.md`.

## Conventions

- Endpoints live in `Api/Features/<Feature>/` as a `Map<Feature>Endpoints()`
  extension on `IEndpointRouteBuilder`, registered under `/api/v{version}/`.
- One use case per file in `Application/Features/<Feature>/`, named for what it
  does: `CreateCourseHandler`, not `CourseService`.
- DTO suffixes: `...Request` inbound, `...Response` outbound. No shared read/write DTOs.
- Async everywhere; every method that touches I/O takes a `CancellationToken` and
  passes it down.
- Errors return RFC 9457 `application/problem+json`. Validation failures are 400 with
  an `errors` dictionary; never throw raw exceptions across the endpoint boundary.
- Table and column names are `snake_case` (configured globally); C# stays `PascalCase`.

## Commands

    dotnet build
    dotnet test
    dotnet format --verify-no-changes
    dotnet ef migrations add <Name> -p src/CalisBakalimEnik.Infrastructure -s src/CalisBakalimEnik.Api
    dotnet ef database update      -p src/CalisBakalimEnik.Infrastructure -s src/CalisBakalimEnik.Api

Integration tests spin up real Postgres and Redis via Testcontainers — Docker must
be running. They do not touch the developer database.

## Branches

`main` is production. `qa` is integration. Feature branches cut from `qa`, PR back
into `qa`, then `qa` is promoted to `main` by PR. Never commit directly to either.

## Secrets

Never commit `appsettings.Production.json`, the FCM service-account JSON, the JWT
signing key or SMTP/SMS credentials. Local development uses
`dotnet user-secrets`; the server reads environment variables. `appsettings.json`
holds structure and safe defaults only.
