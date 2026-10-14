# 2026-10-14 — password recovery tenant scope

Branch `cursor/scope-password-recovery-tenant-d51a` in `vlr-api`, from `develop`. Not merged. No migration, secrets, or deploy.

`PasswordRecoveryService` used to resolve the recovery address with `IgnoreQueryFilters()`, so one request could see members of every tenant. A null tenant also turns the global query filter off.

The lookup now requires `ITenantProvider.TenantId` and matches `core.users` only in that tenant. `POST /api/auth/forgot-password` may set the tenant from `X-Tenant-Subdomain`. The HTTP body stays the same sentence when the email is a member, unknown, or only in another tenant. No current tenant means no lookup and no recovery email.

`vlr-web` `requestPasswordReset` still posts `{ email }` through `publicApi` and does not send the header, so that panel gets the generic success and no mail until it passes the tenant.
