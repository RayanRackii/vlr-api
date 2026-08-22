# 2026-08-22-notifications-external-delivery-gate

Status: approved

## Goal / Problem

F-05 (closed/approved): a credential being present in DEV must not, by itself, enable external email/WhatsApp delivery. Today `NotificationsServiceCollectionExtensions.AddNotificationInfrastructure` selects `ResendEmailProvider` / `MetaWhatsAppProvider` purely on the presence of `Resend:ApiKey`+`Resend:FromEmail` / `WhatsApp:AccessToken`+`WhatsApp:PhoneNumberId`. In DEV this can fire real HTTP to Resend/Meta if a developer pastes keys into `appsettings.Development.json`. We need an explicit configuration gate that is **safe-by-default in DEV** and **does not silently break PROD** if a new env var is forgotten.

## Visible behavior

1. **DEV + credentials present + flag unset** → no external email/WhatsApp. `DevEmailProvider` / `DevWhatsAppProvider` are used (console log, no HTTP).
2. **DEV + flag explicitly `true`** → external delivery is deliberately allowed (still requires credentials; if credentials are missing, Dev providers are used regardless).
3. **PROD + valid existing Railway config + flag unset** → existing `ResendEmailProvider` / `MetaWhatsAppProvider` keep working with no new env var required.
4. **Any environment + flag explicitly `false`** → Dev providers forced, even if credentials are present.
5. **Any environment + credentials missing** → Dev providers, regardless of the flag (credentials remain a hard precondition for external HTTP).

## Repositories

- vlr-api

## Relevant existing ADR / rules

- `vlr-api/AGENTS.md` — Git Work Policy (branch off `develop`, no `main`/PROD writes), Human Decision Gate (closed for F-05).
- `vlr-api/.cursor/rules/10-arquitetura.mdc` — Notifications: providers in `Notifications/Providers/`; never send email/WhatsApp synchronously inside an HTTP request.
- `vlr-api/.cursor/rules/20-convencoes.mdc` — DI via `IServiceCollection` extensions; secrets only via `appsettings.Development.json` (out of git) or Railway env vars.
- `docs/runbooks/password-recovery-resend.md` — B2B recovery flow depends on Resend; must document the new flag.

## Architecture route (optional)

- rolvix-architect

## Execution route (optional)

- api-implementer

## Confirmed decisions

### 1. Config key and shape

Introduce a new `Notifications` section with a single **nullable bool** (tri-state) `AllowExternalDelivery`:

```jsonc
"Notifications": {
  "AllowExternalDelivery": null   // unset | true | false
}
```

- **Tri-state, not plain bool.** A plain `false` default baked into `appsettings.Production.json` would silently disable PROD email if a deploy forgot to override it. Tri-state unset (`null`) is resolved by environment (see §2), so PROD keeps working with zero new env vars.
- New file: `Platform.Api/Notifications/NotificationsOptions.cs` with `bool? AllowExternalDelivery`.
- Bind via `services.Configure<NotificationsOptions>(configuration.GetSection("Notifications"))`.

### 2. Resolution algorithm (when `AllowExternalDelivery` is unset / null)

```
effectiveAllowExternal =
    AllowExternalDelivery                  // explicit wins, true or false
    ?? (hostEnvironment.IsDevelopment()
        ? false                              // DEV safe-by-default
        : true)                              // Production / Staging / other → keep current behavior
```

- Read `IHostEnvironment` (passed into the extension). `IsDevelopment()` is the only special case; every other environment (`Production`, `Staging`, anything custom) falls through to `true`.
- Credentials check stays as today: external provider is registered only when `effectiveAllowExternal && credentialsConfigured`. Otherwise the Dev provider is registered.

### 3. Railway "development" service running `ASPNETCORE_ENVIRONMENT=Production`

- **Out of scope** to write Railway env from this repo.
- **Ops note** (in runbook, not code): a Railway service whose environment name is `Development` needs `Notifications__AllowExternalDelivery=true` to send externally. If a non-Production Railway service should stay silent, set `Notifications__AllowExternalDelivery=false`.

### 4. Test plan at the DI seam

New file `tests/Platform.Api.Tests/Notifications/NotificationsServiceCollectionExtensionsTests.cs`. xUnit, no HTTP, no Docker. Build a `ServiceCollection`, `AddLogging`, `AddOptions`, in-memory `IConfiguration` via `Dictionary<string,string?>`, set `IHostEnvironment` via a fake, call `AddNotificationInfrastructure`, assert resolved types.

Cases (all assert **registered type**, never call `SendAsync`):

