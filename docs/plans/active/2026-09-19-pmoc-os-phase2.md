# 2026-09-19-pmoc-os-phase2

Status: **RELEASED_PROD** (closed 2026-09-24). Historical handoff. Not an active implementation.

```
ROLVIX_PMOC_OS_PHASE2 = RELEASED_PROD
PMOC_OS_ACTIVE_IMPLEMENTATION = NO
H6 = NO
H7 = NO
```

Phase 2 shipped the first Coverage read model. Its calendar-frequency scheduling (`PmocDueCalendar`, Monthly = day 1, Weekly = Monday) was intentionally superseded by Phase 3. Decisions D1–D4, H6, H7, and C1–C3 below are history. D1 is not current product behavior. Current due math is `PmocDueCalculator` in the Phase 3 spec. Do not implement from this file.

This file remains the historical Phase 2 product/domain handoff.

Locked Human Decisions: **D1–D4**, **H6 = NO**, **H7 = NO**, plus contract clarifications **C1–C3**. Do not reopen them.

Shipped scope was Slices 1–2 only. No plan-list aggregates. No Inventory/Asset PMOC surface.

Parent of this plan: `docs/plans/active/2026-09-18-pmoc-os-phase1.md` (released).

---

## OBJECTIVE

For each **currently eligible** asset of a tenant PMOC plan, let an Admin / maintenance manager see whether the asset is covered, its last relevant completed PMOC WorkOrder, the plan’s next calendar due date, and a small operational status — so the product answers: **“Which assets under this PMOC plan need attention?”**

Phase 2 is a **derived read model** on top of the Phase 1 generation loop. It is not a new scheduler.

---

## LOCKED_DECISIONS

| ID | Decision | Phase 2 implication |
|---|---|---|
| **D1 — Due model** | Keep current **calendar-frequency** semantics (`PmocDueCalendar`). Monthly = day 1, Weekly = Monday, etc. | Do **not** switch to “interval since last successful execution”. Do **not** retune Hangfire cron, timezone, or generation schedule. Interval-from-last is a **future** architectural decision. |
| **D2 — AutoGenerate** | `AutoGenerateEnabled` remains a supported tenant path. Manual **Gerar OS** remains first-class. | Phase 2 is **not** an AutoGenerate redesign. No second generator, no scheduling engine, no job orchestration UI, no cron configuration. Read-only eligibility visibility is allowed. |
| **D3 — Applicability** | `MaintenancePlan` → Unit → AssetCategory → eligible Assets. | No `MaintenancePlanAsset`. No per-asset plans. Coverage is **derived** from currently eligible assets. |
| **D4 — Technician** | Technician continues without `pmoc.*`. | Technician experience stays WorkOrder execution + PMOC origin on the OS. No PMOC administration. |

Also still locked from Phase 1 (not reopened): one generator (`IWorkOrderGenerationService`) for Hangfire + `POST /api/work-orders/from-plan`; `WorkOrderTask` snapshot; no CREA / “100% conforme”; no technician `pmoc.*`.

| ID | Decision | Phase 2 implication |
|---|---|---|
| **H6 — Plan list** | **NO** | Do not add overdue/never-executed counts to `GET /api/maintenance-plans` or a `coverage-summaries` endpoint in this ship. |
| **H7 — Inventory** | **NO** | Do not add PMOC last/next/status to Inventory/Asset UI. |

### Contract clarifications (override earlier draft text)

**C1 — openWorkOrder.** There may be multiple open WorkOrders for one plan/asset across different dates. For the singular `openWorkOrder` field, select deterministically: (1) `InProgress` before `Pending`, (2) then `ScheduledDate` ascending, (3) then `Id` ascending. `assetsWithOpenWorkOrder` counts eligible assets with ≥ 1 open relevant WO. Do **not** assume the period unique index implies one open WO per asset.

**C2 — wouldBeConsideredByGenerator.** Exact formula:

