# 2026-08-18-b2c-meu-perfil

Status: approved

## Goal / Problem

Authenticated B2C Customer on a tenant subdomain portal (e.g. `ficc.rolvix.com.br`) needs a **Meu Perfil** page to view their own data, edit only the fields this MVP explicitly allows, save, and receive clear success/error feedback.

This is not a settings area. Out of scope: notification preferences, theme, language, 2FA, sessions/devices, delete account, billing, tenant settings, permissions, organization management.

## Visible behavior

1. Signed-in Customer opens the existing AppShell account/avatar menu and chooses **Meu Perfil**.
2. Route `/app/perfil` (host `ficc.rolvix.com.br`) or `/t/:subdomain/app/perfil` (path mode) loads inside `CustomerAppLayout`.
3. Page fetches `GET /api/customers/me` and shows the current Customer's data.
4. **Nome** is editable. **Foto** is editable via the existing register file-picker + `fileToCompressedDataUrl` flow (never as a text field for `PhotoUrl`).
5. E-mail, telefone and CPF are visible as read-only when present. Password is never displayed and has no change control.
6. Address, ExtraAttributes and identity fields are not editable. They may appear as read-only when useful.
7. Save calls `PATCH /api/customers/me` with only editable fields. Duplicate submit is blocked. Success toast + refresh from GET. Error toast with the real API message; form is not cleared; user stays on the page.

## Repositories

- vlr-api
- vlr-web

## Architecture route

- rolvix-architect (GLM 5.2) — 2026-08-18
- Human Decision Gate resolved by user: **A / A / A** (2026-08-18)
- rolvix-deep-architect / Fable: not used (not recommended)

## Execution route

Sequential (new API contract first):

1. `api-implementer` (Grok 4.6) — `vlr-api` only, branch `feat/customer-profile`
2. `api-reviewer` (Grok 4.6, readonly) — Standards × Spec on `origin/develop...HEAD`
3. `web-implementer` (Grok 4.6) — `vlr-web` only, same branch name `feat/customer-profile`
4. `ui-implementer` (Kimi K3) — visual refinement with browser/render when available; no business-rule or contract changes
5. `web-reviewer` (Grok 4.6, readonly) — Standards × Spec on `origin/develop...HEAD`

One active writer per working tree. Implementers may fix reviewer findings and re-run the matching reviewer until there are no Critical/High (blocker/major) findings.

## Confirmed decisions

| Id | Decision |
|---|---|
| UDR-1 | MVP editable set = **Name + Photo**. Photo uses the existing portal mechanism (`fileToCompressedDataUrl` → data URL stored in `Customer.PhotoUrl`, same as register). Do **not** expose `PhotoUrl` as a text input. |
| UDR-2 | New **`CustomerProfileDto`** for `GET /api/customers/me`. Do **not** widen `CustomerAuthProfileDto`. Login/auth and profile remain separate contracts. |
| UDR-3 | E-mail, phone, CPF and password are **not** editable. Display e-mail/phone/CPF as read-only when it makes sense. Never display the password. Each identity/verification capability is a FOLLOW_UP. |

Photo mechanism check (2026-08-18): reusable upload **does exist** in the portal (`TenantPortalRegisterPage` file input + `fileToCompressedDataUrl` in `tenantPortalService.ts`; backend persists `PhotoUrl` and validates length 32–400_000 in `RegistrationAttributeValidator.NormalizePhoto`). Therefore photo **is in this MVP**. FOLLOW_UP “upload de foto” is **not** opened.

PATCH semantics:

- JSON omit = no change.
- `name` present = update name (trimmed, 2–200 chars).
- `photoUrl` non-null string = replace photo (data URL after client compression; server validates length).
- `photoUrl: null` = clear photo.
- Any other property on the request DTO is not accepted (record with only the editable fields).

## Invariants that must not break

