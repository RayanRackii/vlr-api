# 2026-10-21 — PR #248 review/CI fixes

User explicitly waived unavailable implement/tdd skills and prohibited all Git commands. Read `~/codex-runs/mapi-review.md`, `mapi-ci-detail.txt` and `mapi-ci.txt`. No Git, remote migration, PR mutation or deploy was performed.

- CI's `ResourceSegmentsConcurrencyTests` failed before invoking the service: `segments-` plus a GUID produced a 41-character TaxId for PostgreSQL `varchar(20)` (22001). The fixture now uses a unique 20-character identifier.
- `AssetVisualAssignmentPostgresTests` reused a fixed TaxId across methods sharing a class-scoped database, causing 23505 on `ix_tenants_tax_id` before the FK assertions. Owner and foreign tenant fixtures now use unique values within the mapped maximum length. Product constraints and the actual tenant/revision FK regressions remain unchanged.
- `GetCompositionAsync` and `GetPublicMapAsync` open a repeatable-read transaction before metadata and retain it through all item, decoration and public-art materialization. Write/seed responses dispose their completed write transaction before rereading; the idempotent seed path releases its read/lock transaction first as well.
- Added a deterministic PostgreSQL reader/writer regression for admin and public reads: pause before items after reading metadata, commit a complete save on a second connection, then assert the pending response is wholly v1 and the next read wholly v2. The test also covers new/idempotent seed rereads and transaction cleanup.
- The Development-only demo controller now checks `BookingMaps:DemoSeed:AllowedTenantIds` before switching ambient tenant context or calling the service. Missing/empty configuration denies all tenants with 403 `DEMO_SEED_TENANT_NOT_ALLOWED`; environment and platform-policy restrictions remain. HTTP regressions cover absent/empty allowlist, another allowed UUID, successful allowed seeding, production denial and foreign resource/unit rejection even when that target tenant is allowlisted.
- The shared appsettings default is `[]`. No Development settings file is supplied for deployment, so the runbook documents `BookingMaps__DemoSeed__AllowedTenantIds__0=b27c9a0a-d585-4626-8c13-1468c5a43519` for the FICC Development process. No deployment configuration was changed.
- Added a separate DockerFact/class-fixture migration test that executes all four visual/map migrations Up→Down→Up, compares complete legacy row snapshots excluding extension columns, checks catalog/permission seeds and tests actual Down refusal for referenced revisions and modified seed metadata. It is included automatically by the existing PostgreSQL CI job.

Validation:

- `dotnet build --no-restore -m:1 -p:UseSharedCompilation=false`: 0 warnings/errors.
- Normal `dotnet test` aborts because VSTest cannot create a socket (permission denied). A direct loopback PostgreSQL connection is likewise blocked with `Operation not permitted`; real PostgreSQL/multi-connection tests cannot execute in this sandbox.
- Existing xUnit in-process runner, hosted under the repository test output so source-inspection tests can locate the repo: focused classes **97 total / 92 passed / 0 failed / 5 PostgreSQL skipped**.
- Full suite using the same in-process runner under the test output: **1679 total / 1580 passed / 0 failed / 99 skipped**. An initial runner invocation under `/tmp` could not locate repository source for ten inspection tests; rerunning with the runner hosted in the repository test output resolved all ten.
- Independent Standards and Spec reviews found no actionable issues. No Git commands were used for review.

Next API: run the CI PostgreSQL suite, then validate migrations/demo in authorized DEV with the explicit FICC allowlist before enabling the map flag. Next web: complete the decorations editor and full booking-map settings controls, then validate API integration behind the existing booking UI flag. Reproduction commands and demo setup are in the [runbook](../runbooks/resource-visuals-booking-maps.md).
