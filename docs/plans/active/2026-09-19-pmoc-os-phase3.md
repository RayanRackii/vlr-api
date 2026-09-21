# 2026-09-19-pmoc-os-phase3

Status: **APPROVED** (breaking clean domain). Slice 1 squash-merged to `develop`. Slice 2 Coverage V3 in progress.

```
ROLVIX_PMOC_OS_PHASE1              = RELEASED_PROD
ROLVIX_PMOC_OS_PHASE2              = RELEASED_PROD
PHASE3_DATA_COMPATIBILITY          = BREAKING_ALLOWED
PHASE3_BREAKING_MODEL              = APPROVED
PMOC_OS_PHASE3_SPEC                = APPROVED
PHASE3_IMPLEMENTATION              = SLICE_2_PR
H6                                 = NO
H7                                 = NO
SHARED_DEV_FREEZE                  = PHASE2_UNTIL_SLICES_1_4
```

Parent plans:

- `docs/plans/active/2026-09-18-pmoc-os-phase1.md` (released)
- `docs/plans/active/2026-09-19-pmoc-os-phase2.md` (released; calendar due model **superseded**)

The dual Calendar / Interval / `ScheduleMode` / rolling-compat design is **VOID**. Do not implement it.

---

## OBJECTIVE

One canonical PMOC scheduling model:

```
if no Completed PMOC WorkOrder for this plan+asset:
    effectiveNextDueDate = FirstDueDate
else:
    effectiveNextDueDate =
        BrazilTimeZone.GetCivilDate(latestCompleted.CompletedDate)
        + IntervalDays
```

No `ScheduleMode`. No `MaintenanceFrequency`. No `PmocDueCalendar`. No Monthly=calendar-day semantics.

---

## LOCKED DECISIONS

| ID | Decision |
|---|---|
| **P3-B1** | Breaking forward EF migration: delete PMOC-linked `os.work_orders` (tasks cascade) → delete `pmoc.maintenance_plans` (plan tasks cascade; leftover `work_order_tasks.plan_task_id` SetNull) → drop `frequency` → add `interval_days` NOT NULL + CHECK 1..3650 + `first_due_date` NOT NULL → drop `global_maintenance_templates.frequency`. Keep template seed id `6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e`. Do **not** baseline `__ef_migrations_history`. Apply only via the existing GitHub Environment migration gate — **not** in Slice 1. |
| **P3-B2** | `PmocDueCalculator` (`Platform.Api/Jobs`) is the **only** due authority. Never-executed → `FirstDueDate`. Executed → Brazil civil date of latest Completed + `IntervalDays`. Latest Completed = `CompletedDate DESC`, `ScheduledDate DESC`, `Id DESC`. Ignore Pending / InProgress / Canceled and `MaintenancePlanId` null. Throw if Completed has null `CompletedDate` (no `ScheduledDate` fallback). |
| **P3-B3** | Slice 1 `PmocEngineJob` is **fail-closed**: generate **zero** WorkOrders until Slice 3. Manual `POST /api/work-orders/from-plan` unchanged. |
| **P3-B4** | `CompletedDate` null-on-Completed is **not** a product state: `UpdateStatusAsync` always sets `UtcNow` on Completed and nulls on Pending/InProgress. Calculator throws if it sees a Completed row without `CompletedDate`. No invented fallback. |

Also still locked: Unit+Category eligibility; Technician no `pmoc.*`; H6=NO; H7=NO; C1 openWorkOrder selection; C3 Brazil `asOfDate` via `BrazilTimeZone`; unique `ux_os_work_orders_pmoc_period`; one generator service; no `MaintenancePlanAsset`; no CREA / 100% conforme.

---

## DOMAIN

### MaintenancePlan scheduling

| Field | CLR | DB | Rules |
|---|---|---|---|
| `IntervalDays` | `int` | `integer NOT NULL` | 1..3650 inclusive |
| `FirstDueDate` | `DateOnly` | `date NOT NULL` | Brazil civil; past / today / future all valid |

Remove: `Frequency`. Do not add `ScheduleMode`, `LastMaintenanceAt`, `NextMaintenanceAt`, `ComplianceStatus`.

Edits:

- Changing `IntervalDays` immediately changes **derived** next due for **Executed** assets. No WO mutation.
- Changing `FirstDueDate` affects **NeverExecuted** assets only. Executed assets ignore it.

