# Internal resource planning (resource-planning without rentals)

## Resumo para o dono (pt-BR)

- Hoje só dá para planejar o uso de um recurso se ele for "alugável" e o módulo de Reservas estiver ligado. A proposta separa as duas coisas: o **Planejamento de recursos** passa a funcionar sozinho, em cima do cadastro de recursos (Ativos). Reservas vira opcional.
- Qualquer recurso do cadastro pode ser marcado como "planejável", sem virar alugável e sem aparecer no portal do cliente. Exemplo: a impressora 3D.
- Cada recurso planejável ganha um **horário de funcionamento** (dias e horas) e uma regra simples: livre 24 h, "só pode começar dentro do horário" (a máquina termina sozinha de madrugada) ou "tudo dentro do horário".
- Nova tela **Planejamento** dentro de Agendas: linha do tempo por recurso, por dia e por semana, com o que está ocupado e o que está livre.
- O admin ou o técnico cria uma **alocação**: recurso, início, fim, motivo. Pode ligar a um pedido do catálogo, a uma OS, ou deixar só texto livre. Também serve para bloquear o recurso (manutenção, parada).
- O sistema **avisa conflito** na hora: não deixa duas alocações no mesmo recurso e no mesmo horário.
- As ações de uso continuam as mesmas de hoje: iniciar, concluir trabalho, liberar, cancelar.
- O fluxo "aprovar pedido e alocar" passa a aceitar recursos não alugáveis. Trabalhos de mais de 24 h passam a caber (limite proposto: 30 dias) nesses recursos.
- Quem já usa Planejamento com Reservas não perde nada: as alocações atuais continuam bloqueando a agenda de reservas como hoje e aparecem também na nova tela.
- Entrega em fatias pequenas: 6 PRs na API e 5 na web, cada um testado e publicável sozinho. Duas alterações de banco, só de adição.
- Fica para depois: "qualquer impressora que aceite TPU" (escolha por tipo ou capacidade), vários recursos por produto, reagendar arrastando, sugestão automática de horário, relatório de utilização.
- **Decisões que preciso de você** (detalhes na seção 9): (1) criar a alocação como registro próprio, e não como "reserva interna" (recomendado); (2) o módulo exige só o cadastro de recursos, não o módulo Ativos pago (recomendado, segue a regra já adotada); (3) regra padrão de horário; (4) alocação atrasada continua bloqueando até alguém liberar, ou expira sozinha; (5) o técnico pode criar alocações ou só iniciar e liberar; (6) OS: ligação manual agora, automática depois.

---

Repositories: `vlr-api` and `vlr-web` (cross-repo spec; final home `vlr-api/docs/plans/active/2026-11-19-internal-resource-planning.md`).
Status: **ready to implement** (owner answered section 9 on 2026-11-19) (AGENTS.md Human Decision Gate: domain model, module graph, FE/BE contract, persisted data).
All line references are to `origin/develop` as fetched on 2026-11-19.

## 1. Current state (verified)

| Fact | Where |
|---|---|
| `resource-planning` descriptor: no capabilities, `RequiredModules: [catalog, rentals]` | `Core/Platform.Core.Domain/Constants/PlatformModuleCatalog.cs:76-83` |
| Static dependency check | `PlatformModuleCatalog.cs:178-206` (`ValidateModuleDependencies`) |
| Disable guard, counts unreleased `CatalogOrderItemResourceAllocation` | `Platform.Api/Modules/Admin/Services/AdminTenantService.cs:291-315` |
| Order allocation runtime module check `[catalog, rentals, resource-planning]` | `Platform.Api/Modules/Catalog/Services/CatalogOrderAllocationService.cs:69-74, 152` |
| Requirement must have `RentalAssetId`; one requirement per order | `CatalogOrderAllocationService.cs:117-123, 160-163` |
| Occupancy = `InternalUse` `Reservation` + one `ReservationItem(RentalAssetId)` | `Platform.Api/Modules/Rentals/Services/ResourceOccupancyService.cs:73-158`; contract `Rentals/Contracts/IResourceOccupancyService.cs:20-110` |
| Conflict = overlap over all blocking reservations of the rental asset | `ResourceOccupancyService.cs:298-327`; overdue unreleased InternalUse blocks forever: `Rentals/Services/ReservationOccupancy.cs:14-27` |
| Work window must be an offerable booking window, single day | `Rentals/Services/ResourceWorkWindowValidator.cs:43-75` |
| 24 h cap | `Catalog/Services/CatalogPlanningWindow.cs:6-23` |
| Allocation row: `ReservationItemId` and `RentalAssetId` required, unique per reservation item | `Core/Platform.Core.Domain/Entities/CatalogOrderItemResourceAllocation.cs:13-63`; config `.../Configurations/CatalogOrderItemResourceAllocationConfiguration.cs:26-27, 47-48, 72-99` |
| Requirements hold `RentalAssetId?` / `AssetCategoryId?` | `Entities/CatalogProductResourceRequirement.cs:18-20`, `Entities/CatalogOrderItemResourceRequirement.cs:19-21` |
| Usage actions lock the rental asset row | `Catalog/Services/CatalogOrderAllocationUsageService.cs:84-124` |
| Resource agenda is rentals-gated and keyed by rental asset; pads one day because of the 24 h cap | `Rentals/Controllers/ResourceAgendaController.cs:16-46`; `Catalog/Services/CatalogAgendaService.cs:84-132, 160-193` |
| Catalog endpoints gated by `resource-planning` at action level | `Catalog/Controllers/CatalogOrdersController.cs:83-85, 112-115` |
| Gate convention test allows that override only because RP lists catalog in `RequiredModules` | `tests/Platform.Api.Tests/Authorization/ModuleGateConventionTests.cs:99-118`; namespace map `:19-26` |
| Permission module = key prefix before the first dot | `Core/Platform.Core.Domain/Constants/PermissionCatalog.cs:141-145` |
| Asset has no schedule of its own; `IsRentable` means "bookable via Rentals" | `Entities/Asset.cs:29-30, 46`; schedule fields live on `Entities/RentalAsset.cs:28-38` |
| `WorkOrder` has `AssetId` and a `ScheduledDate` (date only) | `Entities/WorkOrder.cs:10, 21` |
| Web: agenda feature gated `["rentals","catalog"]`; no nav entry for resource-planning | `vlr-web/src/features/agenda/manifest.tsx:8-27`; `src/components/layout/navigation.ts:202-210` |
| Web: product requirement picker lists rental assets | `src/features/catalog/components/CatalogProductResourceRequirements.tsx:20` |
| Web: order page shows the agenda link only with `rentals.schedule.read` | `src/features/catalog/pages/CatalogOrderDetailPage.tsx:378` |
| Web: agenda views `overview / resources / product` | `src/features/agenda/AgendasPage.tsx:30` |

