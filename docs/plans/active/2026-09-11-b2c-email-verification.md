# 2026-09-11-b2c-email-verification

Status: approved

## Goal / Problem

B2C signup activation is currently SMS (Twilio Verify) gated on `core.customers.phone_verified_at`. Product decision: `B2C_PRIMARY_VERIFICATION_CHANNEL = EMAIL`. Signup must verify the customer's email with a 6-digit OTP. Phone stays collected/stored but must not block signup and must not call Twilio Verify during normal register/resend. Existing active customers must not be locked out. DEV-only; no PROD deploy. Twilio infrastructure stays for future explicit phone verification.

## Visible behavior

- Register (`POST /api/auth/customer/register`): insert/resume pending customer, issue 6-digit email OTP, send via configured `IEmailProvider`. Returns `{ customerId, requiresEmailVerification: true, requiresPhoneVerification: true (compat alias), verificationStarted }`. `verificationStarted=false` if email provider definitively failed or IP is rate-limited; customer row is kept.
- Verify (`POST /api/auth/customer/verify-phone` kept; `POST /api/auth/customer/verify-email` alias): `{ email, code }`. Validates hashed OTP, sets `EmailVerifiedAt`, issues JWT. Wrong/expired/reused/over-attempt → 401.
- Resend (`POST /api/auth/customer/resend-verification`): new OTP, invalidates previous pending OTP, sends email. 202 on success, unknown email, already-verified, or cooldown. 429 on IP rate limit.
- Login requires `IsEmailVerified`; else 401 `Email is not verified. Complete email verification first.`
- Legacy `request-otp` / `verify-otp`: unchanged Twilio SMS. Out of slice.
- Frontend: register → `/verify-email` (redirect from `/verify-phone`). Masked email, 6-digit code, 45s resend cooldown, email copy in pt-BR/en/es.

## Repositories

- vlr-api
- vlr-web

Cross-repo branch: `feat/b2c-email-verification`. Merge order: **API first**.

## Relevant existing ADR / rules

- `vlr-api/.cursor/rules/10-arquitetura.mdc` — B2C JWT Customer; phone SMS gate superseded by this spec.
- `vlr-api/.cursor/rules/20-convencoes.mdc` — DI, EF async, DTOs, GQF, additive migration.
- `vlr-api/CONTEXT.md` § Portal B2C — SMS-as-signup-gate is now wrong; update after implement.
- New ADR: `docs/adr/0005-b2c-email-verification-migration.md`.

## Architecture route

- rolvix-architect (this spec)
- Merge Risk Gate: **Fable required** (auth + tenant + persisted data). Parent invokes after PRs.

## Execution route

- api-implementer
- web-implementer

## Confirmed decisions

1. Email is the activation proof. Additive `core.customers.email_verified_at`. `IsEmailVerified = EmailVerifiedAt is not null`. Keep `PhoneVerifiedAt` / `IsPhoneVerified` (not the gate).
2. Backfill: `UPDATE core.customers SET email_verified_at = phone_verified_at WHERE phone_verified_at IS NOT NULL;`
3. Reuse `core.otp_codes` additively: `purpose`, `code_hash`, `attempts`, `replaced_at`. Make `code` nullable so new rows store no plaintext. Existing leftover rows get `purpose = 'Legacy'`. New rows `purpose = 'EmailVerification'`.
4. HMAC-SHA256 of the 6-digit code bound to customer id + purpose, key = `Supabase:JwtSecret`. No `Auth__CustomerVerificationChannel` config.
5. OTP: 6-digit, TTL 10 min, one-time, resend sets `replaced_at` on previous active row, max 5 attempts, 45s cooldown via existing `IPhoneVerificationSendGate` (keep name), IP 10/10min. Resend does not leak account existence.
6. Send path: **synchronous** `IEmailProvider.SendAsync` in register/resend. Not `NotificationQueue`. Durability matches current Twilio Verify (sync HTTP; row committed before send; resend recovers).
7. Email: subject `Código de verificação Rolvix`; `RolvixEmailLayout.Wrap`. Reuse Resend/`Notifications__AllowExternalEmail`. Do not couple to WhatsApp flags or tenant notification settings.
8. Shape: `CustomerAuthService` → `ICustomerVerificationCodeService` → `IEmailProvider`. OTP logic not in the email provider.
9. Keep `POST /verify-phone`; add `POST /verify-email` to the same action. JSON `{ email, code }` unchanged. Register DTO: canonical `requiresEmailVerification`; `requiresPhoneVerification` is a serialized alias of the same value.
10. Duplicate/resume/login gates use `EmailVerifiedAt` instead of `PhoneVerifiedAt`.
11. Frontend route `verify-email` + redirect from `verify-phone`.
12. `request-otp` / `verify-otp` untouched.
13. Pending SMS users (`EmailVerifiedAt` null): resume/resend via email OTP.
14. Twilio client/config/DI kept; not invoked by register/resend/verify-email.

## Invariants that must not break

- GQF on Customer and OtpCode; no cross-tenant OTP.
- Email lowercase trim; uniqueness per tenant.
- No email-existence leak on resend (202 unknown/verified/cooldown; 429 IP limit).
- Existing actives can log in after migration (backfill).
- Tenant delete still removes OtpCodes.
- Legacy Twilio OTP login still works.