`plan.IsActive && plan.AutoGenerateEnabled && PmocDueCalendar.IsDueToday(plan.Frequency, asOfDate)`

Do **not** include `eligibleAssets > 0` or `hasPlanTasks`. A plan may be considered by the job and produce zero OS. Do not change `PmocEngineJob`.

**C3 — asOfDate.** Use one Brazil-local `asOfDate` for the complete calculation (`BrazilTimeZone.GetToday` via injected `TimeProvider`). The same value drives `lastDueDate`, `nextDueDate`, status, and `wouldBeConsideredByGenerator`. Expose `asOfDate` on the coverage response. Do not introduce a second timezone implementation.

---

## DOMAIN_MODEL

Civil dates in this spec use **Brazil today** (`HangfireExtensions.GetBrazilToday()` / `BrazilTimeZone.GetToday`). That is the same clock the generator uses. Do not invent a second timezone.

### Eligible asset

An asset is eligible for a plan **iff all** of the following hold (same rules as `WorkOrderGenerationService` + `PmocEngineJob`):

1. Same tenant as the plan (GQF / `ITenantScoped`; Hangfire job already loads tenant-scoped rows with GQF off by design — the read API must use normal tenant context).
2. `asset.UnitId == plan.UnitId`
3. `asset.CategoryId == plan.AssetCategoryId`
4. `asset.Status == AssetStatus.Active` (**not** `Inactive`, **not** `Maintenance`)
5. `asset.ScheduledDeletionAt == null`

**Not** an eligibility gate in current generation, therefore **not** a Phase 2 gate:

- `Asset.RequiresMaintenance` — Inventory metadata only. Adding it here would silently diverge from Hangfire / Gerar OS.

WEB `GenerateWorkOrderDialog` currently client-filters unit + category + `Active`, and does **not** see `ScheduledDeletionAt`. Coverage **must** follow the **API generator**, not the dialog’s weaker filter.

`plan.IsActive` and `plan.AutoGenerateEnabled` do **not** change eligibility. Inactive plans still have a coverage set. Generation remains blocked by existing API (`ArgumentException` “not active”) and Hangfire (`IsActive && AutoGenerateEnabled`).

#### Eligibility changes (current coverage, historical OS)

Coverage always reflects **current** eligibility. Historical WorkOrders remain historical (`GET /api/work-orders?maintenancePlanId=` / related OS). They are never rewritten.

| Event | Coverage rows | Historical WorkOrders |
|---|---|---|
| Asset becomes Inactive / Maintenance / scheduled-deletion | Dropped | Unchanged |
| Asset changes category or unit | Follows new match (leave old plan, possibly enter another) | Unchanged; still linked by `MaintenancePlanId` |
| Plan changes `UnitId` / `AssetCategoryId` / `Frequency` | Recomputed from live plan header (`UpdateAsync` already allows this) | Unchanged |
| Plan becomes inactive | Rows remain (same eligible assets); header shows inactive; generator will not run | Unchanged |
| Plan deleted | Impossible if any WO exists (`409 PLAN_IN_USE`) | n/a |

There is **no** eligibility history table. Do not invent “was this asset eligible in August?”. Overdue is computed only for **currently eligible** assets against the **live** calendar.

### Relevant WorkOrder

A WorkOrder participates in this plan’s maintenance-state calculation **iff**:

- `TenantId` = current tenant
- `MaintenancePlanId` = this plan id
- `AssetId` = the eligible asset
- `Status != Canceled`

Canceled is excluded from uniqueness today (`ux_os_work_orders_pmoc_period` filter `status <> 'Canceled'`) and from generation duplicate checks. Canceled WOs are **not** last maintenance, **not** period coverage, **not** open work.

`POST /api/work-orders` (manual client-task OS, `MaintenancePlanId` null) **never** participates. That path is not PMOC generation.

| Status | In last-executed? | Covers current calendar period? | Open / in-flight? |
|---|---|---|---|
| `Canceled` | No | No | No |
| `Pending` | No | No | Yes |
| `InProgress` | No | No | Yes |
| `Completed` | Yes (candidate) | Yes if `ScheduledDate >= lastDueDate` | No |

