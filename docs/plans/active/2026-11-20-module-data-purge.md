# Module data purge and storage orphan report

Status: proposed (2026-11-20). Cross-repo (vlr-api + vlr-web). Owner asked: "tem como colocarmos uma
regra no banco ou no nosso BE para remover dados 'desconectados'?"

## 1. Problem and decision

Disabling a module (`PUT /api/admin/tenants/{id}`, `AdminTenantService.SyncTenantModules`) only removes
the `TenantModule` row. Reservations, orders, work orders, PMOC plans and allocations stay in the
database, hidden. That is intended: a module can be switched off by mistake or temporarily, and some
records (orders, reservations) may have to be kept for fiscal or audit reasons. There are no orphan
rows in the database sense, because tenant data is linked by foreign keys (Cascade/Restrict/SetNull).

The real leftovers are:
- **Dormant module data.** It lives under a tenant whose module is off, and nobody can see it or remove it.
- **Storage objects** (images, attachments). These have no FK, so the database cannot guarantee
  that a database row still points to each one.

Decision:
1. **Never delete data automatically when a module is disabled.**
2. Deleting a module's data is a **separate, explicit platform-admin action**:
   - preview first;
   - typed confirmation;
   - **scheduled 30 days ahead**, cancellable until then;
   - executed by a daily job.

   This follows the existing `ScheduledDeletionAt` pattern (asset categories, `AssetCategoryService`,
   30 days, `DataRetentionJob`) and the trial purge (`TrialLimits.PurgeDays = 30`, `TrialLifecycleJob`).
3. A weekly **read-only orphan report** for storage. No automatic storage deletion in v1.
4. Fix the tenant-purge storage gap found while writing this spec (see 3.1).

Out of scope (v1):
- export before purge;
- purging the `inventory` module (assets are the shared registry, see 2.2);
- LGPD per-customer erasure;
- automatic orphan deletion.

## 2. Model

### 2.1 What belongs to a module
The module is derived from the table schema, which the EF model already has:

| Schema | Owner | Module-purgeable |
|---|---|---|
| `rentals` | `rentals` | yes |
| `catalog` | `catalog` | yes |
| `os` | `os` | yes |
| `pmoc` | `pmoc` | yes |
| `planning` | `resource-planning` | yes |
| `assets` | shared registry (`inventory` and every module) | **no** |
| `core` | platform | **no** |

New class `Platform.Api/Services/Tenancy/ModuleDataScope.cs`:
- `IReadOnlyList<IEntityType> EntitiesFor(IModel model, string module)` returns the entities with
  `TenantId` in the module's schema, plus their FK dependents inside the same schema.
- The deletion order reuses the leaf-first algorithm of `TenantPurger.BuildDeletionOrder`. Extract it
  to an internal helper that takes a candidate set, so both purgers share it.
- A model test fails when an entity lives in a schema that is not in the table above. A future
  schema must be classified explicitly.

### 2.2 Blockers (purge refused, preview shows why)
- The module is active for the tenant.
- An entity **outside** the scope has a `Restrict` FK into the scope, and matching rows exist. Example:
  catalog allocations pointing at `rentals` resources when only `rentals` is purged.
  - The preview lists each blocking table and its row count.
  - The admin must purge the dependent module first or keep the data.
- Unreleased allocations (`Scheduled`/`Running`/`Cleaning`) in the scope. This is the same rule as
  `EnsureNoUnreleasedAllocationsOnDisableAsync`.
- `SetNull` FKs from outside the scope are allowed; they null out, as today.

### 2.3 New entity (additive migration, schema `core`)
`TenantModuleDataPurge`:
- `Id`, `TenantId` (FK tenants, cascade), `Module` (string 64);
- `RequestedByUserId`, `RequestedAt`, `ScheduledFor` (`RequestedAt + 30d`);
- `CancelledAt?`, `CancelledByUserId?`, `CancelReason?`;
- `ExecutedAt?`, `ResultJson?` (rows deleted per table, storage objects deleted, warnings);
- `RowVersion` (concurrency).