## Implementation scope

See parent investigation + architect handoff. API first, then WEB may proceed from this contract.

### API

- Domain: `Customer.EmailVerifiedAt`, `MarkEmailVerified`; `OtpCode` purpose/hash/attempts/replaced; `Code` nullable.
- Migration `B2cEmailVerification` (additive + backfill). EF command: `dotnet ef migrations add B2cEmailVerification --project Core/Platform.Core.Infrastructure --startup-project Platform.Api`
- `ICustomerVerificationCodeService`: Issue (invalidate prior, return plaintext once), Verify (TTL, attempts, HMAC, one-time).
- `CustomerAuthService` register/resend/login/verify-email as above. Catch `HttpRequestException` on send → `verificationStarted=false` (register) or silent 202 (resend). Do not throw 503 from register for email send failure (keep customer + DTO).
- Reuse `PhoneVerificationInvalidException` / rate-limit exceptions for HTTP mapping.
- Profile DTOs: add `EmailVerified`; keep `PhoneVerified`.
- Tests in `tests/Platform.Api.Tests/CustomerAuth/` as listed below.

### WEB

- Route rename + redirect; page rename; i18n; masked email; 45s resend cooldown; 6-digit paste-friendly input.
- Zod: `requiresEmailVerification` required; `requiresPhoneVerification` optional compat; `emailVerified` on profiles (optional default false so old API shapes still parse during rollout).
- Page tests following `TenantPortalAgendaPage.test.tsx` (user-event + mocked services). E2E Playwright not present for this flow → unit/component tests are the seam.

## Likely affected areas / files

Listed in architect output. Confirm `IEmailProvider` is registered (`AddNotificationInfrastructure`); DI registration order does not need to change — resolve at request time.

## Test seams

- API: `ICustomerAuthService` + controller + in-memory `AuthHarness`. Fake `IEmailProvider` recorder. Real `CustomerVerificationCodeService` with test HMAC key + `TestTimeProvider`. Keep `FakePhoneVerificationClient` for legacy request-otp/verify-otp.
- WEB: Zod + page tests (RTL). `TEST_INFRASTRUCTURE_MISSING` for Playwright E2E.

## Verification strategy

- `dotnet test` CustomerAuth (and full suite if practical)
- `npm test` + `tsc` / `npm run build`
- DEV smoke with human-owned email (parent/human). No PROD.

## Product-level "how to test"

1. DEV with Resend: register → inbox subject `Código de verificação Rolvix` → enter code on `/verify-email` → `/app`.
2. Confirm Twilio is not called (no SMS).
3. Resend cooldown; old code rejected; new code works.
4. Invalid/expired code stays on page.
5. Grandfathered customer (PhoneVerifiedAt set) can log in after migration.

## Do not

- Remove Twilio infrastructure.
- Change tenant Notification Settings / WhatsApp flags.
- Require phone verification for activation.
- Call Twilio on register/resend/verify-email.
- Lock out existing actives.
- Second email provider or NotificationQueue for signup OTP.
- `Auth__CustomerVerificationChannel` config theater.
- Leak email existence on resend.
- Retire request-otp/verify-otp.
- Deploy PROD.
- Edit applied migrations.

## Documentation that may need updating

- `vlr-api/CONTEXT.md` and `vlr-web/CONTEXT.md` mirror
- both `ROADMAP.md`
- ADR 0005
- Authentication context pack is planned/not present — `CONTEXT_PACK_UPDATE_RECOMMENDED` deferred.

## OTP security rules (implementer)

- Generate with `RandomNumberGenerator.GetInt32(0, 1_000_000)` formatted `D6`.
- Hash: HMAC-SHA256(key=Utf8(JwtSecret), utf8($"{customerId:N}:{purpose}:{code}`)). Store Base64 `code_hash`.
- Compare with `CryptographicOperations.FixedTimeEquals`.
- Verify looks up latest non-used, `replaced_at` null, `purpose=EmailVerification`, not expired.
- Wrong code increments `attempts` and saves. At 5, treat as invalid even if later correct (do not reveal).
- Issue marks prior matching rows `replaced_at = now`.
- New rows: `code = null`, never write plaintext digits to `code`.

## Email failure behavior

| Case | Register | Resend |
|---|---|---|
| Provider 2xx | `verificationStarted=true` | 202, cooldown recorded |
| Provider 4xx/5xx / HttpRequestException | 200 + `verificationStarted=false`, customer kept | 202, errors swallowed (anti-enum) |
| IP limited | 200 + `verificationStarted=false` | 429 |
| Cooldown | 200 + `verificationStarted=true`, no second send | 202, no second send |
| Process crash after commit before send | customer pending; user uses resend | — |

Do not activate account without successful OTP verify.

## Existing-user compatibility

- Active = historically `PhoneVerifiedAt` set → backfill `EmailVerifiedAt`.
- Pending SMS = both null → email OTP on next register/resend.
- Login after migration uses `EmailVerifiedAt` only.

## Merge Risk Gate

Fable required. Compact dossier after implementation.