`CompletedDate` is set to `DateTimeOffset.UtcNow` when status becomes `Completed`, and cleared when reverted to `Pending` or `InProgress` (`WorkOrderService.UpdateStatusAsync`). Completing still requires mandatory tasks filled.

### Last maintenance

For each eligible asset + plan:

`lastMaintenance` = the unique Completed relevant WorkOrder chosen by:

1. `CompletedDate` descending (null `CompletedDate` on `Completed` is unexpected after Phase 1; fallback `UpdatedAt` then `CreatedAt` for display only)
2. then `ScheduledDate` descending
3. then `Id` descending

Return:

- `workOrderId`
- `scheduledDate` (`DateOnly` — PMOC period key)
- `completedDate` (`DateTimeOffset?` — actual execution instant, UTC)

If none: `lastMaintenance = null`. That is the **NeverExecuted** input. Do not fake a date.

**LAST EXECUTED MAINTENANCE** = that Completed WorkOrder. Pending / InProgress never count, even if scheduled for today.

### Next due

**Do not change calendar semantics.** Extend `PmocDueCalendar` with **pure helpers** used only by the read model (generation continues to call `IsDueToday` only):

| Helper | Meaning |
|---|---|
| `IsDueToday(frequency, today)` | **Unchanged.** Daily always; Weekly Monday; Monthly day 1; Quarterly 1 Jan/Apr/Jul/Oct; Semiannual 1 Jan/Jul; Annual 1 Jan. |
| `LastDueOnOrBefore(frequency, today)` | Most recent calendar due date `<= today`. Daily = today. |
| `NextDueOnOrAfter(frequency, today)` | Next calendar due date `>= today`. If today is a due day, this **is** today (Hangfire would generate today). |

`nextDueDate` is **plan-level**, identical for every eligible asset. That is a consequence of D1, not a defect to “fix” with per-asset math.

Display / DTO:

- `asOfDate` = Brazil today
- `lastDueDate` = `LastDueOnOrBefore(plan.Frequency, asOfDate)`
- `nextDueDate` = `NextDueOnOrAfter(plan.Frequency, asOfDate)`

**Identified ambiguity (do not invent scheduling to hide it):**

- The due model cannot produce a **per-asset** next date. Last completed OS does **not** shift `nextDueDate`.
- Manual Gerar OS may use a `ScheduledDate` that is **not** a calendar due day. Period coverage therefore uses `ScheduledDate >= lastDueDate` on Completed WOs, **not** “exactly equal to the due day”. A completed OS scheduled inside the current calendar window counts as covering the current period.
- A future-dated Completed OS with `ScheduledDate >= lastDueDate` also covers the current period. Accept that edge; do not add interval-from-last rules.
- Changing `plan.Frequency` immediately changes `lastDueDate` / `nextDueDate` / status. Historical `ScheduledDate` values stay as stored.

### Operational status

**DueSoon is excluded** from the Phase 2 contract. It needs a threshold (fixed, frequency-derived, or configurable) that the calendar model does not already have. Simplest valid model:

```
NeverExecuted | OnTrack | Overdue
```

Wire codes (JSON enum strings, same style as `WorkOrderStatus`). WEB i18n, not extra domain states.

Let:

- `hasCompletedEver` = exists relevant Completed WO for this asset+plan
- `coversCurrentPeriod` = exists relevant Completed WO with `ScheduledDate >= lastDueDate`

Then, **deterministic**:

| Status | Rule |
|---|---|
| `NeverExecuted` | `!hasCompletedEver` |
| `OnTrack` | `coversCurrentPeriod` |
| `Overdue` | `hasCompletedEver && !coversCurrentPeriod` |

Properties:

