# 2026-08-31-twilio-verify-phone

Status: approved (user already decided the product; one minor, reversible contract addition is documented under Confirmed decisions)

## Goal / Problem

Replace the local-RNG OTP + `core.otp_codes` + `NotificationQueue` SMS path for **B2C Customer phone verification** with **Twilio Verify v2** as the exclusive provider. Twilio generates and validates the OTP; the API never generates a 6-digit code for phone verification again. Scope is the `CustomerAuth` module only. Catalog SMS notification infrastructure (`ISmsProvider` / `DevSmsProvider` / `CatalogNotificationPublisher` / `NotificationQueue` SMS path for Catalog) is untouched. No EF migration, no PROD.

## Visible behavior

- **Register** (`POST /api/auth/customer/register`): DTO unchanged. After the Customer is persisted, the API starts a Twilio Verify `Verification` to `Customer.Phone` (E.164 `+55…`). Response unchanged (`{ customerId, requiresPhoneVerification: true }`). If Twilio start fails (provider/rate-limit/missing config), the Customer remains persisted (unverified) and the endpoint returns the mapped error (503 / 429).
- **Verify phone** (`POST /api/auth/customer/verify-phone`): DTO unchanged (`{ email, code }`, 6-digit). The API runs a Twilio `VerificationCheck` against `Customer.Phone`. On `approved` → `Customer.MarkPhoneVerified(now)` (existing `PhoneVerifiedAt` semantics preserved) → issue JWT. On invalid/expired → 401; on 429 → 429; on provider failure → 503.
- **Resend verification** (new, additive: `POST /api/auth/customer/resend-verification`, body `{ email }`): looks up the Customer by email (tenant-scoped), starts a new Twilio `Verification` on `Customer.Phone`. Returns 202 on `pending`. Same error mapping as register.
- **Legacy request-otp** (`POST /api/auth/customer/request-otp`): DTO unchanged (`{ name, contact }`). Rewired to Twilio. No local code is generated. `Accepted` on `pending`. No longer overwrites `Customer.Name` on existing customers.
- **Legacy verify-otp** (`POST /api/auth/customer/verify-otp`): DTO unchanged (`{ contact, code }`). Rewired to `VerificationCheck` on the resolved phone. On `approved`, marks phone verified if not already, returns JWT.
- **Login** (`POST /api/auth/customer/login`): unchanged. Still requires `IsPhoneVerified`.
- **Tenant isolation**: preserved. All endpoints go through `IPublicTenantBinder.BindFromSubdomainAsync` + `AppDbContext` Global Query Filter.
- **Frontend**: register → verify-phone navigation unchanged. Verify-phone page gains a "Resend code" action. Error toasts use `parseApiError` passthrough.

## Repositories

- vlr-api (primary)
- vlr-web (Resend button + i18n keys + service function; no existing DTO shape changes)

## Relevant existing ADR / rules

- `vlr-api/.cursor/rules/10-arquitetura.mdc` — CustomerAuth module; B2C JWT `Customer`; tenant filter; `X-Tenant-Subdomain`.
- `vlr-api/.cursor/rules/20-convencoes.mdc` — DI via `AddXxxModule`; async EF; DTOs; `{ error: string }`.
- `vlr-api/.cursor/rules/00-produto.mdc` — B2C cadastro → SMS no celular → login e-mail+senha.
- `vlr-api/CONTEXT.md` — celular verificado por SMS no cadastro (prova de posse), não autentica.
- **No new ADR.** Scoped provider swap, reversible, clean seam.

## Architecture route

- rolvix-architect. No Fable escalation now. Merge Risk Gate later will likely require Fable (auth + external provider).

## Execution route

- api-implementer (vlr-api) → build/test → api-reviewer
- web-implementer (vlr-web) → build → web-reviewer
- Same branch name both repos: `feat/twilio-verify-phone`

## Confirmed decisions

