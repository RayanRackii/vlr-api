# Super-admin tenant cache: use the access token as a hint (2026-11-13)

Repositories: vlr-api only. Branch: `fix/admin-tenant-cache-token-hint`. Decision taken by the user
("Token hint") at the Human Decision Gate.

## Problem (seen in PROD v18, 2 Fly machines)
`SupabaseUserTenantMetadataCache` (10 s, per process) caches the live `app_metadata.tenant_id`
of a super-admin. Enter/exit invalidate it only in the process that served them
(`SupabaseAuthAdminClient.PutAppMetadataAsync`). Log 2026-11-13 19:24: `exit` on machine B
(B caches "no tenant"), `enter` on machine A, then for ~8 s every request that lands on B answers
403 in < 1 ms (cache hit → `session.Resolve(null)` in `PlatformAdminTenantSessionMiddleware`),
while A answers 200. Same for a tenant switch X → Y (B would keep X up to 10 s).

## Rule
The cached value may be used only when it equals the tenant in the request's access token
(`SupabaseAppMetadataParser.TryExtractTenantId(context.User)`; both null counts as equal).
On mismatch, read the live value from Supabase (as on a cache miss) and store it. The live value
stays the only authority: the token is a hint to skip the cache, never a source of the tenant.
The web already refreshes the session after enter and exit, so the new token carries the new
tenant and the stale machine re-reads at once.

## Changes
1. `Core/Platform.Core.Infrastructure/Supabase/ISupabaseAuthAdminClient.cs`: add
   `Task<Guid?> GetUserTenantAppMetadataAsync(string supabaseUserId, TenantMetadataCacheHint hint, CancellationToken cancellationToken = default)`
   with a **default interface implementation** that calls the existing overload (so the ~10 test
   fakes keep compiling). New `public readonly record struct TenantMetadataCacheHint(Guid? TokenTenantId)`
   in the same folder. XML doc: the hint only decides whether the cache may be used.
2. `SupabaseAuthAdminClient.cs`: implement the new overload. `TryGet` hit **and**
   `cached == hint.TokenTenantId` → return cached; otherwise `CaptureEpoch` → live read → `Store`
   → return live (same epoch logic as the existing method; share a private helper). Existing
   overload unchanged.
3. `Platform.Api/Authentication/PlatformAdminTenantSessionMiddleware.cs`: in `ShouldResolve`/
   `InvokeAsync`, take `SupabaseAppMetadataParser.TryExtractTenantId(context.User)` and call the
   new overload with it. Nothing else changes (membership check, 503 on `DbException`, Auth
   failure fallback).
4. `SupabaseUserTenantMetadataCache.cs` class summary: replace "Assumes a single API replica…"
   with one sentence on the token hint covering other replicas.
5. ROADMAP.md: one entry under a new heading "Cache de tenant do super-admin com 2 máquinas
   (2026-11-13)", Portuguese, same style as the entries above.

## Tests (write first; xUnit)
In `tests/Platform.Api.Tests/Authentication/SupabaseAuthAdminTenantMetadataCacheTests.cs`
(reuse its client/handler helpers):
- cached null + hint X → live read happens (handler GET count +1) and returns the live value.
- cached X + hint X → no live read, returns X.
- cached X + hint Y → live read; cached X + hint null → live read.
- existing overload without hint keeps today's behaviour (cache hit, no live read).
In `PlatformAdminTenantSessionMiddlewareLoggingTests.cs` (or a new file next to it):
- two middleware calls sharing one cache: first with a token without tenant and live = null
  (stores null), then live switches to tenant T (simulating enter on another machine, no
  invalidation) and the token carries T → session resolves T (with an active membership
  seeded), proving the stale-null 403 is gone.
Use `TenantClaimTypes.AppMetadata` with JSON `{"tenant_id":"<guid>"}` for the token claim.

## Out of scope
Pool sizes, the 503 path, web changes, any other caller of `GetUserTenantAppMetadataAsync`
(`StaffTenantMembershipService` keeps the old overload).

## Done when
Diff limited to the files above; tests added; no .NET 10 SDK locally, so CI must be green on the
PR. Commit `fix(auth): use the access token as a hint for the super-admin tenant cache`, push the
branch, open the PR to `develop` with this spec linked. Do not merge.
