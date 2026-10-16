# Staff writes check the GoTrue session, not only the JWT

Issue #184. A staff browser signed out in one tab and still created a lesson in another. The access token had not expired.

**Status:** accepted (2026-10-15)

## Decision

1. Supabase access tokens are checked locally: signature (JWKS ES256/RS256, or the legacy HS256 secret) and `exp`, with one minute of clock skew. Issuer and audience are not checked. That check does not see logout.
2. Logout deletes the GoTrue session row and revokes the refresh token. The access JWT stays cryptographically valid until `exp`. Current GoTrue `GET /auth/v1/user` runs `requireAuthentication` → `maybeLoadUserOrSession`. When the JWT has a `session_id` and that row is gone, GoTrue returns 403 `session_not_found`.
3. Staff writes (POST, PUT, PATCH, DELETE) on the Supabase JWT scheme call that endpoint with the caller's bearer. The service role is sent only as `apikey`. A 401 or other 4xx fails the request (HTTP 401). GET, HEAD, OPTIONS, and TRACE do not call GoTrue.
4. Timeout, network failure, HTTP 429, and HTTP 5xx fail the write closed as HTTP 500 (`Staff session check failed.`). They are not reported as a revoked session. The client response and the log line do not include the token or the Auth body.
5. Redirects are disabled on that client. The call is bounded to 3 seconds. There is no cache of a positive result, so the next write after logout sees the deletion.
6. Customer JWTs are not Supabase sessions. They stay on `CustomerJwtSessionValidator` (password stamp) for every request, including reads. Signing out of the B2C portal does not revoke that JWT until it expires, unless the password stamp changes.
7. Past booking and lesson starts are a separate rule: HTTP 400 `START_IN_THE_PAST`, tenant clock via `TenantBookingClock` (today `America/Sao_Paulo`).

## Considered options

- **Local JWT only.** This is what allowed the logged-out tab to create the lesson. Rejected for writes.
- **SELECT on `auth.sessions` from the API role.** Same fact (missing row means logged out), but the app connection may not have that grant, and the table is GoTrue's. Rejected.
- **Shorten the access-token lifetime.** Reduces the window and does not reject a token that was just revoked. Not this change.
- **Check every request, including reads.** Stronger, and it adds an Auth round-trip to every panel GET. The issue asks for writes. Reads remain valid until `exp`.

## Limitations

- A token with no `session_id` claim is not tied to a row. GoTrue can still return 200 for that user. Logout is then invisible until `exp`. Current Supabase access tokens include `session_id`.
- GoTrue is the authority. If a future Auth build stops loading the session on `GET /user`, this check stops seeing logout. We do not keep a second copy of `auth.sessions`.
- A logged-out tab can still read until the access token expires.
- B2C logout is not a GoTrue session. This ADR does not add a B2C server session.
- Each staff write pays one Auth round-trip. An Auth outage rejects the write with 500.

## Implementation

`GoTrueAccessSessionProbe`, `StaffWriteSessionGuard`, wired from `OnTokenValidated` on the Supabase JWT scheme in `SupabaseAuthenticationExtensions`.
