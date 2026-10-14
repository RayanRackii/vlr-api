# 2026-10-14 — public trial onboarding uses invite

Branch `feat/trial-onboarding-invite` in `vlr-api`, from `develop`. Draft PR. No migration, no deploy, no secrets.

`POST /api/onboarding/tenants` with `IsTrial: true` no longer accepts `AdminPassword`. It creates the tenant, the SuperAdmin role, and a pending `core.user_invites` row for `AdminEmail`, then queues the same `/invite?token=` email used by `ITenantUserAdminService.InviteAsync`. The email is queued only after the transaction commits. Accept stays `POST /api/invites/accept`: the invitee sets the password and that call creates the Supabase user.

Sending a non-blank `AdminPassword` on a trial request returns 400 `AdminPassword is not accepted.` Omitting it returns 201 with `inviteId` and null `adminUserId` / `supabaseAuthId`. Duplicate trial email or phone still returns 409. The Super-Admin wizard still calls `InviteAsync` without a password and still creates permanent tenants through `POST /api/admin/tenants`.

`IsTrial: false`, or a body that omits `isTrial`, returns 400 `{ "error": "ANONYMOUS_TRIAL_REQUIRED" }` and creates nothing (no tenant, no Supabase user, no invite email). A validated trial create consumes one slot in a per-IP window of 10 attempts / 10 minutes (`429` `{ "error": "ANONYMOUS_TRIAL_RATE_LIMITED" }`). The IP is the first `X-Forwarded-For` hop, otherwise `RemoteIpAddress` — the same read as password recovery. Issue #109 still owns changing that trust decision. A `SupabaseAuthAdminException` on this route is logged and returned as `409` `SIGNUP_REJECTED` (provider 409/422) or `502` `SIGNUP_UNAVAILABLE` (anything else). The provider message is not in the response.

`vlr-web` must drop the public onboarding password field, always send `isTrial: true`, accept null `adminUserId` / `supabaseAuthId` plus `inviteId`, and tell the person to set the password from the invite email instead of redirecting them to `/login` as if the account already exists. The CONTEXT mirror in that repo is not updated here.