Unique partial index: one pending purge (`CancelledAt IS NULL AND ExecutedAt IS NULL`) per
`(TenantId, Module)`.

## 3. Slices (one PR each, in order)

### 3.1 API-1: tenant purge also deletes `tenants/{id:N}/` (bug fix, independent)
- `TenantPurger.PurgeAsync` deletes only the storage prefix `{id:N}/`.
- Portal landing images use `tenants/{id:N}/landing/...` (`PortalLandingImageRules`, line 77), so a
  purged tenant leaves its landing images in the public bucket.
- Fix: delete both prefixes `{id:N}/` and `tenants/{id:N}/` in each bucket. Keep the existing warning
  log behaviour.
- Test (next to `tests/Platform.Api.Tests/Admin/AdminTenantDeleteAmbientFilterTests.cs`): a fake `IStorageProvider` records `DeletePrefixAsync` calls and
  asserts both prefixes for both buckets.
- Done when the test fails without the fix and passes with it.

### 3.2 API-2: scope + preview
- `ModuleDataScope` (2.1) with the model test: every tenant-scoped entity is classified, and the
  order has no cycles for each purgeable module.
- `GET /api/admin/tenants/{id}/modules/{module}/data-purge/preview`:
  - platform admin only, same guard as the other `/api/admin/tenants` endpoints;
  - returns `{ module, moduleActive, tables: [{ table, rows }], storageObjects, blockers: [{ code,
    table?, rows? }], pending: { id, scheduledFor }? }`.
- Counts use `IgnoreQueryFilters()` plus an explicit `TenantId == id`, the same as the
  disable guard comment explains.
- Storage count for v1:
  - catalog: `CatalogProductFile` and `ProductRequestFile` `StorageKey`s of rows in scope;
  - os: `WorkOrderTaskFile` `StorageKey`s (under `{id:N}/work-orders/`).
  No listing API is needed here.
- Tests:
  - unknown module → 400;
  - `inventory`/core → 400 `module.not_purgeable`;
  - active module → blocker `module.active`;
  - Postgres test with rows in `rentals` and a catalog allocation → blocker `fk.restrict` on the
    catalog table;
  - tenant isolation: rows of another tenant are never counted.

### 3.3 API-3: schedule, cancel, execute
- `POST /api/admin/tenants/{id}/modules/{module}/data-purge`, body `{ confirm: "<module key>" }`:
  - requires the module off and no blockers;
  - creates the `TenantModuleDataPurge` with `ScheduledFor = now + 30d`;
  - returns 409 on blockers, with the preview body.
- `DELETE /api/admin/tenants/{id}/modules/{module}/data-purge` cancels the pending purge (reason optional).
- Re-enabling the module in `AdminTenantService` cancels any pending purge for it in the same
  transaction, with reason `module.reenabled`.
- `ModuleDataPurgeJob`, a daily Hangfire job registered next to `DataRetentionJob` in
  `HangfireExtensions`. For each due purge:
  - lock the tenant row (`FOR UPDATE`, same as `TenantPurger`);
  - re-check that the module is off and that there are no blockers. If either fails, cancel with the
    reason instead of failing;
  - delete in `ModuleDataScope` order inside one transaction (`ExecuteDeleteAsync` per entity, filtered
    by `TenantId`);
  - after commit, delete the storage keys collected before the delete (best effort, warnings in
    `ResultJson`);
  - set `ExecutedAt` and `ResultJson`.
- Log with `PlatformScope.Begin(logger, "module-data-purge")`. Never log row contents.
- Tests:
  - schedule → pending row, date +30d;
  - second schedule → 409;
  - cancel;
  - re-enable cancels;
  - job: a due purge deletes only that tenant's rows of that schema, and another tenant's identical
    rows survive (Postgres test);
  - a not-yet-due purge is untouched;
  - a blocker appearing after scheduling → cancelled with reason, nothing deleted.
