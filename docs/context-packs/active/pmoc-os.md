# PMOC / OS Context Pack

Derived context — NOT canonical.

- Scope: PMOC (maintenance plans, Rolvix template library) and OS (work orders, technician execution)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `docs/plans/active/2026-09-18-pmoc-os-phase1.md`; `docs/plans/active/2026-09-19-pmoc-os-phase2.md` (calendar due **superseded**); `docs/plans/active/2026-09-19-pmoc-os-phase3.md`
- Last verified: 2026-09-21 (Phase 3 Slice 3 interval generator)
- Verified at commit(s):
  - API `origin/develop` (Phase 3 Slice 2): `eea9e410f9ca872b2d5e0f687c22b107d86c3cc4`

## Purpose

Load when the question is GlobalMaintenanceTemplate, MaintenancePlan, PlanTask, PmocEngineJob, WorkOrder, WorkOrderTask, Biblioteca Rolvix, Gerar OS, or PMOC/OS permissions.

This pack describes **shipped Phase 1 + Phase 2 on PROD**, **Phase 3 Slices 1–2 on develop**, and **Slice 3 interval generation on this branch**. WEB is unchanged. Shared Railway DEV stays frozen on Phase 2 until Slices 1–4 complete. Railway DEV autodeploy stays OFF.

## Canonical sources

- `CONTEXT.md` — modules `pmoc` / `os`; Asset Registry vs Inventory
- `docs/adr/0004-module-dependencies-asset-registry.md` — PMOC needs a provisioning family; OS consumes assets
- `docs/plans/active/2026-09-18-pmoc-os-phase1.md` — approved Phase 1 handoff (H1–H5)
- `docs/plans/active/2026-09-19-pmoc-os-phase3.md` — **canonical** final scheduling model (P3-B1..B4). Dual Calendar/Interval VOID.
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
GlobalMaintenanceTemplate (checklist/provenance only; 1 seed: ANVISA RE 09 + NR-10)
        ↓ GET library Published-only; GET by id includes Deprecated
        ↓ POST /api/maintenance-plans/from-template (server clone + tenant IntervalDays/FirstDueDate)
MaintenancePlan + PlanTasks (IntervalDays 1..3650, FirstDueDate; header PUT including AutoGenerateEnabled)
        ↓ derived due: PmocDueCalculator (never executed → FirstDueDate; executed → Brazil civil CompletedDate + IntervalDays)
        ↓ Hangfire PmocEngineJob once per Brazil civil day (`0 6 * * *`) OR POST `/api/work-orders/from-plan`
        ↓ IWorkOrderGenerationService snapshot
WorkOrder + WorkOrderTasks snapshot (`SourcePlanName`)
```

- Seed id `6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e` — preserve.
- No `MaintenanceFrequency`, `ScheduleMode`, or `PmocDueCalendar`.
- `POST /api/work-orders` is **manual** client tasks (`/os/nova`); does not snapshot PlanTasks.
- Duplicate protection: unique filtered index `ux_os_work_orders_pmoc_period` (canceled excluded).
- DELETE unused plan: hard delete + cascade PlanTasks. Used (any WO with `MaintenancePlanId`, including Canceled): **409 `PLAN_IN_USE`**. FK Restrict is race backstop.
- MaintenancePlanResponse always includes `originKind`, `sourceTemplateId`, `sourceTemplateVersion`, `autoGenerateEnabled`, `intervalDays`, `firstDueDate`.
- `GET /api/global-templates` is Published-only; response includes `libraryKey`, `version`, `status`, `sourceReferences`. **No scheduling fields.**
- `GET /api/global-templates/{id}` returns the full template + tasks and **includes Deprecated**.
- `POST /api/maintenance-plans/from-template` clones a Published row (`OriginKind=RolvixTemplate`, frozen `SourceTemplateVersion`, new PlanTask ids) and **requires tenant** `intervalDays` + `firstDueDate`. Generic `POST /api/maintenance-plans` stays Custom and also requires those fields.
- `POST /api/work-orders/from-plan` (`os.work_orders.create`) generates Pending OS from plan+asset; 409 `DUPLICATE_WORK_ORDER` on the PMOC period unique key. Manual `POST /api/work-orders` stays client-task (`/os/nova`). Completing a PMOC-linked WO resets the interval clock.
- `GET /api/work-orders?maintenancePlanId=` is additive (AND with `assetId`); related OS contract. `WorkOrderResponse.sourcePlanName` is nullable.
- Hangfire `pmoc-engine` remains `0 6 * * *` Brazil. Slice 3 generates for each eligible DueToday/Overdue asset with no open PMOC OS. `ScheduledDate` is `effectiveNextDueDate`. Manual from-plan still allows an open OS. Completion and generation share `pg_advisory_xact_lock` on `(plan, asset)`.
- `GET /api/maintenance-plans/{id}/coverage` (`pmoc.plans.read`) is Coverage V3: one Brazil `asOfDate`; per-asset `historyStatus` / `lastMaintenance` / `effectiveNextDueDate` / `dueStatus` / `needsAttention` / `openWorkOrder`; summary history+due partitions; `wouldBeConsideredByGenerator = IsActive && AutoGenerateEnabled && ANY DueToday|Overdue` (non-promissory). No server-side status filter. No persistence. No Hangfire call. Due math exclusively `PmocDueCalculator`. Eligibility predicate is shared with the generator.
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

Phase 2 calendar coverage contract is **superseded**. Coverage V3 is the public contract. No pagination. No server-side status filter. No `coverage-summaries` (H6=NO). No Inventory/Asset PMOC surface (H7=NO).

## Important implementation seams

- `Platform.Api/Modules/Pmoc/`
- `Platform.Api/Modules/Pmoc/Services/MaintenancePlanCoverageService.cs`
- `Platform.Api/Modules/WorkOrders/`
- `Platform.Api/Jobs/PmocEngineJob.cs`, `PmocDueCalculator.cs`, `HangfireExtensions.cs`
- `Platform.Api/Modules/WorkOrders/Services/WorkOrderGenerationService.cs`
- `Core/Platform.Core.Domain/Entities/{GlobalMaintenanceTemplate,MaintenancePlan,PlanTask,WorkOrder,WorkOrderTask}.cs`
- `Core/Platform.Core.Infrastructure/Persistence/Seed/GlobalTemplateSeed.cs`
- `vlr-web/src/features/pmoc/`
- `vlr-web/src/features/workOrders/`

## Known gaps / open constraints

- Slice 1–4 Phase 1 API + WEB Slices 5–6 and Phase 2 coverage are **released to PROD**
- Phase 3 Slice 1 is on `develop`: final interval domain + fail-closed job replaced in Slice 3 + destructive migration **file only** (not applied to shared DEV/PROD)
- Phase 3 Slice 2 is on `develop`: Coverage V3 public contract
- Phase 3 Slice 3 (this branch): asset-aware automatic generation
- Phase 3 Slice 4 WEB is **not implemented**
- H6 plan-list aggregates = **NO**; H7 Inventory PMOC surface = **NO**
- Unique-index collision precheck: DEV `jzptnjyzijklutinpxag` = 0, PROD `kbptdzfbngelzdhriyhf` = 0 (`MIGRATION_B = ALLOWED`)
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
