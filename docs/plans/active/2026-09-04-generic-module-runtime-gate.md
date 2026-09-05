# 2026-09-04 — Generic Inactive Commercial Module Enforcement (Wave 5)

Status: ready for implementation (no open Human Decision Gate)
Repositories: vlr-api only
Owner: api-implementer → api-reviewer → Merge Risk Gate (Fable required: auth/tenant/contract-adjacent runtime gate)

Architecture route: rolvix-architect (audit complete). Fable is the Merge Risk Gate, not a second architecture pass.

## Goal / Problem

Today only the Catalog B2C portal has an explicit inactive-module 403 (`CatalogModuleGate`). Every other commercial surface relies *indirectly* on `PermissionResolver` filtering the RBAC catalog by `tenant_modules`. That is fragile and — critically — does **not** cover B2C `Customer` JWT endpoints or anonymous public endpoints, which never pass through `PermissionResolver`. Wave 5 introduces one declarative, canonical runtime gate, keyed by `PlatformModuleCatalog`, that complements RBAC (does not replace it).

## Visible behavior

Inactive commercial module → HTTP 403 `{ "error": "Module is not active for this tenant." }`. Active module still requires normal auth + RBAC. Core and Platform Admin administration stay ungated by commercial entitlements.

## Confirmed decisions

- Runtime gate complements RBAC; it does not replace `[RequirePermission]`.
- Canonical location: `[RequireActiveModule]` as `IAsyncAuthorizationFilter` (not middleware, not `IAuthorizationHandler`).
- `CatalogModuleGate`: **replace** (delete). One source of truth.
- Never gate `asset-registry`. Never treat `core` as a `tenant_modules` row.
- `orders` / `pedidos` resolve to `catalog`.
- Support mode inside a tenant does **not** bypass inactive modules.
- Platform Admin platform-mode (`TenantId` null) skips the gate; admin endpoints are not annotated.
- B2C Customer JWT: tenant resolved before module evaluation (2026-09-02 ordering).
- Invalid annotation keys (`maintenance`, `asset-registry`, unknown, aliases) fail at startup (`MODULE_KEY_INVALID`).
- No DB migration, no data backfill, no WEB, no PROD.

## Invariants that must not break

- Inventory does not authorize Rentals/PMOC/OS and vice versa.
- Wave 2: `/api/rental-assets`, `/api/maintenance-plans/asset-categories`, `/api/work-orders/assets` work when inventory is OFF if the owning module is ON.
- PMOC family/category provisioning rules from `2026-09-04-asset-registry-category-provisioning.md` unchanged.
- OS O2: module gate checks entitlement only, not Asset existence.
- `GET /api/admin/modules` and tenant create/edit remain available to PlatformAdmin.
- CustomerJwt null-tenant bug must not return.

## Audit findings

### CURRENT_MODULE_GATES

1. **`CatalogModuleGate`** — `Platform.Api/Modules/Catalog/Services/CatalogModuleGate.cs`. Queries `dbContext.TenantModules` for `catalog` active; throws `CatalogModuleInactiveException`. Consumed only by `CatalogPortalController.EnsureModuleAsync` → 403. One-off, module-specific.
2. **`PermissionResolver`** — RBAC filtering by `tenant_modules`. Indirect. Does not cover B2C Customer or anonymous public.
3. **`RequirePermissionAttribute`** → `perm:{key}` → `PermissionAuthorizationHandler`. Depends on resolver filtering. Indirect.

### UNGATED_MODULE_SURFACES

- B2C Customer JWT rentals (`mine` / POST reservations / queue / B2C rental-assets GETs / `slots/book`) — no `rentals` check.
- Anonymous public rentals (`availability`, public rental-assets/layouts/schedule).
- B2B inventory / rentals / pmoc / os / catalog controllers — only indirect via RBAC.

### DUPLICATED_GATE_LOGIC

- `CatalogModuleGate.EnsureActiveAsync` duplicates `PermissionResolver.LoadActiveModulesAsync`.
- `UserDirectoryService.LoadActiveModulesAsync` (`GET /api/users/me`) is a third copy.

## Canonical gate location

**Endpoint metadata attribute + MVC `IAsyncAuthorizationFilter`.**

Rejected:

- Middleware / route-prefix — `api/assets` is shared by `AssetsController` (inventory) and `RentalPricingsBulkController` (rentals).
- `IAuthorizationHandler` — `[AllowAnonymous]` short-circuits authorization handlers, so public endpoints would not be gated.

`IAsyncAuthorizationFilter` runs for `[AllowAnonymous]` actions, after authentication, once per request.

**`CatalogModuleGate`: replace.** Delete `ICatalogModuleGate`, `CatalogModuleGate`, `CatalogModuleInactiveException`, and `CatalogPortalController.EnsureModuleAsync`. Annotate `CatalogPortalController` with `[RequireActiveModule(PlatformModules.Catalog)]`.

## Implementation

### 1. `[RequireActiveModule]`

`Platform.Api/Authorization/RequireActiveModuleAttribute.cs` — `IAsyncAuthorizationFilter`. Constructor takes `PlatformModules.*` constant. Validation of keys is at startup, not construction.

### 2. `ITenantModuleAccessor` (Scoped)

