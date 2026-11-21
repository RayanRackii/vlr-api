# Plans, pricing page and Rolvix billing

Status: proposed (2026-11-21). Cross-repo (vlr-api + vlr-web). Source: owner-requested pricing study
`C:\Users\rayan\Rolvix\strategy\precificacao-e-aquisicao.md` (sections 2.2, 4, 6.2) and the payments plan
`docs/plans/active/2026-11-15-payments-asaas.md` ("Later slices" item 7), ADR 0009.

## Resumo para o dono (pt-BR)
- O Rolvix passa a ter **planos** (Essencial, Profissional, Completo) dentro de **pacotes** (Reservas, Manutenção e PMOC,
  Locação). Pacote = conjunto de módulos que já existem; plano = limites (recursos, usuários, unidades, mensagens).
- Os limites do trial viram limites por plano, no mesmo ponto do código que já barra o trial (`TrialGuard`).
- Uma **página de preços** pública em `rolvix.com.br/precos` e duas landings (arenas, PMOC), com o botão
  "Começar teste grátis" levando ao cadastro, que passa a registrar de onde o lead veio.
- **Cobrança do Rolvix pelo Asaas**: assinatura mensal ou anual por empresa, Pix/cartão/boleto, aviso antes de
  bloquear, bloqueio em somente leitura (o mesmo do trial vencido) e desbloqueio automático quando o pagamento entra.
- Trial passa a 30 dias de uso e 90 até a exclusão.
- Fica para depois (spec própria): emissão de NFS-e, plano gratuito com taxa, analytics de funil, WhatsApp com cota.

## 1. Current state (verified 2026-11-21)
- Only "trial" vs "not trial": `Tenant.IsTrial`, `TrialEndsAt`, `TrialPurgeAt`, `ConfigureAsTrial`
  (`Core/Platform.Core.Domain/Entities/Tenant.cs:74-78, 294-298`); `IsTrialReadOnly(now)` drives the read-only mode.
- Limits are trial constants: `TrialLimits.MaxAssets = 10`, `MaxUsers = 20`, `TrialDays = 14`, `PurgeDays = 30`
  (`Core/Platform.Core.Domain/Constants/TrialLimits.cs`).
- `Platform.Api/Services/Trial/TrialGuard.cs` (`ITrialGuard`) is the single gate: `EnsureWritableAsync`,
  `EnsureCanCreateAssetsAsync`, `EnsureCanInviteUserAsync`; called from `AssetService`, `TenantUserAdminService`,
  `CompanyProfileService`, `PortalAppearanceService`, `RentalLayoutService`.
- Modules are switched only by the platform admin (`PUT /api/admin/tenants/{id}`).
- Price in code: web `PRICE_PER_MODULE_BRL` (R$ 199) used by `TenantOnboardingWizard.tsx` and the
  "receita estimada" in `AdminDashboardPage.tsx`.
- Payments module: slice 1a merged (`Platform.Api/Modules/Payments/`: `IPaymentGateway`, `AsaasGateway`,
  `AsaasOptions`, `PaymentSecretProtector`). No subscription, customer or webhook code yet.
- Public web: `src/features/landing` (`/` → `LandingPage`), `src/features/onboarding` (self-serve trial at `/onboarding`).
  No pricing page, no lead source.

## 2. Decisions (owner, from the study; defaults to confirm before implementation)
| # | Decision | Default |
|---|---|---|
| D1 | Price table | Essencial R$ 99, Profissional R$ 189, Completo R$ 299 per month; annual = 10× monthly |
| D2 | Packages | Reservas (rentals + portal), Manutenção e PMOC (inventory + pmoc + os), Locação (rentals + inventory + catalog) |
| D3 | Limits per package × plan | Study table 4.3 (resources/assets, users, units, WhatsApp messages) — stored as data, not code |
| D4 | Add-ons | module R$ 69, unit R$ 49, 1.000 WhatsApp messages R$ 59 (catalog entries; purchase flow later) |
| D5 | Trial | 30 days of use, purge at 90; trial gets the Profissional limits of its package |
| D6 | Launch | "Fundador" coupon: 40% for 12 months, first 20 tenants (admin-applied) |
| D7 | Payment methods | Pix, card (recurring), boleto; dunning by e-mail (WhatsApp later); read-only after 7 days overdue |
| D8 | Platform fee on tenant payments | 0% on paid plans (ADR 0009 `PlatformFeePercent` stays 0) |
| D9 | Invoices (NFS-e) | out of scope here; manual via the accountant until a dedicated spec |

