# Booking availability API — 2026-10-21

User authorized rollout steps 1 + 2 in this API worktree, waived the missing implement/tdd skills and explicitly prohibited Git operations. No branch, commit, PR, remote operation or deployment is part of this session. Frontend is out of scope.

Added generic public daily availability with per-window price, physical capacity and all six states, including occupied derived intervals; explicit tenant scoping and existing module/subdomain binding. Legacy route contracts and pricing precedence remain; the two booking routes share only pure price ordering/total helpers. Public branding adds a tenant UUID allowlist flag through existing application configuration, default off. No migration or persisted-data changes.

Accepted defaults and API details, schemas, fixtures, errors, limits and manual validation are recorded in [the contract](../contracts/booking/README.md). Added unit/service, actual controller/JWT integration, fixture/compatibility tests and an optional PostgreSQL fixed-query/no-write test. Build/test evidence is recorded at completion in the task result; unavailable PostgreSQL and restricted test sockets are reported explicitly, not treated as passes.

Final validation:

- `dotnet build Platform.slnx --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`: passed, zero warnings/errors. Restore used the cached packages with `NuGetAudit=false` because this sandbox cannot reach the vulnerability feed.
- Standard `dotnet test` was attempted but cannot start its local VSTest socket (SocketException 13); `/tmp/wbk-api-test-results/all.trx` records the aborted run.
- The actual xUnit executor ran the full suite in-process: **1540 total, 1451 passed, 0 failed, 89 skipped**. New coverage contributes **41 cases: 40 passed, 0 failed, 1 PostgreSQL case skipped**. Docker/local PostgreSQL is unavailable, so the fixed-query/no-write assertion remains unverified against that provider. Final log: `/tmp/wbk-api-all-tests-final.log`.
- The temporary runner used the normal test output directory and local assembly resolution so existing source-inspection and host-startup tests ran normally. No repository test infrastructure was replaced; temporary runner entry files were removed from build output afterward.
- Python Draft 2020-12 validation with format checking passed all three daily fixtures. Fixture/typed-contract/legacy compatibility and actual HTTP response assertions also passed in xUnit.
- Independent Standards and Spec reviews: Critical=0, High=0, Medium=0. Review used captured filesystem snapshots without Git. This is a local implementation review, not a delivery/merge gate.

Next API: expected-price guard in rollout step 4, then private history reads in step 5. Next web: step 3's table/date strip/selection and schema validation behind the flag. DEV validation and rollout activation remain pending.
