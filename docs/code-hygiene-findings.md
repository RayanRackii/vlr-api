# Code hygiene findings — 2026-08-04

Sweep after `LogoSvg` branding work. Scope: monorepo `C:\Free` (backend + frontend). No mass deletion without product decision — items below are inventory + recommended next cleanups.

## Fixed in this pass

| Item | Action |
|------|--------|
| Product use of `logoUrl` / `<img src=…>` in portal & admin | Replaced by `LogoSvg` + `TenantLogoMark` + DOMPurify |
| Duplicate `getTenantBaseDomain` (admin hook vs portal service) | Single source: `frontend/src/lib/tenantDomain.ts`; admin re-export removed |
| Local `initials()` in `TenantPortalLayout` | Removed; initials live in `TenantLogoMark` |

## Obsolete / legacy (keep for now)

| Item | Where | Notes |
|------|--------|--------|
| Column / property `Tenant.LogoUrl` | Domain + EF + DB `logo_url` | Writes always set `null`. Drop column in a later migration after prod data confirmed unused. |
| B2C OTP login (`request-otp` / `verify-otp`) | `CustomerAuthController` + service | Documented in CONTEXT as legacy until password flow is fully stable. |
| `InviteUser` / `submitInvitePassword` | Backend MediatR feature; FE stub service | Invite token flow incomplete (regra de ouro). |
| `RolePermission` / fine-grained RBAC | EF model | Persistido; little/no runtime enforcement (already on ROADMAP). |

## Duplication / smell (not rewritten this pass)

| Item | Notes |
|------|--------|
| Branding color defaults (`#0F766E`) | Repeated in portal layout, logo mark, customer layout — candidate for one `DEFAULT_TENANT_PRIMARY` constant. |
| `iconForModule` | Only maps `rentals` → same icon as default; placeholder until more B2C modules. |
| MediatR islands (`CreateTenant`, `InviteUser`) vs Modules pattern | CONTEXT already forbids expanding MediatR without decision; leftover architectural split. |
| Customer `PhotoUrl` as large text / data-URL | Column widened to `text`; storing base64 in DB is costly — move to object storage later. |

## Infra debt (unchanged)

| Item | Notes |
|------|--------|
| Hangfire | `WorkerCount = 1` + capped pool — intentional vs Supabase pool limits; dashboard requires PlatformAdmin (F-02). |
| Migrations pending on hosted DB | `AddTenantLogoSvg`, and possibly earlier menu/registration migrations — apply via EF or SQL scripts under `backend/scripts/`. |
| No automated tests | Backend/frontend — known. |

## Intentionally not “junk”

| Item | Why keep |
|------|----------|
| `SvgMarkupValidator` + client DOMPurify | Defense in depth (API reject + render sanitize). |
| Empty `LogoSvg` → initials fallback | Product requirement. |
| Obsolete `LogoUrl` column until drop migration | Safer than surprise schema break on Railway/Supabase. |

## Suggested follow-ups (priority)

1. Apply `AddTenantLogoSvg` on Supabase; paste FICC SVG in admin; smoke-test portal.
2. Drop `logo_url` after confirming no external consumers.
3. ~~Remove deprecated re-export from `usePlatformAdmin`.~~ Done.
4. Retire OTP-only B2C endpoints when password login is the only path in UI.
5. Extract shared `DEFAULT_TENANT_PRIMARY` (small cleanup).