1. **Twilio Verify v2 exclusive** for B2C phone verification. No local RNG OTP for phone. `core.otp_codes` is no longer written or read on this path.
2. **Sync calls from CustomerAuth** to Twilio Verify (start + check) — **not** via `NotificationQueue` / `ISmsProvider`. Phone verification is an auth challenge, not a Catalog notification.
3. **HttpClient + Twilio Verify REST**, no SDK. Auth = HTTP Basic `ApiKeySid:ApiKeySecret` (not Auth Token). `VerifyServiceSid` from config.
4. **Fail closed** if Twilio config missing when a real send/check is attempted. No `DevSms`-style fallback that logs a code for this path.
5. **No EF migration.** `OtpCode` entity + `core.otp_codes` table remain.
6. **Preserve DTOs** for register / login / verify-phone / request-otp / verify-otp.
7. **Additive resend endpoint** `POST /api/auth/customer/resend-verification` `{ email }`. Register-flow verify-phone page only has `email`. `request-otp` requires `{ name, contact }` and previously overwrote `Customer.Name`.
8. **RequestOtp Name-overwrite fix**: `Name` is only set when creating a new Customer.
9. **`Customer.PhoneVerifiedAt` semantics preserved** — on `approved`, call `MarkPhoneVerified(DateTimeOffset.UtcNow)`.
10. **Brazilian phones via `BrazilianDocumentValidator.NormalizePhoneBr`** (already returns `+55…`).
11. **No PROD.** Stop before PROD promotion. DEV smoke only against Railway DEV credentials.

## Invariants that must not break

- Tenant isolation: Customer resolved through `AppDbContext` with the active tenant filter.
- `{ error: string }` response shape for all error paths.
- `Customer` is never returned directly; only DTOs/records.
- Never log OTP codes, API keys, or full phone (last-4 only).
- Tests never call live Twilio and never send real SMS.
- Catalog SMS path is unchanged.
- No migration; `OtpCode` entity stays compilable.

## Twilio REST endpoints and auth

- **Start Verification**
  - `POST https://verify.twilio.com/v2/Services/{VerifyServiceSid}/Verifications`
  - body (form-encoded): `To=+5511999991111&Channel=sms`
  - JSON `status` ∈ `pending` | `approved` | `canceled`
- **Verification Check**
  - `POST https://verify.twilio.com/v2/Services/{VerifyServiceSid}/VerificationCheck`
  - body (form-encoded): `To=+5511999991111&Code=123456`
  - JSON `status`; wrong/expired code typically 404 (Twilio 60200/60202).
- **Auth**: HTTP Basic, username = `ApiKeySid`, password = `ApiKeySecret`.
- **Config keys** (Railway DEV already set, never printed): `Twilio__AccountSid`, `Twilio__ApiKeySid`, `Twilio__ApiKeySecret`, `Twilio__VerifyServiceSid`.

## Error → HTTP mapping table

| Twilio outcome | Exception | HTTP | Body |
|---|---|---|---|
| `status == pending` (start) | none | 200 (register) / 202 (request-otp, resend) | normal response |
| `status == approved` (check) | none | 200 | JWT / auth response |
| `status == canceled` | `PhoneVerificationInvalidException` | 401 (verify) / 400 (register/resend) | `{ "error": "Verification canceled. Please request a new code." }` |
| 404 / expired / invalid code (check) | `PhoneVerificationInvalidException` | 401 (verify) | `{ "error": "Invalid or expired verification code." }` |
| 429 or Twilio 60203 (max send) / 60202 (max check) | `PhoneVerificationRateLimitedException` | 429 | `{ "error": "Too many verification attempts. Try again shortly." }` |
| 5xx / network / timeout | `PhoneVerificationProviderException` | 503 | `{ "error": "Phone verification provider unavailable. Try again." }` |
| Config missing on call | `PhoneVerificationProviderException` | 503 | same (do not reveal config state) |
| Customer has no phone | `ArgumentException` | 400 | `{ "error": "Phone is required for SMS verification." }` |
| Customer not found (verify-phone / resend) | `UnauthorizedAccessException` / `KeyNotFoundException` | 401 / 404 | existing mapping |

## What happens to `core.otp_codes`

- No migration. Table and `OtpCode` entity remain.
- `CustomerAuthService` no longer writes or reads `core.otp_codes`.
- `IssueAndEnqueuePhoneCodeAsync`, `ConsumeOtpAsync`, `GenerateOtpCode`, `OtpLifetime` are removed.

## Implementation scope

### vlr-api

**New files**

- `Platform.Api/Modules/CustomerAuth/PhoneVerification/IPhoneVerificationClient.cs`
  - `Task StartVerificationAsync(string phoneE164, CancellationToken ct);`
  - `Task CheckVerificationAsync(string phoneE164, string code, CancellationToken ct);`
- `Platform.Api/Modules/CustomerAuth/PhoneVerification/TwilioVerifyOptions.cs` — bind from `Twilio` section: `AccountSid`, `ApiKeySid`, `ApiKeySecret`, `VerifyServiceSid`, `BaseUrl` (default `https://verify.twilio.com/v2/`).
- `Platform.Api/Modules/CustomerAuth/PhoneVerification/TwilioVerifyPhoneVerificationClient.cs` — `HttpClient`. Basic auth. Never logs code/key; logs phone last-4 + status only.
- `Platform.Api/Modules/CustomerAuth/PhoneVerification/PhoneVerificationExceptions.cs`
- `tests/Platform.Api.Tests/Fakes/FakePhoneVerificationClient.cs`

