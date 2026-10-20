# Platform Taxonomy — API (2026-10-20)

Spec: [`platform-taxonomy`](../plans/active/2026-10-20-platform-taxonomy.md).
Implementation left in working tree; no Git commands, commits/push, connection/secret changes,
remote migration or deployment. PM owns delivery. Web owns its implementation and CONTEXT/ROADMAP mirror.

Implemented shared tenant category/tag CRUD, permissions, 3-level hierarchy and immutable slugs;
typed composite tenant FKs, tenant-row mutation locking, atomic Catalog assignments,
descendant/category + AND tag filters, direct product metadata and scoped consumer discovery.
Tenant deletion removes joins and categories from leaves to roots. Existing AssetCategory unchanged.

Migration `20261020220848_AddPlatformTaxonomyAndCatalogAssignments` generated with EF 10.0.9;
designer and snapshot included, permissions inserted idempotently. No existing-product classification/backfill.
`migrations has-pending-model-changes` reports no pending changes.

Validation:
- .NET 10.0.401 SDK at `/home/rolvixpilot/.dotnet/dotnet`; offline restore from existing NuGet cache.
- `dotnet build --no-restore -m:1 -p:UseSharedCompilation=false`: passed, zero warnings/errors.
- Standard `dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj` attempted; VSTest aborts
  before execution with `SocketException (13): Permission denied` opening its local TCP channel.
- Actual tests executed with xUnit's executor in process through a temporary runner under
  `/tmp/taxonomy-inproc` (no TCP listener). Full suite: **1499 total, 1411 passed, 0 failed, 88 skipped**.
  New taxonomy cases: **57 total, 50 passed, 0 failed, 7 skipped**.
  Logs: `/tmp/taxonomy-all-tests-final.log`; VSTest abort: `/tmp/taxonomy-test-results/all.trx`.
- Docker unavailable; seven new PostgreSQL tests plus existing Docker tests skip. New tests cover
  composite FKs, uniqueness races, concurrent reparent/delete/assignment, rollback, cascade/restrict,
  and both portal search branches. Existing three PostgreSQL tenant-cleanup cases now seed a
  three-level tree, both assignment kinds and multiple tenants.
- Normal review: Critical=0, High=0, Medium=0 after correcting model-state error envelope and
  adding HTTP regressions. This is not the delivery Merge Risk Gate.

How to test outside the sandbox: run `dotnet build` and
`dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj` with Docker running.
Use read/manage taxonomy permissions to POST category root/child and two tags, save them through
Catalog's existing product POST/PUT, then GET products with `?category=<root>&tags=<a>,<b>`.
Check staff results, Customer portal active-only results and `/taxonomy-options` discovery;
unassigned labels must not leak into portal options. Omitted/null taxonomy preserves updates;
empty arrays clear assignments; invalid references return 400 and leave product/joins unchanged.

Next API: execute PostgreSQL and standard-runner validation; authorized DEV migration/API delivery.
Next Web: shared management/filter/assignment UI + glossary mirror, API first → Web;
preserve portal/Slot priority. No Rentals integration in this task.
