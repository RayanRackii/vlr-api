# Configurable rate-limit client IP header

Public per-IP limits still partition on one client address (ADR 0006). Which header carries that address depends on the edge in front of the process. Railway overwrites `X-Real-IP`. Fly.io (region gru) sets `Fly-Client-IP` and does not validate `X-Real-IP`, so a caller on Fly can spoof `X-Real-IP`.

**Status:** accepted (2026-10-16)

## Decision

1. `RateLimit:ClientIpHeader` (environment `RateLimit__ClientIpHeader`) is the only header the limiter reads.
2. Allowed values, compared without regard to case after trim: `X-Real-IP` and `Fly-Client-IP`. A missing or blank value selects `X-Real-IP`.
3. Any other value fails process start (`ValidateOnStart`). The reader also refuses a name outside that pair, so a bad value cannot be turned into a lookup of `X-Forwarded-For` or `CF-Connecting-IP`.
4. Parsing, canonical form, and the fallback stay as in ADR 0006, applied to the selected header only. A missing or malformed value uses `Connection.RemoteIpAddress`. The raw header text is never the key. A null socket address still collapses to the gates' shared `unknown` bucket.
5. A private `RemoteIpAddress` does not disable the selected header. Both edges present their own socket address, often on a private network. Ignoring the header whenever that address is private would put every public caller in one bucket. A private-network call that omits the selected header, or sends a malformed one, is keyed by the socket address and does not honor any other forwarded header.
6. The committed default is `X-Real-IP`. Railway needs no new variable. Fly must set `Fly-Client-IP` in the environment. No production file hardcodes the Fly header. The process does not detect the host.

## Threat model

- **Spoofed `X-Real-IP` on Fly.** Keeping ADR 0006's header after the move would let the client choose the bucket. With `Fly-Client-IP` configured, `X-Real-IP` is ignored. Trust in `Fly-Client-IP` assumes the Fly proxy overwrites that header on public ingress, the same kind of assumption ADR 0006 makes about Railway and `X-Real-IP`.
- **Spoofed `Fly-Client-IP` on Railway.** The default still reads only `X-Real-IP`. A client-supplied `Fly-Client-IP` is not the key.
- **`X-Forwarded-For` and `CF-Connecting-IP`.** Never read, on either setting. Configuring either name prevents startup.
- **Missing or malformed selected header.** One bucket per socket address. On public ingress that address is the proxy, so those requests share the proxy's bucket. Rotating garbage, a list, or a repeated header does not open a new bucket. If the socket address is missing too, every such request shares the `unknown` bucket.
- **Caller that never crossed the edge** (private network, direct bind). That caller can still send one well-formed value of the *selected* header and choose a bucket. Same residual risk as ADR 0006. There is still no proxy CIDR list. Treating a private socket address as "ignore the header" would collapse public ingress.
- **Fly left on the default.** `X-Real-IP` stays spoofable. The option has to be set before that deploy takes public traffic. Startup will not infer it.
- **Typo in the option.** The process does not start, and it does not silently trust a different header.

## Considered options

- **Free-form header name.** Rejected. The deploy could point the limiter at `X-Forwarded-For` and reopen #109.
- **Detect Railway vs Fly from request headers.** Rejected. A header the client can send is not evidence of which edge terminated the connection.
- **Ignore the selected header when `RemoteIpAddress` is private.** Rejected. See decision 5 and ADR 0006.

## Consequences

- Unset, blank, and `X-Real-IP` behave as ADR 0006. Railway is unchanged.
- Fly is wrong until the environment selects `Fly-Client-IP`.
- No migration. Forwarded-headers middleware stays unregistered.

## Implementation

`RateLimitOptions` and `RateLimitClientIp`, registered from `Program.cs`. Staff recovery, customer auth, and anonymous trial create still call `RateLimitClientIp.From(HttpContext)`. The reader loads the option from that request's services.
