# 2026-09-18-pmoc-os-phase1

Status: **approved** (product Human Gates H1–H5 locked)

Implementation must not start until the parent receives an explicit implementation gate. This file is the canonical Phase 1 handoff for implementers.

Baseline (planning SHA, `develop`):

| Repo | SHA |
|---|---|
| vlr-api | `8953f6ae4ac12b82abd84ba4611636126c4ad21f` |
| vlr-web | `85ce88f272e792d1c3016bb34c930ea6ce6994b6` |

## Goal / Problem

PMOC and OS already exist as a partial operational backbone (global seed template, tenant `MaintenancePlan` + `PlanTask`, Hangfire calendar generation, Work Order execution snapshots). Phase 1 productizes the loop:

**Biblioteca Rolvix → server clone → tenant PMOC (customize) → Gerar OS (manual or explicit auto) → OS execution from snapshot.**

Without: a second generator, silent auto-enable for new plans, CREA certification claims, template CMS, asset-state scheduling, offline, or technician PMOC access.

## Visible behavior

- Staff with `pmoc` browse **Biblioteca Rolvix**, preview a published template, and **Usar modelo** (server clone).
- Staff create **PMOC personalizado** (no lineage).
- Plan detail: overview, origin, active + “Gerar ordens de serviço automaticamente”, frequency, unit/category, checklist edit, related OS, Gerar OS.
- New plans: auto-generation **off**. Existing PROD plans: auto-generation **on** (migration backfill).
- Hangfire `pmoc-engine` (06:00 Brazil, same calendar/asset rules) only processes `IsActive && AutoGenerateEnabled`.
- OS originating from a plan shows `Origem: PMOC {SourcePlanName}` and links to `/pmoc/:id` only when the user has `pmoc.plans.read` and module `pmoc` is active.
- Technicians keep executing `/os/:id` from `WorkOrderTask` snapshots. No new `pmoc.*` keys for Technician.

## Repositories

- vlr-api
- vlr-web

Merge order: **API first**. WEB slices start only after the contracts they call exist on `develop` (or a coordinated pair). Same slug prefix: `feat/pmoc-os-phase1-<slice>`. **No mega-PR. No frontend mirror of this spec.**

## Relevant existing ADR / rules

- `CONTEXT.md` — modules `pmoc` / `os`; Asset Registry (not Inventory); Permission keys
- `docs/adr/0004-module-dependencies-asset-registry.md` — PMOC/OS use registry; PMOC needs a provisioning family
- `.cursor/rules/10-arquitetura.mdc` — Hangfire `pmoc-engine` 06:00; GQF off when `TenantId` is null
- `.cursor/rules/00-produto.mdc` / `20-convencoes.mdc`
- Tenant RBAC: `docs/plans/active/2026-08-27-tenant-rbac-v1.md`
- Do **not** apply `.cursor/rules/30-rentals.mdc` as PMOC rules

## Architecture route

- Product/technical plan: parent on `develop` (this file)
- `rolvix-architect` (`glm-5.2`): compact handoff **if available** at implementation start. If unavailable: `SUBAGENT_UNAVAILABLE` — do not claim another model was the architect
- `rolvix-deep-architect`: **not** for this planning turn. **Merge Risk Gate** on schema / concurrency / Hangfire PRs per `AGENTS.md`

## Execution route (future — do not invoke in the spec turn)

- Slice API: `api-implementer` (`grok-4.6`) → `api-reviewer` (`grok-4.6`)
- Slice WEB: `web-implementer` (`grok-4.6`) → optional `ui-implementer` (`kimi-k3`) for library/detail polish → `web-reviewer` (`grok-4.6`)
- One writer per working tree
- Parent: Git, PR, Merge Risk Gate, squash to `develop`

## Confirmed decisions (H1–H5)

| ID | Decision | Recorded |
|---|---|---|
| H1 | `AutoGenerateEnabled` independent of `IsActive`. Existing rows **true**. New rows **false**. Job: `IsActive && AutoGenerateEnabled`. Cron/TZ/calendar/assets unchanged. Phase 1 UI exposes the setting. | YES |
| H2 | Version on existing `GlobalMaintenanceTemplate`: `LibraryKey`, `Version`, `Status`, `SourceReferences`. No version table. Published = immutable. Future vN = new row, same `LibraryKey`. Lineage points at exact row. | YES |
| H3 | Unused plan: hard delete. Used (any WO with `MaintenancePlanId`): **409 `PLAN_IN_USE`**, deactivate via `IsActive=false`. WO→Plan FK **SetNull → Restrict**. WO Task→PlanTask stays **SetNull**. `SourcePlanName` snapshot. No reconstruction of already-nulled origins. | YES |
| H4 | Gerar OS technician **optional**. Pending + unassigned allowed. If supplied, existing assignee/permission rules. | YES |
| H5 | Checklist add/edit/remove/reorder allowed after WOs exist. Changes affect **future** OS only. Never mutate existing `WorkOrder` / `WorkOrderTask`. | YES |

Also locked (prior product direction, not reopened):

