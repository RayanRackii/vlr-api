# 2026-10-14 — PermissionResolver ambient guard

Branch `fix/permission-resolver-ambient-guard` from `develop`. Draft PR only. No migration, config, secrets, or merge.

`PermissionResolver` now compares `ITenantProvider.TenantId` with the explicit `tenantId` before the per-request memo and before `ITenantModuleAccessor`. A mismatch, including a null ambient, returns an empty set and is not memoized. Catalog `order.created` staff fan-out returns no B2B recipients when the ambient tenant is null (no cross-tenant user scan, no `order.TenantId` fallback). `WorkOrderAssigneeGuard` requires `user.TenantId == tenantId`.

Left as they were: Platform Admin short-circuit in `PermissionAuthorizationHandler`, `[RequireActiveModule]`, and `UserDirectoryService.LoadActiveModulesAsync`.
