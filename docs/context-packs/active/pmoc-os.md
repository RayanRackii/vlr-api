# PMOC / OS Context Pack

Derived context — NOT canonical. Current production product. Not an implementation handoff.

- Scope: PMOC (maintenance plans, Rolvix template library) and OS (work orders, technician execution)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `docs/plans/active/2026-09-19-pmoc-os-phase3.md` (current model, released); Phase 1 and Phase 2 specs are history
- Last verified: 2026-09-24 (PROD closeout). Image upload contract updated 2026-10-18.
- Verified at commit(s):
  - API `origin/main`: `c837988abfb55255c1c867ca62d9618ec3c6dd18`
  - WEB `origin/main`: `254bd333ef2b2c3b4f798ef6327091f85890fd36`

```
ROLVIX_PMOC_OS_PHASE1 = RELEASED_PROD
ROLVIX_PMOC_OS_PHASE2 = RELEASED_PROD
ROLVIX_PMOC_OS_PHASE3 = RELEASED_PROD
PMOC_OS_PHASE4 = NOT_PLANNED
PMOC_OS_ACTIVE_IMPLEMENTATION = NO
```

## Current model

```
MaintenancePlan
  → Unit + AssetCategory
  → currently eligible Assets
  → IntervalDays + FirstDueDate + checklist
  → manual Gerar OS or automatic generation
  → WorkOrder task snapshots
  → completion history
  → per-asset Coverage
```

Scheduling uses `PmocDueCalculator` only:

- No Completed PMOC WorkOrder: `effectiveNextDueDate = FirstDueDate`
- Completed history: Brazil civil date of the latest `CompletedDate` + `IntervalDays`

`HistoryStatus`: `NeverExecuted` | `Executed`.

`DueStatus`: `NotDue` | `DueToday` | `Overdue`.

`needsAttention` is `DueToday` or `Overdue` only. NeverExecuted is not attention by itself.

Eligibility: same tenant, plan `UnitId`, plan `AssetCategoryId`, Asset `Active`, `ScheduledDeletionAt` null. `RequiresMaintenance` is not a gate. `IsActive` and `AutoGenerateEnabled` do not hide Coverage rows.

Automatic generation runs when the plan is active and auto-generate is on, and the asset is DueToday or Overdue. It skips an existing Pending or InProgress PMOC work order for that plan and asset. `ScheduledDate` is `effectiveNextDueDate`, including a past date. Only `Completed` resets the clock. Pending and InProgress do not. Manual Gerar OS stays first-class and may be created before, on, or after the due date, including while another PMOC OS is open.

`PmocPlanAssetLock` row locks (`pmoc.maintenance_plans` and `assets.assets` `FOR UPDATE`) include `tenant_id`. `TryGenerateAutomaticAsync` receives that tenant id from the job and reloads the plan and asset with the same predicate. The advisory lock is unchanged. Empty tenant id throws. No schema change.

Due state is derived, not stored. Coverage and the generator share `PmocDueCalculator`. H6 (plan-list aggregates) is NO. H7 (Inventory/Asset PMOC surface) is NO.

There is no `Frequency`, `ScheduleMode`, `PmocDueCalendar`, or `MaintenancePlanAsset`. Applicability stays Unit + AssetCategory. Technician has no `pmoc.*`. Work order task rows are historical snapshots.

Hangfire `pmoc-engine` is `0 6 * * *` in Brazil (`E. South America Standard Time` / `America/Sao_Paulo`). One evaluation per civil day. The job does not expose a cron editor.

## What the product does not promise

These are future product possibilities, not unfinished Phase 3 work: per-plan asset membership, per-asset interval override, DueSoon, a cron editor, tenant timezone configuration, PMOC state on Inventory/Assets, a plan-list summary dashboard, a compliance percentage, a CREA certification guarantee, regulatory-document management, a template CMS for scheduling, an offline technician workflow, and Technician PMOC administration.

## Contracts