Generic-product rule: packages and plans are **data** (rows), never `if tenant == …` or resource-type checks.

## 3. Data model (vlr-api; additive migrations, schema `billing`)
- `billing.plan_catalog` (platform-wide, seeded by migration, editable later by platform admin):
  `id`, `package_key` (`reservas|manutencao|locacao`), `plan_key` (`essencial|profissional|completo`), `name`,
  `monthly_price_cents`, `annual_price_cents`, `currency` (`BRL`), `modules` (jsonb array of module keys),
  `limits` (jsonb: `assets`, `rentable_resources`, `users`, `units`, `whatsapp_messages_month`; null = unlimited),
  `is_active`, `sort_order`. Unique `(package_key, plan_key)`.
- `billing.tenant_subscriptions` (one current row per tenant): `id`, `tenant_id` (FK, unique where `ended_at is null`),
  `plan_catalog_id`, `billing_cycle` (`monthly|annual`), `status` (`trialing|active|past_due|read_only|canceled`),
  `current_period_start`, `current_period_end`, `discount_percent` (0–100), `discount_until`,
  `provider` (`asaas`), `provider_customer_id`, `provider_subscription_id`, `past_due_since`, `canceled_at`,
  `ended_at`, `row_version`, timestamps.
- `billing.subscription_events` (append-only history): `id`, `tenant_id`, `subscription_id`, `type`
  (`created|plan_changed|payment_received|payment_overdue|read_only|reactivated|canceled`), `provider_event_id`
  (unique, dedupe), `payload` (jsonb, no card data), `created_at`.
- `core.tenants`: add `lead_source` (varchar 100, null) and `lead_campaign` (varchar 100, null) for the funnel.
- Trial tenants get a `trialing` subscription on signup (package chosen in onboarding, plan Profissional,
  `current_period_end = TrialEndsAt`). Existing tenants: a backfill creates a `trialing` row for trial tenants and,
  for the rest, an `active` row on the package that matches their active modules (or `completo` of `reservas` when
  ambiguous) with `discount_percent = 100` until the owner sets real prices — no tenant is blocked by the rollout.

## 4. API
### 4.1 Plan limits (replaces the trial-only limits)
- `IPlanLimits` (new, `Platform.Api/Services/Billing/`): `GetAsync(tenantId)` returns the effective limits from the
  current subscription's plan (trial included); tenants without a subscription → no limits (safe default for the
  rollout window).
- `TrialGuard` becomes `PlanGuard` (keep `ITrialGuard` as a thin alias during the slice to avoid touching every
  caller twice, or rename in one slice — implementer's call, documented): asset create (by `assets` or
  `rentable_resources` for rentable creates), user invite (`users`), unit create (`units`); error code
  `plan.limit_reached` with `{ limit, current, max, planKey }` → 409. Read-only check: `IsTrialReadOnly` OR
  subscription `status == read_only`.
- Read-only must keep every GET working and block writes exactly where trial read-only blocks today.

### 4.2 Staff endpoints (tenant admin, permission `settings.billing` new)
- `GET /api/billing/subscription` → plan, package, status, period, limits, usage counters, next charge, discount.
- `GET /api/billing/plans` → catalog for the tenant's package (and other packages, for upgrade).
- `POST /api/billing/subscription/checkout` `{ planCatalogId, billingCycle, method }` → creates/updates the Asaas
  customer (root account, Rolvix as merchant) and subscription; returns `{ invoiceUrl, pixQrCode?, pixCopyPaste? }`.
  Idempotent (reuse the `Idempotency-Key` infra from P1-B, scope `billing.checkout`).