**Edited files**

- `CustomerAuthService.cs` — replace `NotificationQueue` with `IPhoneVerificationClient`.
- `ICustomerAuthService.cs` — add `ResendVerificationAsync`.
- `CustomerAuthController.cs` — map new exceptions; add `resend-verification`.
- `CustomerAuthDtos.cs` — add `ResendVerificationRequestDto`.
- `CustomerAuthModuleExtensions.cs` — accept `IConfiguration`; bind options; `AddHttpClient<IPhoneVerificationClient, TwilioVerifyPhoneVerificationClient>`.
- `Program.cs` — `AddCustomerAuthModule(builder.Configuration)`.
- `appsettings.json` — empty `"Twilio": {}` placeholder (no secrets).
- `CustomerDocumentTests.cs` — pass `FakePhoneVerificationClient` instead of `NotificationQueue`.

**New tests** in `tests/Platform.Api.Tests/CustomerAuth/PhoneVerificationTests.cs`:

- Register starts verification on `Customer.Phone`.
- Register + provider error → exception, customer persisted unverified.
- Register + rate-limit → rate-limit exception.
- VerifyPhone approved → `PhoneVerifiedAt` set, JWT returned.
- VerifyPhone invalid → invalid exception, `PhoneVerifiedAt` stays null.
- VerifyPhone rate-limited / provider error.
- Resend starts on existing customer's phone; missing customer → not found.
- RequestOtp phone contact starts on that phone; email contact starts on `Customer.Phone`; no Name overwrite.
- VerifyOtp approved → marks verified + JWT.
- No test reads/writes `core.otp_codes`.
- Controller mapping: provider → 503, rate-limit → 429 (construct controller with fakes; no WebApplicationFactory).
- Twilio client unit tests with `HttpMessageHandler` fake (no network): pending, approved, 404, 429, 500, missing config. Never send real SMS.

### vlr-web

- `tenantPortalService.ts` — `resendCustomerPhoneVerification(subdomain, { email })`.
- `TenantPortalVerifyPhonePage.tsx` — Resend code button; disable while submitting; no cooldown required (429 protects).
- `src/locales/{pt-BR,en,es}/common.json`:
  - `tenantPortal.verify.resend`
  - `tenantPortal.verify.resendSubmitting`
  - `tenantPortal.verify.resendToastSuccess`
  - Update `tenantPortal.verify.subtitle` so it no longer says the code appears in the API log.

## Test seams

- `IPhoneVerificationClient` — sole seam for faking Twilio.
- `InMemoryAppDb` + `FakeTenantProvider`.
- `FakeJwtIssuer`.

## Verification strategy

- `dotnet test` (unit tests, fake Twilio).
- `npm run build` on vlr-web.
- One controlled DEV SMS smoke against Railway DEV after merge to `develop`. No PROD.

## Product-level "how to test"

1. Portal B2C → register with a real Brazilian mobile. Expect SMS with a 6-digit Twilio code.
2. Enter the code on `/verify-phone`. Expect JWT → `/app`.
3. Wrong code → 401 toast.
4. Resend → new SMS.
5. Login before verifying → "Phone number is not verified."
6. After verifying, login with email + password → success.

## Do not

- Do not touch `main`, PROD, migrations, or PROD secrets.
- Do not route phone verification through `NotificationQueue` / `ISmsProvider` / `CatalogNotificationPublisher`.
- Do not change Catalog SMS infrastructure.
- Do not generate local OTP codes for phone verification.
- Do not add a Twilio SDK.
- Do not log OTP codes, API keys, or full phone numbers.
- Do not create local Twilio secrets. Fail closed locally if config missing.
- Do not alter existing register/login/verify-phone/request-otp/verify-otp DTO shapes.
- Do not remove `OtpCode` entity or `core.otp_codes` table.
- Do not overwrite `Customer.Name` on `RequestOtp`/resend.
- Do not call live Twilio in tests.
- Do not promote to PROD.

## Documentation that may need updating

- `vlr-api/ROADMAP.md` and `vlr-web/ROADMAP.md` (checklist + Histórico).
- `vlr-api/CONTEXT.md` and `vlr-web/CONTEXT.md` (SMS line: Twilio Verify for B2C phone verification; keep "celular não autentica").
- No new ADR.
