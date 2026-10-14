# 2026-10-14 — PermissionResolver Fable follow-ups (#114)

Branch `fix/permission-resolver-fable-114` from `develop` (`a074097`, #113). Draft PR only. No migration, config, secrets, deploy, or merge. GLM/Fable merge gate still required.

`PublishOrderEventAsync` returns before reading `TenantNotificationChannelConfigs` when `ITenantProvider.TenantId` is null. A null ambient no longer creates a customer delivery from another tenant's active channel config. Staff fan-out stays behind the same ambient requirement.

Manual `WorkOrderGenerationService.GenerateAsync` requires the ambient tenant and no longer falls back to `plan.TenantId`. A missing ambient throws `UnauthorizedAccessException` before the assignee guard. `TryGenerateAutomaticAsync` is unchanged (Hangfire passes the plan tenant explicitly).