- `GET/POST /api/maintenance-plans`, header update, `PUT /api/maintenance-plans/{id}/tasks`, `POST /api/maintenance-plans/from-template`
- `GET /api/maintenance-plans/{id}/coverage` (`pmoc` + `pmoc.plans.read`)
- `GET /api/global-templates` (Published only; checklist and provenance; no scheduling fields)
- `POST /api/work-orders/from-plan` and `GET /api/work-orders?maintenancePlanId=`. Authenticated from-plan with no tenant context is 403 `FORBIDDEN`; unauthenticated is 401
- `POST /api/work-orders` with `maintenancePlanId` uses the same active-plan, asset-eligibility, and duplicate checks as from-plan (`409 DUPLICATE_WORK_ORDER`). Client-supplied tasks are kept.
- Closed orders (`Completed` or `Canceled`) reject task writes with `409 WORK_ORDER_CLOSED`. Web task writes, including `src/lib/offlineSync.ts`, remain `PATCH /api/work-orders/{id}/tasks/{taskId}` until the web client sends photos. There is no bulk task route. Task, status, cancel, and assignee lock `os.work_orders` with `FOR UPDATE` before reading the caller or the order, then reload. Image upload writes the new object under a fresh key first, then takes that lock only to swap the one row in `os.work_order_task_files` (same file id). A failed or lost swap deletes only the new object. The previous object is deleted after commit, and only when `IsKeyForTask` accepts the key. A value that commits while the order is still open is kept when completion runs next. `Completed` cannot return to `Pending` or `InProgress` (`409 WORK_ORDER_REOPEN_NOT_ALLOWED`). `POST /api/work-orders/{id}/cancel` (`os.work_orders.execute` or `os.work_orders.create`) is the exit from `Completed`; it clears `CompletedDate`. Canceled orders stay out of coverage. Image retrieval does not take that lock.
- The second `DELETE /api/assets/{id}` of an asset that has a work order returns `409 ASSET_IN_USE`. There is no bulk asset delete. The second `DELETE /api/asset-categories/{id}` when a plan or a work order still references the category returns `409 CATEGORY_IN_USE`.
- Task values: `400 INVALID_TASK_VALUE` plus `reason`. Completion with unfilled mandatory tasks: `400 MANDATORY_TASKS_INCOMPLETE` plus `tasks` (`id`, `title`). A checkbox is filled only when the stored value is `true`. An `Image` task is filled only when `value` is the id of an `os.work_order_task_files` row for that task. `POST /api/work-orders/{id}/tasks/{taskId}/files` (multipart field `file`, `os.work_orders.execute`, order the caller can access) stores jpeg/png/webp up to 5 MB in `Storage:PrivateBucket` and sets that id. Replacing a photo keeps that id and changes the storage key. `PATCH` with `null` clears the answer and leaves the object. `GET /api/work-orders/{id}/tasks/{taskId}/files/{fileId}/url` returns `{ url, isPublic: false }`. Wrong type or magic bytes: `400` `FILE_TYPE_NOT_ALLOWED` or `FILE_CONTENT_MISMATCH`. Over 5 MB: `400` `FILE_TOO_LARGE`. Missing permission: `403` `FORBIDDEN`. Module `os` inactive: `403` `Module is not active for this tenant.` Other tenant or an order the caller cannot access: `404`. A patched id that is not a file of that task: `400` `INVALID_IMAGE_REFERENCE`. A closed order rejects the upload with `409` `WORK_ORDER_CLOSED`.
- `PUT /api/work-orders/{id}/assignee` requires `os.work_orders.assign`. The assignee must have `os.work_orders.execute`. Null clears the assignee. `Completed` and `Canceled` return `409 WORK_ORDER_CLOSED`. `TenantAccessBootstrapper` adds `os.work_orders.assign` to existing Admin and SuperAdmin roles and does not remove other grants. There is no system Manager role.
- Delete of a plan that already has a work order: 409 `PLAN_IN_USE`
- Seed template id `6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e`

Phase 2 calendar Coverage is historical only. See `docs/plans/active/2026-09-19-pmoc-os-phase2.md`.

## Outside this product

Not active PMOC work. Do not treat the product as incomplete because of them.

- Platform: `OperationCanceledException` may be logged as 500 while the proxy reports client 499
- Release process: define a supported Railway (legado) traffic block before the next breaking schema cutover. Phase 3 could not set replicas to 0; clearing the region started the Phase 3 deploy and removed the Phase 2 process before the migration finished (~3 minutes). No product defect. See `docs/sessions/2026-09-24-pmoc-os-closeout.md`
- WEB backlog: inactive-plan Gerar OS UX, Novo Plano permission visibility, explicit checklist reorder, expected 409 console noise
- Tests: WEB Vitest worker-memory history
- DEV cleanup pending: isolated database `pmoc_phase3_concurrency` (do not drop the shared DEV database)