Newly eligible asset with no Completed PMOC WO uses `FirstDueDate` even if the asset became eligible after that date (may already be Overdue). No per-asset onboarding date.

### History / due (derived)

```
PmocHistoryStatus: NeverExecuted | Executed
PmocDueStatus:     NotDue | DueToday | Overdue
needsAttention  =  DueToday || Overdue
```

NeverExecuted alone is **not** attention.

### GlobalMaintenanceTemplate

Checklist / provenance only. Remove `Frequency`. Do **not** add interval fields. Clone: tenant request supplies `intervalDays` + `firstDueDate`. Seed GUID unchanged.

### Calculator

`PmocDueCalculator.Compute(intervalDays, firstDueDate, asOfDate, lastCompleted?)`.

Consumers: Coverage V3, Slice 3 Hangfire. Not duplicated in either.

### Auto generation (final — Slice 3, not this slice)

```
plan.IsActive && plan.AutoGenerateEnabled
→ each eligible asset with effectiveNextDueDate <= BrazilToday
→ skip if relevant Pending/InProgress PMOC WO exists
→ GenerateAsync(ScheduledDate = effectiveNextDueDate)
```

Slice 1 job **does not** implement this. It returns without creating OS.

### Manual generation

`POST /api/work-orders/from-plan` stays first-class. Caller `ScheduledDate`. Completing that PMOC-linked WO **does** reset the interval later.

---

## SLICE 1 SCOPE

Include: domain, remove Frequency + PmocDueCalendar, destructive forward migration (file only), plan CRUD + from-template contract, calculator, history/due enums, fail-closed job, tests, this spec.

Exclude: Coverage V3 **final** contract (owned by Slice 2), interval Hangfire generation, WEB, H6, H7, PROD/DEV apply of the destructive migration, Slice 2+.

Coverage compile stance (Slice 1, superseded by Slice 2): drop calendar fields (`frequency`, `lastDueDate`, `isDueToday`); rows use calculator + `historyStatus` + `dueStatus` + `needsAttention`; `wouldBeConsideredByGenerator = false` while the job is fail-closed.

---

## MIGRATION

Forward EF only. Order:

1. `DELETE FROM os.work_orders WHERE maintenance_plan_id IS NOT NULL` (tasks cascade).
2. `DELETE FROM pmoc.maintenance_plans` (plan tasks cascade).
3. Drop `maintenance_plans.frequency`.
4. Add `interval_days`, `first_due_date`, CHECK 1..3650.
5. Drop `global_maintenance_templates.frequency`.

Do not delete: tenants, units, assets, users, permissions, rentals, inventory, **manual** WorkOrders (`maintenance_plan_id IS NULL`), ficc non-PMOC data.

FK: `WorkOrder.MaintenancePlan` Restrict → delete WOs **before** plans. `WorkOrderTask` Cascade from WO. `PlanTask` Cascade from plan. `WorkOrderTask.PlanTaskId` SetNull.

---

## SLICE 1 PRECHECK (read-only, 2026-09-19)

Not applied. Aggregates only.

| Env | Plans | PMOC-linked WO | WO tasks | Manual WO preserved | Templates | Plan tasks |
|---|---|---|---|---|---|---|
| DEV `jzptnjyzijklutinpxag` | 5 | 10 | 29 | 6 | 1 | 21 |
| PROD `kbptdzfbngelzdhriyhf` | 3 | 2 | 12 | 2 | 1 | 14 |

FK (both envs, `confdeltype`): `os.work_order_tasks.work_order_id` → CASCADE; `os.work_orders.maintenance_plan_id` → RESTRICT; `pmoc.plan_tasks.maintenance_plan_id` → CASCADE; `os.work_order_tasks.plan_task_id` → SET NULL; `pmoc.maintenance_plans.source_template_id` → SET NULL; `pmoc.global_template_tasks` → CASCADE. No FK from notifications. No unexpected blocker. Delete linked OS first, then plans.

---

## SLICE 2 SCOPE — Coverage V3

Endpoint: `GET /api/maintenance-plans/{id}/coverage` (`pmoc` + `pmoc.plans.read`). Tenant 404 for missing/foreign plan. No new permission. Technician remains without `pmoc.*`.

