# 2026-08-31-b2c-pending-registration

Status: approved (user specified required behavior after DEV smoke)

## Goal / Problem

Register persists the Customer, then Twilio Verify start can fail (DEV: HTTP 403 mapped to 503). Uniqueness on email/phone/document then strands the user: they cannot register again and never reach `/verify-phone`. Unverified customers must be **pending registrations** that can be resumed.

## Visible behavior

- Register is **idempotent** for an unverified Customer in the same tenant when **email + phone + document** all match and `PhoneVerifiedAt == null`: resume that row (update password hash + name), do not insert a duplicate, try to send verification again.
- A **verified** Customer matching email, phone, document, or CPF still returns 409 duplicate.
- Partial overlap with a pending row (same email, different phone/document, etc.) → 409, no silent hijack, no delete.
- After persist or resume, Twilio start failure **does not** fail the HTTP register as a fatal error. Response is 200:
  `{ customerId, requiresPhoneVerification: true, verificationStarted: false }`.
- Frontend always navigates to `/verify-phone` on that outcome; explains that the account exists but the code could not be sent; shows Reenviar código.
- Do **not** delete the Customer when Twilio fails (timeout after accept is ambiguous).
- `POST /api/auth/customer/resend-verification`: pending → attempt send; already verified or unknown email → **neutral 202**, no send, no 404; application rate limit (IP) → 429; same email cooldown → 202 without a second Twilio call (idempotent).
- Login of verified customers unchanged (still requires `IsPhoneVerified`).

## Repositories

- vlr-api
- vlr-web

## Architecture route

- User product decision (this spec). No new ADR.

## Execution route

- api-implementer then/with web-implementer
- Branch: `fix/b2c-pending-registration`

## Confirmed decisions

1. Pending = `PhoneVerifiedAt == null`. Resume only when email, phone, **and** document match that pending row (same tenant).
2. Never delete on Twilio failure.
3. Register DTO request unchanged. Response adds `verificationStarted: boolean`.
4. Catch provider/rate-limit/invalid-on-start after persist; return 200 + `verificationStarted`.
5. Resend never 404; 202 for unknown/verified/cooldown; 429 only for IP abuse; do not leak existence.
6. Application rate limit: `IMemoryCache` + `TimeProvider`. Per tenant+email: 45s cooldown after a **successful** start (second resend/register-resume → 202, no Twilio). Per IP: 10 starts / 10 minutes → 429.
7. On pending resume, refresh `PasswordHash` and `Name` from the new request so the password they just typed is the one that works after verify.
8. Do not touch PROD, `main`, or migrations.
9. Twilio DEV 403 is diagnosed separately (config present); this task must not print secrets.

## Implementation scope

### vlr-api

- `RegisterCustomerResponseDto(Guid CustomerId, bool RequiresPhoneVerification, bool VerificationStarted)`
- `CustomerAuthService.RegisterAsync`: uniqueness as specified; `TryStartVerification` catch → flag.
- `ResendVerificationAsync`: no KeyNotFoundException; skip send if missing/verified; use send gate; swallow provider errors (still 202). Rate-limit exception still bubbles for 429.
- `PhoneVerificationSendGate` + fake for tests.
- Controller: register no longer 503 for start failure (service does not throw). Resend: KeyNotFound catch unused; always Accepted unless 429/400.
- Improve Twilio client log: include parsed numeric `code` (e.g. 20003) on non-2xx; 401/403 stay provider exception (do not reveal auth to client).
- Tests listed in user request (see Test seams).

### vlr-web

- `registerResponseSchema.verificationStarted: z.boolean()`
- Register page: on success, navigate with `{ email, verificationSendFailed: !verificationStarted }`.
- Also: if register HTTP 503 with provider-unavailable (old API / mixed deploy), navigate to verify-phone with `verificationSendFailed: true` instead of fatal toast-only.
- Verify-phone page: banner when `verificationSendFailed`; Reenviar already exists.
- i18n pt-BR/en/es.

## Do not

- Delete customers on Twilio failure.
- Change verified login.
- Touch Catalog SMS, `main`, PROD, migrations.
- Print Twilio secrets.
- Call live Twilio in tests.
