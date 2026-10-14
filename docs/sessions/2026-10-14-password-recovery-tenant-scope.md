# 2026-10-14 — password recovery tenant scope

Branch `cursor/scope-password-recovery-tenant-d51a` in `vlr-api`, from `develop`. Merged as #100 (`c901e83`) on `develop` (DEV deployed). No migration, secrets, or deploy. The harden follow-up is in the update below.

`PasswordRecoveryService` used to resolve the recovery address with `IgnoreQueryFilters()`, so one request could see members of every tenant. A null tenant also turns the global query filter off.

The lookup now requires `ITenantProvider.TenantId` and matches `core.users` only in that tenant, with the same exact email comparison as `UserDirectoryService` after the request address is trimmed and lowercased. `POST /api/auth/forgot-password` may set the tenant from `X-Tenant-Subdomain`. The HTTP body stays the same sentence when the email is a member, unknown, or only in another tenant, and when the subdomain is unknown or inactive. That resolution failure is caught on this route only, so it stays 200 and queues nothing. No current tenant means no lookup and no recovery email.

`vlr-web` `requestPasswordReset` still posts `{ email }` through `publicApi` and does not send the header, so that panel gets the generic success and no mail until it passes the tenant.

## Update — merged, then harden

#100 is on `develop` (`c901e83`, DEV deployed). The same `develop` also contains #101 (`23f7bbb`, tenant filter on `FOR UPDATE`) and the #96 tree (stale template locks / lesson source; merge `ef8c7a5` on `main` matches #95 `83c0f09` on `develop`).

Follow-up branch `fix/password-recovery-harden-lows` (not merged; needs GLM/Fable): an inactive `core.users` row does not queue recovery mail, and the public route uses `PhoneVerificationSendGate` scope `staff-password-recovery`. Email cooldown stays a generic 200. The IP window returns 429. No migration and no deploy.
