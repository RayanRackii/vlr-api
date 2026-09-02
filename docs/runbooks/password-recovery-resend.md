# Password recovery (B2B) via Resend

## Goal

B2B "Esqueci a senha" must send a **Rolvix-branded** email (same chrome as invites), not the default Supabase Auth template.

## How it works

1. Frontend `POST /api/auth/forgot-password` with `{ email }` (does **not** call `resetPasswordForEmail`).
2. API looks up the Auth user (admin). If missing → still returns a generic OK (anti-enumeration).
3. API calls GoTrue `POST /auth/v1/admin/generate_link` with `type=recovery`.
4. API builds a **first-party** link (avoids Supabase Site URL fallback to `localhost:3000`):

   `https://rolvix.com.br/reset-password?token_hash=…&type=recovery`

5. API enqueues HTML via `RolvixEmailLayout` + Resend (or Dev log fallback).
6. `/reset-password` calls `supabase.auth.verifyOtp({ token_hash, type: 'recovery' })`, then `updateUser({ password })`.

## External delivery gate

Credentials alone never enable Resend or Meta. Unset flags are **false in every environment** (including a Railway service whose `ASPNETCORE_ENVIRONMENT` is Production). This is fail-closed everywhere: Production does **not** auto-enable Resend.

Per channel:

```
effectiveEmail =
    (Notifications:AllowExternalEmail ?? Notifications:AllowExternalDelivery) == true
    && Resend credentials present

effectiveWhatsApp =
    (Notifications:AllowExternalWhatsApp ?? Notifications:AllowExternalDelivery) == true
    && WhatsApp credentials present
```

An explicit channel flag overrides the legacy global `AllowExternalDelivery`. Catalog SMS stays on `DevSmsProvider`.

**`Notifications__AllowExternalEmail=true` is a PROD requirement** for invite and password-recovery mail. It is not a DEV-only optional switch. Without it, Production stays on `DevEmailProvider` (console `DEV EMAIL`) even when Resend credentials exist.

Do not bake `false` into `appsettings.Production.json` for the global flag — that would disable PROD email if the env var were forgotten. Channel-specific `false` is the safe way to keep one provider on Dev while the other is live.

| Setting | Effect with credentials |
|---------|------------------------|
| All unset | Dev email + Dev WhatsApp |
| `AllowExternalDelivery=true` only | Resend + Meta (legacy) |
| `AllowExternalWhatsApp=true`, email unset/false | Meta only; email stays Dev |
| `AllowExternalEmail=false` with global true | Email Dev; WhatsApp follows global |
| `AllowExternalEmail=true` + Resend creds | Resend (required in PROD for invite/recovery) |

Startup always logs Information `External email delivery enabled|disabled` and `External WhatsApp delivery enabled|disabled` (no secrets).

Startup also logs **Error** (no secret values — not ApiKey, FromEmail, or ServiceRoleKey):

- Host is Production **and** `DevEmailProvider` is selected (unset/false email gate, or incomplete Resend).
- Email gate is true (`AllowExternalEmail` or global `AllowExternalDelivery`) **and** Resend is incomplete (missing ApiKey or FromEmail) — email stays on Dev. This Error can fire in Development too.
- Host is Production **and** `DevStorageProvider` is selected (missing `Supabase:Url` or `Supabase:ServiceRoleKey`).

The process still starts. Dev fallbacks are not silent in Production.

Local DEV: leave flags unset to stay on Dev providers. To send WhatsApp only: `AllowExternalWhatsApp=true` and `AllowExternalEmail=false`.

Do **not** set `Notifications__AllowExternalDelivery=true` on Railway unless WhatsApp should also go live.

## Railway PROD variables (names only)

Set on the **production** Railway service (not implied by `ASPNETCORE_ENVIRONMENT`):

| Variable | Required | Role |
|---|---|---|
| `Notifications__AllowExternalEmail` | **yes** (`true`) | Opt-in Resend. Do **not** set `Notifications__AllowExternalDelivery=true` unless WhatsApp should also go live. |
| `Resend__ApiKey` | **yes** | Resend API key |
| `Resend__FromEmail` | **yes** | Verified sender |
| `Resend__FromName` | recommended | Default in code: `Rolvix` |
| `App__FrontendBaseUrl` | **yes** | Invite/recovery links; must be `https://rolvix.com.br`, never localhost |
| `Supabase__Url` | **yes** (existing Auth) | Same project URL for Auth **and** Storage HTTP / public/signed URL prefix. Do **not** set `Storage__SupabaseUrl`. |
| `Supabase__ServiceRoleKey` | **yes** (existing Auth) | Same service role as Auth. Do **not** set `Storage__ServiceRoleKey`. |
| `Storage__PublicBucket` | optional | Default `catalog-public` |
| `Storage__PrivateBucket` | optional | Default `catalog-private` |
| `Storage__SignedUrlTtlSeconds` | optional | Default `900` |

Supabase Storage: buckets `catalog-public` (public) and `catalog-private` (private) must exist. Public bucket policy must allow read of customer-visible objects.

After restart, boot log must **not** Error on Dev email/storage for this service. Creating a tenant must **not** log `DEV EMAIL`. File URLs must **not** contain `dev-storage.local`.

This repo cannot write Railway. A human must set the variables above.

## Ops checklist

- [ ] `Notifications__AllowExternalEmail=true` on Railway **production** (required for invite/recovery; not DEV-only).
- [ ] `Resend__ApiKey` / `Resend__FromEmail` / `Resend__FromName` set on Railway (same as invites).
- [ ] **`App__FrontendBaseUrl=https://rolvix.com.br` on Railway** — never `localhost`.
- [ ] Reuse existing `Supabase__Url` and `Supabase__ServiceRoleKey` on Railway production (catalog files). Do **not** duplicate as `Storage__SupabaseUrl` / `Storage__ServiceRoleKey`.
- [ ] Confirm Supabase Storage buckets `catalog-public` and `catalog-private` exist (public bucket allows read of customer-visible objects).
- [ ] Confirm boot logs: `External email delivery enabled`; no Error for `DevEmailProvider` / `DevStorageProvider` in Production.
- [ ] Supabase Auth → URL Configuration:
  - Site URL: `https://rolvix.com.br` (not `localhost:3000`)
  - Redirect URLs include `https://rolvix.com.br/**` and `http://localhost:5173/**` for local API tests
- [ ] Do not use Supabase's Recovery email template for this flow anymore.

## Why the old link broke

`action_link` goes through `*.supabase.co/auth/v1/verify?…&redirect_to=…`. If `redirect_to` is missing from the allowlist, Supabase substitutes the **Site URL** (often still `http://localhost:3000`). The hash lands on a dead origin.

## Do not

- Point Supabase custom SMTP at Resend just for this — templates would still be Supabase Go templates.
- Call `resetPasswordForEmail` from the client again.
- Put `localhost` in Railway `App__FrontendBaseUrl`.
- Set `Notifications__AllowExternalDelivery=true` unless WhatsApp should also go live.
- Commit secret values (ApiKey, ServiceRoleKey) in git or `appsettings.json`.
