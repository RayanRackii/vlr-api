# 2026-10-16 — tenant names on GET /api/users/me

`CurrentUserResponse` gains `tenantTradeName` and `tenantLegalName` (#191). Both are nullable. The values come from `Tenant.TradeName` and `Tenant.LegalName` for the tenant already resolved on the caller (`ITenantProvider.TenantId`). A platform super-admin with no tenant gets both fields as JSON null. A stored name that is missing or only whitespace is returned as null. The response does not include the tax id.

No migration. The pull request stays open until the two security reviews on this contract change.

Web follow-up: the B2B header in `vlr-web` should show `tenantTradeName`, falling back to `tenantLegalName`, from `GET /api/users/me`.
