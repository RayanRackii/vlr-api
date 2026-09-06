# 2026-09-05-reserved-tenant-subdomains

Status: approved

## Goal / Problem

Prevent future tenants from claiming infrastructure/platform-reserved first-level labels under `*.rolvix.com.br` when a subdomain is assigned. Wildcard DNS/host resolution is already complete in PROD; unknown reserved hosts (e.g. `admin.rolvix.com.br`) may continue to render tenant-not-found. This task only blocks tenant **create/rename** onto reserved names — it is not a DNS/Vercel/CORS/host-resolver task.

## Visible behavior

- Super-Admin create and edit reject a reserved slug with HTTP 400 `{ "error": "This subdomain is reserved and cannot be used." }`.
- Trial auto-allocation (`TrialSubdomainGenerator`) never emits a reserved slug.
- Existing tenants are not disabled or renamed. Grandfathered rows keep working for unrelated edits that retain the same slug.
- WEB toasts the API error via existing `parseApiError` (no second reserved list in Zod).

## Repositories

- vlr-api

## Relevant existing ADR / rules

- `vlr-api/.cursor/rules/10-arquitetura.mdc`
- `vlr-api/.cursor/rules/20-convencoes.mdc`
- `vlr-api/.cursor/rules/00-produto.mdc`
- `AGENTS.md`

## Architecture route

- rolvix-architect. No `rolvix-deep-architect`.

## Execution route

- api-implementer

## Confirmed decisions

1. **Canonical reserved set (v1):** `www`, `api`, `app`, `admin`, `dev`, `staging`, `preview`, `mail`, `support`. Do NOT add `auth`, `login`, `account`, `dashboard`, `status`, `help`, `docs`, `cdn`, `static`, `assets` in v1.
2. **Create (`AdminTenantService.CreateAsync`):** always 400 when the normalized slug is reserved. After Trim + ToLowerInvariant, before uniqueness.
3. **Update (`AdminTenantService.UpdateAsync`):** 400 only when the NEW normalized slug is reserved **AND** differs from the tenant's current subdomain.
4. **Trial allocation:** treat reserved names as taken; skip them in `TrialSubdomainGenerator.AllocateAsync`.
5. **No disable/rename** of existing tenants. No data migration.
6. **Error:** HTTP 400 `{ "error": "This subdomain is reserved and cannot be used." }` via existing `ArgumentException` mapping.
7. **Single source of truth:** one frozen set in `Platform.Core.Domain`. WEB does not duplicate it.
8. **Audit:** DEV SQL (including inactive) found no reserved slugs. PROD active branding 404 for the canonical + optional names; `ficc` 200. PROD inactive rows not queried.

## Invariants that must not break

- Unique filtered index on `core.tenants.subdomain` unchanged.
- `NormalizeSubdomain` remains Trim + ToLowerInvariant.
- WEB `getHostTenantSubdomain` and `/t/:slug` untouched.
- Existing tenants remain functional.
- `{ "error": string }` shape preserved.
- DNS, Vercel, CORS, Railway, Supabase untouched.

## Implementation scope

- Reserved-names constant in `Platform.Core.Domain`.
- `AdminTenantService.CreateAsync` / `UpdateAsync` guards.
- `TrialSubdomainGenerator.AllocateAsync` skip reserved candidates.
- Tests at AdminTenantService and TrialSubdomainGenerator seams.
- One-line `CONTEXT.md` glossary note; `ROADMAP.md` check + Histórico.
- No migration. No DTO/controller signature change. No WEB files.

## Likely affected areas / files

- `Core/Platform.Core.Domain/Constants/` (new reserved-names type)
- `Platform.Api/Modules/Admin/Services/AdminTenantService.cs`
- `Core/Platform.Core.Domain/Services/TrialSubdomainGenerator.cs`
- `tests/Platform.Api.Tests/Admin/`
- `CONTEXT.md`, `ROADMAP.md`

## Test seams

- `AdminTenantService.CreateAsync` / `UpdateAsync` (existing `AdminTenantAssetCategoryProvisioningTests` harness or sibling).
- `TrialSubdomainGenerator.AllocateAsync`.

## Verification strategy

- New tests: create reserved → ArgumentException; case variant → same; valid slugs pass; update to reserved → reject; update keeping current slug → allow; grandfathered reserved slug + unrelated edit → allow; trial skips `mail`.
- Full `dotnet test` on `tests/Platform.Api.Tests`.

## Product-level "how to test"

- Super-Admin wizard: subdomain `admin` → toast “This subdomain is reserved and cannot be used.”
- Edit tenant: change slug to `api` → same 400; keep current non-reserved slug → 200.
- `https://admin.rolvix.com.br` still tenant-not-found (wildcard unchanged).

## Do not

- Expand the reserved set beyond the 9 canonical names.
- Touch DNS, Vercel, CORS, host resolver, `/t/:slug`.
- Disable, rename, or migrate existing tenants.
- Add a Zod reserved list in WEB.
- Call Fable.

## Documentation that may need updating

- `CONTEXT.md` (canonical): one-line reserved-set note under Subdomain.
- `ROADMAP.md`: item + Histórico 2026-09-05.
- No ADR.
- No vlr-web CONTEXT mirror (WEB has no list).