Lazy-load `tenant_modules` once per request for `ITenantProvider.TenantId`. Empty set when TenantId is null. Query shape: `IgnoreQueryFilters`, `IsActive`, `ModuleName`. Refactor `PermissionResolver` to use it. Optionally refactor `UserDirectoryService` (not blocking). No cross-request cache.

### 3. Filter algorithm

1. If `TenantId` is null and a public subdomain is available (route `subdomain` or `X-Tenant-Subdomain`), bind via `IPublicTenantBinder.BindFromSubdomainAsync`, then continue.
2. If `TenantId` is still null → **skip** (platform mode / non-tenant public).
3. If declared key is in the active set → pass.
4. Else 403 `{ "error": "Module is not active for this tenant." }` (ObjectResult, do not throw).

Support mode with tenant_id present **enforces**. Platform-mode PlatformAdmin on unannotated admin endpoints is unaffected.

### 4. Startup validator

`ModuleGateStartupValidator`: every `[RequireActiveModule]` key must `TryNormalize` and be in `PlatformModuleCatalog.Commercial` (`IsCommercial && !IsLegacy`). Fail with `MODULE_KEY_INVALID` listing controller/action/key.

### 5. Controllers to annotate (class-level)

Inventory: `AssetsController`, `AssetCategoriesController`, `AssetFamiliesController` → `PlatformModules.Inventory`.

Rentals: `RentalAssetsController`, `RentalPricingsController`, `RentalPricingsBulkController`, `RentalLayoutsController`, `OccupancyKindsController`, `ScheduleController`, `ReservationsController` → `PlatformModules.Rentals`. Verify which controller owns public `rental-assets` (do not mis-annotate).

PMOC: `MaintenancePlansController` (includes `/asset-categories`), `GlobalTemplatesController` → `PlatformModules.Pmoc`.

OS: `WorkOrdersController` (includes `/assets` picker) → `PlatformModules.WorkOrders` (`"os"`).

Catalog: `CatalogProductsController`, `CatalogOrdersController`, `CatalogNotificationsController`, `CatalogPortalController` → `PlatformModules.Catalog`.

Scan for any other commercial module controller missed in the audit and annotate it. Do not annotate by route prefix.

### 6. Do not annotate

Core: Dashboard, Users, Roles, Units, RegistrationFields, ModuleMenuItems.

Platform Admin: AdminModules, AdminTenants, CreateTenant.

Auth/onboarding: CustomerAuth (branding, registration-schema).

Infra: health, hangfire, WhatsApp webhooks.

## Test matrix

`tests/Platform.Api.Tests/Authorization/ModuleRuntimeGateTests.cs` plus updates:

1. B2B module on + permission → 200.
2. B2B module off → 403 even if permission key retained (gate independent of RBAC).
3. B2B module on + permission absent → 403 (RBAC still applies).
4. B2C Catalog/Rentals active → works.
5. B2C Catalog/Rentals inactive → 403.
6. Anonymous public, module on → 200.
7. Anonymous public, module off → 403.
8. PlatformAdmin platform mode on annotated tenant endpoint → not 200 from gate skip alone (RBAC governs).
9. Support mode, module off → 403.
10. Support mode, module on → 200 where RBAC permits.
11. CatalogPortal 403 after CatalogModuleGate removal.
12. inventory OFF + rentals/pmoc/os ON: `/api/rental-assets` 200, `/api/assets` 403, PMOC categories 200, OS assets 200. inventory ON does not substitute for rentals/pmoc/os.
13. catalog-only: catalog 200; inventory/rentals/pmoc/os 403.
14. `MODULE_KEY_INVALID` for maintenance / asset-registry / unknown.
15. B2C Customer with platform-admin email, module off → 403.
16. `ITenantModuleAccessor` loads `tenant_modules` once per request when both RequireActiveModule and PermissionResolver run.
17. Core endpoint (e.g. `/api/users/me` or dashboard) available regardless of commercial module selection.
18. `GET /api/admin/modules` still available to PlatformAdmin.
19. Tenant A entitlement never satisfies tenant B.

Existing `CustomerJwtBearerPipelineTests` and `InventoryHttpGateTests` must stay green. Run full `dotnet test tests/Platform.Api.Tests`.

## Migrations

None.

## Do not

- Auto-enable inventory or any module.
- Gate asset-registry.
- Gate core / platform-admin / auth / webhooks / health.
- Replace RBAC.
- Infer module from route prefix.
- Cross-request cache for tenant_modules.
- Keep CatalogModuleGate as a second gate.
- Touch WEB, main, PROD, Railway, Supabase, tenant_modules data.

## Documentation

Update `ROADMAP.md` Wave 5 checkbox. Spec lives here. Do not invent a second ADR unless the gate location needs one (it does not — ADR 0004 already separates entitlement vs capability).

## How to test

1. `dotnet test tests/Platform.Api.Tests`
2. Catalog-only tenant: `GET /api/reservations` → 403; public rental-assets → 403.
3. Enable rentals: same calls 200 (with auth/permission as required).
4. B2C Customer rentals on → book 200; disable rentals → 403.
5. Platform Admin support into rentals-off tenant → rentals endpoint 403.
6. Annotate with `maintenance` in a unit test of the validator → `MODULE_KEY_INVALID`.
