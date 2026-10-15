# 2026-10-15 — customer JWT store failures (#176)

Branch `fix/issue-176-customer-jwt-db-500` in `vlr-api`, from `develop`. Draft PR. No migration, no deploy.

`CustomerJwtSessionValidator` used to call `Fail("Session expired.")` when the stamp read threw. That is HTTP 401, so a database outage looked like an expired B2C session. vlr-web #148 only stopped the client from treating that 401 as a retry loop.

A missing row, a claim that does not match `PasswordChangedAt`, or a token without customer and tenant ids still fails the session (401). Request cancellation still propagates. Any other exception is logged as the exception type plus a five-character Postgres `SqlState` when one is present, then replaced with `CustomerSessionStoreUnavailableException` (fixed message, no inner exception). JwtBearer rethrows that, and the existing pipeline exception handler returns HTTP 500 `{ "error": "An unexpected error occurred." }`.

The stamp query still uses `IgnoreQueryFilters` and requires both ids from the customer JWT. The ambient tenant is not the source of the customer row.