ADR 0004 already says dependent modules need the **Asset Registry capability**, not the Inventory entitlement (`docs/adr/0004-module-dependencies-asset-registry.md`, decisions 4-7). This spec follows it.

## 2. Decision: dedicated `AssetAllocation`, with a rentals bridge

### Options weighed

| | A. Keep `Reservation` (InternalUse) for everything | B. New `AssetAllocation` targeting `Asset` (recommended) |
|---|---|---|
| How a non-rentable asset is scheduled | A hidden `RentalAsset` row per plannable asset | A planning profile row on the asset; no rental row |
| Coupling to rentals | Total. Rentals tables and services become mandatory infrastructure while the module is "off" | One optional bridge call for assets that are also rentable |
| Availability engine reuse | Full reuse, but it enforces booking rules: allowed durations, slot grid, single day (`ResourceWorkWindowValidator.cs:51-56`) | New, small engine: overlap query + working hours. No slot grid needed for internal use |
| Risk to existing rentals readers | High. Every rentals list, portal, booking map, pricing readiness and dashboard must learn to hide internal-only resources. The repo already needed `InternalUseExclusionTests` for the reservation side | Low. Rentals code is unchanged |
| Free time ranges, jobs over 24 h | Fights the model | Natural |
| Migration risk | Low DDL, high behavior risk | Two additive migrations, one insert-only backfill |
| Effort | S to M, then a long tail of exclusions | M to L, contained |

**Recommendation: B.** `Reservation` is a customer-booking aggregate (customer check constraint, pricing, deposit, reminders); `InternalUse` was a Phase 0 shortcut. A non-rentable asset must not get a rental row just to own a calendar.

### How existing rentals tenants keep working (the bridge)

Rule: **an asset has exactly one calendar authority.**

- Asset with an active `RentalAsset` and `rentals` active: the rental schedule stays the authority. An allocation on it writes an `AssetAllocation` **and**, through the existing `IResourceOccupancyService`, the same `InternalUse` reservation as today. `AssetAllocation.ReservationItemId` stores the link. The rentals grid, the portal and all reservation readers see exactly what they see today. Window rules stay as today (offerable window, single day, 24 h).
- Any other asset: `AssetAllocation` alone holds the occupancy. Rules come from the asset's planning profile.

So `AssetAllocation` is always the planning record (one source for the Planejamento agenda and the lifecycle), and the reservation is a mirror that exists only where rentals needs it.

`CatalogOrderItemResourceAllocation` stays as the catalog-side link (requirement, sequence, planning revision, current flag) and gains `AssetAllocationId`. Existing rows are backfilled with a matching `AssetAllocation` (insert-only), so new code has one path. Its `Status` and `Actual*` columns keep being written in the same transaction by the usage service during phase 1; dropping them is a later contract step, out of scope.

## 3. Module dependency change

### 3.1 Descriptor

`PlatformModuleCatalog.cs:76-83` becomes:

```
new(PlatformModules.ResourcePlanning, IsCommercial: true, IsLegacy: false,
    Provides: [], RequiredCapabilities: [AssetRegistryCapability], Aliases: ["planejamento"])
```

No `RequiredModules`. `catalog`, `os` and `rentals` become optional integrations, detected at runtime.

### 3.2 `ValidateModuleDependencies`