- `POST /api/billing/subscription/change-plan` `{ planCatalogId, billingCycle }` → upgrade now (prorate by Asaas
  update), downgrade at period end; refuse a downgrade whose limits are below current usage (409 `plan.usage_above_limit`).
- `POST /api/billing/subscription/cancel` → cancels at period end; data kept; becomes read_only after `current_period_end`.

### 4.3 Platform admin
- `GET/PUT /api/admin/tenants/{id}/subscription` → set plan, cycle, discount (`Fundador` = 40% for 12 months),
  status override (`active` for manual/offline payments, e.g. pilot clients paying by Pix outside the app).
- `GET /api/admin/billing/summary` → MRR, active/past_due/read_only/trialing counts, by package (replaces the web
  "receita estimada").
- `GET/PUT /api/admin/billing/plans` → edit catalog prices/limits (audit row in `subscription_events` with tenant null
  or a separate audit — implementer chooses, documented).

### 4.4 Asaas integration (root account; extends slice 1a gateway)
- Gateway additions: `CreateCustomerAsync`, `CreateSubscriptionAsync` (cycle MONTHLY/YEARLY, billingType
  PIX|CREDIT_CARD|BOLETO, value after discount, `externalReference = tenantId`), `UpdateSubscriptionAsync`,
  `CancelSubscriptionAsync`, `GetPaymentPixQrCodeAsync`. Root API key from Fly secret (human step), never logged.
- Webhook `POST /api/webhooks/payments/asaas/billing` (anonymous, token header check against
  `Payments:Asaas:BillingWebhookToken`; dedupe by event id in `subscription_events.provider_event_id`; return 200
  fast; process in a Hangfire job). Events: `PAYMENT_RECEIVED`/`PAYMENT_CONFIRMED` → `active`, extend period, clear
  `past_due_since`, reactivate from `read_only`; `PAYMENT_OVERDUE` → `past_due` (set `past_due_since`);
  `SUBSCRIPTION_DELETED` → `canceled`.
