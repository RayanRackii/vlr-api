# 2026-10-14 — Permission and tenant convention (#148)

Follow-up from #146. When the Super-Admin Auth read fails, the resolver stays in platform mode: no ambient tenant, and the global query filter is off.

`PermissionTenantConventionTests` reflects over controller actions. An action outside `PlatformAdminPolicy` must carry `[RequirePermission]` or `[RequireAnyPermission]`, an explicit tenant gate, or a public allow-list entry. The tenant gates the test recognizes are `Authorize(Policy = "Customer")` and a direct call to `DirectoryAuthorization.RequireTenant` or `GateAsync`. `GET /api/users/me` is not public: platform mode still returns a tenant-less profile, and `UserDirectoryService.GetCurrentAsync` reads `TenantId` before it queries users.

No production code changed. No migration. Draft until the GLM/Fable gate.
