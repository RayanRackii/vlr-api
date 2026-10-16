# 2026-10-16 — configurable rate-limit client IP header

`RateLimit:ClientIpHeader` chooses the only header used as the per-IP rate-limit key. Allowed values are `X-Real-IP` (default, blank, or missing) and `Fly-Client-IP`. ADR [`docs/adr/0008-rate-limit-client-ip-header.md`](../adr/0008-rate-limit-client-ip-header.md). Parsing and the `RemoteIpAddress` fallback stay in ADR 0006.

Railway needs no new variable. Fly (gru) must set `RateLimit__ClientIpHeader=Fly-Client-IP` before public traffic, because `X-Real-IP` is not overwritten there. A value outside the pair fails process start. `X-Forwarded-For` and `CF-Connecting-IP` are still unread. A private socket address does not turn the selected header off: both edges present their own address, and ignoring the header on a private socket would put every public caller in one bucket.

No migration.
