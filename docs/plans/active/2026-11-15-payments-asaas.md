# Online payments with Asaas (2026-11-15)

Repositories: vlr-api and vlr-web. Decision record: `docs/adr/0009-payment-gateway-asaas.md` (read it first; it wins on rules).
Approved by the owner after the gateway study. One slice per PR, in order; each slice is a new Sonnet session that starts
from this file. Slices marked **Opus review** touch secrets, tenant isolation or migrations.

## Why Asaas (cost study, public prices Nov 2026)
| | Pix | Card one-off | Boleto | Monthly | Multi-tenant model |
|---|---|---|---|---|---|
| **Asaas** | R$1,99 (R$0,99 first 3 months; negotiable) | 2,99% + R$0,49, paid ~32 days | R$1,99 | none | Subaccounts via API + native split by walletId |
| Mercado Pago | 0,99% | 3,98% (30 d) – 4,98% (D0) | R$3,49 | none | OAuth per seller + `application_fee` |
| Pagar.me | ~0,99–1,19% | ~3,19–4,39% | R$3,49 | negotiated | Split only for PSP clients |
| Stripe BR | 1,19% (invite only) | 3,99% + R$0,39 | R$3,45 | none | Connect; Express deprecated in BR |
| Efí | 1,19% | — | — | none | Pix split only between Efí accounts (max 20) |

Sources: asaas.com/precos-e-taxas, docs.asaas.com/docs/split-de-pagamentos, docs.asaas.com/docs/criacao-de-subcontas,
mercadopago.com.br/developers (split-payments, marketplace), docs.pagar.me/reference/split-1, docs.stripe.com/payments/pix,
docs.stripe.com/connect/express-accounts, sejaefi.com.br/tarifas, dev.efipay.com.br (split Pix).
Break-even: the flat R$1,99 beats 0,99% only above ~R$201. Accepted for the onboarding and split model; negotiate volume price.

**Human steps before slice 1b runs on DEV:** Asaas sandbox account (sandbox.asaas.com), split enabled under
Integrações › Segurança, and these Fly secrets on the DEV app only:
`Payments__Asaas__BaseUrl=https://api-sandbox.asaas.com/v3`, `Payments__Asaas__RootApiKey`,
`Payments__Asaas__PlatformWalletId`, `Payments__Asaas__WebhookBaseUrl` (public API base URL),
`Payments__EncryptionKeys__v1` (base64 of 32 random bytes). Before PROD: confirm with Asaas sales the negotiated Pix price,
subaccount cost/limits and white-label KYC. Never print or commit any of these values.

## Repo conventions every slice must follow (checked 2026-11-15)
- Tenant entities implement `ITenantScoped` (`Core/Platform.Core.Domain/Common/ITenantScoped.cs`).
  `AppDbContext.ApplyTenantQueryFilters` adds the filter by reflection: write only an `IEntityTypeConfiguration`
  (picked up by `ApplyConfigurationsFromAssembly`), no manual `HasQueryFilter`. Platform tables (no tenant) do **not**
  implement it.
- DbContext: `AppDbContext` (`Core/Platform.Core.Infrastructure/Persistence`). Migrations in
  `Core/Platform.Core.Infrastructure/Persistence/Migrations`, snapshot `AppDbContextModelSnapshot`. Additive only.
- Cross-tenant reads: `using var _ = PlatformScope.Begin(logger, "<name>")`. `PlatformScope` has **no** tenant.
  Per-tenant work from a job or webhook: new DI scope, set `AmbientTenantContext.TenantId`, then resolve `AppDbContext`
  and services — copy the pattern of `Platform.Api/Jobs/ReservationReminderJob.cs`.
- Recurring jobs: `Platform.Api/Jobs/HangfireExtensions.cs` (`MapPlatformRecurringJobs`).
- Permissions: nested classes in `Core/Platform.Core.Domain/Constants/Permissions.cs` with lowercase dotted keys, defined in
  `PermissionCatalog.cs` (`Define(key, name, description)`; its doc says "50 keys" — update it and any key-count test),
  default role grants, and the web permission type. `tests/Platform.Api.Tests/Authorization/PermissionTenantConventionTests.cs`
  (`PublicAllowList`) and `ModuleRuntimeGateTests.cs` must stay green.
- OpenAPI: vlr-api snapshot `tests/Platform.Api.Tests/Contracts/openapi.full.snapshot.json`
  (`OpenApiFullSnapshotContractTests`); web contract `vlr-web/openapi/openapi.json` via `npm run gen:api`.
