# 2026-10-14 — public trial onboarding uses invite

Branch `feat/trial-onboarding-invite` in `vlr-api`, from `develop`. Draft PR. No migration, no deploy, no secrets.

`POST /api/onboarding/tenants` with `IsTrial: true` no longer accepts `AdminPassword`. It creates the tenant, the SuperAdmin role, and a pending `core.user_invites` row for `AdminEmail`, then queues the same `/invite?token=` email used by `ITenantUserAdminService.InviteAsync`. The email is queued only after the transaction commits. Accept stays `POST /api/invites/accept`: the invitee sets the password and that call creates the Supabase user.

Sending a non-blank `AdminPassword` on a trial request returns 400 `AdminPassword is not accepted.` Omitting it returns 201 with `inviteId` and null `adminUserId` / `supabaseAuthId`. Duplicate trial email or phone still returns 409. `IsTrial: false` on the same anonymous route still requires a password (legacy). The Super-Admin wizard still calls `InviteAsync` without a password.

`vlr-web` must drop the trial signup password field and stop requiring `adminUserId` / `supabaseAuthId`. The CONTEXT mirror in that repo is not updated here.
