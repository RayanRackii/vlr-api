# 2026-10-16 — staff phone on the current user

`GET /api/users/me` now includes `phone` (`string` or null). `PUT /api/users/me` replaces that number for the signed-in staff member only. The route and the body have no user id. A string is normalized to E.164 with `BrazilianDocumentValidator.NormalizePhoneBr`, the same helper used for other phone fields. Null clears the number. A body that omits `phone` does not clear it. An invalid number returns 400 `{ "error": string }` and does not echo the submitted value.

The lookup reads the ambient tenant before it queries `core.users`, keeps the global tenant filter, and also matches `TenantId`. Another membership, including the same login in another tenant, is left unchanged. PUT is not a safe HTTP method, so the existing staff-session guard still rejects a revoked or expired GoTrue session.

`tenantTradeName` and `tenantLegalName` were already on this GET (#205). No gap there.

Migration `20261016211139_AddUserPhone` adds nullable `core.users.phone` (`varchar(32)`) and drops it in `Down`. It is not applied. The phone is not unique and is not a customer document. The pull request stays a draft until the two security reviews.
