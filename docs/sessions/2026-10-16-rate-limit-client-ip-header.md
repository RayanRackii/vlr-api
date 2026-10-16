# 2026-10-16 — configurable rate-limit client IP header

`RateLimit:ClientIpHeader` chooses the only header used as the per-IP rate-limit key. Allowed values are `X-Real-IP` (default, blank, or missing) and `Fly-Client-IP`. ADR [`docs/adr/0008-rate-limit-client-ip-header.md`](../adr/0008-rate-limit-client-ip-header.md). Parsing and the `RemoteIpAddress` fallback stay in ADR 0006.

Railway needs no new variable. If `FLY_APP_NAME` is set and the selected header is not `Fly-Client-IP`, or if `RAILWAY_ENVIRONMENT` or `RAILWAY_PROJECT_ID` is set and the selected header is `Fly-Client-IP`, the process does not start. With none of those variables, either allowed header still boots. Startup logs the selected header name once. A value outside the pair fails process start. `X-Forwarded-For` and `CF-Connecting-IP` are still unread. A private socket address does not turn the selected header off: both edges present their own address, and ignoring the header on a private socket would put every public caller in one bucket.

No migration.
