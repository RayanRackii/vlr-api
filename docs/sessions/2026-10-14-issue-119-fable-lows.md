# 2026-10-14 — Fable Lows from #118 (#119)

Branch `fix/issue-119-fable-lows` from `develop` (`91e31bf`, #118). Draft PR only. No migration, config, secrets, deploy, or merge. GLM/Fable merge gate still required (auth/tenant).

`PublishOrderEventAsync` returns before reading channel configs or inserting a notification when `order.TenantId` differs from the ambient tenant. The warning logs the event type only. Notification and delivery rows that do persist use the ambient tenant id.

`EnsureTenantChannelDefaultsAsync` loads existing channel configs with `TenantId == tenantId` and applies the same predicate to tracked locals, so another tenant's rows do not suppress defaults when the global query filter is off.

`POST /api/work-orders/from-plan` stays 403 `FORBIDDEN` for an authenticated caller with no tenant context. That is the permission-policy contract pinned by #118 and already in PROD via release #120. Unauthenticated callers receive 401. `GenerateAsync` still throws `UnauthorizedAccessException` if the action is reached without a tenant; the policy rejects first, so that exception does not decide the HTTP status.