- Never log, return or put in an exception message: API keys, webhook tokens, `access_token` headers, CPF.

---

## Slice 1a — API: secret protector + Asaas client (no DB) — **Opus review**
Branch `feat/payments-gateway-client`.
- `Platform.Api/Modules/Payments/Security/PaymentSecretProtector.cs`: AES-256-GCM. Keys from options
  `Payments:EncryptionKeys:{version}` (base64, exactly 32 bytes); the highest version encrypts, any listed version decrypts.
  Format `v{n}.{nonce b64}.{ciphertext b64}.{tag b64}`, 12-byte random nonce, associated data = tenant id (so a ciphertext
  copied to another tenant's row does not decrypt). Missing key → `PaymentConfigurationException` thrown at use, not at
  startup (DEV/PROD without payments keep booting).
  *Why not ASP.NET Data Protection:* its key ring would live in the same database as the ciphertext, and a subaccount key
  can move tenant money.
- `Gateways/IPaymentGateway.cs` (provider-neutral, no Asaas types leak):
  `CreateSubaccountAsync(SubaccountRequest)` → `SubaccountResult(ExternalAccountId, WalletId, ApiKey, OnboardingUrl?)`;
  `RegisterWebhookAsync(apiKey, url, token, events)`; `CreateCustomerAsync(apiKey, CustomerRequest(name, email, cpf, phone?))` → id;
  `CreatePixChargeAsync(apiKey, PixChargeRequest(customerId, amount, dueAt, description, externalReference, split?))`
  → `ChargeResult(ExternalId, PixPayload, PixQrImageBase64, InvoiceUrl, DueAt)`; `GetChargeAsync(apiKey, externalId)`;
  `FindChargeByExternalReferenceAsync(apiKey, reference)`; `GetAccountStatusAsync(apiKey)` → Pending / Active / Rejected.
  Refund is slice 6.
- `Gateways/Asaas/AsaasGateway.cs`, `AsaasOptions.cs` (`BaseUrl`, `RootApiKey`, `PlatformWalletId`, `WebhookBaseUrl`),
  `AsaasDtos.cs`. Typed `HttpClient`, header `access_token` set per request, 10 s timeout, `AllowAutoRedirect = false`.
  Non-2xx → `PaymentGatewayException(code, httpStatus)` with the Asaas error *code* only, never the body. Webhook events
  registered: `PAYMENT_RECEIVED`, `PAYMENT_CONFIRMED`, `PAYMENT_OVERDUE`, `PAYMENT_DELETED`, `PAYMENT_REFUNDED`.
- DI registration in a `PaymentsServiceCollectionExtensions` called from the module setup.
- Tests (`tests/Platform.Api.Tests/Payments/`, fake `HttpMessageHandler`, no network): round-trip encrypt/decrypt;
  other tenant id fails; old version still decrypts after adding v2; each gateway call sends the right path, method, body and
  `access_token`; error mapping hides the body; the logged/exception text never contains the key (assert on a captured
  logger and on `exception.ToString()`).

## Slice 1b — API: tenant payment account — **Opus review**
Branch `feat/payments-account`.
- Entity `Core/Platform.Core.Domain/Entities/TenantPaymentAccount.cs` (`ITenantScoped`): `Id`, `TenantId` (unique index),
  `Provider` ("asaas"), `ExternalAccountId?`, `WalletId?`, `EncryptedApiKey?`, `WebhookTokenHash?` (SHA-256 hex),
  `Status` (`PaymentAccountStatus`: Creating, Pending, Active, Rejected, Failed), `OnboardingUrl?`,
  `PlatformFeePercent` (decimal(5,2), 0–20, default 0, set by platform admin only — no endpoint in this slice),
  `CreatedAt`, `UpdatedAt`. Migration `AddTenantPaymentAccounts` (one table, additive).
- Permissions: `Permissions.Payments.AccountRead = "payments.account.read"`, `AccountWrite = "payments.account.write"`;
  `Define(...)` both; grant to the tenant admin default role like the other settings permissions; update the key count.
- `Modules/Payments/Services/PaymentAccountService.cs`:
  - `GetAsync()` → `{ status, provider, onboardingUrl, platformFeePercent }` or 404. Never key, wallet id or token.
  - `CreateAsync(request)`: `trialGuard.EnsureWritableAsync`. Validate `Tenant.TaxId` (11 or 14 digits after stripping) and
    the request (name, e-mail, mobile phone, postal code, address, address number, province, `companyType` when CNPJ,
    `incomeValue`) → 400 with field errors. Existing row with Status ≠ Failed → 409 `PAYMENT_ACCOUNT_EXISTS`.
    Race-safe order: insert/update the row as **Creating** and commit (unique `TenantId` violation → 409); then call
    `CreateSubaccountAsync` with the root key; generate a 32-byte random webhook token, `RegisterWebhookAsync` on the
    subaccount with URL `{WebhookBaseUrl}/api/webhooks/payments/asaas`; store `EncryptedApiKey`, `WalletId`,
    `ExternalAccountId`, `WebhookTokenHash`, Status Pending. The raw token is discarded after the register call.
    Provider failure → Status Failed (log tenant id + external account id if one was created, so support can clean it up)
    and 422 `PAYMENT_PROVIDER_REJECTED`; a Failed row can be retried.
  - `RefreshStatusAsync()` → `GetAccountStatusAsync` → updates Status.
  - `IPaymentAvailability.IsOnlinePaymentActiveAsync()` → Status == Active (used by slice 3).
- `Controllers/PaymentAccountController.cs`, route `api/payments/account`: `GET` (AccountRead), `POST` and
  `POST refresh-status` (AccountWrite). Core feature: no `RequireActiveModule` (same as `UsersController`); add it to
  whatever list `ModuleRuntimeGateTests` uses for core controllers.
- Tests: create sends root-key subaccount call then subaccount-key webhook call; stored key decrypts back and ≠ plain;
  GET JSON contains none of key / wallet id / token; second create → 409; Failed → retry works; tenant A cannot see B;
  invalid TaxId → 400; Asaas 400 → 422 without provider body; no permission → 403; trial read-only → blocked;
  OpenAPI snapshot updated.

## Slice 2 — Web: Configurações › Pagamentos
Branch `feat/payments-settings` (vlr-web, after slice 1b is on develop and `npm run gen:api` is regenerated).
- `src/features/payments/`, route `/configuracoes/pagamentos`, nav item under Configurações gated by
  `payments.account.read`; write actions need `payments.account.write`.
- Standard `Page`/`PageHeader` + `FormPage`/`SaveBar` like the other settings forms.
  - No account (404) or Failed: short text ("Receba o Pix das reservas direto na sua conta Asaas.") + form prefilled from
    the tenant profile; CNPJ/CPF read-only from the profile with a link to edit it there when empty.
  - Creating/Pending: chip "Em análise", "Completar cadastro" → `onboardingUrl` (new tab), "Atualizar status".
  - Active: chip "Ativa", read-only summary. Rejected: chip "Recusada" + support text.
- pt-BR strings in the i18n files. Vitest: each state; submit posts the form; 409 reloads and shows the account; 400 field
  errors land on the fields.

## Slice 3a — API: deposit amount (pure)
Branch `feat/payments-deposit-amount`. No migration.
- `Modules/Rentals/Services/DepositAmountCalculator.cs`:
  `percent = PolicyResolver.EffectiveDepositPercent(asset.DepositPercent, categoryPolicy, tenantPolicy)
   ?? (int?)Math.Round(pricing?.DepositPercentage ?? 100m, MidpointRounding.AwayFromZero)`;
  `amount = Math.Round(reservation.TotalAmount * percent / 100m, 2, MidpointRounding.AwayFromZero)`.
  Several items with different assets → the highest percent among them. Read at charge-creation time (not stored on the
  reservation). 0 → no online charge.
- Update the XML docs of `BookingPolicy.DepositPercent` and `RentalAsset.DepositPercent` ("applied to the online deposit
  charge, see ADR 0009"). Tests: each precedence level, rounding, multi-item max, zero.

## Slice 3b — API: charges and the portal endpoints — **Opus review**
Branch `feat/payments-deposit-charge`.
- Entities, migration `AddPaymentCharges` (additive):
  - `PaymentCharge` (`ITenantScoped`): `Id`, `TenantId`, `Provider`, `ExternalId?` (unique with Provider, filtered not null),
    `SubjectType` (Reservation, CatalogOrder), `SubjectId`, `CustomerId`, `Method` (Pix), `Amount`, `Currency` "BRL",
    `Status` (Creating, Pending, Paid, Expired, Failed, Refunded), `PixPayload?`, `PixQrImageBase64?`, `InvoiceUrl?`,
    `DueAt`, `PaidAt?`, `IdempotencyKey` (unique), `PlatformFeeAmount`, `CreatedAt`, `UpdatedAt`, `RowVersion`.
    Index `(TenantId, SubjectType, SubjectId)`.
  - `PaymentCustomer` (`ITenantScoped`): `Id`, `TenantId`, `CustomerId`, `Provider`, `ExternalCustomerId`;
    unique `(TenantId, CustomerId, Provider)`.
- `PaymentChargeService.CreateReservationDepositChargeAsync(reservationId, cpf?)` — customer endpoint:
  1. `trialGuard.EnsureWritableAsync`; online payment active (else 409 `ONLINE_PAYMENT_UNAVAILABLE`); reservation owned by
     the caller (else 404), status PendingDeposit (else 409 `RESERVATION_NOT_PENDING`), amount > 0 (else 409 `NO_DEPOSIT_DUE`).
  2. CPF: `Customer.Cpf`, or the `cpf` in the body (valid CPF digits → saved to `Customer.Cpf`); neither → 422 `CPF_REQUIRED`.
  3. In one transaction with `ReservationLocks.LockByReservationIdAsync`: a Pending charge with `DueAt > now` for this
     reservation → return it. Else insert a **Creating** row, `IdempotencyKey = $"reservation:{id}:{n}"` with
     n = count of charges for the reservation + 1, `DueAt = now + 30 min`; commit. A unique violation (concurrent POST) →
     reload and return the other request's charge.
  4. Outside the transaction: get or create the `PaymentCustomer`, then `CreatePixChargeAsync` with
     `externalReference = charge.Id`, split to `PlatformWalletId` only when `PlatformFeePercent > 0`.
     Success → fill provider fields, Status Pending. Provider error → Status Failed, 422 `PAYMENT_PROVIDER_REJECTED`.
     If the save after a provider success fails, the webhook still finds the charge by `externalReference` (slice 3c).
- Endpoints (module gate: same as the existing customer reservation endpoints):
  - `POST /api/reservations/mine/{id}/deposit-charge` body `{ cpf?: string }` → charge DTO.
  - `GET /api/payments/portal/charges/{id}` → `{ id, status, amount, pixPayload, pixQrImageBase64, dueAt, paidAt }`, only for
    the owning customer in the current tenant (else 404). Both satisfy the Customer-policy rule in
    `PermissionTenantConventionTests`.
  - Staff reservation DTO gains `payment: { status, amount, paidAt } | null` (latest charge).
- Tests: precondition codes; CPF missing / saved; idempotent repeat returns the same charge; two concurrent creates → one
  provider call (or one surviving charge); customer B gets 404 for A's reservation and charge; tenant isolation on reads;
  provider failure → Failed and a retry creates n+1; split only when fee > 0; OpenAPI snapshot updated.

## Slice 3c — API: webhook, processor, expiry — **Opus review**
Branch `feat/payments-webhook`.
- Migration `AddPaymentWebhookEvents` (additive): `PaymentWebhookEvent` — platform table, **not** `ITenantScoped`:
  `Id`, `Provider`, `ExternalEventId` (unique with Provider), `EventType`, `ExternalChargeId?`, `TenantId?`, `PayloadJson`,
  `ReceivedAt`, `ProcessedAt?`, `Error?` (code only).
- `Modules/Payments/Controllers/AsaasWebhookController.cs`, `POST api/webhooks/payments/asaas`, `[AllowAnonymous]`,
  `[RequestSizeLimit(65_536)]`; read the body with a bounded reader (not `ReadToEndAsync`). Add
  `AsaasWebhookController.Receive` to `PublicAllowList`.
  1. Parse; no `payment` object → 200, ignored, nothing stored.
  2. Under `PlatformScope.Begin(logger, "payments.webhook")`: find the charge by `payment.id` = `ExternalId`, else by
     `payment.externalReference` = charge `Id`; then that tenant's `TenantPaymentAccount.WebhookTokenHash`.
  3. Compare SHA-256(header `asaas-access-token`) with the hash using `CryptographicOperations.FixedTimeEquals`. Unknown
     charge → compare against a fixed dummy hash. Unknown charge or mismatch → the **same** 401, nothing stored.
  4. `ExternalEventId` = top-level `id`; absent → SHA-256 hex of the body. Insert the event (with TenantId); unique violation
     → 200, no work. Enqueue Hangfire `PaymentEventProcessor.ProcessAsync(eventId)`; return 200.
  Logs: event type, event id, charge id, tenant id. Never the token or body.
- `PaymentEventProcessor.ProcessAsync(eventId)`: under `PlatformScope` read the event and its tenant; then a new DI scope with
  `AmbientTenantContext.TenantId` set (pattern of `ReservationReminderJob`). Already `ProcessedAt` → return.
  - `PAYMENT_RECEIVED` / `PAYMENT_CONFIRMED`: if the charge has no `ExternalId` yet (save failed in 3b), fill it from the event.
    Charge already Paid → mark processed. Reservation subject → `ReservationService.ConfirmFromPaymentAsync(chargeId)`.
  - `PAYMENT_OVERDUE` / `PAYMENT_DELETED` → Expired (if not Paid). `PAYMENT_REFUNDED` → Refunded. Others → processed, no change.
  - Exceptions → `Error` code, rethrow so Hangfire retries.
- Extract from `ReservationService.ConfirmAsync` a private `ConfirmCoreAsync(tenantId, reservation, ...)` holding the status
  change and `domainEvents.DispatchAsync(... ReservationConfirmed ...)`. `ConfirmAsync` keeps its trial guard and staff
  path unchanged. New `ConfirmFromPaymentAsync(chargeId)` (system path, no trial guard, no staff permission):
  `domainEvents.EnsureReadyAsync`, **one transaction**: `ReservationLocks.LockByReservationIdAsync`, load charge and
  reservation, charge → Paid + `PaidAt`, `reservation.DepositPaid = charge.Amount`, and if PendingDeposit → `ConfirmCoreAsync`;
  Confirmed/Canceled/Completed → reservation status unchanged. `SaveChangesAsync`, commit, then `ScheduleDeliveries` for the
  queued deliveries (through `INotificationOutboxScheduler`) **after** commit — same order as `ConfirmAsync`.
- `PaymentChargeExpiryJob` every 5 min in `MapPlatformRecurringJobs`: under `PlatformScope` list tenant ids with Pending or
  Creating charges past `DueAt`, then per tenant (new scope + `AmbientTenantContext`) mark them Expired.
- Tests: missing/wrong token → 401, nothing stored; unknown charge → same 401; body > 64 KB rejected; duplicate event →
  one processing; RECEIVED → charge Paid, reservation Confirmed, `DepositPaid` set, `ReservationConfirmed` delivery
  scheduled once after commit; RECEIVED on a Canceled reservation → status unchanged, charge Paid; charge found by
  externalReference when `ExternalId` is null; processor in tenant A never touches tenant B rows; expiry job; staff
  `ConfirmAsync` behaviour unchanged (existing tests green); OpenAPI snapshot updated.

## Slice 4 — Web: Pix step in the portal + staff chip
Branch `feat/payments-portal-pix` (vlr-web, after 3c + `gen:api`). Confirm file names before editing; expected:
`src/features/tenantPortal/pages/TenantPortalAgendaPage.tsx`, the "Minhas reservas" detail, and
`src/features/rentals/pages/ReservationsPage.tsx`.
- After a PendingDeposit booking, call `deposit-charge`. 409 `ONLINE_PAYMENT_UNAVAILABLE` / `NO_DEPOSIT_DUE` → today's
  "aguardando confirmação" text. 422 `CPF_REQUIRED` → CPF field, then retry with `{ cpf }`.
- Pix panel: amount, QR image, "Copiar código Pix", countdown to `dueAt`; poll `GET /api/payments/portal/charges/{id}` every
  4 s, paused while the tab is hidden, stopped on Paid/Expired/past `dueAt`. Paid → "Reserva confirmada"; Expired →
  "Gerar novo Pix" (POST again). The same panel opens from "Minhas reservas" for a PendingDeposit reservation.
- Staff: chip "Pix pendente" / "Pago" / "Pix expirado" from `payment`; manual Confirm unchanged.
- Vitest: panel renders QR + copy; poll → confirmed; expired → new charge; CPF flow; staff chip states. Mobile 375px.

## Later slices (own specs)
5. Cart checkout (Pix on order creation or approval). 6. Refunds on cancel (`RefundAsync`, tenant policy).
7. Rolvix billing of tenants (subscriptions on the root account; replaces the hard-coded R$199 per module).
8. Platform-admin endpoint for `PlatformFeePercent`.

## Verification on DEV (after slice 4)
Asaas sandbox + tenant **zzr14locacao**: create the account in Configurações › Pagamentos → book a rentable with deposit in
the portal → Pix shown → confirm the payment in the sandbox panel (or its payment-confirm sandbox endpoint) → webhook reaches
the DEV API → reservation Confirmed, chip "Pago", confirmation notification queued. Desktop and mobile. Never on FICC.
PROD only after the owner says "pode promover" and sets the production secrets.
