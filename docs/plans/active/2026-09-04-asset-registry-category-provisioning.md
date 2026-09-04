# 2026-09-04 — Asset Registry category provisioning (no Inventory)

Status: **approved / implemented** (Wave: `PMOC_GENERIC_POLICY = FAIL FAST`; `OS_GENERIC_POLICY = ALLOW`)

The A/B fork is decided: PMOC + only non-provisioning families (`generic`, or omitted keys that default to generic) is **HTTP 400**. OS + generic-only remains **allowed**. Seed-on-create and seed-on-update (newly added families) are in code.

## Goal / Problem

`POST /api/rental-assets` and `POST /api/maintenance-plans` both require an `AssetCategory` row. Super-Admin family opt-in (`TenantAssetFamily`) does not always create one. Generic-only tenants get a family row and **zero** tipos. Rentals looks like an HTTP API and still cannot create a rentable. PMOC cannot start a plan. Inventory must not be auto-enabled; PMOC/OS must not gain registry CRUD.

## Locked decisions

| ID | Decision |
|---|---|
| PMOC_RESOURCE_POLICY | **P1** — no PMOC `AssetCategory` CRUD. Provision ≥1 usable tipo **when selected families support PMOC**. Do not invent fake generic PMOC tipos. |
| OS_RESOURCE_POLICY | **O2** — OS consumes existing Assets. No `POST /api/work-orders/assets`. No `inventory.*`. OS-only entitlement is allowed and **not** self-sufficient from an empty tenant. |
| PMOC_GENERIC_POLICY | **FAIL FAST** — if entitlements include `pmoc` and selected families have **no** seed-map family (`spaces` / `electrical` / `goods`), throw `ArgumentException` → HTTP 400 `{ "error": "PMOC requires at least one asset family with available resource types." }`. Applies on create **and** update. Validate after resolving family keys (omitted keys still default to `generic`). |
| OS_GENERIC_POLICY | **ALLOW** — OS + generic-only is 200. Do not apply the PMOC rule to OS. |
| Rentals | Self-service remains `POST/PUT /api/rental-assets` (`rentals.assets.*`). Rentals + generic-only is commercially valid (no 400, no fake generic category). |
| Schema | Do not add `FamilyId` to `AssetCategory` for this fix. No migration. No PROD rewrite. |

## Why generic-only has zero categories

`AssetCategory` is tenant-scoped only — **no `FamilyId`** (`AssetCategory.cs`). Example tipos are a **hand-curated seed map**, not a family FK:

| Family key | Seeded example |
|---|---|
| `spaces` | Quadra |
| `electrical` | Quadro elétrico |
| `goods` | Caçamba |
| `generic` | **none** (no map entry) |

Canonical map: `AssetCategoryExampleSeeds` next to `AssetFamilyKeys`. `CanProvisionExampleCategory` / `HasPmocProvisioningFamily` derive from that map. Shared seeder: `AssetCategoryExampleSeeder` (create + trial + newly added families on edit). Idempotent skip if `(TenantId, Name)` already exists (`IgnoreQueryFilters`).

`AdminTenantService.CreateAsync` and `CreateTenantHandler` trial seed use the same helper. `SyncTenantAssetFamiliesAsync` seeds example categories **only for newly added** family ids.

Omitted `assetFamilyKeys` → `ResolveFamiliesAsync` inserts `generic` (rolling-deploy default). WEB wizard now requires ≥1 family, so Super-Admin create usually sends keys; generic-only is still reachable if the admin picks only Genérico, or an old client omits the field.

Trial (`CreateTenantHandler`) always opts into spaces+electrical+goods+generic and seeds the three named tipos (generic still adds no extra row).

Wave 1 “auto-provision `TenantAssetFamily` when a requiring module is on and families are empty” is **only the generic fallback**, not gated on rentals/pmoc/os, and **does not seed a category**.

## Expectations per family (after understanding, not after a fake seed)

| Family | Example tipo | Rentals create (`CategoryId` required) | PMOC tipo (P1) | OS Asset |
|---|---|---|---|---|
| `spaces` | Quadra | **Yes** | Yes (any category works) | From Rentals or Inventory |
| `electrical` | Quadro elétrico | Yes if they rent it | **Yes** (family that supports PMOC) | From Inventory (typical) or a rentable |
| `goods` | Caçamba | **Yes** | Yes | From Rentals or Inventory |
| `generic` | — | **No** until a tipo exists | **No** — generic does not “support PMOC”; do not seed a fake PMOC tipo | **No** until an Asset exists (O2) |

