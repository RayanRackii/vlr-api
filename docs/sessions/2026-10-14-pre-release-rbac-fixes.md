# 2026-10-14 — pre-release RBAC fixes

Branch `cursor/pre-release-rbac-fixes-063a` from `develop` (`a3eee72`). Not merged. No migration, role grant, secrets, or infra.

Two follow-ups from the Fable review of `origin/main...origin/develop`:

- `EnsureCanAssignRolesAsync` now sends `AdminWildcardExcludedKeys` and explicit-grant keys into `EnsureCanGrantKeysAsync`. A user with `core.users.assign_roles` and without `core.customers.read` cannot assign a role that carries `core.customers.read`, including to themselves (`PUT /api/users/{id}/roles` → 403 `PRIVILEGE_ESCALATION_BLOCKED`). A tenant Admin may still opt in. `GetEnabledCatalogKeysAsync` is now `GetAdminWildcardKeysAsync`.
- `UsersController.List` no longer calls the unreachable `RequireTenant`. `GET /api/users` without a tenant stays 403 `FORBIDDEN`. The 2026-10-13 roadmap line that said 401 is corrected.

`dotnet test` Release against loopback Postgres 16 (`ROLVIX_TEST_POSTGRES_CONNECTION`): 851 passed, 0 failed, 1 skipped. The skip is the pre-existing `WorkOrderGenerationServiceTests.Concurrent_duplicate_requires_postgres_unique_index`.
