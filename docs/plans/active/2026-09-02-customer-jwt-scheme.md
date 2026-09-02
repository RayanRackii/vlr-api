# 2026-09-02-customer-jwt-scheme

Status: approved

## Goal / Problem

Customer login issues HS256 JWT (`iss=platform.b2c`, `aud=platform.customer`). Authenticated Customer endpoints use the default Supabase JwtBearer handler (OIDC/JWKS). Authentication fails → 401 → frontend clears the Customer session.

## Confirmed decisions

- Do **not** weaken/replace B2B JwtBearer (keep MetadataAddress, JWKS, existing TokenValidationParameters including legacy HS256 fallback).
- Add named scheme `CustomerJwt` with strict HS256 + `Supabase:JwtSecret`, `ValidateIssuer`/`ValidateAudience` true (`platform.b2c` / `platform.customer`), lifetime + signature required, `ValidAlgorithms` = HS256 only.
- Bind policy `"Customer"` to `CustomerJwt` only (`AddAuthenticationSchemes` + RequireAuthenticatedUser + RequireRole Customer). Do not accept B2B scheme on that policy.
- All `[Authorize(Policy = "Customer")]` endpoints inherit the scheme via the policy. Anonymous B2C stays anonymous.
- Tenant isolation unchanged: `tenant_id` / `customer_id` from authenticated Customer JWT.
- No WEB, no interceptor change, no migration, no Railway/Supabase/Twilio/WhatsApp/main/PROD.

## Repositories

- vlr-api

## Architecture / execution

- api-implementer
- api-reviewer
- Merge Risk Gate: **Fable required** (auth boundary)

## Implementation notes

Extract Customer JWT `TokenValidationParameters` construction so tests share production settings. Remove the B2B comment that HS256 IssuerSigningKey is used to validate B2C tokens.

CustomerJwtIssuer contract (iss/aud/claims/lifetime) unchanged.

## Tests

See user instruction. Cover `GET /api/catalog/portal/products` with a valid Customer JWT. Full `dotnet test tests/Platform.Api.Tests`.