- Global templates are platform-owned; tenants clone, they do not edit the source
- Tenant plan is not a live view of the template
- No silent template auto-update
- Custom PMOC with null lineage; existing plans = Custom
- Applicability remains Unit + AssetCategory
- One shared generation service (manual + Hangfire)
- Offline is **not** Phase 1; keep snapshot invariants
- Copy: Biblioteca Rolvix / Modelos padrão / Fontes — not “Normas/CREA” or “100% conforme”

---

## 1. APPROVED PRODUCT DECISIONS

```
GlobalMaintenanceTemplate (LibraryKey + Version, Published immutable)
        ↓ SERVER CLONE
MaintenancePlan (OriginKind, SourceTemplateId, SourceTemplateVersion)
        ↓ tenant customizes PlanTasks
        ↓
manual Gerar OS  OR  Hangfire if IsActive && AutoGenerateEnabled
        ↓
WorkOrder (MaintenancePlanId Restrict, SourcePlanName)
        ↓
WorkOrderTasks SNAPSHOT (execution truth)
```

Custom path: `POST /api/maintenance-plans` → `OriginKind=Custom`, source fields null, `AutoGenerateEnabled=false` unless the client sends true.

UI copy for the flag: **“Gerar ordens de serviço automaticamente”**.

---

## 2. CURRENT DOMAIN

Verified on planning SHA.

| Entity | Table | Notes |
|---|---|---|
| `GlobalMaintenanceTemplate` | `pmoc.global_maintenance_templates` | Name, Description, Frequency, Jurisdiction, TargetEquipmentType. **One** seed: id `6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e`, name `PMOC Padrão ANVISA (RE 09) + NR-10` |
| `GlobalTemplateTask` | `pmoc.global_template_tasks` | Six seeded tasks; same template id |
| `MaintenancePlan` | `pmoc.maintenance_plans` | Tenant, Unit, Name, Description, Frequency, AssetCategory, IsActive, PlanTasks. **No lineage, no auto flag** |
| `PlanTask` | `pmoc.plan_tasks` | Created only at plan create; PUT header does not touch tasks |
| `WorkOrder` | `os.work_orders` | Asset, optional MaintenancePlanId (**SetNull**), optional AssignedUserId, Status, ScheduledDate, Notes |
| `WorkOrderTask` | `os.work_order_tasks` | Snapshot scalars + optional PlanTaskId (**SetNull**) |

Hangfire `PmocEngineJob` (`pmoc-engine`, `0 6 * * *`, Brazil TZ, WorkerCount=1): all `IsActive` plans, calendar `IsDueToday`, assets matching unit+category+Active+not scheduled-deletion, `AnyAsync` duplicate (non-unique index `ix_work_orders_tenant_id_maintenance_plan_id_asset_id_schedule`), copies PlanTasks, Status=Pending, no assignee.

`POST /api/work-orders` does **not** snapshot PlanTasks; client supplies tasks (`/os/nova` always `maintenancePlanId: null`).

WEB: `/pmoc` list, `/pmoc/novo` create + client-side template form-fill, `/os`, `/os/:id` (snapshot), `/os/nova`. No plan detail. CREA copy in i18n.

Permissions (keep): `pmoc.plans.read/write`, `pmoc.templates.read`, `os.work_orders.read/create/execute/assign`. Technician legacy: OS read/execute + `inventory.assets.read` only.

---

## 3. TARGET DOMAIN

### GlobalMaintenanceTemplate (additive)

| Field | Type | Rules |
|---|---|---|
| `LibraryKey` | string, max 80, required | Stable catalog identity. Seed: `pmoc-ar-condicionado-anvisa-nr10` |
| `Version` | int, required | Seed: `1`. Future editions = new row |
| `Status` | enum string `Published` \| `Deprecated` | Seed: `Published`. Library lists Published only |
| `SourceReferences` | string?, max 2000 | Optional sources/refs. Seed: text from Lei 13.589/2018, Anvisa RE 09, NR-10 — **not** CREA-certified |

Published rows are **immutable** in application code (no update/delete API for tenants; no mutate-in-place of tasks). Deprecated rows remain GET-by-id for lineage display.

Index: unique `(LibraryKey, Version)`.

### MaintenancePlan (additive)

| Field | Type | Rules |
|---|---|---|
| `OriginKind` | enum string `Custom` \| `RolvixTemplate` | Existing → `Custom`. Clone → `RolvixTemplate` |
| `SourceTemplateId` | Guid? FK → global template, **SetNull** | Null for custom |
| `SourceTemplateVersion` | int? | Frozen at clone; null for custom |
| `AutoGenerateEnabled` | bool, required | See §5 |

`IsActive` = operational/usable (detail, Gerar OS, appear as active).  
`AutoGenerateEnabled` = Hangfire may generate. Independent.

### WorkOrder (additive)

| Field | Type | Rules |
|---|---|---|
| `SourcePlanName` | string?, max 200 | Copied from `plan.Name` at generation. Backfill from current plan name where FK set |

`MaintenancePlanId` remains nullable (manual OS). FK **Restrict**.

### PlanTask / WorkOrderTask

No new columns. Snapshot remains title, input type, configuration, mandatory, order.

### Enums (new)

