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

Credentials alone never enable Resend or Meta. Unset flags are **false in every environment** (including a Railway service whose `ASPNETCORE_ENVIRONMENT` is Production).

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

Do not bake `false` into `appsettings.Production.json` for the global flag — that would disable PROD email if the env var were forgotten. Channel-specific `false` is the safe way to keep one provider on Dev while the other is live.

| Setting | Effect with credentials |
|---------|------------------------|
| All unset | Dev email + Dev WhatsApp |
| `AllowExternalDelivery=true` only | Resend + Meta (legacy) |
| `AllowExternalWhatsApp=true`, email unset/false | Meta only; email stays Dev |
| `AllowExternalEmail=false` with global true | Email Dev; WhatsApp follows global |

Startup logs `External email delivery enabled|disabled` and `External WhatsApp delivery enabled|disabled` (no secrets).

Local DEV: leave flags unset to stay on Dev providers. To send WhatsApp only: `AllowExternalWhatsApp=true` and `AllowExternalEmail=false`.

Railway: this task does **not** set env vars. After merge, DEV can set `Notifications__AllowExternalWhatsApp=true` without enabling Resend.

## Ops checklist

- [ ] `Resend:ApiKey` / `FromEmail` / `FromName` set on Railway (same as invites).
- [ ] **`App:FrontendBaseUrl=https://rolvix.com.br` on Railway** — never `localhost`.
- [ ] (DEV only, optional) `Notifications:AllowExternalEmail=true` for real Resend; `AllowExternalWhatsApp=true` for Meta only.
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