**Rentals is not fully self-sufficient** for a tenant whose only family is `generic`. The HTTP surface exists; `EnsureCategoryExistsAsync` still 404s without a category. Club path: Super-Admin selects `spaces` (or `goods`).

**PMOC is usable without Inventory** when at least one of spaces/electrical/goods was selected at provision (or added later and seeded). PMOC + generic-only is rejected at the API.

**OS** stays a consumer (O2) and may be enabled with generic-only.

## Smallest canonical provisioning fix (no extra product fork)

1. One seed map (share trial + admin): `Spaces→Quadra`, `Electrical→Quadro elétrico`, `Goods→Caçamba`. **`Generic` has no entry.**
2. Call it from create **and** from family sync on **newly added** family ids.
3. Idempotent: skip if `(TenantId, Name)` already exists.
4. No new endpoints, no `inventory` insert, no PMOC/OS CRUD, no `AssetCategory.FamilyId`, no migration, no backfill of existing generic-only tenants (optional later, human-gated).

This closes: “edit tenant, add Electrical, PMOC still has zero tipos.”

This does **not** make generic-only Rentals or PMOC self-sufficient. That is intentional under P1.

## Visible behavior

- Create with `spaces` → Quadra exists; Rentals `POST /api/rental-assets` works; PMOC picker lists Quadra.
- Create with `generic` only → 0 categories (unchanged). Rentals/OS/Catalog remain valid; PMOC → 400.
- Update: add `electrical` → Quadro elétrico appears once; re-save does not duplicate.
- Omitted `assetFamilyKeys` → still `[generic]`, 0 categories (rolling deploy preserved). PMOC + omitted keys → 400.
- Catalog-only + generic → Catalog unaffected.

## Repositories

- vlr-api (implemented on `feat/asset-category-family-provisioning`)
- vlr-web: none required for seed/PMOC 400 (WEB shows `parseApiError`)

## Architecture / execution route

- rolvix-architect (this audit)
- api-implementer (this implementation)
- Fable on the **implementation** PR (persisted domain seed + PMOC combination rule). This docs handoff: `FABLE_MERGE_REVIEW_NOT_REQUIRED`

## Invariants

- Never auto-enable `inventory`.
- Never `asset-registry` in `tenant_modules`.
- Never PMOC/OS registry write APIs.
- `MaintenancePlan.AssetCategoryId` remains required.
- GQF on `AssetCategory.TenantId` unchanged.

## Decided combo policy

**PMOC** + only `generic` (or omitted keys → generic): **API 400**. Matches ADR 0004 “API owns combination rules.” WEB shows `parseApiError`.

**OS** + only `generic`: **allowed (200)** under O2. Do not apply the PMOC rule to OS.

Combinations: PMOC + spaces/electrical/goods → valid; PMOC + generic + electrical → valid; edit that leaves PMOC with only generic → 400.

## Do not

- Seed “Tipo PMOC” / “Equipamento genérico” for `generic`.
- `POST /api/work-orders/assets` or `POST /api/maintenance-plans/asset-categories`.
- Grant `inventory.*` because PMOC/OS is on.
- Backfill PROD tenants.
- Change rolling-deploy default away from `generic` in the same PR as the seed extract (separate, if ever).

## Test seams (implementation)

- create `spaces` → Quadra
- create `generic` only → 0 categories
- update add `electrical` → Quadro elétrico; idempotent re-add
- omitted keys → generic family, 0 categories
- `pmoc` + `generic` only → 400
- `os` + `generic` only → 200
- `rentals` + `generic` only → 200

## How to test (after code)

1. Super-Admin: Rentals + `spaces`, no Inventory → Quadra; `/configuracoes/recursos` creates a space.
2. Edit: add `electrical` → PMOC `/pmoc/novo` lists Quadro elétrico without Ativos.
3. Catalog + `generic` → no tipos; Catalog works.
4. PMOC + generic → 400. OS + generic → 200. Rentals + generic → 200.

## Documentation after code

`CONTEXT.md` (api + web mirror): generic family has no example tipo; PMOC needs a supporting family; Rentals needs ≥1 `AssetCategory`; OS consumes Assets (O2).
`ROADMAP.md` §4: check provisioning item.