`MaintenancePlanOriginKind { Custom = 0, RolvixTemplate = 1 }`  
`GlobalTemplateStatus { Published = 0, Deprecated = 1 }`  

Store as string, max 32, like existing domain enums.

No `GlobalTemplateVersion` table. No `TenantPlanRevision` table.

---

## 4. DATABASE CHANGES

Schema `pmoc` / `os` only. Additive + one FK behavior change + one unique filtered index (gated by precheck).

1. `pmoc.global_maintenance_templates`: `library_key`, `version`, `status`, `source_references`; unique `(library_key, version)`; HasData update for the existing seed **preserving Ids**.
2. `pmoc.maintenance_plans`: `origin_kind` NOT NULL, `source_template_id` NULL FK SetNull, `source_template_version` NULL, `auto_generate_enabled` NOT NULL.
3. `os.work_orders`: `source_plan_name` NULL; FK to `pmoc.maintenance_plans` **OnDelete Restrict** (replace SetNull).
4. After precheck (§6): drop non-unique `ix_work_orders_tenant_id_maintenance_plan_id_asset_id_schedule`; create:

```text
ux_os_work_orders_pmoc_period
UNIQUE (tenant_id, maintenance_plan_id, asset_id, scheduled_date)
WHERE maintenance_plan_id IS NOT NULL AND status <> 'Canceled'
```

EF (indicative):

```csharp
builder.HasIndex(w => new { w.TenantId, w.MaintenancePlanId, w.AssetId, w.ScheduledDate })
    .IsUnique()
    .HasFilter("maintenance_plan_id IS NOT NULL AND status <> 'Canceled'")
    .HasDatabaseName("ux_os_work_orders_pmoc_period");
```

`status` is already stored as string (`HasConversion<string>()`). Filter must use `'Canceled'` (enum name), matching `ReservationQueueTicket` filtered unique style.

Keep other WO indexes.

`WorkOrderTask.PlanTaskId` stays SetNull. `PlanTask` cascade from plan stays (unused-plan delete).

---

## 5. MIGRATION / BACKFILL RULES

Do not apply from a laptop (`docs/runbooks/database-migrations.md`). API does not migrate on startup.

### AutoGenerateEnabled (H1)

```sql
ALTER TABLE pmoc.maintenance_plans
  ADD COLUMN auto_generate_enabled boolean NOT NULL DEFAULT true;

ALTER TABLE pmoc.maintenance_plans
  ALTER COLUMN auto_generate_enabled SET DEFAULT false;
```

| Population | Value |
|---|---|
| Rows existing at migration | **true** (DEFAULT true on ADD) |
| New inserts after default flip | **false** unless request sets true |

CLR: `bool AutoGenerateEnabled { get; set; }` defaults **false**. Do **not** `HasDefaultValue(true)` on the EF model (that would make new entities true).

### Other backfills

| Column | Existing rows |
|---|---|
| `origin_kind` | `'Custom'` |
| `source_template_id` / `source_template_version` | NULL (no name heuristic) |
| template `library_key` | `'pmoc-ar-condicionado-anvisa-nr10'` |
| template `version` | `1` |
| template `status` | `'Published'` |
| template `source_references` | optional: Lei 13.589/2018; Resolução Anvisa RE 09; NR-10 |
| `source_plan_name` | `UPDATE os.work_orders w SET source_plan_name = p.name FROM pmoc.maintenance_plans p WHERE w.maintenance_plan_id = p.id AND w.source_plan_name IS NULL` |
| Already SetNull WOs | leave `source_plan_name` NULL; origin stays Manual |

Preserve seed template and task Guids.

Suggested split inside Slice 1:

- **Migration A:** columns, backfill, seed HasData, FK Restrict (no unique index).
- **Migration B:** unique filtered index — **only after §6 precheck is empty** (or Human approves a repair that this spec does **not** invent).

If collisions exist, ship A without B. `PRODUCTION_HUMAN_APPROVAL_REQUIRED` before B.

---

## 6. UNIQUE INDEX PRECHECK

**Mandatory before creating/applying Migration B.** Read-only. Do **not** delete, merge, or cancel colliding Work Orders automatically.

### Query (DEV and PROD)

```sql
SELECT
  tenant_id,
  maintenance_plan_id,
  asset_id,
  scheduled_date,
  COUNT(*) AS cnt,
  ARRAY_AGG(id ORDER BY created_at) AS work_order_ids,
  ARRAY_AGG(status ORDER BY created_at) AS statuses
FROM os.work_orders
WHERE maintenance_plan_id IS NOT NULL
  AND status <> 'Canceled'
GROUP BY tenant_id, maintenance_plan_id, asset_id, scheduled_date
HAVING COUNT(*) > 1
ORDER BY cnt DESC, scheduled_date;
```

Empty result set = safe to create `ux_os_work_orders_pmoc_period`.

### Operational procedure