The method (`:178-206`) needs no logic change: it skips descriptors with no `RequiredModules`. Update its doc comment and the comment in `AdminTenantService.cs:291-297`. Tests that assert "resource-planning requires catalog and rentals" flip to "activates with inventory only" and "activates alone, capability auto-provisioned".

Capability provisioning: ADR 0004 decision 5 says activating a registry-dependent module provisions a default `TenantAssetFamily`. I did not find a hook keyed on `RequiredCapabilities` in `Platform.Api` (only `AdminModulesController.cs:23` reads it). The implementer must locate the existing provisioning for rentals/pmoc/os near `AdminTenantService.cs:243, 317` and make `resource-planning` follow it. **Unverified item.**

### 3.3 Business guards replacing the lost static guarantees

All inside the existing `LockTenantModulesAsync` scope in `AdminTenantService.cs:258-315`, all with `IgnoreQueryFilters()` and explicit tenant id (same reason as `:302-304`):

| Module being disabled | Rejected when |
|---|---|
| `resource-planning` | any `AssetAllocation` in Scheduled, Running or Cleaning (replaces the catalog-only count; keep the old count until backfill is proven) |
| `rentals` | any unreleased `AssetAllocation` with `ReservationItemId` not null |
| `catalog` | any unreleased `AssetAllocation` linked to a catalog order item |
| `os` | allowed. The allocation keeps its label; the link stops being navigable |

Error text names the count, same style as `:311-313`.

### 3.4 Runtime checks and gates

- `CatalogOrderAllocationService.RequiredModules` (`:69-74`) drops `rentals`. Rentals is required only when the target asset is rental-backed; the occupancy port checks that.
- `ModuleGateConventionTests.cs:112-118`: `IsStricterDependentModule` no longer holds for RP. Replace with an explicit rule: an action-level gate may add a second module when the class-level gate still declares the expected one (both filters run). Applies to `CatalogOrdersController.cs:84, 113`. Add `["Platform.Api.Modules.ResourcePlanning.Controllers"] = PlatformModules.ResourcePlanning` to the namespace map (`:19-26`).
- New permissions use the prefix `resource-planning.` so `ModuleKeyOf` (`PermissionCatalog.cs:141-145`) maps them to the module. Check that `PermissionPolicies.TryParse` and the web permission parser accept a hyphen in the prefix; if not, that is a stop-and-ask, not a rename of the module key.
- `tests/Platform.Api.Tests/Architecture/module-dependency-baseline.txt`: new allowed edges `ResourcePlanning -> Assets`, `ResourcePlanning -> Rentals` (contract namespace only), `Catalog -> ResourcePlanning`, `WorkOrders -> ResourcePlanning`. Planning never references Catalog or WorkOrders namespaces.
- `AdminModuleCatalogItemDto` (`Admin/Dtos/AdminModuleCatalogItemDto.cs`) should also expose `RequiredModules` so the web wizard stops hardcoding; optional, slice API-1.

### 3.5 Web gating

- `src/features/agenda/manifest.tsx:8-9`: add `"resource-planning"` to `modules` and the new read permission to `permissions`; new child and route `/agendas/planejamento` gated `modules: ["resource-planning"]`.
- `src/features/admin/moduleCatalog.ts:24-38`: add category (`operations`) and icon for `resource-planning` (name key already at `:46`). Check `KNOWN_COMMERCIAL_MODULE_KEYS` in `src/features/admin/schemas/adminTenantSchemas.ts` and `toCanonicalModuleName` in `src/features/catalog/customerNav.ts` handle the hyphenated key.
- Any wizard rule that forces catalog + rentals when RP is ticked must go (search the tenant wizard for `resource-planning`).
- `src/features/manifests.test.ts` gets the new route and nav entry.

## 4. Data model

New schema `planning`. All tables tenant-scoped (`ITenantScoped`), composite tenant-qualified FKs as in `CatalogOrderItemResourceAllocationConfiguration.cs:82-99`. Entities in `Core/Platform.Core.Domain/Entities/`, configurations in `Core/Platform.Core.Infrastructure/Persistence/Configurations/`, `DbSet`s next to `AppDbContext.cs:177`.

### 4.1 `AssetPlanningProfile` (`planning.asset_planning_profiles`)

One row per plannable asset. Presence plus `IsPlannable` is the opt-in.

| Column | Type | Notes |
|---|---|---|
| `id`, `tenant_id`, `created_at`, `updated_at` | | |
| `asset_id` | uuid | FK `(tenant_id, asset_id)` to `assets.assets`, unique per tenant |
| `is_plannable` | bool | default true |
| `capacity` | int | default 1, check `>= 1`. Phase 1 UI fixes 1 |
| `hours_policy` | varchar(32) | `Anytime` (default), `StartWithinHours`, `WithinHours` |
| `default_prep_minutes`, `default_cleanup_minutes` | int | default 0, check `>= 0` |

### 4.2 `AssetWorkingHours` (`planning.asset_working_hours`)

Weekly capacity calendar. Zero rows with a non-`Anytime` policy means closed all week (API rejects saving that combination).

| Column | Type | Notes |
|---|---|---|
| `id`, `tenant_id` | | |
| `asset_id` | uuid | FK as above |
| `day_of_week` | smallint | 0 to 6 |
| `open_time`, `close_time` | time | check `close_time > open_time`; several rows per day allowed (lunch break) |