**Filter: NONE.** Phase 3 does not bind a server-side operational-status (or due/history) query filter. Expected cardinality is small; H6/H7 remain NO. Every currently eligible asset is returned.

Root: `planId`, `asOfDate` (one Brazil today for the whole response), `intervalDays`, `firstDueDate`, `isActive`, `autoGenerateEnabled`, `eligibleAssetCount`, `wouldBeConsideredByGenerator`, `summary`, `assets`.

Removed Phase 2 fields: `frequency`, `lastDueDate`, plan-level calendar `nextDueDate`, `isDueToday`, `operationalStatus`, `assetsWithPmocHistory`, coverage/compliance %, CREA, `DueSoon`.

Per-asset: `assetId`, `name`, `tag`, `historyStatus` (`NeverExecuted` | `Executed`), `lastMaintenance` (null or latest Completed PMOC), `effectiveNextDueDate`, `dueStatus` (`NotDue` | `DueToday` | `Overdue`), `needsAttention`, `openWorkOrder`.

`lastMaintenance` ordering: `CompletedDate DESC`, `ScheduledDate DESC`, `Id DESC`. Uses `CompletedDate` only. No `ScheduledDate` fallback.

Due: exclusively `PmocDueCalculator`. Coverage maps `due.NextDueDate` → `effectiveNextDueDate`. Do not duplicate interval math.

Summary: `eligibleAssets`, `assetsNeverExecuted`, `assetsExecuted`, `assetsNotDue`, `assetsDueToday`, `assetsOverdue`, `assetsNeedingAttention`, `assetsWithOpenWorkOrder`.

Invariants: `eligibleAssets = never + executed = notDue + dueToday + overdue`; `assetsNeedingAttention = dueToday + overdue`. NeverExecuted is **not** attention by itself.

`wouldBeConsideredByGenerator = plan.IsActive && plan.AutoGenerateEnabled && ANY eligible DueToday|Overdue`. Non-promissory (open OS / duplicate / other generator safety may still skip creation). Does **not** call `PmocEngineJob`.

C1 `openWorkOrder`: InProgress before Pending, then `ScheduledDate ASC`, `Id ASC`. Independent of `dueStatus`.

Eligibility unchanged. `IsActive` / `AutoGenerateEnabled` do **not** hide Coverage rows. `RequiresMaintenance` is **not** a gate.

Sort: Overdue, DueToday, NotDue, then tag, name, AssetId.

Query: plan + currently eligible assets + plan-linked non-Canceled WorkOrders (enough for last Completed + C1). Group/join in memory. No N+1. Do not persist due state. Manual `MaintenancePlanId=null` WOs never affect Coverage.

No new migration. Shared DEV stays on Phase 2: do not apply `20260920002154_ApplyPmocOsPhase3FinalScheduling`; do not deploy Phase 3 API to Railway DEV.

Slug: `feat/pmoc-os-phase3-coverage-v3`. Squash → `develop`. Do not merge until Fable ALLOW + Human. Do not start Slice 3.

---

## DEPLOYMENT (later gate — do not execute in Slices 1–2)

Not rolling compatible. Later: stop relevant PROD traffic → apply destructive PMOC migration → deploy Phase 3 API → recreate fixtures → API smoke → deploy Phase 3 WEB → integrated smoke → reopen. Phase 3 is **not** released to PROD until Slice 3 restores automatic generation.

---

## OUT OF SCOPE

`MaintenancePlanAsset`, per-asset overrides, DueSoon, cron UI, template interval fields, reporting/CREA/%, attachments, offline tech, Technician `pmoc.*`, H6, H7, Phase 1 hardening, repo-wide migration baseline, wiping non-PMOC data.

---

## IMPLEMENTATION SLICES

| Slice | What |
|---|---|
| **1** | Schema + plan/template API + calculator + fail-closed job (merged) |
| **2** (this) | Coverage V3 public contract |
| **3** | Hangfire interval generation + concurrency |
| **4** | WEB |

Then DEV QA, API/migration PROD gate, WEB PROD gate, closeout — each Human-authorized.

Slug Slice 1: `feat/pmoc-os-phase3-final-scheduling-foundation` (squash-merged). Slice 2: `feat/pmoc-os-phase3-coverage-v3`. Squash → `develop`. Do not merge until Fable ALLOW + Human.
