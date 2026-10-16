# Rate-limit client IP

Public per-IP limits (staff password recovery, B2C verification and password reset, anonymous trial create) partition on the client address. Issue #109.

**Status:** accepted (2026-10-15)

**Update (2026-10-16):** ADR 0008 selects the header with `RateLimit:ClientIpHeader`. The default is still `X-Real-IP`. Parsing, canonical form, and the socket-address fallback below are unchanged.

**Human decision:** Rayan, from Railway's edge behavior documented in October 2026. The edge overwrites `X-Forwarded-For` and `X-Real-IP`. The number of hops is not fixed. Railway does not publish proxy CIDRs. `X-Real-IP` is the documented client-IP header.

## Decision

1. The partition key is `X-Real-IP` when that header has exactly one value and the value is a single IPv4 or IPv6 address (`IPAddress.TryParse` after trim).
2. The stored key is the canonical address text. An IPv6 scope id is dropped. An IPv4-mapped IPv6 address is stored as IPv4. Equivalent spellings (`203.0.113.005`, `::ffff:203.0.113.5`, `2001:0db8::1`, `fe80::1%1` / `fe80::1%2`) share one bucket.
3. `X-Forwarded-For` and `CF-Connecting-IP` are not read.
4. Forwarded-headers middleware is not registered. `ForwardLimit = 1` with an empty `KnownProxies` list is rejected: the hop count is not fixed, and there is no published proxy range to trust.
5. A missing or malformed `X-Real-IP` — the usual shape of a private-network call that never crossed the edge — uses `Connection.RemoteIpAddress`, also canonical. The raw header is never the key. Rotating garbage, a comma-separated list, or a second header value stays on that socket address.
6. When the socket address is missing too, the reader returns null. `PhoneVerificationSendGate` and `AnonymousTrialCreateGate` already collapse null to one shared `unknown` bucket and still count the attempt.

## Considered options

- **First `X-Forwarded-For` hop:** rejected. A client can rotate the first hop and open a new bucket (#109).
- **`ForwardedHeadersMiddleware` with `ForwardLimit = 1` and cleared `KnownProxies`:** rejected. Railway does not publish the proxy CIDRs, and the hop count is not a stable one.
- **Reject anything but a strictly dotted IPv4 or compressed IPv6:** not required. `IPAddress` already accepts one address, and the canonical text stops spelling variants from becoming extra buckets. A value that does not parse falls back to the socket address.

## Consequences

- On public ingress the edge overwrites `X-Real-IP`, so the client cannot choose the key.
- A caller who reaches the process without that edge can still send any single well-formed `X-Real-IP` and pick a bucket. That residual risk is accepted. There is no proxy list to distinguish that path from public ingress, and treating a private `RemoteIpAddress` as "ignore the header" would collapse every public request onto the edge's socket address.
- No migration and no proxy configuration. Leaving `RateLimit:ClientIpHeader` unset keeps this `X-Real-IP` behavior (ADR 0008).

## Implementation

`Platform.Api/Http/RateLimitClientIp.cs`, used by staff recovery, customer auth, and anonymous trial create.