Unique `(tenant_id, asset_id, day_of_week, open_time)`. Times are wall-clock in the tenant zone (`TenantTimeZoneProvider`, as used in `CatalogAgendaService.cs:52`; do not use `TenantBookingClock`, it lives in Rentals).

Date exceptions (holiday, downtime) are **not** a table: they are allocations of kind `Unavailable`.

### 4.3 `AssetAllocation` (`planning.asset_allocations`)

| Column | Type | Notes |
|---|---|---|
| `id`, `tenant_id`, `created_at`, `updated_at` | | |
| `asset_id` | uuid | FK `(tenant_id, asset_id)`, Restrict |
| `unit_id` | uuid | snapshot of the asset's unit, for filtering |
| `kind` | varchar(32) | `Use`, `Unavailable` |
| `title` | varchar(200) | required; free text reason |
| `notes` | varchar(1000) | optional |
| `quantity` | int | default 1, check `>= 1` |
| `planned_work_start`, `planned_work_end` | timestamptz | check end > start |
| `occupied_start`, `occupied_end` | timestamptz | work window plus prep and cleanup, stored, so readers never pad |
| `time_zone_id` | varchar(64) | snapshot, as `CatalogOrderItemResourceAllocation.TimeZoneId` |
| `status` | varchar(32) | reuse `AllocationStatus` (`Core/Platform.Core.Domain/Enums/AllocationStatus.cs:7-14`) |
| `actual_start_at`, `actual_work_finished_at`, `actual_release_at` | timestamptz null | |
| `source` | varchar(32) | `Manual`, `CatalogOrder`, `WorkOrder` |
| `catalog_order_item_id` | uuid null | FK `(tenant_id, …)` to `catalog.order_items`, Restrict |
| `work_order_id` | uuid null | FK `(tenant_id, …)` to the work orders table, Restrict |
| `reference_label` | varchar(120) null | snapshot for display ("Pedido 123", "OS 45"), so the agenda needs no cross-module join |
| `reservation_item_id` | uuid null | set only on rental-backed assets; FK Restrict; unique when not null |
| `created_by_user_id`, `actor_id` | uuid null | |
| `cancel_reason` | varchar(1000) null | |
| `row_version` | bytea | concurrency token, same pattern as `CatalogOrderItemResourceAllocation.cs:32` |

Checks: at most one of `catalog_order_item_id`, `work_order_id` not null; `occupied_start <= planned_work_start`; `occupied_end >= planned_work_end`.
Indexes: `(tenant_id, asset_id, occupied_start)`; partial `(tenant_id, asset_id) WHERE status IN ('Scheduled','Running','Cleaning')`; `(tenant_id, work_order_id)`; `(tenant_id, catalog_order_item_id)`.

Blocking rule (one static helper, used by writer and readers, mirroring `ReservationOccupancy.cs:14-27`): `Scheduled` blocks `[occupied_start, occupied_end)`; `Running` and `Cleaning` block from `occupied_start` until released, with no implicit end. `Released` and `Cancelled` never block. See decision 4 in section 9 for `Scheduled` past its end.

Conflict: `sum(quantity of blocking overlaps) + requested > capacity`. No Postgres exclusion constraint in phase 1 (it would need `btree_gist` and cannot express capacity); serialization is by `SELECT … FOR UPDATE` on the `assets.assets` row, the same technique as `Rentals/Services/RentalAssetLocks.cs:15-45`.

Lock order, fixed: tenant-modules advisory lock, then order or work-order row, then asset rows ascending by id, then rental-asset rows ascending (inside the bridge). Customer booking only ever takes the rental-asset lock, so no cycle. The reviewer must confirm no existing path takes a rental-asset lock and then an asset row lock.

### 4.4 Changes to catalog tables

- `catalog.order_item_resource_allocations`: add `asset_allocation_id uuid null` (FK, unique when not null); make `reservation_item_id` and `rental_asset_id` nullable.
- `catalog.product_resource_requirements` and `catalog.order_item_resource_requirements`: add `asset_id uuid null` (FK to `assets.assets`). Backfill from `rentals.rental_assets.asset_id`. `RentalAssetId` stays and keeps being written when the asset is rental-backed, for old readers. If a DB check enforces "exactly one of rental_asset_id / asset_category_id" (the entity comment at `CatalogProductResourceRequirement.cs:7` implies one), it must be replaced by "exactly one of asset_id / asset_category_id, rental_asset_id optional". **Verify in the snapshot before writing the migration.**

### 4.5 Migrations (both additive; deep review required: schema, persisted data, tenant isolation)

