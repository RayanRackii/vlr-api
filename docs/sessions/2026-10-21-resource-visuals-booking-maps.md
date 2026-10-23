# 2026-10-21 — resource visuals and booking maps

Continued the user's existing implementation of `ux-map-spec.md` API PRs 2, 3, 5, 6 and API/seed 10. User explicitly authorized proceeding without unavailable implement/tdd skills and prohibited all Git operations. Existing edits were retained; no Git, PR, merge, remote migration or deploy was performed.

- Fixed the missing Rentals.Services import in `BookingMapHttpTests`; adjusted its foreign-tenant fixture to enable the module/flag so it exercises map ownership rather than module rejection.
- Updated permission-catalog regression expectations for four new tenant keys, preserving default user/technician bundles.
- Generated `ExtendRentalMapsAndVisualAssignments` with installed dotnet-ef 10.0.9, an offline connection and `--no-build` after building. Added locked legacy ownership/unit preflight; Up only adds model extensions/replaces constraints and Down preserves original entities.
- Finished SQL-only `SeedResourceVisualCatalogV1` and `AddBookingMapPermissions`, with guarded Down and administrative grants. PostgreSQL validation caught duplicate scaffold map operations in the seed migration; removed them and added a regression requiring exactly one SQL operation in each seed direction.
- Added model-snapshot/additive/rollback checks and a relational regression for simultaneous platform/tenant segment replacements. Independent review found a Medium race in replacement semantics; fixed using a transaction and tenant-row lock. Reviewer verified the fix and migrations with no remaining actionable severe findings.
- Build: zero warnings/errors. VSTest blocked with SocketException (permission denied); in-process xUnit from the repository test output: **1675 total / 1578 passed / 0 failed / 97 skipped**. Focused feature/permission tests: **96 total / 93 passed / 0 failed / 3 skipped**.
- Applied generated SQL to an isolated PostgreSQL 16 single backend without sockets: Up→Down→Up preserved sentinel legacy asset, resource, map and placement. Also checked idempotent seeds, 13 templates/revisions, 15 tags, Admin grants, tenant-composite/reference FKs, immutable published art, seed Down refusal for referenced/modified data and legacy-link preflight refusal. Multi-connection concurrency tests remain unexecuted in this sandbox.

Contracts, permission keys, migration IDs, demo request and test instructions: [runbook](../runbooks/resource-visuals-booking-maps.md).

Next API: run PostgreSQL multi-connection tests, validate/apply through the approved DEV migration workflow and smoke-test the explicit demo tenant before enabling its flag. Next web: consume the catalog/map/settings contracts, evolve the existing editor/picker, and implement map/list booking with the selection/revalidation regressions behind the existing flag. No frontend changes in this task.
