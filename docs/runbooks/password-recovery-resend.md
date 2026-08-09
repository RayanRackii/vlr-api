# Password recovery (B2B) via Resend

## Goal

B2B "Esqueci a senha" must send a **Rolvix-branded** email (same chrome as invites), not the default Supabase Auth template.

## How it works

1. Frontend `POST /api/auth/forgot-password` with `{ email }` (no longer calls `supabase.auth.resetPasswordForEmail`).
2. API looks up the Auth user (admin). If missing → still returns a generic OK (anti-enumeration).
3. API calls GoTrue `POST /auth/v1/admin/generate_link` with `type=recovery` and `redirect_to=https://rolvix.com.br/reset-password` (via `App:FrontendBaseUrl`).
4. API enqueues HTML via `RolvixEmailLayout` + Resend (or Dev log fallback).
5. User opens the link → lands on `/reset-password` with a recovery session → `supabase.auth.updateUser({ password })` as before.

## Ops checklist

- [ ] `Resend:ApiKey` / `FromEmail` / `FromName` set on Railway (same as invites).
- [ ] `App:FrontendBaseUrl=https://rolvix.com.br` in Production.
- [ ] Supabase Auth → URL Configuration: allow redirect `https://rolvix.com.br/reset-password` (and local `http://localhost:5173/reset-password` for dev).
- [ ] Optional: in Supabase Auth email templates, leave Recovery unused — the app never triggers Supabase's send for this flow anymore.

## Do not

- Point Supabase custom SMTP at Resend just for this — templates would still be Supabase Go templates and diverge from invite layout.
- Call `resetPasswordForEmail` from the client again (that reintroduces the generic email).
