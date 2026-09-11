# 2026-09-11-b2c-email-verification-auth-bypass

Status: approved

## Goal / Problem

`POST /api/auth/customer/verify-otp` issues a Customer JWT after Twilio phone check without requiring `EmailVerifiedAt`. That bypasses the canonical B2C activation gate introduced in ADR 0005.

## Visible behavior

- `verify-otp` with a valid Twilio code still records `PhoneVerifiedAt` when missing.
- If `EmailVerifiedAt` is null → 401 `{ error: "Email is not verified. Complete email verification first." }` and **no JWT**.
- If `EmailVerifiedAt` is set (including grandfathered backfill) → 200 + JWT as today.
- `request-otp` unchanged (Twilio Start; may still create a phone-only pending row).
- Login / verify-email / register / resend unchanged except they remain gated on email for JWT.

## Repositories

- vlr-api

## Relevant existing ADR / rules

- `docs/adr/0005-b2c-email-verification-migration.md`
- `docs/plans/active/2026-09-11-b2c-email-verification.md`

## Architecture route

- rolvix-architect (this spec)
- Merge Risk Gate: **Fable required** (auth/JWT)

## Execution route

- api-implementer

## Confirmed decisions

1. Canonical JWT rule: `EmailVerifiedAt != null`. Phone verification is not an activation requirement.
2. `VerifyOtpAsync`: Twilio Check → MarkPhoneVerified + SaveChanges if needed → if `!IsEmailVerified` throw `UnauthorizedAccessException` with the same login message → else `BuildAuthResponse`.
3. Fail-closed in `BuildAuthResponse` if `EmailVerifiedAt` is null (sole production mint point). Do **not** change `CustomerJwtIssuer.IssueToken` (bearer pipeline test fixtures).
4. Do not delete `request-otp` / `verify-otp`. Do not stop RequestOtp create-if-missing in this slice.
5. No WEB. No PROD. No Twilio removal. No migration.

## Invariants that must not break

- Grandfathered customers (`EmailVerifiedAt` backfilled) can still log in and, if they hit verify-otp after Twilio, still receive JWT.
- Email-verified signup customers still get JWT from verify-email and login.
- Register/resend/verify-email still do not call Twilio.
- Tenant GQF isolation.

## Implementation scope

- `CustomerAuthService.VerifyOtpAsync` + `BuildAuthResponse`
- Rewrite `VerifyOtp_approved_marks_phone_verified_and_returns_jwt` into the cases listed under tests
- Add the user-required regressions in CustomerAuth tests
- One-sentence ADR 0005 append; ROADMAP/CONTEXT one-liner

## Test seams

`ICustomerAuthService` via `CustomerAuthHarness` (existing).

Required:

1. email-unverified Customer cannot obtain JWT through legacy verify-otp (phone-only pending AND registered-but-unverified email)
2. phone verification alone does not activate (EmailVerifiedAt stays null; login still 401)
3. email-verified Customer can authenticate normally (login + optional verify-otp JWT)
4. grandfathered (EmailVerifiedAt set, simulating backfill) remains functional
5. verify-email still activates and issues JWT
6. login still requires EmailVerifiedAt
7. normal signup/resend still does not call Twilio
8. tenant isolation remains intact

## Verification strategy

`dotnet test --filter FullyQualifiedName~CustomerAuth` then full API suite if practical.

## Product-level "how to test"

DEV: pending register (do not verify email) → request-otp → verify-otp → 401, phone_verified_at set, email_verified_at null. Then verify-email → login 200. Repeat verify-otp after email verified → 200 JWT.

## Do not

- Remove Twilio
- Redesign auth / delete endpoints
- Require PhoneVerifiedAt for JWT
- Touch vlr-web
- Deploy PROD
- Put the gate inside `CustomerJwtIssuer`

## Documentation that may need updating

- ADR 0005 (one sentence)
- CONTEXT.md leftover OTP-login wording if it still says verify-otp authenticates
- ROADMAP.md Histórico
