# Staff writes check the GoTrue session, not only the JWT

Issue #184. A staff browser signed out in one tab and still created a lesson in another. The access token had not expired.

**Status:** accepted (2026-10-15). Probe cache and status mapping updated 2026-10-16 (#194, #195, #196, #199).

## Decision

1. Supabase access tokens are checked locally: signature (JWKS ES256/RS256, or the legacy HS256 secret) and `exp`, with one minute of clock skew. Issuer and audience are not checked. That check does not see logout.
2. Logout deletes the GoTrue session row and revokes the refresh token. The access JWT stays cryptographically valid until `exp`. Current GoTrue `GET /auth/v1/user` runs `requireAuthentication` → `maybeLoadUserOrSession`. When the JWT has a `session_id` and that row is gone, GoTrue returns 403 `session_not_found`.
3. Staff writes (POST, PUT, PATCH, DELETE) on the Supabase JWT scheme call that endpoint with the caller's bearer. The service role is sent only as `apikey`. GET, HEAD, OPTIONS, and TRACE do not call GoTrue.
4. Only a documented revoked or invalid session is HTTP 401. Those codes are `session_not_found` (the row is gone) and `session_expired`. GoTrue sends them as legacy `error_code`, as a string `code` on API version 2024-01-01, or in the `x-sb-error-code` header. The HTTP status is not the signal: a 401 or 404 from Kong (wrong `apikey`, no route) has none of those codes and is not a revoked session.
5. Timeout, network failure, HTTP 3xx, HTTP 429, HTTP 5xx, and any other non-2xx without those codes fail the write closed as HTTP 500. The client body is the generic handler text `An unexpected error occurred.` The exception text `Staff session check failed.` is not that body. The log records the status and, when the code is one of a fixed Auth set (`session_not_found`, `session_expired`, `bad_jwt`, `user_banned`, `user_not_found`, and a few other Auth codes), that code. Anything else is `unrecognized`. A missing code is `absent`. It does not record the token, the apikey, an email, a session id, or the Auth body. `user_banned`, `user_not_found`, and `bad_jwt` are not session-revocation codes; they take this 500 path and still do not write.
6. Redirects are disabled on that client. The call is bounded to 3 seconds. A per-process cache remembers the outcome for 2 seconds, keyed only by the JWT `session_id` (a non-empty UUID). The raw token is not a key and is not stored in the cache. Concurrent probes for one session share one GoTrue call. The cache holds at most 1024 sessions, and at most 1024 calls are in flight in that map; past that, a call still runs and does not grow the map. A token with no `session_id` is not cached. Logout is visible on the next write after the 2 seconds. The cache is not shared across processes.
7. Customer JWTs are not Supabase sessions. They stay on `CustomerJwtSessionValidator` (password stamp) for every request, including reads. Signing out of the B2C portal does not revoke that JWT until it expires, unless the password stamp changes.
8. Past booking and lesson starts are a separate rule: HTTP 400 `START_IN_THE_PAST`, tenant clock via `TenantBookingClock` (today `America/Sao_Paulo`).

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
- Each staff write pays one Auth round-trip unless this process saw the same session within 2 seconds. An Auth outage rejects the write with 500. A burst for one session calls GoTrue once per 2 seconds in this process. There is no pager; the log line is the signal.
- `user_banned`, `user_not_found`, and `bad_jwt` fail closed as 500, not as 401. A signing-key mismatch must not look like every session was revoked. A banned or deleted user still cannot write.

## Implementation

`GoTrueAccessSessionProbe`, `StaffWriteSessionGuard`, wired from `OnTokenValidated` on the Supabase JWT scheme in `SupabaseAuthenticationExtensions`.
