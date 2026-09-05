# 2026-09-04 — Module dependencies and Asset Registry

Status: approved (ADR [`0004-module-dependencies-asset-registry.md`](../../adr/0004-module-dependencies-asset-registry.md))

## Goal / Problem

Rolvix treated Inventory/Ativos as the silent base of every business module. Catalog & Orders showed that a sellable product is not an Asset. Rentals, PMOC and OS still need `Asset` / `AssetCategory` rows. The platform must separate **commercial entitlement** from **internal capability** without moving schema or auto-enabling Ativos.

## Repositories

- vlr-api (canonical ADR, metadata, gates)
- vlr-web (wizard, Explore, nav, rentals-without-Ativos UX)

Branch (both): `chore/asset-registry-module-dependencies` (this docs wave). Implementation waves use their own `feat/` branches from `develop` after this merges.

Merge order: **API first** for any wave that adds metadata or gates; **WEB first** is invalid if it hardcodes a second graph.

## Confirmed decisions (do not reopen)

| ID | Decision |
|---|---|
| HD-1 | Inventory is commercially optional. |
| HD-2 | Rentals requires Asset Registry, not the Ativos UX. |
| HD-3 | Orders belong to Catalog (`orders`/`pedidos` → `catalog`). |
| HD-4 | PMOC requires Asset Registry, not the Ativos module. |
| HD-5 | Capabilities auto-provision. Commercial module dependencies API-reject. Never auto-enable `inventory`. |
| HD-6 | `maintenance` is legacy/undefined; block new usage until redefined. |
| HD-7 | Trial Catalog is a separate product decision; out of this change. |
| SCH-1 | Do not move Asset tables to Core. Do not create a second registry store. |

## Canonical graph

```
provides:
  inventory → asset-registry          # same assets.* tables + Ativos UX

requires (capability, not entitlement):
  rentals → asset-registry
  pmoc    → asset-registry
  os      → asset-registry
  catalog → (none)
  inventory → (none)

commercial containment:
  orders ⊂ catalog                    # alias only; no tenant_modules row

legacy:
  maintenance                         # ignore existing rows; reject new activation
```

`core.tenant_modules` stores **only** commercial keys: `inventory`, `pmoc`, `os`, `rentals`, `catalog`. Never `asset-registry`. Never new `maintenance`.

## Authoritative layer

**API** owns module metadata and combination rules (`NormalizeModules` / tenant create-update). WEB consumes a read model (dedicated GET or payload already returned on admin tenant DTOs). Do not encode a second graph in `MODULE_KEYS` checkboxes.

## Auto-provision vs reject

When `rentals`, `pmoc`, or `os` is activated and the tenant has no `TenantAssetFamily`:

- Insert default family opt-in (existing rolling-deploy default: `generic`, or the families Super-Admin already sent on the wizard).
- Do **not** insert `TenantModule(inventory)`.

When a payload commercially requires another commercial module that is missing (none today except treating `orders` as its own module — already normalized):

- **400** `{ "error": string }`. Do not add the missing module.

Deactivate `inventory` while dependents stay active: **200**, remove the inventory row, keep `assets.*` data, hide Ativos.

Deactivate the last module that required Asset Registry: keep registry data (same as Catalog disable: data preserved).

## Waves (no code in the ADR/docs PR except glossary/roadmap)

### Wave 0 — this PR (docs only)

- ADR 0004.
- `CONTEXT.md` (api canonical + web mirror): Asset Registry, TenantModule semantics, architecture tree, Catalog independence.
- ROADMAPs: checklist + Histórico.
- Product rules: stop listing `maintenance` as a live module; list `catalog`; point at the ADR.

### Wave 1 — API metadata + combination rules

Owner: `api-implementer`. Tests in `tests/Platform.Api.Tests`.

- Centralize metadata next to `PlatformModules` (provides / requiredCapabilities / commercial flag / aliases). Keep it boring (static data), not a plugin framework.
- `NormalizeModules` / `SyncTenantModules`:
  - Reject unknown keys (including **new** `maintenance`).
  - Strip or 400 `maintenance` on create/update (prefer **400** so Super-Admin sees the contract). Existing DB rows: do not delete in this wave; PermissionResolver and dashboard must not grow new `maintenance` behavior.
  - Never add `inventory` because `rentals`/`pmoc`/`os` is present.
  - Auto-provision `TenantAssetFamily` when a requiring module is on and families are empty.
- Expose metadata to Super-Admin/WEB (`GET /api/admin/modules` or equivalent). Include human-facing `requires` labels (Ativos vs recursos) — WEB i18n may override copy; API must not return the string `Asset Registry` as customer copy.
- `CreateTenantHandler.TrialModules`: stop adding `maintenance`. Do **not** add `catalog` (HD-7).
- Tests: catalog-only tenant accepted; rentals without inventory accepted; inventory-only accepted; `maintenance` on write rejected; orders alias → catalog; no silent inventory insert.

