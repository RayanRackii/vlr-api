# B2C signup verification is email, not SMS

B2C account activation is proof of email possession via a 6-digit OTP (Resend/`IEmailProvider`, hashed in `core.otp_codes`). Phone remains collected for future WhatsApp, but `PhoneVerifiedAt` is no longer the login gate. Twilio Verify stays in the codebase for legacy `request-otp`/`verify-otp` and a future explicit phone-verify feature; normal register/resend must not call it.

**Status:** accepted (2026-09-11)

Existing SMS-verified customers are grandfathered: `email_verified_at = phone_verified_at` so they are not locked out. That trusts email without a new proof for those rows — the alternative was forcing every active B2C user through email OTP.

Signup OTP is sent **synchronously** in the HTTP request (same durability class as Twilio Verify). It does not use the in-memory `NotificationQueue` used by B2B invite/recovery.

HMAC of the OTP uses `Supabase:JwtSecret` so no second auth secret is introduced. OTP TTL is 10 minutes, so JWT secret rotation only invalidates in-flight codes.

Spec: [`docs/plans/active/2026-09-11-b2c-email-verification.md`](../plans/active/2026-09-11-b2c-email-verification.md).
