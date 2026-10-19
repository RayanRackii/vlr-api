# Staff invite when the email already has an account

## Threat

Staff identity is one Supabase Auth user per email, global across tenants. `core.users` is one membership row per tenant, with that tenant's roles. The same Auth id can appear in several tenants.

`POST /api/invites/accept` used to be anonymous and, when the email already existed in Auth, called `SetUserPassword` with the password from the body and overwrote `app_metadata.tenant_id` with the inviting tenant. A tenant admin who invited that email could make the invitee replace the global password (lockout) or, with the activation link, take the account over. The other tenant's membership row was not edited, but the credential and the active tenant were.

## Contract

`POST /api/invites/preview` with `{ "token" }` is anonymous. Pending token returns `{ email, fullName, tenantId, tenantName, existingAccount, expiresAt }`. Revoked, accepted, expired, and unknown tokens use the same errors as accept and do not reveal a password.

`POST /api/invites/accept`:

| Case | Body | Session | Result |
|---|---|---|---|
| New email | `{ token, password }` (password at least 8 characters) | none | Creates the Auth user, sets `app_metadata.tenant_id` to the inviting tenant, creates the membership and the invite roles. `existingAccount: false`. |
| Email already in Auth, or already on a `core.users` row in any tenant | password ignored | none, or a Customer JWT | **409** `{ "error": "EXISTING_ACCOUNT_SIGN_IN_REQUIRED" }`. Password hash unchanged. No membership. Token stays pending. |
| Same, but the staff JWT `email` and `sub` are that account | `{ token }` | staff bearer | Membership only in the inviting tenant, with the invite's roles (roles of another tenant are dropped). Password unchanged. `app_metadata.tenant_id` unchanged. `existingAccount: true`. |
| Staff JWT for a different email or `sub` | | staff bearer | **403** `{ "error": "INVITE_ACCOUNT_MISMATCH" }`. No writes. |
| Auth id and local memberships disagree | | | **409** `{ "error": "INVITE_IDENTITY_CONFLICT" }`. No writes. |

Accepted, revoked, and expired tokens still fail and do not write credentials. Reusing an accepted token does not create a second membership.

The invite email no longer tells the person to set a password as the only step. A new account still sets one on the page. An existing account must sign in at `https://rolvix.com.br/login` and accept.

Password recovery (`POST /api/auth/forgot-password`) is unchanged and remains the way to replace a staff password.

## How a person with several tenants gets a tenant at login

Login is Supabase email + password. This API does not issue the staff access token and does not show a tenant picker on login.

The token carries one `app_metadata.tenant_id`. For staff who are not Platform Admins, that claim is the request tenant. There is no automatic choice among memberships. Until something writes the metadata, the person keeps the tenant already stored there (the last new-account invite, or the last explicit selection).

After an existing account accepts a second tenant, metadata stays on the previous tenant. The client lists and selects:

- `GET /api/users/me/tenants` — active memberships of this Auth user only (`tenantId`, `legalName`, `tradeName`, `subdomain`). Also `accessTokenTenantId`, `authMetadataTenantId`, and `sessionRefreshRequired` when those two differ. No permission in the current tenant is required, so a stale token tenant still allows the list. Platform Admin receives **409** `PLATFORM_ADMIN_USES_ENTER` and keeps using enter/exit.
- `POST /api/users/me/tenant` with `{ "tenantId" }` — allowed only when this Auth user has an active membership in an active tenant. Writes `app_metadata.tenant_id`. Does not change the password and does not create a membership. Response `sessionRefreshRequired` is true when the access token still names another tenant.

The current access token does not change in that call. The client refreshes the Supabase session, then calls the API with the new token. Until refresh, staff routes still use the tenant claim already on the token.

## How to test

1. User A in tenant A, known password. Tenant B admin invites A's email (`POST /api/users/invite`).
2. `POST /api/invites/preview` → `existingAccount: true`.
3. `POST /api/invites/accept` with a new password and no bearer → 409 `EXISTING_ACCOUNT_SIGN_IN_REQUIRED`. A still logs in with the old password. A has no row in tenant B.
4. Sign in as A, `POST /api/invites/accept` with the bearer and the token, no reliance on the new password. A is a member of B with the invited roles only. A's roles in A are unchanged. The access token still opens A until `POST /api/users/me/tenant` and a session refresh.
5. New email: accept with a password still creates the user and that password is the one that logs in.
