# Internal resource planning: AssetAllocation on Asset, decoupled from rentals and catalog

Resource planning (`resource-planning`) was built in Phase 0 on top of Rentals and Catalog: an allocation is a `CatalogOrderItemResourceAllocation` realized through an `InternalUse` reservation of a `RentalAsset`. That makes the module unusable for a resource that is not rentable (for example an internal 3D printer) and ties its activation to two other commercial modules.

**Status:** accepted (2026-11-19). Implementation spec: [`docs/plans/active/2026-11-19-internal-resource-planning.md`](../plans/active/2026-11-19-internal-resource-planning.md). Slice API-1 (this ADR, the descriptor change and the disable guards) ships first; the tables and endpoints follow in later slices.

**Human decisions (owner, 2026-11-19):** dedicated allocation record on `Asset` with a mirror reservation only for rentable assets; capability only (no hard requirement of inventory, rentals or catalog); working hours reject rather than warn, default policy `StartWithinHours`; manual allocations that were never started expire after their end, started ones block until released, order-linked ones keep today's behavior; a technician may create, start, finish and release, cancelling someone else's allocation is admin-only; OS link is manual in phase 1; rentable assets keep the rental schedule limits in phase 1; maximum duration 30 days on planning-only assets; plannable is opt-in per asset.

## Decision

1. **`resource-planning` requires the Asset Registry capability only** (ADR 0004). Its descriptor declares `RequiredCapabilities: [asset-registry]` and no `RequiredModules`. It can be sold alone, with `inventory`, or with any mix of `catalog`, `rentals` and `os`. The capability is already provisioned for every tenant (the default `TenantAssetFamily`), so no module-specific provisioning hook is needed.
2. **`AssetAllocation` on `Asset` is the planning record** (schema `planning`, introduced in slices API-2 and API-3). An asset has a planning profile (plannable opt-in, hours policy, weekly working hours). An allocation is a window on one asset with a kind (`Use` or `Unavailable`), a status (the existing `AllocationStatus`), and an optional link to a catalog order item or a work order.
3. **One calendar authority per asset.** An asset with an active `RentalAsset` and `rentals` active keeps the rental schedule as the authority: an allocation on it also writes the same `InternalUse` reservation as today through the occupancy port, and stores the link (`ReservationItemId`). Any other asset is occupied by the `AssetAllocation` alone. The reservation is a mirror that exists only where rentals needs it.
4. **Catalog and OS are optional integrations**, detected at runtime and reached through contracts owned by planning (an `IAllocationReferenceResolver` implemented by Catalog and WorkOrders). The dependency points inward: planning never references the Catalog or WorkOrders namespaces.
5. **Business guards replace the lost static module dependency.** Inside the tenant-modules advisory lock of `AdminTenantService.UpdateAsync`, each with `IgnoreQueryFilters()` and an explicit tenant id:
   - `resource-planning` cannot be disabled while any allocation is unreleased (Scheduled, Running, Cleaning);
   - `rentals` cannot be disabled while `resource-planning` stays active and an unreleased allocation is backed by a reservation;
   - `catalog` cannot be disabled while `resource-planning` stays active and an unreleased allocation is linked to a catalog order item;
   - `os` may be disabled; the allocation keeps its label and the link stops being navigable.

   Until `AssetAllocation` exists (slice API-3) the guards count the unreleased `CatalogOrderItemResourceAllocation` rows, which are exactly the allocations that exist (each is backed by a reservation and an order item). When `AssetAllocation` lands the guards also count it, and the old count stays until the backfill is proven.
6. **Gate convention.** The module-gate convention test no longer relies on `RequiredModules` to accept an action-level `[RequireActiveModule(resource-planning)]` inside a Catalog controller. The rule is explicit: an action-level gate may add a second module while the class-level gate still declares the expected module; both filters run, so the action needs both.
7. **Admin catalog exposes `RequiredModules`** (`GET /api/admin/modules`), so the web wizard stops hardcoding dependencies.
8. **Permissions** for the module use the prefix `resource-planning.` (`ModuleKeyOf` maps a key to its module by the prefix before the first dot).

ADR 0004 decision 6 says "Today the only commercial containment is Orders ⊂ Catalog". That stays true: this ADR does not add a commercial containment for resource planning.

## Considered options

- **Hidden `RentalAsset` per plannable asset (keep `Reservation` InternalUse for everything):** rejected. Makes rentals tables and services mandatory infrastructure for a module that is "off", forces every rentals reader (grid, portal, booking map, pricing readiness, dashboard) to hide internal-only resources, and fights free time ranges and jobs over 24 h.
- **Rentals reads planning tables for availability:** rejected for phase 1. Changes the customer-booking hot path and its tests for a case (rentable assets with long jobs) that is out of scope; the mirror reservation keeps rentals untouched.
- **Polymorphic link without FK (type + id columns):** rejected. Loses tenant-qualified composite FKs and referential integrity; explicit nullable FK columns with a check "at most one link" are used instead.
- **Postgres exclusion constraint for overlap in phase 1:** rejected. It needs `btree_gist` and cannot express capacity above 1; serialization is by a row lock on the asset (same technique as the rental-asset locks).
- **Keep `RequiredModules: [catalog, rentals]`:** rejected by the owner (decision 2).

## Consequences

- A tenant may sell resource planning without rentals or catalog; assets are then created through another module that provides the registry (ADR 0004 consequences apply: the web wizard shows a hint).
- Dual status on rental-backed and order-linked allocations (`AssetAllocation` and the catalog allocation row) until a later contract step removes the catalog columns.
- New lock order for planning writes: tenant-modules advisory lock, order or work-order row, asset rows ascending by id, then rental-asset rows ascending (inside the bridge). Customer booking only takes the rental-asset lock, so there is no cycle.
- New permission prefix `resource-planning.` (hyphen): parsers must accept it (checked in slice API-2).
- Existing Phase 0 tenants see no behavior change in slice API-1: the order flow still requires rentals at runtime.
- Slice API-2 creates the three `planning` tables in one additive migration (`AddAssetPlanning`). The tenant-qualified composite FKs need a `(tenant_id, id)` unique key on `assets.assets` and `os.work_orders`; the migration adds both (`id` is already the primary key, so they cannot fail on existing data). Profile and working hours cascade with the asset (pure configuration); allocations restrict (history).
- Slice API-3 writes `AssetAllocation` for planning-only assets behind `IAssetOccupancyService` (the rentals bridge plugs into the same port in API-4). The row lock is `SELECT ... FOR UPDATE` on `assets.assets`, taken by allocation create, edit and usage and by the planning-profile PUT, so a working-hours change never interleaves with an allocation validated against the old hours. The concurrency token exposed to clients is the base64 `row_version` (`version` / `expectedVersion`), as on catalog orders. A manual allocation that was never started expires at read time (no job); `Unavailable` blocks are not subject to working hours.