1. **Do not** print connection strings, passwords, or PII beyond WO ids / status / dates.
2. Preferred: add a read-only count to `MigrationInspectorRunner` (same family as `TechnicianAssignments`) so workflow `database-migrations` `mode=list` prints `PMOC_WO_PERIOD_COLLISIONS=<n>`. If `n > 0`, also log grouped ids (no asset names required).
3. Dispatch GitHub workflow `database-migrations`: `target=development`, `mode=list`. Confirm identity ref `jzptnjyzijklutinpxag`.
4. Human/parent runs the same list against **production** (`kbptdzfbngelzdhriyhf`) when PROD apply is in scope. Production apply remains `APPLY_PRODUCTION` + environment reviewers.
5. Attach precheck output to the Slice 1 PR (or the PR that contains Migration B).
6. If `n > 0`: **STOP** unique-index apply. Emit `PRODUCTION_HUMAN_APPROVAL_REQUIRED`. Leave collisions in place. Do not auto-repair.
7. If `n = 0`: Migration B may be included/applied.

Local `dotnet ef database update` / `railway run` against DEV/PROD is forbidden.

---

## 7. API CONTRACTS

Default: **additive**. Existing WEB continues until it opts in.

Error body convention: `{ "error": "<message>" }` plus machine `code` where specified. 409 via `Conflict(...)`.

