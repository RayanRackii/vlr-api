# 2026-09-02-prod-silent-dev-providers

Status: approved

## Goal / Problem

Production was silently using Dev notification and storage providers: invite emails logged `DEV EMAIL` and never reached Resend; catalog file URLs were `https://dev-storage.local/...`.

Root causes (verified): Catalog HD-5 made `AllowExternalEmail`/`AllowExternalDelivery` unset = false in every environment (including Production), inverting F-05. Storage registers `DevStorageProvider` when `Storage:SupabaseUrl` or `Storage:ServiceRoleKey` is empty. Both fallbacks look like success.

HTML of the invite was **not** the incident cause (email never reached Resend). Nested `<p>` in logs is wrapping; polish is included as a small related fix.

## Visible behavior

- Unset notification flags remain fail-closed in every environment (HD-5). Production send requires explicit `Notifications__AllowExternalEmail=true` **and** Resend credentials.
- F-05 Production+creds+unset → Resend is **not** restored (Railway `development` often has `ASPNETCORE_ENVIRONMENT=Production`).
- Process still starts if Dev providers are selected. Startup emits `LogError` (no secrets) when:
  - host is Production **and** `DevEmailProvider` is selected;
  - email gate is true (`AllowExternalEmail` or global `AllowExternalDelivery`) **and** Resend config is incomplete;
  - host is Production **and** `DevStorageProvider` is selected.
- Invite HTML uses table/`td` blocks instead of `<p>` (email-client safer). Structure remains valid.
- Catalog file API contract unchanged: `GET /api/catalog/products/{id}/files/{fileId}/url` still returns `{ url, isPublic }`. No `PublicBaseUrl`.

## Repositories

- vlr-api

## Relevant existing ADR / rules

- F-05 `docs/plans/active/2026-08-22-notifications-external-delivery-gate.md` (not restored)
- Catalog HD-5 `docs/plans/active/2026-08-28-catalog-orders.md`
- `docs/runbooks/password-recovery-resend.md`
- `.cursor/rules/10-arquitetura.mdc` (notifications async; DI)
- `.cursor/rules/20-convencoes.mdc` (secrets not in git)

## Architecture route

- rolvix-architect (audit 2026-09-02)
- `FABLE_MERGE_REVIEW_NOT_REQUIRED`: config + local hardening; no domain/API contract change. Human chose D1=A (not B/C).

## Execution route

- api-implementer
- Ops: human sets Railway PROD env vars (this repo cannot write Railway)

## Confirmed decisions

- **D1 A:** Keep explicit email opt-in. Ops sets `Notifications__AllowExternalEmail=true` + Resend on Railway PROD. Do not restore F-05.
- **D2 A + hardening:** Do not fail startup on `IsProduction()`. Add `LogError` for the three cases above. No secrets in logs.
- **D3 A:** No `PublicBaseUrl`. Public/signed URL prefix is the project URL (originally `Storage__SupabaseUrl`). Follow-up unifies that host on `Supabase__Url` / `Supabase__ServiceRoleKey` — see ops table below.
- **D4:** Small invite/recovery HTML polish + render test if natural. HTML is not the incident cause.
- **P2:** out of scope (follows this hotfix).

### Proposed (not this PR) — identifying Railway production vs development

Do **not** implement without a further human decision.

Simple existing signals (no new Rolvix architecture):

1. Railway already injects `RAILWAY_ENVIRONMENT_NAME` (`production` vs `development`). Reading it would distinguish the two services even when both set `ASPNETCORE_ENVIRONMENT=Production`.
2. A single optional `App:DeploymentName` in Rolvix config would be a new key and is **not** in this PR.

This hotfix keeps using `IHostEnvironment.EnvironmentName == Production` only for louder logs, knowing Railway development may also match.

## Invariants that must not break

- Unset channel flags stay false everywhere.
- Credentials remain a hard precondition for Resend/Supabase HTTP.
- No synchronous email inside HTTP.
- No secrets in logs, repo, or `appsettings.json`.
- No catalog API contract change.
- No fail-start based solely on `IsProduction()`.
- Private files never get a public URL.

## Implementation scope

1. Notification gate hosted service: `LogError` when Production + Dev email, and when email gate true + Resend incomplete. Keep existing Information lines for enabled/disabled if useful; Error must be unmistakable.
2. Storage: hosted service (same pattern as notifications) `LogError` when Production + Dev storage. Do not change DI selection.
3. `RolvixEmailLayout`: replace body `<p>` with table/`td` (and Wrap greeting). Encode untrusted strings as today. Keep invite URL, CTA, recovery copy.
4. Tests:
   - Existing notification DI tests remain (Production + unset + creds → DevEmailProvider).
   - New tests assert Error logs for the three cases (captured `ILogger`; start hosted services). Assert log text does not contain ApiKey / ServiceRoleKey values.
   - Storage DI seam tests: creds → `SupabaseStorageProvider`; missing → `DevStorageProvider`.
   - HTML render: Wrap+InviteBody has no `<p`; greeting/company/url encoded; CTA `Definir senha` + encoded href; no `<tr>` whose next element is `<p`.