- Daily `BillingDunningJob`: e-mail at D-3 (upcoming), D0 (due), D+1 and D+5 (overdue) using the notifications outbox
  (new templates pt-BR/en/es); at D+7 overdue → `read_only` + event. Trial end: D-5, D-1, D0 e-mails ("assine para
  continuar") with the checkout link.
- Trial expiry: when `TrialEndsAt` passes without a paid subscription → `read_only` (as today); purge at 90 days
  (`TrialLimits.PurgeDays = 90`, `TrialDays = 30`).

### 4.5 Onboarding / lead source
- `/onboarding` request gains optional `packageKey`, `leadSource`, `leadCampaign` (from `utm_source`/`utm_campaign`
  or `?ref=` captured by the web). Stored on the tenant; trial subscription created on the chosen package.

## 5. Web
- **Pricing page** `src/features/landing/pages/PricingPage.tsx` at `/precos` (public, both `rolvix.com.br` and
  `www`): package switcher (Reservas / Manutenção e PMOC / Locação), three plan cards from `GET /api/public/plans`
  (new anonymous read of the active catalog), monthly/annual toggle ("2 meses grátis"), add-ons list, FAQ
  (sem fidelidade, sem taxa de adesão, Pix/cartão/boleto, 30 dias grátis), CTA → `/onboarding?package=…&plan=…`
  preserving `utm_*`/`ref`. Follows the landing design language; pt-BR/en/es.
- **Vertical landings** `/arenas` and `/pmoc`: hero, 3 benefits, screenshot slots, pricing teaser for the package,
  CTA. Copy from study section 5.2. Generic components; vertical content as data (no tenant-specific code).
- **Onboarding**: package step preselected from the query; capture `utm_*`/`ref` into the request.
- **Configurações › Plano e cobrança** (`src/features/billing/`): current plan, status chip, usage bars vs limits,
  next charge, "Assinar"/"Trocar plano"/"Cancelar", checkout dialog (Pix QR + copia-e-cola, card/boleto via Asaas
  invoice URL), payment history (from events). Trial/read-only banners link here instead of "Assine para continuar"
  with no action.
- **Limit errors**: map `plan.limit_reached` to an inline "Limite do plano atingido — ver planos" with a link.
- **Admin**: tenant page gets a "Assinatura" panel (plan, discount, status override); dashboard "receita estimada"
  → `GET /api/admin/billing/summary` (MRR); remove `PRICE_PER_MODULE_BRL` (onboarding wizard shows the plan price).
- `npm run gen:api` after each API slice; tolerant readers for every new field.

## 6. Slices (one PR each; DEV first; PROD only with "pode promover")
| Slice | Repo | Content | Migration | Review |
|---|---|---|---|---|
| B1 | api | `billing` schema, plan catalog seed, tenant subscriptions + backfill, `IPlanLimits`, guard generalization, trial 30/90, lead source columns, admin subscription GET/PUT, public plans GET | yes | **Opus** (migration, tenant isolation) |
| B2 | web | `/precos`, `/arenas`, `/pmoc`, onboarding package + lead capture, admin subscription panel, MRR card, remove `PRICE_PER_MODULE_BRL`, limit error mapping | — | Sonnet reviewer |
| B3 | api | Asaas customer/subscription gateway methods, staff billing endpoints (subscription, plans, checkout, change, cancel), billing webhook + dedupe + job | yes (events table if not in B1) | **Opus** (secrets, webhook auth) |
| B4 | api | Dunning job + templates, trial-end e-mails, read_only/reactivation transitions | no | Sonnet reviewer |
| B5 | web | Configurações › Plano e cobrança, checkout dialog, banners link to it, payment history | — | Sonnet reviewer |
| B6 | ops | Human steps: Asaas root account (production), Fly secrets (`Payments__Asaas__RootApiKey`, `BillingWebhookToken`), webhook URL registration in Asaas, sandbox first on DEV | — | owner |

Order: B1 → B2 (pricing page can ship with manual billing) → B3 → B4 → B5 → B6 for PROD charging.
Manual billing (study step 2) works from B1: the admin sets plan/discount/`active` while invoices are created by hand in Asaas.

## 7. Tests
- B1: plan limits per package/plan (asset, rentable resource, user, unit) → 409 `plan.limit_reached`; trial uses
  Profissional limits; no-subscription tenant unlimited; read-only via subscription status blocks writes but not
  reads; backfill (Postgres) creates exactly one current row per tenant, trial vs active, idempotent; tenant isolation;
  public plans returns only active rows; admin endpoints platform-admin only; trial dates 30/90.
- B2: pricing page renders packages/plans/toggle from the API, CTA carries package/plan/utm; onboarding sends
  lead fields; admin MRR uses the summary; limit error mapping.
- B3: checkout idempotent (same key → same Asaas call count 1); webhook bad token 401; duplicate event processed
  once; RECEIVED → active + period extended; OVERDUE → past_due; DELETED → canceled; fake `HttpMessageHandler` for
  Asaas; root key never in logs/responses.
- B4: dunning schedule D-3/D0/D+1/D+5, read_only at D+7, reactivation on payment; trial-end e-mails.
- B5: plan page states (trialing/active/past_due/read_only), usage bars, checkout dialog Pix rendering, banners link.
- E2E on DEV with the Asaas sandbox: trial tenant → checkout Pix → sandbox payment → webhook → `active` and banner gone.

## 8. Done when
- Prices/limits live as data; no `PRICE_PER_MODULE_BRL` and no trial-only constants for limits.
- `/precos`, `/arenas`, `/pmoc` public on DEV; onboarding records lead source and package.
- A DEV trial tenant can subscribe with the Asaas sandbox and leaves read-only automatically on payment; overdue
  goes to read-only after 7 days; admin sees MRR.
- PROD charging only after B6 human steps and "pode promover".

## 9. Risks
- Limits that bite existing tenants: backfill gives everyone `discount_percent = 100` and the largest matching plan;
  the owner adjusts per tenant.
- Webhook spoofing: token header + event re-fetch from Asaas before changing status (B3).
- Double charging on retries: Idempotency-Key infra + one provider subscription per tenant (unique).
- NFS-e obligation for clients that are companies: manual until a dedicated spec (D9).
- Capacity: billing jobs run on the single Hangfire worker; keep them short and batched.

## 10. Amendments after the spec review (2026-11-21) — binding, they override earlier sections
1. **Trial → paid conversion.** New domain method `Tenant.ConvertFromTrial()` clears `IsTrial`, `TrialEndsAt`,
   `TrialPurgeAt`. Called whenever a subscription becomes `active` on a paid plan (admin override in B1; webhook in B3).
   `TrialLifecycleJob` never purges or marks read-only a tenant whose current subscription is `active` or `past_due`:
   when a subscription exists it is the source of truth. Tests in B1 (admin activation) and B3 (webhook activation).
2. **Nobody blocked by the rollout or by manual billing.** `tenant_subscriptions` gains `limits_unlimited boolean`
   (default false) and `billing_mode` (`provider|manual`, default `provider`). The backfill sets non-trial tenants to
   `manual`, `limits_unlimited = true`, `discount_percent = 100`. Plan limits are skipped when `limits_unlimited`.
   Dunning (B4) and the automatic `past_due → read_only` transition apply only to `billing_mode = provider` rows that
   have a `provider_subscription_id`; manual rows advance `current_period_end` only through the admin override.
   Test (B1/B4): a backfilled tenant never becomes limited, `past_due` or `read_only`.
3. **Modules source of truth = `TenantModule`** (platform admin). `plan_catalog.modules` is the default module set
   applied when the trial tenant is created. B3 `change-plan` may enable the new plan's missing modules (never
   disables any; disabling stays a platform-admin action subject to the existing guards and to spec
   2026-11-20-module-data-purge).
