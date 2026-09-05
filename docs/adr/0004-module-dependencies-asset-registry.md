# Module dependencies, core capabilities and Asset Registry

Commercial modules are entitlements in `core.tenant_modules`. Technical need for physical/resource identity is a separate internal capability (**Asset Registry**), provided by the existing `assets.*` tables — not by auto-enabling the Inventory (Ativos) module. Catalog (including Orders) does not use that capability. Asset tables stay where they are; there is no second registry store and no move to Core.

**Status:** accepted (2026-09-04)

**Human decisions:** Inventory is commercially optional. Rentals, PMOC and OS require Asset Registry, not the Ativos UX. Orders belong to Catalog. Invalid combinations: auto-provision **capabilities**; API **reject** missing **commercial** dependencies; never insert `inventory` because a dependent module was selected. `maintenance` is legacy — block new usage until redefined. Trial Catalog is out of scope.

## Decision

1. **`tenant_modules` = commercial entitlement** (what the tenant bought / may see). It is not the dependency graph.
2. **Asset Registry** = internal capability: persist and reference `Asset`, `AssetCategory`, `AssetFamily` for a tenant. Not a `tenant_modules` row. Not shown as “Asset Registry” in product UI.
3. **Inventory (`inventory`)** = optional commercial module: Ativos navigation, `inventory.*` permissions, full asset-management UX.
4. **Capability graph (authoritative on the API):**
   - `inventory` provides Asset Registry (same tables; plus the commercial UX).
   - `rentals`, `pmoc`, `os` require Asset Registry.
   - `catalog` requires nothing from the registry. `orders` / `pedidos` are aliases of `catalog`, not a second module.
   - `maintenance` is undefined; do not activate it on new tenants or in new code.
5. **Provisioning:** activating `rentals` / `pmoc` / `os` auto-provisions the capability (e.g. default `TenantAssetFamily` when none exist). It does **not** write `inventory` into `tenant_modules`.
6. **Commercial dependencies:** if module A commercially requires module B, the API rejects the write when B is absent. Do not auto-enable B. Today the only commercial containment is Orders ⊂ Catalog (already normalized).
7. **Deactivate Inventory** while Rentals/PMOC/OS remain active: **allowed**. Hide Ativos; keep registry rows; dependent modules keep working through their own UX.
8. **Schema:** do not move `assets.*` to Core; do not create another Asset table.

## Considered options

- **A — Inventory mandatory for every module:** rejected. Catalog products (coffee, shirt, service) are not Assets.
- **B — Every module fully independent:** rejected. Rentals (`RentalAsset.AssetId`), OS (`WorkOrder.AssetId`) and PMOC (`MaintenancePlan.AssetCategoryId`) already depend on registry rows.
- **C — Capabilities vs commercial modules + explicit graph:** accepted.
- **Move Asset into Core / second store:** rejected. Unnecessary identity migration; Catalog does not need it.

## Consequences

- Super-Admin may sell Catalog-only, Rentals without Ativos, or Ativos as an add-on.
- WEB wizard/Explore must consume API metadata; they must not auto-tick Inventory when Rentals is selected.
- Product copy talks about recursos / Ativos, never “Asset Registry”.
- Permission filtering stays: `inventory.*` only if `inventory` is entitled. Dependent modules need authorized paths to create/read the Assets they own (rentable resources, PMOC categories, work-order assets) without that entitlement.
- Existing `maintenance` rows may remain; new activations are rejected. No entitlement backfill of `inventory`.
- Implementation: [`docs/plans/active/2026-09-04-module-dependencies-asset-registry.md`](../plans/active/2026-09-04-module-dependencies-asset-registry.md).