- Opus read-only review: this touches tenant isolation, a migration and data deletion.

### 3.4 WEB-1: admin "Excluir dados do módulo"
- Admin tenant edit page (`vlr-web/src/features/admin/...`, the modules section): for each **disabled**
  purgeable module with `rows > 0` in the preview, show "Dados guardados: N registros" and the
  button "Excluir dados…".
- Dialog:
  - preview table (table → rows, storage objects);
  - blockers in plain language;
  - the text "Os dados serão excluídos em 30 dias. Até lá você pode cancelar.";
  - a field where the admin types the module key to enable the destructive button.
- Pending state: chip "Exclusão agendada para dd/mm/aaaa" and a "Cancelar exclusão" button.
- After the API slices: `npm run gen:api` and `check:api`.
- Tests (vitest): button hidden when the module is active or there is no data; confirm disabled until
  the typed key matches; blockers rendered; pending chip plus cancel.
- Copy in pt-BR, en and es. Generic: no tenant or resource names.

### 3.5 API-4: weekly storage orphan report (read-only)
- `IStorageProvider.ListAsync(bucket, prefix, ct)` returns `(key, size, updatedAt)`, paged. Supabase uses
  the `/storage/v1/object/list/{bucket}`; `DevStorageProvider` lists the local folder.
- `StorageOrphanReportJob`, weekly (Sunday 03:30 BRT). For each bucket it lists the objects and marks
  as orphan:
  - objects under `{tenantN}/` or `tenants/{tenantN}/` whose tenant no longer exists;
  - catalog keys with no `CatalogProductFile` or `ProductRequestFile` row;
  - work-order keys with no `WorkOrderTaskFile` row;
  - landing keys that are not the tenant's current landing image key.

  Objects newer than 24 h are ignored, to avoid racing uploads.
- Stores the latest report as a `StorageOrphanReport` row (`GeneratedAt`, `TotalObjects`,
  `OrphanCount`, `OrphanBytes`, `SampleJson` with the first 200 keys). Additive migration.
- `GET /api/admin/storage/orphan-report` returns the latest report (platform admin).
- **No deletion.** A later slice can add "delete listed orphans" after the owner reviews a few reports.
- Tests: the classification function on fixed inputs (each orphan kind plus a non-orphan for each);
  the 24 h grace period; the admin-only guard.

### 3.6 WEB-2 (optional): show the latest orphan report in the platform admin
A read-only card: date, orphan count, size, sample keys.

## 4. Rules
- Never run a purge against FICC without the owner's explicit written OK for that tenant and
  module. Smoke tests only on gammera and DEV test tenants.
- PROD: API-3 and API-4 add migrations, so follow the usual flow: backup, list, apply only the expected
  additive migration, deploy, postcheck.
- The first PROD use of the purge is a dry run: open the preview only, and do not schedule.

## 5. Done when
- API-1 is merged and deployed. A purged DEV test tenant leaves no `tenants/{id}/` objects.
- On DEV:
  - the preview shows correct counts and blockers for gammera with rentals off;
  - scheduling, cancelling and re-enabling behave as in 3.3;
  - a test tenant purge executed by triggering the job manually removes only that module's rows.
- Admin UI flow checked at 1440 and 375 px.
- The orphan report has run once on DEV and once on PROD (read-only), and the numbers have been
  reported to the owner.

## 6. Open questions for the owner (defaults assumed)
1. Grace period: **30 days** (same as types and trial).
2. Who can request a purge: **platform admin only**. The tenant admin cannot.
3. Export before purge: **not in v1**.
4. Should disabling the last module of a tenant also offer tenant offboarding (whole-tenant purge
   after N days)? **Not in v1.** `TenantPurger` already exists for a manual delete.