4. **Existing trials are not re-based**; 30/90 applies to trials created after B1 ships.
5. **Guard naming decided:** `ITrialGuard`/`TrialGuard` are renamed to `IPlanGuard`/`PlanGuard` in B1 (all callers).
6. **`settings.billing`** is registered in B1 and granted to the system admin/owner roles by migration (same pattern as
   `GrantResourcePlanningPermissions`).
7. **Public inputs:** lead fields are validated server-side (≤ 100 chars, `[A-Za-z0-9._-]`, otherwise dropped) and never
   rendered as HTML; `GET /api/public/plans` is cacheable (short `Cache-Control`) and served from the active catalog only.
8. **Plan catalog editing** (`GET/PUT /api/admin/billing/plans`) is removed from B1 and gets its own small spec later
   (with an audit table of its own; `subscription_events` stays subscription-scoped).
9. **Webhook (B3) hardening:** constant-time token comparison; tenant resolved from the stored
   `provider_subscription_id`/`provider_customer_id` (never trust payload tenant/`externalReference` alone); re-fetch
   the payment/subscription from Asaas before changing status; runs under `PlatformScope` with explicit `TenantId`
   filters; transitions are idempotent and monotonic (an older `PAYMENT_OVERDUE` never overrides a newer
   `PAYMENT_RECEIVED`; compare event/payment dates and use `row_version`). B3 tests cover each point plus a
   cross-tenant event.
10. **Checkout idempotency (B3):** extend `IdempotencyService` to accept a staff user as principal (scope
    `billing.checkout`), and make the provider call retry-safe: deterministic `externalReference = tenantId`, look up an
    existing Asaas customer/subscription before creating.
11. **Discount expiry (B3/B4):** a daily step ends expired discounts (`discount_until <= now`): sets
    `discount_percent = 0` and updates the Asaas subscription value; event `plan_changed`.
12. **Time zone:** dunning and trial e-mail days are computed in `America/Sao_Paulo`.
13. **Study reference:** the pricing study lives outside the repos (`Rolvix/strategy/`, not versioned).
