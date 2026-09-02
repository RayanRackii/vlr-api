# 2026-09-02-customer-auth-tenant-context

Status: approved

## Goal / Problem

After PR #40, Customer policy succeeds but `GET /api/catalog/portal/products` returns 401 `Tenant context is required.` `UseAuthentication` only runs B2B JwtBearer; `CustomerJwt` runs later in PolicyEvaluator. `HttpContextTenantProvider` uses first identity `IsAuthenticated` and can return null. Null tenant disables GQF (`CurrentTenantId == null || ...`). Never bypass the catalog gate.

## Confirmed decisions

- PolicyScheme / ForwardDefaultSelector as default authenticate scheme (keep name `Bearer`). Peek unvalidated JWT `iss == platform.b2c` → forward `CustomerJwt`; otherwise forward B2B JwtBearer (named e.g. `Supabase`). Selector must not throw on malformed tokens; must not trust claims for authz/tenant.
- Do not make `CustomerJwt` the universal default handler.
- Customer policy stays `CustomerJwt` only. DefaultPolicy / PlatformAdmin authenticate the B2B scheme (not CustomerJwt) **and** reject Customer principals (`role=Customer` or `customer_id`).
- Harden `HttpContextTenantProvider`: any authenticated identity; tenant_id only from authenticated identities; Customer branch **before** platform-admin; Customer missing tenant_id still throws; never return null for authenticated Customer (including when the B2C email is in `PlatformAdmin:Emails`).
- `IPlatformAdminChecker.IsPlatformAdmin` returns false for Customer principals. Email allowlist alone is not enough.
- No WEB, no GQF semantic change, no migration, no gate removal.

## Repositories

- vlr-api

## Architecture / execution

- api-implementer
- api-reviewer
- Merge Risk Gate: **Fable required** (auth routing + tenant isolation). Ask Fable: scheme-confusion, unvalidated JWT routing, B2B↔B2C crossover, null-tenant/GQF, multiple ClaimsIdentity.

## Tests

See user instruction (production-parity shared secret, UseAuthentication establishes Customer principal, GQF/null-tenant never reaches catalog queries, multi-identity TenantProvider, missing tenant_id fail-closed).
