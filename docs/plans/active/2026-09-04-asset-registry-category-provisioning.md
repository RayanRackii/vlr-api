# 2026-09-04 — Asset Registry category provisioning (no Inventory)

Status: blocked (`USER_DECISION_REQUIRED` — combo `pmoc`/`os` + only `generic`)

The **seed-on-update / idempotent example categories** slice is otherwise approved (P1/O2 already locked). Do not implement the 400-reject rule until the fork below is decided.

## Goal / Problem

`POST /api/rental-assets` and `POST /api/maintenance-plans` both require an `AssetCategory` row. Super-Admin family opt-in (`TenantAssetFamily`) does not always create one. Generic-only tenants get a family row and **zero** tipos. Rentals looks like an HTTP API and still cannot create a rentable. PMOC cannot start a plan. Inventory must not be auto-enabled; PMOC/OS must not gain registry CRUD.

## Locked decisions

| ID | Decision |
|---|---|
| PMOC_RESOURCE_POLICY | **P1** — no PMOC `AssetCategory` CRUD. Provision ≥1 usable tipo **when selected families support PMOC**. Do not invent fake generic PMOC tipos. |
| OS_RESOURCE_POLICY | **O2** — OS consumes existing Assets. No `POST /api/work-orders/assets`. No `inventory.*`. OS-only entitlement is allowed and **not** self-sufficient from an empty tenant. |
| Rentals | Self-service remains `POST/PUT /api/rental-assets` (`rentals.assets.*`). |
| Schema | Do not add `FamilyId` to `AssetCategory` for this fix. No migration. No PROD rewrite. |

## Why generic-only has zero categories

`AssetCategory` is tenant-scoped only — **no `FamilyId`** (`AssetCategory.cs`). Example tipos are a **hand-curated seed map**, not a family FK:

| Family key | Seeded example (create only today) |
|---|---|
| `spaces` | Quadra |
| `electrical` | Quadro elétrico |
| `goods` | Caçamba |
| `generic` | **none** |

`AdminTenantService.SeedExampleCategories` (`:602-625`) has no `Generic` entry. It runs **only on create** (`CreateAsync` `:135`). `SyncTenantAssetFamiliesAsync` (`:537-564`) adds/removes `TenantAssetFamily` on edit and **never seeds**.

Omitted `assetFamilyKeys` → `ResolveFamilyIdsAsync` inserts `generic` (rolling-deploy default, `:576-579`). WEB wizard now requires ≥1 family, so Super-Admin create usually sends keys; generic-only is still reachable if the admin picks only Genérico, or an old client omits the field.

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

**PMOC is usable without Inventory** when at least one of spaces/electrical/goods was selected at provision (or added later **and** seeded — today add-on-edit does not seed).

**OS** stays a consumer (O2).

## Smallest canonical provisioning fix (no extra product fork)

1. One seed map (share trial + admin): `Spaces→Quadra`, `Electrical→Quadro elétrico`, `Goods→Caçamba`. **`Generic` has no entry.**
2. Call it from create **and** from family sync on **newly added** family ids.
3. Idempotent: skip if `(TenantId, Name)` already exists.
4. No new endpoints, no `inventory` insert, no PMOC/OS CRUD, no `AssetCategory.FamilyId`, no migration, no backfill of existing generic-only tenants (optional later, human-gated).

This closes: “edit tenant, add Electrical, PMOC still has zero tipos.”

This does **not** make generic-only Rentals or PMOC self-sufficient. That is intentional under P1.

## Visible behavior (after implementation of the smallest fix)

- Create with `spaces` → Quadra exists; Rentals `POST /api/rental-assets` works; PMOC picker lists Quadra.
- Create with `generic` only → 0 categories (unchanged).
- Update: add `electrical` → Quadro elétrico appears once; re-add does not duplicate.
- Omitted `assetFamilyKeys` → still `[generic]`, 0 categories (rolling deploy preserved).
- Catalog-only + generic → Catalog unaffected.

## Repositories

- vlr-api (implementation after this spec is unblocked / seed slice approved)
- vlr-web: none required for the seed slice. Combo error i18n only if Option A is chosen.

## Architecture / execution route

- rolvix-architect (this audit)
- api-implementer after the fork is decided **or** after parent splits “seed-on-update” as its own PR with A/B deferred
- Fable on the **implementation** PR (persisted domain seed). This docs handoff: `FABLE_MERGE_REVIEW_NOT_REQUIRED`

## Invariants

- Never auto-enable `inventory`.
- Never `asset-registry` in `tenant_modules`.
- Never PMOC/OS registry write APIs.
- `MaintenancePlan.AssetCategoryId` remains required.
- GQF on `AssetCategory.TenantId` unchanged.

## USER_DECISION_REQUIRED

When Super-Admin activates `pmoc` and/or `os` and selects **only** `generic`:

- **Option A — API 400** `{ "error": string }` if no PMOC-supporting family (`spaces` / `electrical` / `goods`) is selected. Fail-fast; matches ADR 0004 “API owns combination rules.” WEB shows `parseApiError`. **Recommended.**
- **Option B — API 201/200**, tenant stays generic-only with 0 categories. PMOC/OS empty states already explain Super-Admin provisioning (Wave 4). Risk: silent unusable PMOC if nobody reads the empty state.

OS-only + generic is already accepted as “not self-sufficient” (O2). Option A would also 400 OS+generic-only, which is stricter than O2’s “allowed but not guaranteed.” If A is chosen, **prefer applying the reject only when `pmoc` is on**, and leave OS+generic as 200 (O2). Confirm that nuance.

**Question:** Reject `pmoc` + only-`generic` at the API (A), or accept and rely on WEB empty states (B)? If A: should `os` + only-`generic` also 400, or stay allowed under O2?

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
- (if A) `pmoc` + `generic` only → 400

## How to test (after code)

1. Super-Admin: Rentals + `spaces`, no Inventory → Quadra; `/configuracoes/recursos` creates a space.
2. Edit: add `electrical` → PMOC `/pmoc/novo` lists Quadro elétrico without Ativos.
3. Catalog + `generic` → no tipos; Catalog works.
4. Combo per A/B.

## Documentation after code

`CONTEXT.md` (api + web mirror): generic family has no example tipo; PMOC needs a supporting family; Rentals needs ≥1 `AssetCategory`; OS consumes Assets (O2).
`ROADMAP.md` §4: check provisioning item.