- `NeverExecuted` is explicit and is **not** folded into `Overdue`.
- Daily: `lastDueDate` is always today, so OnTrack requires a Completed OS with `ScheduledDate >= today`. That is the honest calendar reading (due every day).
- On a monthly due day (1st), last month’s completion does **not** cover (`ScheduledDate >= today` fails). Assets with history show `Overdue` until this period’s OS is completed. That is useful on generation morning; it is **not** due-day grace.
- Open Pending/InProgress WOs do **not** change the enum. They are a separate row field `openWorkOrder` selected per **C1**.

Do not add `InFlight`, `DueSoon`, `Compliant`, or `AtRisk`.

---

## COVERAGE

Computed only over **currently eligible** assets. Vanity / compliance percentages are out of scope.

| Metric | Derivation | Why it helps |
|---|---|---|
| `eligibleAssets` | Count of eligible assets | Denominator. Empty category/unit is an operator problem. |
| `assetsNeverExecuted` | Status = NeverExecuted | “Never ran PMOC on this asset.” |
| `assetsOverdue` | Status = Overdue | History exists, current calendar window not completed. |
| `assetsOnTrack` | Status = OnTrack | Current window covered. |
| `assetsWithPmocHistory` | `Overdue + OnTrack` (= `hasCompletedEver`) | How much of the set has ever been executed. |
| `assetsWithOpenWorkOrder` | Eligible assets with a Pending/InProgress relevant WO | “OS already generated, not finished.” |

Omit: coverage %, “compliance score”, CREA labels, counts of Canceled WOs, Hangfire success rate.

`assetsNeverExecuted + assetsOverdue` is the **attention set**. Default WEB sort puts that set first.

---

## API

Inspected convention: `MaintenancePlansController` is `[Route("api/maintenance-plans")]`, `[RequireActiveModule(PlatformModules.Pmoc)]`. Nested reads already exist (`GET .../asset-categories`). Work-order **writes** stay on `/api/work-orders/from-plan`. Lists in this module are **unpaginated** `IReadOnlyList` (plans, related OS).

### Endpoint

```
GET /api/maintenance-plans/{id}/coverage
```

- Module: `pmoc`
- Permission: `pmoc.plans.read` only
- Tenant: required (`ITenantProvider`); 401/403 existing patterns
- Missing plan: **404**
- Technician / any principal without `pmoc.plans.read`: **403** (module gate + permission). Do not add `pmoc.*` to `TechnicianLegacyKeys`.
- Do **not** require `os.work_orders.read` on this GET (plan managers can see coverage). WEB **navigation** to `/os/:id` stays wrapped in existing `os.work_orders.read`. Gerar OS stays `os.work_orders.create`.
- Do **not** require `inventory.assets.read`. PMOC already uses Asset Registry, not Inventory ownership.

Optional query (keep minimal):

| Query | Default | Notes |
|---|---|---|
| `status` | all | Repeatable or comma list: `NeverExecuted`, `OnTrack`, `Overdue` |
| *(no pagination)* | all eligible rows | Same cardinality assumption as current plan/OS lists. If a tenant exceeds ~500 eligible assets on one plan, add pagination later — do not design it now. |

No sort query. Server applies the product sort below.

### Persistence / migration

| | |
|---|---|
| **Persistence required** | **NO** |
| **Migration likely** | **NO** |

Do **not** add `LastMaintenanceAt`, `NextMaintenanceAt`, or `ComplianceStatus` on `Asset` (or on `MaintenancePlan`). Source of truth remains Assets, MaintenancePlans, WorkOrders, snapshots, `PmocDueCalendar`.

### Response DTO (conceptual)

`MaintenancePlanCoverageResponse`:

```
planId
asOfDate                 // Brazil today
frequency                // existing MaintenanceFrequency
lastDueDate
nextDueDate              // plan-level calendar
isActive
autoGenerateEnabled
isDueToday               // PmocDueCalendar.IsDueToday(frequency, asOfDate)
hasPlanTasks
eligibleAssetCount       // same as summary.eligibleAssets
wouldBeConsideredByGenerator
  // C2: isActive && autoGenerateEnabled && isDueToday
  // Read-only operator trust. Not Hangfire state. Not “will succeed”.
  // Not gated on eligibleAssets or hasPlanTasks.
summary
  eligibleAssets
  assetsWithPmocHistory
  assetsNeverExecuted
  assetsOverdue
  assetsOnTrack
  assetsWithOpenWorkOrder
assets[]                 // per eligible asset, sorted
```

