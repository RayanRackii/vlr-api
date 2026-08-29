# Agent Feedback — PermissionCatalog keys without migration INSERT

Date: 2026-08-28
Status: Open
Severity: High
Repository: vlr-api

Agent: api-implementer (first Catalog & Orders commit)
Model: grok-4.6
Branch: `feat/catalog-orders`
PR: pending into `develop`

## What happened

Six `catalog.*` keys were added to `PermissionCatalog` / `Permissions` without `INSERT INTO core.permissions`. `RoleService` attaches only rows that exist in the DB, so custom-role grants of those keys were dropped. `TenantAccessBootstrapper.EnsureAsync` was not called on tenant update, so enabling the catalog module did not seed the missing rows.

## Expected behavior

New `PermissionCatalog` keys land in the same migration as `core.permissions` (plus Admin/SuperAdmin `role_permissions`), with a test that a custom role can persist a catalog key after bootstrap.

## Impact

- código: RBAC looked granted in the C# catalog but not in Postgres until EnsureAsync ran
- custo: extra review cycle
- arquitetura: module permissions not enforceable for custom roles
- produção: none (migration not applied)
- tempo: one High review-fix
- UX: Admin/custom roles could not grant catalog capabilities after a naive migrate

## Why it happened

Implementer treated in-memory `PermissionCatalog.All` as sufficient. Existing RBAC v1 migration used SQL INSERT; that pattern was not copied for the new keys.

## Resolution

Migration `20260828175423_AddCatalogOrdersAndCustomerDocument` INSERTs the six keys and grants them to Admin/SuperAdmin. `AdminTenantService` update calls `EnsureAsync`. Test `EnsureAsync_inserts_missing_catalog_keys_so_custom_role_can_persist_them`.

## Prevention

Treat `PermissionCatalog.All` + migration INSERT + `TenantAccessBootstrapper` as one unit. Fail CI if catalog keys exist in code but not in the migration SQL.

## Promotion

Suggested: Rule — “New `PermissionCatalog` keys require a migration `INSERT` into `core.permissions` in the same PR; RoleService drop is not a seed.” Not promoted yet.

## References

- [API reviewer](1a7ee5e1-a4f3-4239-b739-1dccc5d0fdba) `AGENT_FEEDBACK_RECOMMENDED`
- `Core/Platform.Core.Infrastructure/Persistence/Migrations/20260827184023_AddTenantRbacV1.cs`
- `Platform.Api/Modules/Roles/Services/RoleService.cs` (`ReplaceRolePermissionRowsAsync`)