### Keep

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/maintenance-plans` | `pmoc.plans.read` | Response gains origin/auto fields |
| GET | `/api/maintenance-plans/{id}` | `pmoc.plans.read` | Already includes tasks |
| GET | `/api/maintenance-plans/asset-categories` | `pmoc.plans.read` | Unchanged |
| POST | `/api/maintenance-plans` | `pmoc.plans.write` | Custom PMOC. `OriginKind=Custom`. `AutoGenerateEnabled` default **false** if omitted |
| PUT | `/api/maintenance-plans/{id}` | `pmoc.plans.write` | Header only + `autoGenerateEnabled` + `isActive`. **Not** tasks |
| DELETE | `/api/maintenance-plans/{id}` | `pmoc.plans.write` | Unused 204; used **409** `{ error, code: "PLAN_IN_USE" }` |
| GET | `/api/global-templates` | `pmoc.templates.read` | Default **Published only**. Keep `?jurisdiction=` |
| GET/POST/PATCH | `/api/work-orders` | existing OS keys | POST remains **client-task manual OS** — do not snapshot PlanTasks here |
| GET | `/api/work-orders/assets` | `os.work_orders.read` | Unchanged (client may filter unit/category) |

`[RequireActiveModule(Pmoc)]` on plan/template controllers; `[RequireActiveModule(WorkOrders)]` on work-order controllers.

### Add

| Method | Path | Permission | Body / behavior |
|---|---|---|---|
| GET | `/api/global-templates/{id}` | `pmoc.templates.read` | Full template + tasks. 404 if missing. **Includes Deprecated** (lineage/preview of old edition) |
| POST | `/api/maintenance-plans/from-template` | `pmoc.plans.write` | Server clone. See below |
| PUT | `/api/maintenance-plans/{id}/tasks` | `pmoc.plans.write` | Replace-set checklist. See §10 |
| POST | `/api/work-orders/from-plan` | `os.work_orders.create` | Manual Gerar OS. OS module gate |
| GET | `/api/work-orders?maintenancePlanId=` | `os.work_orders.read` | Additive filter; keep `assetId` |

Optional convenience (not required): `GET /api/maintenance-plans/{id}/eligible-assets`. Server validation on generate is mandatory either way.

### POST `/api/maintenance-plans/from-template`

```json
{
  "templateId": "<guid>",
  "unitId": "<guid>",
  "assetCategoryId": "<guid>",
  "name": "optional override",
  "description": "optional override",
  "isActive": true,
  "autoGenerateEnabled": false
}
```

Defaults: `isActive=true`, `autoGenerateEnabled=false`, `name` = template name if omitted.

Server:

1. Template exists and `Status == Published` (else 404/400)
2. Unit + category exist in tenant (same as create)
3. Copy all template tasks (order, title, input, config, mandatory) — **ignore any client task list**
4. `OriginKind=RolvixTemplate`, `SourceTemplateId=template.Id`, `SourceTemplateVersion=template.Version`
5. Single transaction
6. 201 + `MaintenancePlanResponse`

Deprecated / missing template: do not clone.

### POST `/api/work-orders/from-plan`

```json
{
  "planId": "<guid>",
  "assetId": "<guid>",
  "assignedUserId": null,
  "scheduledDate": "2026-09-18"
}
```

201 + `WorkOrderResponse`. Duplicate → **409** `{ error, code: "DUPLICATE_WORK_ORDER" }`. Validations: §8.

### Additive DTO fields

`MaintenancePlanResponse`: `originKind`, `sourceTemplateId`, `sourceTemplateVersion`, `autoGenerateEnabled`.

`CreateMaintenancePlanRequest` / `UpdateMaintenancePlanRequest`: `autoGenerateEnabled` (create: omit = false; update: required bool in Phase 1 WEB — current WEB does not PUT, so no live client break).

`GlobalMaintenanceTemplateResponse`: `libraryKey`, `version`, `status`, `sourceReferences`.

`WorkOrderResponse`: `sourcePlanName` (nullable). Keep `maintenancePlanId`.

`PUT .../tasks`:

```json
{
  "tasks": [
    { "id": "<existing guid or omit>", "title": "...", "inputType": "Checkbox", "isMandatory": true, "order": 1, "configuration": null }
  ]
}
```

At least one task. `id` present = update that PlanTask (must belong to the plan). `id` absent = insert. Existing PlanTasks not listed = **delete** (FK SetNull on historical WO tasks). Reorder via `order`. Never touch `WorkOrderTask` rows except SetNull on `plan_task_id`.

### Compatibility

- Extra response fields: current WEB Zod will fail **if** it parses those endpoints strictly. `getPlans` / `getGlobalTemplates` **will** break when API starts returning new required-looking fields unless Zod is updated **or** new fields are optional in API and WEB.

**Staged rollout rule:** make new response fields always present (non-breaking for unknown JSON **only if WEB `.strict()` is off). Current schemas use `z.object({...})` **without** `.strict()`, so **unknown keys are stripped** and lists still parse. Confirm: Zod default strips unknown keys. **WEB `getPlans` / `getGlobalTemplates` keep working** without WEB changes.

- `sourcePlanName` optional/nullish in WEB until Slice 6.
- Do not change `POST /api/work-orders` semantics.

---

## 8. SHARED WORK ORDER GENERATION SERVICE

Name follows repo conventions. Indicative: `IWorkOrderGenerationService` in `Platform.Api/Modules/WorkOrders/Services/`, registered from `WorkOrdersModuleExtensions`.

**Single implementation** used by:

1. `POST /api/work-orders/from-plan`
2. `PmocEngineJob`

Do **not** duplicate: plan/asset validation, PlanTask snapshot, duplicate handling, WorkOrder construction.

### Command

```text
PlanId, AssetId, ScheduledDate, AssignedUserId?
```

Job supplies `ScheduledDate = HangfireExtensions.GetBrazilToday()`, `AssignedUserId = null`.

### `GenerateAsync` (authoritative)

1. Load plan + tasks. HTTP: current tenant GQF. Job: `TenantId` null → GQF off; still set `WorkOrder.TenantId = plan.TenantId`.
2. Plan `IsActive`; ≥1 task. Else skip (job) / 400 (HTTP). Manual generate does **not** require `AutoGenerateEnabled`.
3. Asset exists, `Status == Active`, `ScheduledDeletionAt == null`, `UnitId` and `CategoryId` match the plan. Else 400.
4. Assignee optional; if set: active user + `os.work_orders.execute` (same as `WorkOrderService.CreateAsync`).
5. Insert WO: `Pending`, `MaintenancePlanId`, `SourcePlanName = plan.Name`, `Notes = null`, copy PlanTasks ordered → WorkOrderTasks (`PlanTaskId`, title, input, configuration, mandatory, order, `Value = null`).
6. Duplicate unique violation or pre-check: job **continue**; HTTP **409 DUPLICATE_WORK_ORDER**.

`WorkOrderService.CreateAsync` stays for `/os/nova` (client tasks, no snapshot). Do not route that through the generator.

### Hangfire-only (not in GenerateAsync)

- Filter `IsActive && AutoGenerateEnabled`
- `IsDueToday(frequency, brazilToday)` — **copy as-is** from current `PmocEngineJob` (Daily / Monday / day-1 / quarter / semester / year)
- Eligible asset query (unit, category, Active, not scheduled deletion)
- Per-plan transaction as today
- No commercial module check (PROD parity)
- Cron `0 6 * * *`, Brazil TZ, job id `pmoc-engine`, WorkerCount=1 — **unchanged**

Job must not assign technicians.

---

## 9. HANGFIRE PARITY REQUIREMENTS

After refactor, these must match pre-change behavior **except** the new `AutoGenerateEnabled` filter:

| Item | Must remain |
|---|---|
| Job id | `pmoc-engine` |
| Cron | `0 6 * * *` |
| TZ | `HangfireExtensions.ResolveBrazilTimeZone()` / `GetBrazilToday()` |
| Due calendar | existing `IsDueToday` switch |
| Assets | unit + category + Active + `ScheduledDeletionAt == null` |
| Status | `Pending` |
| Assignee | none |
| Notes | null |
| ScheduledDate | Brazil today |
| Task copy | all PlanTasks by `Order` |
| Tenants | GQF off; all tenants |
| Empty tasks | skip + warning |
| Duplicate | skip (now unique-index backed) |

**Must change:** `Where(plan => plan.IsActive)` → `Where(plan => plan.IsActive && plan.AutoGenerateEnabled)`.

Existing PROD plans backfilled true → continue generating. New plans false → no auto until the tenant opts in.

Parity tests: calendar matrix; asset filter; skip inactive; skip auto-off; skip empty tasks; snapshot fields; no assignee; duplicate no-op.

There are **zero** `PmocEngineJob` tests today — Slice 4 must add them.

---

## 10. PLAN TASK EDIT SEMANTICS

`PUT /api/maintenance-plans/{id}/tasks` (H5).

| Action | Plan (future OS) | Existing OS |
|---|---|---|
| Add | next snapshot includes | unchanged |
| Edit scalars | next snapshot uses new values | unchanged copied scalars |
| Reorder | next `Order` | unchanged |
| Remove | omitted from next snapshot | unchanged; `PlanTaskId` SetNull |

Forbidden: rewriting `WorkOrderTask` title/config/order/value; deleting WO tasks; “sync open OS”.

Minimum 1 task after replace. Invalid JSON configuration → 400 (same as create).

Header PUT may still change unit/category/frequency/name/`IsActive`/`AutoGenerateEnabled`. That does not rewrite historical WOs; it changes future applicability/auto.

---

## 11. DELETE / DEACTIVATE RULES

`Used` = `WorkOrders.Any(w => w.MaintenancePlanId == plan.Id)` (tenant-scoped HTTP).

| Case | Result |
|---|---|
| Not found | 404 |
| Used | **409** `{ "error": "...", "code": "PLAN_IN_USE" }`. Instruct client to set `IsActive=false` |
| Unused | hard delete; cascade PlanTasks; 204 |

UI: hide/disable delete when related count > 0; show deactivate.

Restrict FK: cannot delete a used plan even from raw cascade.

Already-nulled historical WOs: remain Manual; do not guess names.

---

## 12. FRONTEND ROUTES / COMPONENTS

Portuguese routes. Catalog order detail pattern: **one page, stacked sections, top actions — not tabs**.

| Route | Permission | Surface |
|---|---|---|
| `/pmoc` | `pmoc.plans.read` | Meus PMOCs (row → detail). Columns: name, origin badge, frequency, category, active, auto |
| `/pmoc/biblioteca` | `pmoc.templates.read` | Biblioteca Rolvix browse |
| `/pmoc/biblioteca/:templateId` | `pmoc.templates.read` | Preview: metadata, frequency, `sourceReferences`, checklist, Usar modelo |
| `/pmoc/novo` | `pmoc.plans.write` | Custom only. Remove client template clone as canonical path |
| `/pmoc/:id` | `pmoc.plans.read` | Detail loop |

Static paths **before** `/:id`.

**Detail sections (minimum):** overview; origin (Custom vs Modelo Rolvix + version if any); `IsActive`; **Gerar ordens de serviço automaticamente**; frequency; unit/category; checklist (edit if `plans.write`); related OS if `os` module + `os.work_orders.read`; **Gerar OS** if `os` module + `os.work_orders.create`.

Gerar OS dialog: asset (filter unit+category+active), `scheduledDate`, optional technician. POST `from-plan`. Success → `/os/:id` or stay with related list refresh.

Usar modelo: dialog unit + category (+ optional name) → `from-template` → `/pmoc/:id`. Clone defaults `autoGenerateEnabled=false`.

**Nav:** Meus PMOCs, Biblioteca Rolvix, Novo personalizado.

**OS:** `WorkOrdersPage` / `WorkOrderExecutionPage`: `Origem: PMOC {sourcePlanName ?? generic}`. `Link` to `/pmoc/{maintenancePlanId}` only if `can('pmoc.plans.read')` and `pmoc` in `activeModules`. Else plain text. Technician: no link.

**i18n (pt-BR, en, es):** replace “Importar Modelo Padrão (Normas/CREA)”, “Cardápio de Normas”, any 100% conforme. Use Biblioteca Rolvix / Modelos padrão / Fontes e referências.

**Do not** extend `src/lib/offlineSync.ts`.

### Likely WEB files

- `src/routes/AppRoutes.tsx`, `src/components/layout/navigation.ts`
- `src/features/pmoc/services/pmocService.ts` (+ tests)
- `src/features/pmoc/schemas/maintenancePlanSchemas.ts`, `globalTemplateSchemas.ts`
- `src/features/pmoc/pages/MaintenancePlansPage.tsx`, `CreatePlanPage.tsx`
- New: `PmocLibraryPage`, `PmocTemplatePreviewPage`, `PmocPlanDetailPage` (+ tests)
- `src/features/workOrders/services/workOrdersService.ts`, `schemas/workOrderSchemas.ts`
- `WorkOrdersPage.tsx`, `WorkOrderExecutionPage.tsx`
- `src/locales/{pt-BR,en,es}/common.json`
- Reuse: DataTable filters, Form/Select/Dialog, `Can`, `LoadingButton`, catalog-order stacked detail layout

### Likely API files

- Domain entities + enums; EF configurations; seed `GlobalTemplateSeed`
- `MaintenancePlanService` / DTOs / `MaintenancePlansController`
- `GlobalTemplateService` / DTOs / `GlobalTemplatesController`
- New `IWorkOrderGenerationService` + impl
- `WorkOrderService` list filter; `WorkOrdersController` `from-plan` + query
- `PmocEngineJob` — thin loop
- `WorkOrdersModuleExtensions` / `PmocModuleExtensions`
- Tests under `tests/Platform.Api.Tests/Pmoc/` and `WorkOrders/`
- Optional: `MigrationInspectorRunner` collision count

---

## 13. PERMISSIONS

No new catalog keys (keep 46).

| Action | Key | Module |
|---|---|---|
| Browse/preview library | `pmoc.templates.read` | `pmoc` |
| Custom create / clone / edit / deactivate / unused delete | `pmoc.plans.write` | `pmoc` |
| View plan | `pmoc.plans.read` | `pmoc` |
| Gerar OS | `os.work_orders.create` | `os` |
| Related OS / open OS | `os.work_orders.read` | `os` |
| Execute checklist | `os.work_orders.execute` | `os` |
| Assign later | existing OS assign/create flows | `os` |

Technician: **do not** add `pmoc.*`. Origin is text-only.

Hide Gerar OS if `os` inactive or missing create. Hide related OS if missing read/`os`. Hide library nav without `templates.read`.

Default User already has `pmoc.plans.read` + `pmoc.templates.read`.

---

## 14. BACKWARD COMPATIBILITY

- Existing `MaintenancePlan` rows: Custom origin, auto **true** → Hangfire unchanged for them
- New creates (including old WEB `POST /api/maintenance-plans` during staged rollout): auto **false** — approved product change, not a PROD regression of existing rows
- Existing WOs: snapshots untouched; `source_plan_name` backfilled when FK present
- `POST /api/work-orders` and `/os/nova` unchanged
- Template seed Ids preserved
- WEB Zod accepts unknown keys (no `.strict()`) → additive API responses should not crash current WEB
- DELETE used plans: **behavior tightening** (409). Document in PR. Unused delete remains
- Hangfire still ignores commercial module flags

---

## 15. TEST MATRIX

### API

- Seed: Guid preserved; libraryKey/version/status/references
- Custom create: Custom, null source, auto false if omitted
- Clone: Published only; task copy; lineage; auto false default; 400/404 deprecated
- Header PUT: auto/active independent
- Tasks replace: add/update/remove/reorder; ≥1 task; **WO snapshot bytes unchanged**; `PlanTaskId` SetNull on removed
- DELETE unused 204; used 409 `PLAN_IN_USE`; Restrict FK
- `from-plan`: happy; unassigned; assignee without execute; unit/category mismatch; inactive plan; inactive asset; empty tasks; duplicate 409
- Job: due matrix; skip auto-off; skip inactive; skip empty; snapshot; no assignee; Brazil today; duplicate no-op; all tenants when GQF off
- HTTP vs job race → unique violation handled
- List WO `maintenancePlanId`; tenant isolation
- Permissions / module gates
- Migration: existing auto true; new default false; source_plan_name backfill

### WEB

- Library browse/empty/error/loading
- Preview + Usar modelo (unit/category required)
- Custom `/pmoc/novo` without client clone as source of truth
- Detail: origin, auto switch copy, checklist, deactivate vs delete
- Gerar OS dialog; related OS; navigate `/os/:id`
- OS origin text + link / technician no-link
- Permissions + module off
- i18n: no CREA / 100% conforme
- Responsive sanity of library/detail (ui-implementer)

Do not add offline tests that extend `offlineSync`.

---

## 16. IMPLEMENTATION SLICES

| Slice | Repo | What | Migration? | Depends |
|---|---|---|---|---|
| **1** | API | Domain + Migration A (columns, backfill, FK Restrict, seed). Inspector collision count. **Migration B only if precheck empty** | Yes | H1–H3, §6 |
| **2** | API | PUT tasks; DELETE 409; response DTO lineage/auto on GET/PUT | No | 1 |
| **3** | API | GET template by id; published list filter; `from-template` | No | 1 |
| **4** | API | `IWorkOrderGenerationService`; Hangfire call-through + auto filter; `from-plan`; list by planId; `SourcePlanName` | No | 1, 2 |
| **5** | WEB | Routes, nav, i18n, library, preview, clone UX, detail, checklist edit, auto toggle | No | 2, 3 on `develop` |
| **6** | WEB | Gerar OS dialog, related OS, OS origin link | No | 4, 5 |

Do not collapse into one PR. Slice 4 must not change cron/TZ/calendar/asset rules.

---

## 17. GIT / PR SEQUENCE

Base: `develop`.

Branches: `feat/pmoc-os-phase1-foundation` (1) → `...-plan-tasks` (2) → `...-template-clone` (3) → `...-generate-os` (4) → `...-web-library-detail` (5) → `...-web-generate-origin` (6).

- Feature → `develop`: **Squash and merge**
- Release `develop` → `main`: **Create a merge commit**
- Never direct `main`
- API first; WEB after contracts
- Unique-index migration: do not squash-merge Migration B without precheck evidence
- PROD migration: `docs/runbooks/database-migrations.md` + `PRODUCTION_HUMAN_APPROVAL_REQUIRED`

---

## 18. CURSOR AGENT ROUTING

Do **not** invoke in the spec-materialization turn.

After **implementation gate**:

1. Parent Session Bootstrap; create slice branch from `origin/develop`
2. If `glm-5.2` available: `rolvix-architect` may compact this spec; else `SUBAGENT_UNAVAILABLE` and implementers use **this file**
3. `api-implementer` (`grok-4.6`) on vlr-api
4. `api-reviewer` (`grok-4.6`)
5. `web-implementer` (`grok-4.6`) on vlr-web
6. `ui-implementer` (`kimi-k3`) only when library/detail visual polish is central; sequential, not parallel on the same tree
7. `web-reviewer` (`grok-4.6`)
8. Parent: PR, Merge Risk Gate, squash to `develop`

No silent model substitution. Grok Bot is **not** in this workflow.

---

## 19. MERGE RISK GATES

| Slice | Fable Merge Risk Gate |
|---|---|
| 1 | **Mandatory** — schema, backfill, FK, unique index |
| 2 | **Mandatory** — persisted PlanTask delete vs WO snapshot |
| 3 | Usually mandatory — domain clone/lineage; skip only if GLM `FABLE_MERGE_REVIEW_NOT_REQUIRED` |
| 4 | **Mandatory** — Hangfire, concurrency, generation |
| 5–6 | WEB: Fable if FE↔BE contract / authz; i18n/layout-only may skip with documented reason |

Parent prepares GLM Merge Review Dossier. `rolvix-deep-architect` only via Merge Risk Gate (or explicit user approval for architecture escalation). Cost: at most one Fable call per high-risk PR unless architecture changed.

`AI_APPROVED` / squash only when Critical=High=0, tests required passed, Human gates closed, unique-index precheck satisfied for Slice 1B.

---

## 20. PROD RELEASE / MIGRATION SAFETY

1. Merge API slices to `develop`; DEV migrate via workflow `target=development` `mode=list` then `apply`.
2. Collision precheck on DEV; then PROD list when releasing.
3. WEB after API is on the environment it calls.
4. PROD migrate: GitHub Environment reviewers + `confirm_production=APPLY_PRODUCTION`. Identity ref `kbptdzfbngelzdhriyhf`.
5. Rollback ≠ `database update` to previous migration. Forward-fix or Supabase PITR (`database-migrations.md`).
6. After migrate: existing plans still auto-generate at 06:00 Brazil; create a **new** plan and confirm it does **not** auto-generate until the toggle is on; Gerar OS works unassigned; historical OS checklists unchanged.
7. `PRODUCTION_HUMAN_APPROVAL_REQUIRED` for: unique index if collisions; PROD apply; any data repair.

---

## Invariants that must not break

1. Tenant isolation via `ITenantScoped` + GQF; Hangfire GQF off is intentional.
2. Never authorize by role name (`Technician` string).
3. WorkOrderTask snapshot is execution truth; `/os/:id` must not fetch live PlanTasks.
4. PlanTask edits never mutate existing WorkOrderTasks (except `PlanTaskId` SetNull).
5. Published global templates are immutable; tenants never write global tables.
6. Manual OS (`POST /api/work-orders`) stays client-task, not a second PMOC generator.
7. One generator implementation for Hangfire + `from-plan`.
8. Existing PROD auto behavior preserved via `AutoGenerateEnabled=true` backfill.
9. New plans do not auto-generate unless the user enables the setting.
10. No CREA / 100% conforme product claims.
11. Do not extend `offlineSync.ts`.
12. Tests must not hit real Meta/Resend/SMS/Supabase prod.
13. Do not migrate from a laptop; do not touch `main` in implementation PRs.

## Do not

- Second Hangfire job or client-side PlanTask snapshot
- `GlobalTemplateVersion` / plan revision tables
- Per-asset last/next/overdue (Phase 2)
- Pause or retune `pmoc-engine` cron/TZ/calendar
- Auto-enable auto-generation on new plans or clones
- Technician `pmoc.*` permissions
- Reconstruct lost WO origins
- Auto-delete duplicate WOs
- Marketplace, attachments, template CMS, offline Phase 1
- Mega cross-repo PR

## Documentation that may need updating (during implementation, not this turn)

- `ROADMAP.md` (api + web) when slices merge
- `docs/context-packs/active/pmoc-os.md` if code drifts
- WEB i18n as specified
- Optional glossary lines in `CONTEXT.md` only if new terms are needed (`OriginKind` / Biblioteca Rolvix) — keep beachhead Rentals-first

## Test seams (existing)

- `tests/Platform.Api.Tests/Pmoc/MaintenancePlanAssetRegistryTests.cs`
- `tests/Platform.Api.Tests/WorkOrders/*`
- `Platform.Api.Tests` permission/module harnesses
- WEB: `CreatePlanPage.test.tsx`, `pmocPlanCategoriesService.test.ts`, work-order page tests

Add focused tests per slice; Hangfire currently has **no** test seam — create one in Slice 4.

## Product-level how to test (after implementation)

1. Existing tenant plan (pre-migration): still gets OS at 06:00 Brazil when due.
2. Biblioteca → preview → Usar modelo → detail shows Modelo Rolvix + version; auto toggle **off**.
3. Custom `/pmoc/novo` → Custom origin; auto **off**.
4. Edit checklist after an OS exists → old `/os/:id` unchanged; new Gerar OS uses new checklist.
5. Gerar OS without technician → Pending unassigned.
6. Second Gerar OS same plan+asset+date → 409.
7. Delete plan with related OS → 409; deactivate works; Hangfire skips inactive.
8. Technician: sees origin text, no PMOC nav, executes snapshot fields.
9. Copy nowhere says CREA-certified / 100% conforme.

## Verification strategy

API tests first (generation, snapshot isolation, unique index, Hangfire parity). WEB: unit tests for services/schemas + implementer/UI browser pass on library, detail, generate, OS origin. Staged: API on DEV, then WEB pointing at that API.