`wouldBeConsideredByGenerator` does **not** inspect Hangfire, cron, last job run, or duplicate-key outcomes.

Each `assets[]` item:

```
assetId
name
tag
lastMaintenance: { workOrderId, scheduledDate, completedDate } | null
nextDueDate              // echo of plan nextDueDate (explicitly the same for all rows)
operationalStatus        // NeverExecuted | OnTrack | Overdue
openWorkOrder: { workOrderId, status, scheduledDate } | null
```

### Sorting

1. `Overdue`
2. `NeverExecuted`
3. `OnTrack`
4. `tag` ascending, then `name` ascending

### Filtering

Server-side `status` only. Name/tag search can be client-side on the detail page (expected row counts are category-scoped).

### Aggregate on plan list

**Not** part of `GET /api/maintenance-plans` in the initial contract. Default list stays cheap. Optional later endpoint (Human Decision H6):

```
GET /api/maintenance-plans/coverage-summaries
```

One set-based query, `summary` per plan id. Do not N+1 from the list page.

---

## WEB

### Plan detail (primary — in scope)

`PmocPlanDetailPage` (`/pmoc/:id`), new section **Coverage / Situação da manutenção** (i18n), conceptually **above** related OS:

- Header chips from `summary` + read-only auto-generation strip (`autoGenerateEnabled`, `nextDueDate`, `eligibleAssetCount`, `wouldBeConsideredByGenerator` / `isDueToday`).
- Table rows: Asset (name + tag), Last maintenance (`completedDate` display in tenant locale; empty = never), Next due (`nextDueDate`), Status badge, actions:
  - Open last OS if `lastMaintenance` and `os.work_orders.read`
  - Open in-flight OS if `openWorkOrder`
  - Gerar OS remains the existing dialog (`os.work_orders.create`); still first-class; still blocked by API when plan inactive

Do not put PMOC admin controls on Technician routes.

### Plan list (optional — H6)

`MaintenancePlansPage` today: name, category, frequency, active, origin, auto. **Do not** turn it into a dashboard.

Recommendation: **omit** overdue counts from the initial Phase 2 WEB. Detail is the operational surface. If H6 = yes, add a compact `overdue` + `neverExecuted` count per row from `coverage-summaries`, not a chart.

### Asset experience (optional — H7)

There is **no** Asset detail route today (`AssetsPage` list + `/ativos/categorias`). Inventory owns `/ativos`.

Recommendation: **omit** PMOC maintenance state from Inventory in Phase 2. Adding it would couple Inventory UI to `pmoc` + a new read, and would duplicate PMOC management. If H7 = yes later: read-only chips only, gated by module `pmoc` + `pmoc.plans.read`, no Gerar OS / plan edit from Assets.

### Auto-generation visibility

**Include**, read-only, on the Coverage header (data already on the coverage DTO). Helps operator trust (“would the 06:00 job consider this plan today?”).

Do **not** expose: Hangfire dashboard, cron, timezone, last job exception, worker count, per-asset generate logs.

### History

**Reuse** `PlanRelatedWorkOrders` (`GET /api/work-orders?maintenancePlanId=`). No new audit/BI page. Coverage rows deep-link to the relevant OS. That is operational context, not reporting.

---

## PERFORMANCE

Expected shape for one plan (avoid N+1):

1. Load plan by id (existing, tenant GQF) — 404 if missing.
2. One query: eligible assets (`UnitId`, `CategoryId`, `Status == Active`, `ScheduledDeletionAt == null`).
3. One query: WorkOrders where `MaintenancePlanId == id` and `Status != Canceled` (or two queries: Completed vs Pending/InProgress). Project `Id, AssetId, Status, ScheduledDate, CompletedDate, CreatedAt` only.
4. Memory-group by `AssetId`; compute lastMaintenance / open / status; attach only eligible assets.