1. `20261120100000_AddAssetPlanning` — create schema `planning`, the three tables, indexes, checks. Pure create. `Down` drops the schema only if `asset_allocations` is empty, same guard style as `AddResourcePlanningPhase0.Down()` (`docs/runbooks/database-migrations.md:95`).
2. `20261127100000_LinkCatalogAllocationsToAssetAllocations` — add the nullable columns of 4.4; backfill `asset_id`; insert one `asset_allocations` row per existing `order_item_resource_allocations` row (asset from the rental asset, window from planned work plus the requirement's prep and cleanup, same status and actuals, `source = 'CatalogOrder'`, `reservation_item_id` copied, deterministic id so a rerun is idempotent) and set `asset_allocation_id`; drop NOT NULL on the two columns; swap the check constraint if present. No row is deleted or rewritten. `Down` refuses when any allocation has a null `reservation_item_id`.

Expected volume is very small (Phase 0 feature; `ENVIRONMENTS.md` records rentals off for gammera on DEV). List the row counts before applying on PROD.

## 5. API

New module folder `Platform.Api/Modules/ResourcePlanning/` (`Controllers`, `Services`, `Dtos`, `Contracts`, `ResourcePlanningModuleExtensions.cs`), class-level `[RequireActiveModule(PlatformModules.ResourcePlanning)]`, route prefix `api/planning`. Staff only; nothing public or portal.

### 5.1 Permissions (`Permissions.cs` after `:91`, `PermissionCatalog.cs` after `:75`)

| Key | Allows |
|---|---|
| `resource-planning.agenda.read` | list plannable assets, read agenda and allocations |
| `resource-planning.allocations.manage` | create, edit, cancel allocations |
| `resource-planning.allocations.execute` | start, work-finished, release |
| `resource-planning.availability.write` | edit an asset's planning profile and working hours |

System-role bootstrap: Admin gets all by wildcard; Technician gets `agenda.read` and `allocations.execute` (decision 5). `catalog.orders.allocate` stays for the order flow.

### 5.2 Endpoints

| Method and route | Permission | Notes |
|---|---|---|
| `GET /api/planning/assets?search&unitId&plannable=true|false|all` | agenda.read | id, name, tag, category, unit, `isPlannable`, `isRentalBacked`, policy summary. This is the authorized read path ADR 0004 asks for when `inventory` is not entitled |
| `GET /api/planning/assets/{assetId}/profile` | agenda.read | profile plus weekly hours |
| `PUT /api/planning/assets/{assetId}/profile` | availability.write | upsert profile and replace hours atomically. 409 when the asset is rental-backed and the caller tries to set hours (rental schedule is the authority) |
| `GET /api/planning/agenda?from&to&assetIds&unitId` | agenda.read | `resources[]` (with working hours for shading), `items[]` (allocations overlapping by stored occupied window), and for rental-backed assets `externalBusy[]` (customer reservations as anonymous busy intervals). Range validation as `CatalogAgendaController.ValidateRange`. Constant number of round trips regardless of asset count |
| `POST /api/planning/allocations/preview` | agenda.read | computes occupied window, returns `feasible`, `conflicts[]`, `hoursViolation` |
| `POST /api/planning/allocations` | allocations.manage | body: `assetId`, `kind`, `title`, `notes`, `workStart`, `workEnd`, `prepMinutes?`, `cleanupMinutes?`, optional `workOrderId` or `catalogOrderItemId` |
| `GET /api/planning/allocations/{id}` | agenda.read | |
| `GET /api/planning/allocations?workOrderId=|catalogOrderId=|assetId=&status=` | agenda.read | link panels |
| `PATCH /api/planning/allocations/{id}` | allocations.manage | title, notes, window; only while `Scheduled`; needs `expectedVersion`; re-runs conflict check under lock. Not allowed for `source = CatalogOrder` (the order flow owns those) |
| `POST /api/planning/allocations/{id}/usage` | allocations.execute (`cancel` needs allocations.manage) | `{ action: start | work-finished | release | cancel, expectedVersion, reason? }`. For `source = CatalogOrder` returns 409 `planning.order_workflow_required`, pointing to the order endpoint, same idea as `InternalUseRequiresOrderWorkflowException` |

Errors: 409 `planning.conflict` with `conflicts: [{ source: "allocation" | "reservation", allocationId?, start, end, label }]` (label withheld without read permission on the linked thing); 422 `planning.outside_working_hours`; 409 `planning.version_conflict`; 409 `planning.asset_not_plannable`.

Existing routes keep their contract: `GET /api/catalog/agenda/resources` (rentals-gated), `POST /api/catalog/orders/{id}/allocation-preview`, `.../allocations/{id}/usage`, `GET/PUT /api/catalog/products/{id}/resource-requirements` (accepts `assetId` in addition to `rentalAssetId`; responses carry both).

### 5.3 Services

- `Contracts/IAssetOccupancyService` — `LockAssetsAsync`, `ResolvePlannableAssetAsync` (eligibility: asset `Active`, not scheduled for deletion, profile plannable; replaces `ResolveEligibleResourceAsync`, `ResourceOccupancyService.cs:243-268`), `ValidateWindowAsync`, `CheckAvailabilityAsync`, `OccupyAsync` (returns the `AssetAllocation`), `ReleaseAsync(kind)`. Runs inside the caller's transaction and never saves, the convention of `IResourceOccupancyService.cs:14-18`. Internally picks the backend: planning-only, or planning plus the rentals bridge (`CreateInternalUseOccupancyAsync` / `ReleaseInternalUseOccupancyAsync`, plus `ValidateWorkWindowAsync` for the legacy window rules).
- `AssetWorkingHoursRules` — pure static: given policy, weekly hours, zone and an interval, return null or a reason. Handles multi-day intervals and DST.
- `AssetAllocationService` — manual CRUD and usage transitions (same state machine as `CatalogOrderAllocationUsageService.cs:226-290`; extract the shared transition rules into one static class used by both).
- `PlanningAgendaService` — the agenda read.
- `Contracts/IAllocationReferenceResolver` — implemented by Catalog and by WorkOrders, registered only by those modules; validates a link id belongs to the tenant, checks the caller may read it, returns the label. Planning owns the interface, so the dependency points inward.
- `CatalogPlanningWindow.cs:6-23`: keep `MaxMinutes` for rental-backed assets; add a planning-only limit (proposed 43 200 minutes) passed in by the caller.
- `CatalogAgendaService.cs:84-132`: read the occupied window from `AssetAllocation` when present, drop the one-day padding once backfill is in.
- `AvailabilityReadModel.cs:194-216, 269-270` (InternalUse badge join by `ReservationItemId`): unchanged in phase 1, because rental-backed order allocations still carry `ReservationItemId`. Manual allocations on rental-backed assets also create InternalUse reservations with no catalog row: the badge must fall back to `AssetAllocation.reference_label`/`title`. Covered in slice API-4.

## 6. Web

New feature folder `src/features/planning/` (`manifest.tsx`, `services/planningService.ts`, `schemas/planningSchemas.ts`, `components/`), contract via `npm run gen:api` after each API slice.

| Screen | Where | Notes |
|---|---|---|
| **Planejamento agenda** | new view `"planning"` in `src/features/agenda/AgendasPage.tsx:30, 44-55`; route and nav in `src/features/agenda/manifest.tsx:15-27` | Reuse the time × resource matrix (`AgendaDayGrid.tsx`, `AgendaWeek.tsx`, `dayGridModel.ts`), fed by `GET /api/planning/agenda`. Rows = plannable assets; non-working hours shaded; allocation chips by kind and status; overdue highlighted. Follows the shared `PageHeader` pattern (Reservas header is the reference). Add a source in `useAgendaDay.ts:25, 43-59` or a sibling hook; do not load rentals sources when rentals is off |
| **Allocation dialog** | `src/features/planning/components/AllocationDialog.tsx`, modeled on `src/features/agenda/BlockPeriodDialog.tsx` | Asset, kind, title, start, end, prep and cleanup, optional link. Calls preview on change, lists conflicts inline, disables save when not feasible. Opened from an empty cell (prefilled), from a toolbar button, from OS and from order |
| **Allocation detail sheet** | `src/features/planning/components/AllocationSheet.tsx` | Status, window, link, primary usage action (start, concluir, liberar), cancel with reason. Order-linked allocations link to the order instead of showing actions |
| **Asset availability tab** | `src/features/planning/components/AssetAvailabilityPanel.tsx`; mounted as a step or section in `src/features/assets/components/AssetWizard.tsx` when the module is active, and reachable from the agenda row header for tenants without `inventory` | Plannable toggle, hours policy, weekly hours editor. Reuse `src/features/rentals/components/schedule/BusinessHoursEditor.tsx` and `businessHoursModel.ts` if they have no rentals-only imports; otherwise lift them to a shared place first. Read-only notice when the asset is rental-backed |
| **Link from OS** | `src/features/workOrders/pages/WorkOrderExecutionPage.tsx` | "Recursos alocados" block: list from `GET /api/planning/allocations?workOrderId=`, button opening the dialog prefilled with the OS asset and scheduled date. Hidden without the module or permission |
| **Link from catalog order** | `src/features/catalog/pages/CatalogOrderDetailPage.tsx:352-378`; `components/CatalogOrderAllocationPanel.tsx` | Agenda link goes to `/agendas/planejamento` and checks `resource-planning.agenda.read` instead of `rentals.schedule.read` when the asset is not rental-backed. Unit selector only when needed |
| **Product requirement picker** | `src/features/catalog/components/CatalogProductResourceRequirements.tsx:20, 104, 195-215` | Source becomes `GET /api/planning/assets?plannable=true` when RP is active, falling back to rental assets otherwise; sends `assetId` |

i18n: new `planning.*` keys in every locale file; `nav.agendasPlanning` added to the unions at `src/components/layout/navigation.ts:47, 94`. Run `npm run tokens:check`, `npm run lint:ratchet`, `npm run check:api`.

## 7. PR slices

Each slice is one branch, one PR to `develop`, green CI, reviewed by a non-author. Deep (canonical) review is mandatory for API-1 to API-5 (module graph, schema, persisted data, concurrency, contract).

| # | Slice | Content | Tests | Shippable alone because |
|---|---|---|---|---|
| API-1 | `feat(resource-planning): decouple module from rentals and catalog` | ADR 0010; descriptor change (3.1); guards (3.3); gate test rule and namespace map (3.4); expose `RequiredModules` in admin DTO. `CatalogOrderAllocationService.RequiredModules` unchanged here | catalog unit tests for the new combinations; guard tests per row of 3.3 with a second tenant to prove isolation; `ModuleGateConventionTests`; existing `ResourcePlanning/*` tests untouched and green | Order flow still requires rentals at runtime, so nothing changes for current tenants |
| API-2 | `feat(resource-planning): asset planning profile and working hours` | Migration 1; entities and configs; permissions; `GET assets`, `GET/PUT profile`; `AssetWorkingHoursRules` | rules unit tests (three policies, multi-day, split day, DST day); HTTP tests for permission and module gates; tenant isolation on every endpoint; migration SQL test in the style of `tests/Platform.Api.Tests/Pmoc/PmocPhase3MigrationSqlTests.cs` | New tables and endpoints only |
| API-3 | `feat(resource-planning): manual allocations, conflicts and agenda` | `IAssetOccupancyService` (planning-only backend), `AssetAllocationService`, `PlanningAgendaService`, endpoints of 5.2 except links; rental-backed assets rejected with 409 for now | overlap matrix (half-open edges, buffers, capacity); state machine; version conflict; **Postgres** concurrency test: two parallel creates on one asset, exactly one wins; overdue Running keeps blocking; agenda query count constant | Works for non-rentable assets end to end |
| API-4 | `feat(resource-planning): rentals bridge for rentable assets` | Bridge backend; manual allocation on rental-backed asset mirrors InternalUse; `externalBusy`; schedule badge fallback; rentals disable guard exercised | allocation blocks a customer booking and the reverse; release frees the slot; `InternalUseExclusionTests` still green; ScheduleDay golden files unchanged except the new badge case | Purely additive for rentals tenants |
| API-5 | `feat(catalog): allocate orders on non-rentable assets` | Migration 2 with backfill; requirement API accepts `assetId`; allocation, preview and usage services go through the port; runtime module list drops rentals; planning-only duration limit; `CatalogAgendaService` reads stored window | all existing `Catalog*Allocation*` and `ResourcePlanning/*` tests green unchanged (backward compatibility proof); new: approve and allocate on a non-rentable asset with rentals off; job over 24 h; backfill test on a seeded Phase 0 row; `Down` guard | Existing tenants covered by the unchanged tests |
| API-6 | `feat(os): link work orders to resource allocations` | `IAllocationReferenceResolver` for WorkOrders and Catalog; `workOrderId` / `catalogOrderItemId` on create; list by link | resolver rejects another tenant's id; label snapshot; filter endpoints | Optional link on an existing feature |
| WEB-1 | `feat(planning): planejamento agenda view` | feature folder, service, schemas, manifest, nav, module catalog entry, read-only agenda | vitest for the grid model and gating (`manifests.test.ts`); Playwright smoke on **gammera**, desktop and mobile | Needs API-3 on DEV |
| WEB-2 | `feat(planning): allocation dialog and usage actions` | dialog, sheet, conflict display | vitest for form model and conflict rendering; e2e create, start, release | Needs WEB-1 |
| WEB-3 | `feat(planning): asset availability settings` | availability panel in the asset editor and from the agenda | vitest for hours model; e2e save and see shading | Needs API-2 |
| WEB-4 | `feat(catalog): plan orders on non-rentable resources` | picker source, order panel, agenda link | vitest for picker source switch and permission gating; e2e approve and allocate with rentals off | Needs API-5 |
| WEB-5 | `feat(work-orders): allocate resources from an OS` | block and button on the OS page | vitest; e2e | Needs API-6 |

Suggested order: API-1, API-2, API-3, WEB-1, WEB-2, WEB-3 (first usable release: the 3D printer case), then API-4, API-5, WEB-4, API-6, WEB-5. Always DEV first, PROD only with human approval; migrations 1 and 2 follow the production runbook (backup, list pending, apply only the expected ones, record rollback id). No write on the FICC tenant in PROD.

After each slice: update `ROADMAP.md` in the touched repo; after API-1 update `CONTEXT.md` (new terms: Plannable resource, Allocation, Working hours, calendar authority) and its mirror in `vlr-web`.

## 8. Risks

| Risk | Mitigation |
|---|---|
| Two occupancy stores drift on rental-backed assets (allocation released, reservation still blocking, or the reverse) | One port is the only writer of both, in one transaction; test asserts both sides after every transition; a read-only consistency query added to the runbook |
| Duplicate lifecycle status (`AssetAllocation` and the catalog allocation row) during phase 1 | Shared transition class; both written in the same `SaveChanges`; test compares them; removal of the catalog columns tracked as follow-up |
| Deadlock from the new asset-row lock | Fixed lock order (4.3); Postgres concurrency tests; reviewer checks existing asset update paths |
| Backfill on PROD | Insert-only, idempotent, counted before and after; `Down` guard; expected near zero rows |
| Relaxing NOT NULL on `reservation_item_id` / `rental_asset_id` breaks a reader that assumes non-null (`CatalogAgendaService.cs:107, 115`, `CatalogOrderAllocationUsageService.cs:84-89`, `CatalogOrderPlanningMapper.cs`, `CatalogNotificationPublisher.cs:266`, `AvailabilityReadModel.cs:207-213`) | API-5 touches each of these explicitly; nullable reference types make the compiler list them |
| Removing the static module dependency lets an admin disable rentals or catalog under live allocations | Business guards 3.3, tested |
| Hyphenated permission prefix not accepted somewhere | Checked first thing in API-2; stop and ask if it fails |
| Capability provisioning hook not located (3.2) | Resolve in API-1 before coding the rest |
| Forgotten allocations block an asset indefinitely | Overdue highlight in the agenda; decision 4 |
| Tenant without `inventory`, `rentals` or `os` has no way to create assets | Documented; admin wizard shows a hint. Not a blocker for the target case |
| Agenda performance with many assets | Stored occupied window, range index, constant round trips asserted in a test |

## 9. Decisions the owner must make

1. **Model.** Dedicated allocation record on the asset, with a mirror reservation only for rentable assets (recommended), or keep "internal reservation" and give every plannable asset a hidden rental configuration.
2. **Module requirement.** Capability only, per ADR 0004 (recommended): the module can be sold without Ativos, but then assets must be created through another module. Alternative: hard-require the `inventory` module.
3. **Working-hours rule.** Three policies proposed, default `Anytime`. Confirm the three and the default. Confirm that working hours block (reject) rather than only warn.
4. **Overdue allocations.** Started and not released: blocks until released (same as today). Scheduled and never started, past its end: recommended to stop blocking automatically for manual allocations and keep today's behavior for order-linked ones. Confirm.
5. **Technician role.** Read plus start, finish, release (recommended), or also create and cancel.
6. **OS link.** Phase 1 manual from the OS page (recommended). Later: auto-block the asset when an OS is scheduled, auto-release on completion.
7. **Rentable assets.** Phase 1 keeps the rental schedule as the only calendar for them, including the 24 h and booking-window limits. Accept, or require long jobs on rentable assets now (larger scope).
8. **Maximum duration** of one allocation on a planning-only asset: 30 days proposed.
9. **Plannable is opt-in per asset** (recommended), not automatic for every asset.


### Owner answers (2026-11-19) — binding
1. Model: dedicated AssetAllocation on Asset, mirror reservation only for rentable assets. APPROVED.
2. Module requirement: capability only (asset-registry), no hard require of inventory/rentals/catalog. APPROVED.
3. Working hours: three policies kept; DEFAULT = StartWithinHours ("começar no horário"). Hours reject (not warn).
4. Overdue: a manual allocation that was never started stops blocking automatically after its end (auto-expire). Started allocations block until released. Order-linked allocations keep today's behaviour.
5. Technician: may create, start, finish and release allocations; cancelling an allocation created by someone else is admin-only.
6. OS link: manual in phase 1.
7. Rentable assets keep rental-schedule limits in phase 1. Accepted.
8. Max duration 30 days. Accepted.
9. Plannable is opt-in per asset. Accepted.
Status: READY TO IMPLEMENT.

## 10. ADR to write

`vlr-api/docs/adr/0010-internal-resource-planning-asset-allocation.md` (0009 is the latest on `origin/develop`). Content: context (Phase 0 coupling to rentals); decision (resource-planning requires the Asset Registry capability only; `AssetAllocation` on `Asset` is the planning record; one calendar authority per asset; rentals bridge mirrors InternalUse for rental-backed assets; catalog and OS are optional integrations through contracts owned by planning; business guards replace static module dependencies); options rejected (hidden rental asset; making rentals read planning tables; polymorphic link without FK; exclusion constraint in phase 1); consequences (dual status until the contract step, new lock order, new permission prefix). Amends the "Today the only commercial containment is Orders ⊂ Catalog" line of ADR 0004 decision 6 only in that RP no longer adds one.

## 11. Out of scope for phase 1

- Capability or category matching ("any printer that handles TPU"); `AssetCategoryId` on requirements stays rejected.
- More than one requirement per product, more than one planned line per order, batch splitting across assets.
- Capacity above 1 in the UI (the column exists).
- Drag to move or resize, recurring allocations, auto-scheduling or next-free-slot suggestion.
- Long or free-range jobs on rental-backed assets; changing rentals availability to read planning tables.
- Automatic OS coupling; people or technician scheduling (only assets are planned).
- Utilization reports, notifications for allocations, customer or portal visibility.
- Dropping the legacy columns (`rental_asset_id`, `reservation_item_id`, catalog allocation status) and retiring `GET /api/catalog/agenda/resources`.
- Date-exception table for working hours (use `Unavailable` allocations).

## 12. Done when

- A tenant with only `inventory` and `resource-planning` can mark an asset plannable, set its hours, create, start and release an allocation, and is refused on overlap, on DEV with the gammera tenant, desktop and mobile.
- A tenant with `catalog` and `resource-planning`, rentals off, can approve and allocate an order on a non-rentable asset.
- A tenant using Phase 0 with rentals sees no behavior change; every pre-existing test in `tests/Platform.Api.Tests/ResourcePlanning/` and the Catalog allocation suites passes unmodified.
- ADR 0010, `CONTEXT.md` (both repos), `ROADMAP.md` (both repos) and `openapi.json` are updated.