- `customerId` comes **only** from the JWT (`customer_id` / `CustomerClaimTypes.CustomerId`). Never from body, query, or route.
- Tenant isolation via EF Core Global Query Filter on `Customer.TenantId`; `HttpContextTenantProvider` reads `tenant_id` from the Customer JWT.
- `[Authorize(Policy = "Customer")]` on both endpoints. B2B User JWT and anonymous callers are rejected.
- PATCH must not modify `Email`, `Phone`, `Cpf`, `PasswordHash`, `PhoneVerifiedAt`, `LastLoginAt`, `PostalCode`, address columns, `ExtraAttributes`, `TenantId`, `Id`, `CreatedAt`.
- JWT is **not** re-issued on profile save (Name/PhotoUrl are not JWT claims).
- Error shape remains `{ "error": string }`.
- No synchronous e-mail / SMS / WhatsApp / ViaCEP on profile save.
- `CustomerAuthProfileDto` and login/verify responses stay unchanged.
- Full profile is not persisted in `localStorage` (token + subdomain + label only).

## Implementation scope

### vlr-api

- New authorized controller in the CustomerAuth module (do **not** put these on the existing `[AllowAnonymous]` `CustomerAuthController`). Suggested: `CustomerProfileController` with `[Authorize(Policy = "Customer")]` and routes `GET /api/customers/me` and `PATCH /api/customers/me`.
- New DTOs: `CustomerProfileDto` (GET/PATCH response), `UpdateCustomerProfileRequestDto` (`Name?`, `PhotoUrl?` only).
- `ICustomerAuthService` / `CustomerAuthService`: `GetCurrentAsync` + `UpdateProfileAsync`. Resolve customer from JWT the same way as `ReservationsController.ResolveCustomerId()`.
- Domain: `Customer.UpdateProfile(string? name, string? photoUrl, bool clearPhoto)` or equivalent; always `Touch()`.
- Photo validation: reuse the same length bounds as register (`< 32` or `> 400_000` → 400). Prefer extracting/sharing `NormalizePhoto` rather than duplicating ad-hoc rules if that stays local and small.
- No EF migration. No new schema.

### vlr-web

- `fetchCustomerProfile` / `updateCustomerProfile` in `tenantPortalService.ts` (Axios + Zod `safeParse`).
- New `customerProfileSchema` / update form schema. Do **not** reuse `customerAuthProfileSchema` as the GET contract.
- New page under `features/tenantPortal/pages/` (e.g. `TenantPortalProfilePage.tsx`).
- Routes under **both** host mode and `/t/:subdomain` mode, inside `CustomerAppLayout`:
  - host: `path="app/perfil"`
  - path: `path="app/perfil"` under `/t/:subdomain`
- Entry: AppShell account dropdown. Add an **optional** profile action/link so B2B `MainLayout` is unchanged (no profile item unless wired). `CustomerAppLayout` wires it to `tenantPortalPath(subdomain, "app/perfil")`.
- Photo UI: file input + preview (`<img src={photoUrl}>` or avatar). Never a text field for the URL. Reuse `fileToCompressedDataUrl`.
- i18n keys in `src/locales/{pt-BR,en,es}/common.json`. No hardcoded user-visible strings.
- Mutation UX: disable submit while saving; sonner success; `parseApiError` on error; no redirect / no form wipe on error; `LoadingButton` if that is the local pattern.

## Likely affected areas / files

### vlr-api

- `Platform.Api/Modules/CustomerAuth/Controllers/` (new controller)
- `Platform.Api/Modules/CustomerAuth/Dtos/CustomerAuthDtos.cs` (new records; do not change `CustomerAuthProfileDto`)
- `Platform.Api/Modules/CustomerAuth/Services/ICustomerAuthService.cs`
- `Platform.Api/Modules/CustomerAuth/Services/CustomerAuthService.cs`
- `Core/Platform.Core.Domain/Entities/Customer.cs`
- `ROADMAP.md`

### vlr-web

- `src/routes/AppRoutes.tsx`
- `src/features/tenantPortal/pages/` (new page)
- `src/features/tenantPortal/services/tenantPortalService.ts`
- `src/features/tenantPortal/schemas/tenantPortalSchemas.ts`
- `src/features/tenantPortal/components/CustomerAppLayout.tsx`
- `src/components/layout/AppShell.tsx` (optional profile slot)
- `src/locales/{pt-BR,en,es}/common.json`
- `ROADMAP.md`

