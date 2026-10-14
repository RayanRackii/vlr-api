# 2026-10-14 — Super-Admin exit and stale access-token tenant

Issue #134. `POST /api/admin/tenants/exit` already cleared live Auth `app_metadata.tenant_id`. `HttpContextTenantProvider` still read that claim from the access token, so support mode for tenant A continued until refresh.

The HTTP pipeline now resolves a Platform Super-Admin's tenant from the live Auth metadata plus an active `core.users` membership (`PlatformAdminTenantSessionMiddleware`). The access-token claim is not used. No migration.

Staff B2B still takes `app_metadata.tenant_id` from the token; the permission handler checks membership. Customer JWTs still use their own `tenant_id` claim. Public routes still bind the tenant from the subdomain.

Follow-up #147: that Auth admin `HttpClient` times out after 3 seconds. A successful tenant read is cached **in-process** for 10 seconds per auth id. The cache is not shared across processes and assumes a **single replica**. Enter and exit invalidate the cache on this process around the `app_metadata.tenant_id` write. After exit, another replica can still serve the previous tenant for at most that TTL. If the API runs more than one replica, use a shared cache, or have exit deactivate the `core.users` membership — the membership query stays live on every request, including a metadata cache hit. A timed-out read or metadata write becomes `SupabaseAuthAdminException` (504) and still withholds the tenant; the middleware log includes the exception object and does not include tokens or the user payload.
