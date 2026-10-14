# 2026-10-14 — Super-Admin exit and stale access-token tenant

Issue #134. `POST /api/admin/tenants/exit` already cleared live Auth `app_metadata.tenant_id`. `HttpContextTenantProvider` still read that claim from the access token, so support mode for tenant A continued until refresh.

The HTTP pipeline now resolves a Platform Super-Admin's tenant from the live Auth metadata plus an active `core.users` membership (`PlatformAdminTenantSessionMiddleware`). The access-token claim is not used. No migration.

Staff B2B still takes `app_metadata.tenant_id` from the token; the permission handler checks membership. Customer JWTs still use their own `tenant_id` claim. Public routes still bind the tenant from the subdomain.