No EF migration. No PROD data rewrite.

### Wave 2 — Authorization: registry without Ativos

Owner: `api-implementer`. High risk (RBAC). Merge Risk Gate **Fable required**.

Today `PermissionResolver` drops `inventory.*` when `inventory` is off, which is correct for Ativos. Dependent modules must still create/read registry rows:

- **Rentals:** creating a Rentable already goes through `Asset` + `RentalAsset`. Authorize those writes with `rentals.*` (or a narrow new key under `rentals` if existing keys cannot cover create-resource). Do not require `inventory.assets.write`.
- **PMOC:** `AssetCategory` read/write needed for plans: allow via `pmoc.plans.write` / read when `pmoc` is active.
- **OS:** `WorkOrder.AssetId` required — pick/list assets (and create if the product path needs it) via `os.work_orders.*` when `os` is active.
- **Inventory entitled:** `inventory.*` remains the full Ativos surface (all families, bulk, deletion schedule, non-rentable electrical, etc.).
- **Do not** grant `inventory.*` implicitly when inventory is off.

Audit `AssetController` / `AssetCategoriesController` / `AssetService`: split “full inventory API” vs “registry access for an active dependent module”. Fail closed if neither inventory nor a requiring module is active.

Tests: rentals-on/inventory-off can create a rentable Asset; inventory-off cannot hit full Ativos list if that remains inventory-gated; catalog-only cannot write Assets.

### Wave 3 — WEB Super-Admin + Explore + B2B nav

Owner: `web-implementer` (UX copy/layout: `ui-implementer` only if Explore/wizard visuals are the center).

- Wizard/edit: independent Inventory checkbox; selecting Rentals/PMOC/OS does **not** force Inventory.
- Consume API module metadata (no hardcoded “must pick Ativos”).
- Explore módulos: commercial requires in tenant language (“Needs cadastro de recursos” / “Optional: Ativos for full inventory”). Never “Asset Registry”. Catalog: no asset dependency line.
- Sidebar: `/ativos` only if `inventory` active. Rentals/PMOC/OS items from their own modules.
- Remove/cancel ROADMAP idea “always activate inventory on tenant create”.
- `MODULE_KEYS` stays commercial-only; still no Maintenance.

### Wave 4 — Rentals/PMOC/OS UX without Ativos

Owner: `web-implementer` + `ui-implementer` as needed.

When `rentals` is on and `inventory` is off, the tenant must still create courts/resources (existing Asset wizard Operação/Preços, or a Rentals-scoped entry). Same for PMOC tipos and OS asset picker.

Do not duplicate the domain model. Reuse Asset forms behind Rentals/PMOC/OS routes when inventory nav is hidden.

### Wave 5 — Runtime module gates (ROADMAP §4)

Owner: `api-implementer`.

Generic inactive-module 403, using the same metadata. Catalog B2C gate already exists; extend the pattern. Rentals B2C should 403 when `rentals` is off (do not rely on 404). Independent of Asset Registry.

## Migrations

| Kind | This architecture |
|---|---|
| Schema | None |
| Data | None required |
| Entitlement | None (do not backfill `inventory`) |
| Permission catalog | None unless Wave 2 adds a narrow `rentals.resources.write` (prefer reuse first) |

## Do not

- Auto-enable `inventory`.
- Move `assets.assets` to `core`.
- Add `asset-registry` to `tenant_modules`.
- Link `CatalogProduct` to `Asset` in these waves.
- Split Orders into its own module.
- Activate `catalog` on trials as part of this work.
- Expand `maintenance` (dashboard, permissions, nav, wizard).
- Implement in the docs PR.

## How to test (after implementation waves)

1. Super-Admin: create tenant with only Catalog → 201; no `inventory` row; no Ativos nav; Catalog works.
2. Create tenant with only Rentals → 201; no `inventory` row; families provisioned; can create a rentable; `/ativos` hidden; B2C agenda works.
3. Add Inventory later → Ativos nav appears; same Asset rows visible.
4. Remove Inventory, keep Rentals → Ativos disappears; rentals still work.
5. POST tenant with `maintenance` → 400.
6. Explore: PMOC shows a resource requirement; Catalog does not.
7. Catalog-only B2C cannot call asset APIs.

## Follow-ups (not this spec)

- Trial Catalog (HD-7).
- Optional CatalogProduct ↔ Asset integration.
- Redefine or delete `maintenance`.
- Apply unrelated pending Railway migrations.
