# 2026-09-01 — SuperAdmin wizard Resources 403

Status: approved (diagnosis `COMPANY_WIZARD_RESOURCES_403`)

## Goal / Problem

`/admin/tenants/new` step Recursos calls `GET /api/asset-families` and gets **403** for SuperAdmin in platform mode (no `tenant_id`). The catalog is global `AssetFamily` metadata. Tenant isolation must stay closed on the B2B endpoint.

## Repositories

- vlr-api
- vlr-web

Branch (both): `fix/superadmin-wizard-resources-403`

Merge order: **API first**.

## Confirmed decisions

1. Add `GET /api/admin/asset-families` with existing `PlatformAdmin` policy. Must succeed **without** `tenant_id`.
2. Reuse `IAssetFamilyService.ListCatalogAsync`. Return existing `AssetFamilyDetailResponse` (id, key, label, fields, sortOrder, isActive).
3. Do **not** change `GET /api/asset-families`, `GET /api/asset-families/active`, `RequirePermission`, or `PermissionAuthorizationHandler`.
4. WEB: only SuperAdmin wizard + tenant edit use the admin path. B2B `listActiveAssetFamilies` stays on `/api/asset-families/active`.
5. Preserve `assetFamilyKeys` and `POST /api/admin/tenants`. No migration. No `main`/PROD.

## API

- New controller under `Platform.Api/Modules/Admin/Controllers/`, route `api/admin/asset-families`.
- `[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]`.
- No `[RequirePermission]`. No tenant provider requirement.
- Tests (existing xUnit project). Prefer a **minimal isolated TestHost** (`Microsoft.AspNetCore.TestHost` 10.0.9) — do **not** host full `Program.cs` (Hangfire, connection string, WebApplicationFactory). Test auth scheme + `AddRolvixPolicies` + fake `IAssetFamilyService` + the new controller:
  - PlatformAdmin without tenant → **200**
  - authenticated tenant Admin (not on PlatformAdmin allowlist) → **403**
  - unauthenticated → **401**
  - 200 body is catalog DTO only (no `tenantId` / tenant-scoped fields)
- Optional service test: `ListCatalogAsync` returns active global families only (inactive excluded; `TenantAssetFamily` not mixed in).
- Update `ROADMAP.md` §2.7 + Histórico.

## WEB

- Add `listAdminAssetFamilyCatalog()` → `GET /api/admin/asset-families`, same Zod `assetFamilyListSchema`.
- Leave `listAssetFamilyCatalog()` on `/api/asset-families` (B2B catalog).
- `TenantOnboardingWizard` and `TenantEditForm` call the **admin** function.
- `AssetsPage` continues `listActiveAssetFamilies()`.
- Update wizard test mocks. Add a small service test that asserts the three URLs.
- Update `ROADMAP.md` Histórico.

## Do not

- Weaken tenant isolation or succeed all `RequirePermission` for PlatformAdmin without tenant.
- Touch Twilio, WhatsApp, Railway, `main`, migrations, PROD.
