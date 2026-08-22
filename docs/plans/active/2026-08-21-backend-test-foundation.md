# 2026-08-21-backend-test-foundation

Status: approved

## Goal / Problem

No automated tests in vlr-api (`TEST_INFRASTRUCTURE_MISSING`). Add the smallest xUnit project so TrialGuard, B2B/Customer/PlatformAdmin policy boundaries, and reservation/booking conflict seams can regress.

## Repositories

- vlr-api

## Architecture route

- rolvix-architect (GLM). `FABLE_MERGE_REVIEW_NOT_REQUIRED` — conventional local test structure; one mechanical policy extraction.

## Execution route

- api-implementer

## Confirmed decisions

1. Separate project: `tests/Platform.Api.Tests/Platform.Api.Tests.csproj`, xUnit v2, `net10.0`, added under `/tests/` in `Platform.slnx`.
2. Packages: `Microsoft.NET.Test.Sdk` 17.12.x, `xunit` 2.9.x, `xunit.runner.visualstudio` 2.8.x, `Microsoft.EntityFrameworkCore.InMemory` 10.0.9. Hand-rolled fakes only (no Moq).
3. Extract `RolvixAuthorizationOptions.AddRolvixPolicies` from `SupabaseAuthenticationExtensions` (behavior-preserving). Keep `PlatformAdminPolicy` constant on `SupabaseAuthenticationExtensions` or move it with a forwarding const — do not break existing `[Authorize(Policy = ...)]`.
4. **EF InMemory for TrialGuard.** No transactions needed.
5. **Reservation/booking:** `CreateReservationAsync` / `BookSlotAsync` call `BeginTransactionAsync`. EF InMemory cannot run those methods. Prefer SQLite in-memory (`Microsoft.EntityFrameworkCore.Sqlite` 10.0.x) + `EnsureCreated` with a **test-only** `AppDbContext` subclass that remaps `jsonb` columns to TEXT and clears Postgres `HasDefaultValueSql`. If EnsureCreated is still blocked by Postgres-only filters/indexes, fall back to `InternalsVisibleTo` + tests of overlap counting (`GetReservedQuantityAsync` made `internal`) rather than Testcontainers. Do **not** add Docker.
6. Do **not** solve F-01. No parallel race test. Mark `// TODO(F-01)`.
7. No WebApplicationFactory, Hangfire host, Coverlet, CI workflow, clock injection.

## Seams now

- `TrialGuard.EnsureWritableAsync` / `EnsureCanCreateAssetsAsync` / `EnsureCanInviteUserAsync`
- Default / `Customer` / `PlatformAdmin` via `IAuthorizationService`
- Location overlap conflict message on create/book **or** equivalent overlap-quantity seam if transactions block InMemory

## Do not

- F-01 race, Testcontainers, Moq, FluentAssertions, clock refactor, CI yaml, other modules

## Verification

```
dotnet build Platform.slnx
dotnet test
```

## ROADMAP

Replace "Sem testes automatizados." with a line describing the new project. Add Histórico 2026-08-21.
