# 2026-10-16 — GoTrue staff-write probe cache and status mapping

Follow-ups #194, #195, #196, and #199. Staff writes still call GoTrue `GET /auth/v1/user`. HTTP 401 is only `session_not_found` or `session_expired` (`error_code`, string `code`, or `x-sb-error-code`). A Kong 401/404, an HTTP 3xx, 429, or 5xx is HTTP 500 with `{ "error": "An unexpected error occurred." }`. The log line does not include the access token, apikey, email, or session id.

The outcome is cached in this process for 2 seconds, keyed by `session_id`, with at most 1024 entries. Concurrent probes for one session share one GoTrue call. A token with no `session_id` is not cached. There is no pager. ADR `docs/adr/0007-staff-write-session-revocation.md`. No migration.