Do **not** query WorkOrders per asset.

Existing indexes (sufficient to ship):

- `ux_os_work_orders_pmoc_period` (`tenant_id, maintenance_plan_id, asset_id, scheduled_date`) unique filtered
- `ix_work_orders_maintenance_plan_id`
- `ix_work_orders_tenant_id_asset_id_scheduled_date`
- `ix_work_orders_tenant_id_status`

**Migration candidate (do not create now):** covering index

```
ix_work_orders_tenant_id_maintenance_plan_id_status
  (tenant_id, maintenance_plan_id, status)
  INCLUDE (asset_id, scheduled_date, completed_date)
```

Add only if `EXPLAIN` on a real tenant shows the plan-id lookup is hot. Not required for correctness. **Not** a Phase 2 domain schema.

---

## OUT_OF_SCOPE

- Interval-from-last scheduling
- Hangfire cron / TZ redesign; pausing or retuning `pmoc-engine`
- Second generator; job orchestration UI; cron editing
- `MaintenancePlanAsset` / per-asset plans
- Persisted `LastMaintenanceAt` / `NextMaintenanceAt` / `ComplianceStatus` on Asset
- Template CMS; template authoring / version management beyond Phase 1
- CREA compliance claims; “100% compliant” labels; coverage %
- Attachments
- Reporting / BI suite; dashboard redesign
- Offline technician mode; `offlineSync.ts` expansion
- Technician `pmoc.*`
- Marketplace
- Deleting PROD E2E fixtures
- Reconstruction of historical nulled lineage
- Major OS redesign; changing `POST /api/work-orders` into a PMOC generator
- `DueSoon` threshold
- Using `RequiresMaintenance` as a new eligibility gate
- Phase 1 hardening items below (separate track)

---

## PHASE1_HARDENING

Do **not** treat these as Phase 2 domain requirements. Separate track, after or beside Phase 2, never blocking this spec.

### SHOULD_FIX

| Item | Repo | Notes |
|---|---|---|
| `OperationCanceledException` logged as 500 while HTTP is 499 | API | Classify/log as client abort / 499. Not a product 5xx. |
| Gerar OS on inactive plan | WEB | API already rejects; hide/disable + copy on detail. |
| Checklist reorder controls | WEB | H5 already allows reorder in API; WEB append/remove only. |
| Novo personalizado permission visibility | WEB | Route `/pmoc/novo` is write-gated; list **Novo Plano** button is not `Can`-wrapped. |

### BACKLOG

- Expected 409 `PLAN_IN_USE` axios `console.error` noise
- WEB test timeout flakiness

### IGNORE

- Controlled inactive/canceled PROD E2E leftovers (Stage B clone + canceled WO)

### PR structure (recommended)

**Two isolated PRs, not one mega hardening PR, not four.**

1. **API** `fix/pmoc-os-client-abort-logging` — 499 / `OperationCanceledException` classification only.
2. **WEB** `fix/pmoc-os-phase1-ux` — inactive Gerar OS UX + Novo Plano `Can` + checklist reorder.

Do not mix hardening with Phase 2 coverage. Do not implement in this turn.

BACKLOG items: later, each its own tiny PR if they still hurt.

---

## HUMAN_DECISIONS

Do **not** reopen: calendar model, AutoGenerate existence, unit+category applicability, Technician access, DueSoon (excluded above), persistence of asset maintenance columns (not required).

None for Slices 1–2. H6 = **NO**. H7 = **NO**. Portuguese labels (`Nunca executado` / `Em dia` / `Atrasado`) are i18n during WEB Slice 2, not a domain gate.

---

## IMPLEMENTATION_PLAN

Do **not** start any slice until an explicit implementation gate.