## Test seams (when they exist)

This repo has no established automated test suite. Do not create a new testing program.

Verify with:

- `dotnet build` on touched API projects
- `npx tsc --noEmit` and/or Vite build on `vlr-web`
- Manual steps in **Product-level "how to test"**

## Verification strategy

- Build the API after the backend change.
- Typecheck/build the frontend after the UI change.
- Manually: login as Customer → avatar → Meu Perfil → see data → change name (and photo) → save → toast → reload still shows new values.
- Manually/API: PATCH with `email`/`phone`/`cpf` extra properties is ignored or 400; those columns must not change.
- Customer JWT of tenant A cannot read tenant B (GQF + JWT `tenant_id`).
- B2B User JWT and anonymous: 401/403 on both endpoints.

## Product-level "how to test"

1. Open the tenant portal (`ficc.rolvix.com.br` or local `/t/<subdomain>`).
2. Log in as a Customer (e-mail + password).
3. Open the avatar/account menu → **Meu Perfil**.
4. Confirm name, photo preview, e-mail, phone and CPF (if present) render. Password is absent. `PhotoUrl` is not shown as text.
5. Change the name, save: success toast, values update, submit was disabled during save.
6. Change the photo via file picker, save, confirm preview updates. Reload the page: name and photo persist.
7. Trigger a validation error (empty/short name) and an API error (e.g. stop API): toast with real message; form stays; no redirect.
8. Refresh `/app/perfil` while signed in: page still loads the same customer.
9. Sign out: `/app/perfil` redirects to the portal login.

## Do not

- Do not add notification prefs, theme, language, 2FA, sessions, delete account, billing, tenant settings, or permissions.
- Do not make e-mail, phone, CPF, or password editable.
- Do not invent a new photo storage service or expose `PhotoUrl` as a text field.
- Do not widen `CustomerAuthProfileDto` or the login response.
- Do not accept `customerId` from the client.
- Do not add Meu Perfil to `CustomerSidebar` (module menu).
- Do not call ViaCEP, SMS, e-mail, or WhatsApp on save.
- Do not re-issue the Customer JWT on this PATCH.
- Do not persist the full profile in `localStorage`.
- Do not implement on `main` or `develop`.

## Documentation that may need updating

- `vlr-api/ROADMAP.md` and `vlr-web/ROADMAP.md` (checklist + Histórico).
- `CONTEXT.md` only if glossary/beachhead changes — it should not for this MVP.
- After ship: `CONTEXT_PACK_UPDATE_RECOMMENDED` for the planned `authentication` pack (Customer JWT claims, GET/PATCH `/api/customers/me`, auth DTO vs profile DTO split). Do not create the pack in this feature unless the implementer is already updating canonical docs.

---

## Current behavior

- Login/register/verify-phone/OTP live on `[AllowAnonymous] api/auth/customer`.
- Login returns `AuthResponseDto(Token, CustomerAuthProfileDto)` which **omits** CPF and address.
- Frontend stores only token + subdomain + label.
- There is no Customer GET-me or PATCH-me.
- `GET /api/users/me` is B2B and rejects Customer JWTs.
- AppShell avatar menu currently has only account label + Sign out.

## User flow

See **Visible behavior**.

## Editable fields confirmed

- `Name` (text, trim, 2–200)
- `PhotoUrl` (via file picker + `fileToCompressedDataUrl`; never typed as URL text)

## Read-only fields confirmed

- `Id`, `TenantId`, `CreatedAt` (system)
- `Email`, `Phone`, `Cpf` (identity; display only)
- `PostalCode`, address columns (display optional; not editable)
- `ExtraAttributes` (display optional; not editable)
- `PhoneVerified` (display optional)
- Password / `PasswordHash` (never returned, never shown)

## Route/navigation placement

