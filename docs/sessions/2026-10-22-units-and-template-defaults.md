# 2026-10-22 — Units create and visual template defaults

Implemented locally without Git operations, PR, merge, deployment, or remote migration.

- `POST /api/units` creates an active Unit in the ambient tenant, validates trimmed name/code, returns the existing `UnitResponse`, and responds 409 for the existing tenant-local name/code uniqueness constraints. It requires new `core.units.write`.
- `core.units.write` was added to the canonical permission catalog and migration SQL seeds it idempotently and grants existing `Admin`/`SuperAdmin` roles. Down removes grants before the key.
- Published visual template revisions may carry nullable schedule, pricing, and RentalAsset policy defaults. `POST /api/rental-assets` remains legacy by default; `applyTemplateDefaults: true` copies those values only when the relevant create fields were omitted.
- Migration `20261022192746_AddUnitWriteAndVisualTemplateDefaults` has only nullable additions plus permission catalog SQL and a Down path. It was generated with the local dotnet-ef 10.0.9 tool using an offline connection string and was not applied.
- Added HTTP tests for unit permission/tenant isolation/duplicate handling and service tests for applied defaults plus legacy create behavior. The test runner must run outside this sandbox because its TCP listener is blocked; all projects compile here with MSBuild restricted to one node. API build passed after restoring the production projects; restore emitted only NuGet vulnerability-feed warnings due restricted network.
- Remediation: `DefaultPricings` now has an EF JSON value converter and structural comparer in the production configuration, so the non-Npgsql test providers receive a scalar JSON value; the permission-catalog assertion now includes `core.units.write` while preserving the default user bundle.
