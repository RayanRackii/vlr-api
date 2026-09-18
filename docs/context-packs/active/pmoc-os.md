# PMOC / OS Context Pack

Derived context — NOT canonical.

- Scope: PMOC (maintenance plans, Rolvix template library) and OS (work orders, technician execution)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `docs/plans/active/2026-09-18-pmoc-os-phase1.md`
- Last verified: 2026-09-18 (planning; Phase 1 **not implemented**)
- Verified at commit(s):
  - API `develop`: `8953f6ae4ac12b82abd84ba4611636126c4ad21f`
  - WEB `develop`: `85ce88f272e792d1c3016bb34c930ea6ce6994b6`

## Purpose

Load when the question is GlobalMaintenanceTemplate, MaintenancePlan, PlanTask, PmocEngineJob, WorkOrder, WorkOrderTask, Biblioteca Rolvix, Gerar OS, or PMOC/OS permissions.

This pack describes **shipped develop + approved Phase 1 target**. Until Phase 1 slices merge, “Current model” is code; “Phase 1 target” is the spec.

## Canonical sources

- `CONTEXT.md` — modules `pmoc` / `os`; Asset Registry vs Inventory
- `docs/adr/0004-module-dependencies-asset-registry.md` — PMOC needs a provisioning family; OS consumes assets
- `docs/plans/active/2026-09-18-pmoc-os-phase1.md` — approved Phase 1 handoff (H1–H5)
- `.cursor/rules/10-arquitetura.mdc` — Hangfire `pmoc-engine` 06:00 Brazil; GQF off when job `TenantId` is null
- `Core/Platform.Core.Domain/Constants/Permissions.cs` — `Pmoc.*` / `Os.*`
- `Core/Platform.Core.Domain/Constants/PermissionCatalog.cs` — Technician has OS read/execute + inventory assets read only

## Domain vocabulary

- **GlobalMaintenanceTemplate** — platform-owned library row (`LibraryKey` + `Version`). Tenants do not edit it.
- **MaintenancePlan** — tenant PMOC. Custom or cloned. Not a live view of the template.
- **PlanTask** — live checklist of the tenant plan (future OS).
- **WorkOrder** — OS. Technician’s operational unit.
- **WorkOrderTask** — **snapshot** copied at generation. Execution truth.
- **OriginKind** — `Custom` | `RolvixTemplate`.
- **IsActive** — plan is usable.
- **AutoGenerateEnabled** — Hangfire may generate OS. Independent of `IsActive`.

## Current model (code today)

```
GlobalMaintenanceTemplate (1 seed: ANVISA RE 09 + NR-10)
        ↓ frontend form-fill (no lineage)
MaintenancePlan + PlanTasks (header PUT; tasks immutable after create)
        ↓ Hangfire PmocEngineJob (all IsActive, calendar due)
WorkOrder + WorkOrderTasks snapshot
```

- Seed id `6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e` — preserve.
- `POST /api/work-orders` is **manual** client tasks (`/os/nova`); does not snapshot PlanTasks.
- Duplicate protection: `AnyAsync`, non-unique index.
- DELETE plan **SetNulls** `WorkOrder.MaintenancePlanId` (historical OS looks Manual).
- WEB: `/pmoc`, `/pmoc/novo`, `/os`, `/os/:id`, `/os/nova`. CREA copy in i18n.
- Offline: `vlr-web/src/lib/offlineSync.ts` queues WO task PATCH; **not** Phase 1 work.

## Phase 1 target (approved spec)

```
Published template (immutable row)
        ↓ POST /api/maintenance-plans/from-template
MaintenancePlan lineage + AutoGenerateEnabled (new=false, existing=true)
        ↓ PUT tasks (future OS only)
POST /api/work-orders/from-plan  OR  Hangfire if IsActive && AutoGenerateEnabled
        ↓
WorkOrder.SourcePlanName + Restrict FK
WorkOrderTasks snapshot unchanged by later plan edits
```

Routes: `/pmoc`, `/pmoc/biblioteca`, `/pmoc/biblioteca/:templateId`, `/pmoc/novo`, `/pmoc/:id`.

## Critical invariants

- Snapshot execution: `/os/:id` must not read live PlanTasks
- PlanTask edits never mutate existing WorkOrderTasks (except `PlanTaskId` SetNull)
- Published templates are immutable; future vN = new row, same `LibraryKey`
- Existing PROD plans: `AutoGenerateEnabled=true` after migration
- New/cloned plans: `AutoGenerateEnabled=false` unless the user opts in
- One generation service for Hangfire + manual Gerar OS
- Technician: no `pmoc.*`; origin text only unless `pmoc.plans.read` + module on
- Unique `(tenant, plan, asset, scheduled_date)` where plan not null and status ≠ Canceled — **precheck before index**
- Used plan: DELETE 409 `PLAN_IN_USE`; deactivate instead
- No CREA / 100% conforme claims
- Do not extend `offlineSync.ts` in Phase 1

## Current contracts

B2B: `/api/maintenance-plans*`, `/api/global-templates`, `/api/work-orders*`. Modules `pmoc` / `os`.

Phase 1 additive endpoints (spec): `GET /api/global-templates/{id}`, `POST /api/maintenance-plans/from-template`, `PUT /api/maintenance-plans/{id}/tasks`, `POST /api/work-orders/from-plan`, `GET /api/work-orders?maintenancePlanId=`.

## Important implementation seams

- `Platform.Api/Modules/Pmoc/`
- `Platform.Api/Modules/WorkOrders/`
- `Platform.Api/Jobs/PmocEngineJob.cs`, `HangfireExtensions.cs`
- `Core/Platform.Core.Domain/Entities/{GlobalMaintenanceTemplate,MaintenancePlan,PlanTask,WorkOrder,WorkOrderTask}.cs`
- `Core/Platform.Core.Infrastructure/Persistence/Seed/GlobalTemplateSeed.cs`
- `vlr-web/src/features/pmoc/`
- `vlr-web/src/features/workOrders/`

## Known gaps / open constraints

- Slice 1 (domain + Migration A/B) is on `feat/pmoc-os-phase1-foundation`; later slices not implemented
- Unique-index collision precheck: DEV `jzptnjyzijklutinpxag` = 0, PROD `kbptdzfbngelzdhriyhf` = 0 (`MIGRATION_B = ALLOWED`)
- No last/next/overdue asset state (Phase 2)
- No template adoption/diff UX (model must allow later)
- Hangfire ignores commercial module flags (preserve)
- Already-nulled WO origins cannot be reconstructed

## Do not assume

- Two seed templates (ANVISA vs NR-10) — there is **one** combined seed
- Client form-fill as the canonical clone (Phase 1 is server clone)
- `IsActive` meaning “auto-generate”
- Technician needs PMOC module access
- Generic CREA-certified catalog
- Offline/mobile in Phase 1
- A second OS generator besides Hangfire + `from-plan` sharing one service
