# Context pack index

Read this file to choose a pack. Do not load all packs.

## Available

### `rentals`

File: [`active/rentals.md`](./active/rentals.md)

Use for: Reservation, Rentable, Slot, SlotGrid, OpenHours, schedule, pricing, booking conflicts, Layout (rentals picker), B2C self-cancel. Wave 1 **PROD_COMPLETE** (2026-09-06). B2C self-cancel **CLOSED_DEV** (2026-09-07, not PROD). Rentals WhatsApp + 24h reminder **in code** (PROD external off). Tenant WhatsApp on/off: unified `PUT /api/notifications/channel-configs`.

### `catalog`

File: [`active/catalog.md`](./active/catalog.md)

Use for: CatalogProduct, CatalogOrder, ProductRequest, catalog files/storage, catalog notifications, B2C catalog/cart. Tenant channel config for Catalog+Rentals is `GET/PUT /api/notifications/channel-configs` (`core.notifications.*`); catalog wrappers remain for delivery history.

### `pmoc-os`

File: [`active/pmoc-os.md`](./active/pmoc-os.md)

Use for: GlobalMaintenanceTemplate, MaintenancePlan, PlanTask, PmocEngineJob, WorkOrder, WorkOrderTask, Biblioteca Rolvix, Gerar OS. Phase 1 spec: [`docs/plans/active/2026-09-18-pmoc-os-phase1.md`](../plans/active/2026-09-18-pmoc-os-phase1.md) (approved). Slices 1–2 are on `develop`; Slice 3 (template library clone) is on `feat/pmoc-os-phase1-template-library`.

## Planned / on-demand

### `platform-core`

Use for: Tenant, Unit, User, Role, Permission, module foundation.

### `authentication`

Use for: B2B Supabase Auth, B2C Customer JWT, invites.

### `assets`

Use for: Asset, AssetFamily, JSONB attributes, categories.