- Host mode: `/app/perfil`
- Path mode: `/t/:subdomain/app/perfil`
- Navigation: AppShell account/avatar dropdown (optional prop; wired only in `CustomerAppLayout`)
- Not a module menu item

## Existing APIs reused

- Customer JWT + policy `"Customer"`
- `HttpContextTenantProvider` + EF GQF
- `ResolveCustomerId()` pattern (`ReservationsController`)
- `fileToCompressedDataUrl` + register photo validation bounds
- Axios interceptor already sends `rolvix.customer.token` when no Supabase session
- sonner + `parseApiError` + RHF/Zod + i18n

## Required API changes

### `GET /api/customers/me`

- `[Authorize(Policy = "Customer")]`
- Returns `CustomerProfileDto`:
  `Id, TenantId, Name, Email, Phone, Cpf, PostalCode, AddressStreet, AddressNeighborhood, AddressCity, AddressState, PhotoUrl, CreatedAt, PhoneVerified, ExtraAttributes`
- 401 if not a Customer; 404 if the row is missing under the current tenant filter

### `PATCH /api/customers/me`

- `[Authorize(Policy = "Customer")]`
- Body: `UpdateCustomerProfileRequestDto` with only `Name?` and `PhotoUrl?`
- Returns updated `CustomerProfileDto`
- 400 `{ "error": string }` on validation failure

## Validation

- Name: required when provided; trim; 2–200 characters (mirrors register)
- Photo: when provided as string, length 32–400_000 (mirrors `NormalizePhoto`); `null` clears
- Unknown PATCH fields are not bound (DTO has only the two properties)

## States

loading → loaded → editing → saving → success | validation error | API error

- Loading: `PageContentSkeleton` (or equivalent existing skeleton)
- Saving: submit disabled (`LoadingButton` if local pattern)
- Success: toast; re-fetch GET; stay on page
- Validation / API error: inline and/or toast; form kept; submit re-enabled

## Security/auth considerations

- Self-only by JWT `customer_id`
- Tenant isolation by GQF + JWT `tenant_id`
- Photo is a data URL already used at register (no new SSRF/storage surface); size capped
- Identity fields cannot change through this contract
- AppShell profile link is optional so B2B shell does not gain a broken Customer profile route

## Acceptance criteria

- Signed-in Customer finds **Meu Perfil** in the account/avatar menu
- Page shows the current Customer's data from `GET /api/customers/me`
- Only Name and Photo are editable; Photo is a file/preview control, not a URL text field
- E-mail, phone and CPF (when present) look read-only; password is not shown and not editable
- Valid changes save via `PATCH /api/customers/me`; success feedback is clear
- Validation and API errors are clear; no duplicate submit; no silent discard of edits on error
- Refresh of `/app/perfil` keeps the signed-in profile
- Tenant isolation intact; Customer cannot edit another Customer
- B2B User / anonymous cannot use the new endpoints
- `CustomerAuthProfileDto` / login contract unchanged
- UI responsive and consistent with Rolvix; i18n for user-visible strings
- `dotnet build` (API) and frontend typecheck/build pass
- Reviewers report no Critical/High against Standards × Spec

## Open decisions

None. Human Decision Gate closed: A / A / A.

## FOLLOW_UP (out of scope)

- FU-email: change e-mail with verification + JWT re-issue
- FU-phone: change phone with SMS re-verification
- FU-cpf: change CPF with uniqueness / identity rules
- FU-password: B2C password change and/or recovery
- FU-address: edit CEP/address (ViaCEP overwrite vs independent fields)
- FU-extras: edit `ExtraAttributes` against the tenant registration schema
- FU-photo-upload: **not opened** — existing register compression + `PhotoUrl` is reused in this MVP
- FU-header-label: optionally refresh AppShell label from Name after PATCH (today label is e-mail)

## Context pack

`authentication` pack is planned, not present. After this ships: **CONTEXT_PACK_UPDATE_RECOMMENDED** (create pack from canonical code + this spec). Do not treat a missing pack as source of truth.