5. Docs: runbook — `AllowExternalEmail=true` is a **PROD requirement** for invite/recovery, not “DEV only, optional”. List Railway vars (names only). ROADMAP Histórico.
6. Optional: catalog pack note that PROD storage/email need explicit Railway config — only if the pack already states the opposite; do not invent pack work.

## Railway PROD variables (names only; never commit values)

Set on the **production** Railway service (not implied by `ASPNETCORE_ENVIRONMENT`):

| Variable | Required | Role |
|---|---|---|
| `Notifications__AllowExternalEmail` | **yes** (`true`) | Opt-in Resend. Do **not** set `Notifications__AllowExternalDelivery=true` unless WhatsApp should also go live. |
| `Resend__ApiKey` | **yes** | Resend API key |
| `Resend__FromEmail` | **yes** | Verified sender |
| `Resend__FromName` | recommended | Default in code: `Rolvix` |
| `App__FrontendBaseUrl` | **yes** | Invite/recovery links; must be `https://rolvix.com.br`, never localhost |
| `Storage__PublicBucket` | optional | Default `catalog-public` |
| `Storage__PrivateBucket` | optional | Default `catalog-private` |
| `Storage__SignedUrlTtlSeconds` | optional | Default `900` |

Follow-up (`docs/plans/active/2026-09-02-unify-supabase-storage-config.md`): catalog Storage HTTP and public/signed URL prefix reuse existing `Supabase__Url` / `Supabase__ServiceRoleKey`. Do **not** set `Storage__SupabaseUrl` / `Storage__ServiceRoleKey`.

Supabase Storage: buckets `catalog-public` (public) and `catalog-private` (private) must exist. Public bucket policy must allow read of customer-visible objects.

After restart, boot log must **not** Error on Dev email/storage for this service. Creating a tenant must **not** log `DEV EMAIL`. File URLs must **not** contain `dev-storage.local`.

## Likely affected areas / files

- `Platform.Api/Notifications/NotificationsServiceCollectionExtensions.cs`
- `Platform.Api/Storage/StorageServiceCollectionExtensions.cs` (+ small hosted service if needed)
- `Platform.Api/Notifications/RolvixEmailLayout.cs`
- `tests/Platform.Api.Tests/Notifications/NotificationsServiceCollectionExtensionsTests.cs`
- `tests/Platform.Api.Tests/Storage/StorageServiceCollectionExtensionsTests.cs` (new)
- `tests/Platform.Api.Tests/Notifications/RolvixEmailLayoutTests.cs` (new)
- `docs/runbooks/password-recovery-resend.md`
- `ROADMAP.md`

## Test seams

DI-seam xUnit (no HTTP, no Docker). Hosted service `StartAsync` to assert log level. HTML string assertions. Fake `IHostEnvironment` already used in notification tests.

## Verification strategy

- `dotnet test tests/Platform.Api.Tests` (or the new/updated test classes if the full suite is heavy).
- `dotnet build` clean.
- No Railway writes from this machine.

## Product-level "how to test" (post-deploy, human)

1. Restart Railway **production** after env vars.
2. Boot: no `DEV EMAIL` provider Error for that service; `External email delivery enabled`; storage not Dev.
3. Create tenant / invite a real inbox → no `DEV EMAIL` line → email arrives via Resend.
4. Open `/invite?token=` from the email → set password → login at rolvix.com.br.
5. Upload a catalog public image → URL host is Supabase, not `dev-storage.local`.
6. Restart/redeploy container → previously uploaded file still opens (Supabase, not local disk).
7. B2B Abrir on public and private files; B2C product image still renders.

## Do not

- Restore F-05 Production default.
- Fail startup on `IsProduction()`.
- Add `PublicBaseUrl` or change `/files/{id}/url`.
- Enable WhatsApp via global `AllowExternalDelivery`.
- Commit secrets or edit `appsettings.json` with real keys.
- Implement P2.
- Introduce `App:DeploymentName` / `RAILWAY_ENVIRONMENT_NAME` gating in this PR (propose only).
- Merge to `main`.

## Documentation that may need updating

- `docs/runbooks/password-recovery-resend.md`
- `ROADMAP.md` Histórico
- Optional catalog context pack if it claims PROD auto-enables Resend
