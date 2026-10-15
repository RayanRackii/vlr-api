# 2026-10-15 — rate-limit client IP (#109)

Branch `cursor/rate-limit-client-ip-dd11` in `vlr-api`, from `develop`. Draft PR. No migration, no env, no secrets, no deploy.

Rayan accepted the Railway edge behavior from the October 2026 docs: the edge overwrites `X-Forwarded-For` and `X-Real-IP`, the hop count is not fixed, proxy CIDRs are not published, and `X-Real-IP` is the client-IP header. ADR [`docs/adr/0006-rate-limit-client-ip.md`](../adr/0006-rate-limit-client-ip.md).

Staff password recovery, B2C verification and password reset, and anonymous trial create now share `RateLimitClientIp`. A single well-formed `X-Real-IP` is the partition key, stored as the canonical address. `X-Forwarded-For` and `CF-Connecting-IP` are not read. Forwarded-headers middleware is not registered. A missing or malformed header uses `Connection.RemoteIpAddress`, so rotating garbage cannot open a new bucket. A missing socket address still lands in the gates' shared `unknown` bucket.

A client that can reach the API without Railway's edge can still choose any well-formed `X-Real-IP`. That path stays accepted: there is no published proxy range, and ignoring the header whenever the socket address is private would put every public caller on the edge's address.