Slug prefix: `feat/pmoc-os-phase2-<slice>`. Same name on api + web when a slice is cross-repo. API first. Squash to `develop`. No mega-PR. No frontend mirror of this file.

### Slices

| Slice | Repo | What | Migration | Fable merge-risk | UI reviewer | PROD data precheck |
|---|---|---|---|---|---|---|
| **1** | API | `PmocDueCalendar` last/next helpers + tests; `GET /api/maintenance-plans/{id}/coverage` + tests (authz, tenant, eligibility, status matrix, canceled excluded, manual OS excluded, set-based query) | **NO** (unless a later index-only follow-up) | **YES** — new FE↔BE contract + authz | No | Optional: count eligible assets per PROD plan (pagination bound). No unique-index collision. |
| **2** | WEB | Plan detail Coverage section + i18n + tests; consume Slice 1; keep related OS and Gerar OS | NO | **YES** if Slice 1 contract not yet Fable-reviewed; else may `INTEGRATION_MERGE_REVIEW` with Slice 1 or skip if GLM high + local UI. Prefer one Fable on the **contract** (Slice 1) rather than two. | **YES** (optional `ui-implementer` for table/chips) | No |
| **3** | API+WEB | **Only if H6 = yes.** `GET .../coverage-summaries` + list counts | NO | YES (contract) | Optional | Same optional counts |
| **4** | WEB | **Only if H7 = yes.** Read-only Inventory chips | NO | YES (cross-module) | YES | No |

Index candidate: own `chore/` PR **after** Slice 1 if EXPLAIN justifies it. That PR **does** need migration + Fable (schema).

### Model routing (when implementation is gated)

- Canonical plan: this file on `develop` (docs-only until implementation starts).
- `rolvix-architect` (`glm-5.2`): compact handoff **if available** at implementation start. If unavailable: `SUBAGENT_UNAVAILABLE` — do not claim another model was the architect.
- `rolvix-deep-architect` (Fable): **not** for this planning turn. Merge Risk Gate on Slice 1 (contract) per `AGENTS.md`.
- Slice API: `api-implementer` (`grok-4.6`) → `api-reviewer` (`grok-4.6`)
- Slice WEB: `web-implementer` (`grok-4.6`) → optional `ui-implementer` (`kimi-k3`) → `web-reviewer` (`grok-4.6`)
- One writer per working tree
- Parent: Git, PR, Merge Risk Gate, squash to `develop`

### Docs to update **during implementation** (not this turn)

- `ROADMAP.md` (api + web) when slices merge
- `docs/context-packs/active/pmoc-os.md`
- WEB i18n
- Phase 1 spec “Do not / Per-asset last/next/overdue (Phase 2)” becomes done by this plan — update that bullet when Slice 1 ships

### Invariants that must not break

1. Tenant isolation on the new GET (GQF). Hangfire GQF-off remains job-only.
2. Never authorize by role name `Technician`.
3. Do not change `PmocEngineJob` filters, cron, or `IsDueToday`.
4. Do not change `ux_os_work_orders_pmoc_period`.
5. Do not persist duplicated maintenance state on Asset.
6. Manual `POST /api/work-orders` stays out of PMOC coverage.
7. Canceled WOs stay out of coverage and uniqueness.
8. No CREA / 100% conforme copy.
9. Technicians do not gain `pmoc.*`.
10. Do not migrate from a laptop; do not touch `main` in implementation PRs.

---

## Test seams (when implementing)

- Extend `PmocDueCalendar` tests for last/next helpers (all six frequencies, including due-day vs mid-period).
- New coverage service/controller tests: ineligible statuses, scheduled deletion, other unit/category, canceled WO, Pending does not cover, Completed `ScheduledDate` vs `lastDueDate`, manual OS ignored, inactive plan still returns rows, 404, permission 403.
- WEB: detail section rendering, empty eligible set, status badges, OS links `Can`-gated.

Existing: `tests/Platform.Api.Tests/WorkOrders/PmocEngineJobTests.cs` must remain green **without** job behavior changes.