| Env         | Creds | AllowExternalDelivery | Expected IEmailProvider | Expected IWhatsAppProvider |
|-------------|-------|-----------------------|-------------------------|----------------------------|
| Development | yes   | unset (null)          | DevEmailProvider        | DevWhatsAppProvider        |
| Development | yes   | true                  | ResendEmailProvider     | MetaWhatsAppProvider       |
| Development | yes   | false                 | DevEmailProvider        | DevWhatsAppProvider        |
| Development | no    | true                  | DevEmailProvider        | DevWhatsAppProvider        |
| Production  | yes   | unset (null)          | ResendEmailProvider     | MetaWhatsAppProvider       |
| Production  | yes   | false                 | DevEmailProvider        | DevWhatsAppProvider        |
| Production  | no    | unset (null)          | DevEmailProvider        | DevWhatsAppProvider        |
| Staging     | yes   | unset (null)          | ResendEmailProvider     | MetaWhatsAppProvider       |

- Resolve via `provider.GetRequiredService<IEmailProvider>().GetType()` and assert `.Name`.
- Use a tiny fake `IHostEnvironment`. Do not pull `IHostEnvironment` from a real host builder.
- Do not send real HTTP to Resend/Meta.

### 5. Files to touch (small blast radius, no migration, no FE)

- `Platform.Api/Notifications/NotificationsOptions.cs` — **new**.
- `Platform.Api/Notifications/NotificationsServiceCollectionExtensions.cs` — accept `IHostEnvironment`; bind options; gate Resend/Meta.
- `Platform.Api/Program.cs` — pass `builder.Environment`.
- `Platform.Api/appsettings.json` — add empty `"Notifications": {}` (no default value; preserves tri-state).
- `tests/Platform.Api.Tests/Notifications/NotificationsServiceCollectionExtensionsTests.cs` — **new**.
- `docs/runbooks/password-recovery-resend.md` — "External delivery gate" subsection.
- `ROADMAP.md` — Histórico: F-05 fix; F-08 BY_DESIGN; F-01 concurrency tests passed with Docker (2026-08-22). No `main`/PROD.

## Confirmed decisions (product)

- F-08: B2B and B2C sessions are independent. Logout of one surface does **not** clear the other. **CLOSED_BY_DESIGN / NO_FIX.** Optional future "Sair de todas as sessões" is not in this PR.
- F-04 remains VALIDATION_REQUIRED (do not touch).
- F-10 / F-16 remain BLOCKED_HUMAN (do not touch).

## Invariants that must not break

- Notifications stay enqueued and dispatched in the background; no synchronous email/WhatsApp inside an HTTP request.
- Credentials remain a **hard precondition** for external HTTP: `AllowExternalDelivery=true` with missing credentials still yields Dev providers.
- `appsettings.Production.json` is not modified to set the flag.
- No migration, no schema change, no FE change, no `main`/PROD deploy, no secret writes.
- Existing Railway Resend/WhatsApp credentials continue to drive real sends in PROD with zero config change.

## Implementation scope

- Add `NotificationsOptions` (`bool? AllowExternalDelivery`).
- Extend `AddNotificationInfrastructure` to accept `IHostEnvironment` and apply the gate.
- Update the single call site in `Program.cs`.
- Add the DI-seam test file.
- Doc updates (runbook + ROADMAP; CONTEXT optional one-liner in the existing Notifications bullet — no glossary change).

## Likely affected areas / files

- `Platform.Api/Notifications/NotificationsServiceCollectionExtensions.cs`
- `Platform.Api/Notifications/NotificationsOptions.cs` (new)
- `Platform.Api/Program.cs`
- `Platform.Api/appsettings.json`
- `tests/Platform.Api.Tests/Notifications/NotificationsServiceCollectionExtensionsTests.cs` (new)
- `docs/runbooks/password-recovery-resend.md`
- `ROADMAP.md`

## Test seams (when they exist)

- DI resolution of `IEmailProvider` / `IWhatsAppProvider` (no HTTP, no Docker). Pattern: `RolvixAuthorizationPolicyTests`.

## Verification strategy

- `dotnet build` clean.
- `dotnet test tests/Platform.Api.Tests` — new tests pass; existing tests unaffected.

## Product-level "how to test"

- DEV + creds + flag unset/false → `DEV EMAIL` / `DEV WHATSAPP` logs, no Resend/Meta HTTP.
- DEV + flag explicitly true + creds → Resend/Meta providers registered.
- PROD + valid config + flag unset → Resend/Meta still registered.

## Do not

- Do not change `appsettings.Production.json` to set the flag.
- Do not introduce a plain `bool` default (must be `bool?` tri-state).
- Do not touch F-04, F-10, F-16, `main`, PROD, secrets, DNS, or the frontend.
- Do not implement "Sair de todas as sessões".
- Do not change `FrontendBaseUrlResolver`.
- Do not add SMS to the gate (no credential path today).
- Do not edit migrations, schema, or domain entities.
- Do not send real HTTP to Resend/Meta in tests.
- Do not commit `appsettings.Development.json` secrets.

## Documentation that may need updating

- `docs/runbooks/password-recovery-resend.md`
- `ROADMAP.md`
- `CONTEXT.md` — optional one-line clarification in the Notifications bullet; no glossary change.

## FABLE_MERGE_REVIEW_REQUIRED

Yes. Notifications + DEV/PROD + DI. Parent invokes Fable at the merge gate with a dossier.
