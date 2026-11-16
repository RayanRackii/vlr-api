# Online payments with Asaas (2026-11-15)

Repositories: vlr-api and vlr-web. Decision record: `docs/adr/0009-payment-gateway-asaas.md` (read it first; it wins on rules).
Approved by the owner after the gateway study. Implement one slice per PR, in order. Each slice is a new Sonnet session.

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

**Human steps before slice 1 ships to DEV:** an Asaas sandbox account (sandbox.asaas.com), split enabled under
Integrações › Segurança, and the Fly secrets `Payments__Asaas__BaseUrl=https://api-sandbox.asaas.com/v3`,
`Payments__Asaas__RootApiKey`, `Payments__Asaas__PlatformWalletId`, `Payments__Asaas__WebhookBaseUrl` on the DEV app.
Before PROD: confirm with Asaas sales the negotiated Pix price, subaccount cost/limits and white-label KYC.
Never print or commit the keys.

---

## Slice 1 — API: Payments module, Asaas client, tenant payment account
Branch `feat/payments-account`. **Opus review required** (secrets, migration, tenant isolation).

Files (new unless noted):
- `Platform.Api/Modules/Payments/Gateways/IPaymentGateway.cs` — provider-neutral:
  `CreateSubaccountAsync(SubaccountRequest)` → `SubaccountResult(ExternalAccountId, WalletId, ApiKey, OnboardingUrl?)`;
  `RegisterWebhookAsync(apiKey, url, token)`; `CreateCustomerAsync(apiKey, CustomerRequest)` → id;
  `CreatePixChargeAsync(apiKey, PixChargeRequest(customerId, amount, dueAt, description, externalReference, split?))`
  → `ChargeResult(ExternalId, PixPayload, PixQrImageBase64, InvoiceUrl, DueAt)`; `GetChargeStatusAsync(apiKey, id)`;
  `GetAccountStatusAsync(apiKey)` → Pending / Active / Rejected. (Refund comes in slice 6.)
- `Gateways/Asaas/AsaasGateway.cs`, `AsaasOptions.cs` (`BaseUrl`, `RootApiKey`, `PlatformWalletId`, `WebhookBaseUrl`),
  `AsaasDtos.cs`. Typed `HttpClient`, header `access_token`, 10 s timeout, no redirects. Map Asaas errors to
  `PaymentGatewayException(code)` without the response body in the message. Register in DI only via the module.
- `Core/Platform.Core.Domain/Entities/TenantPaymentAccount.cs` — `ITenantEntity`: `Id`, `TenantId` (unique), `Provider`
  (string, "asaas"), `ExternalAccountId`, `WalletId`, `EncryptedApiKey`, `WebhookTokenHash` (SHA-256 hex),
  `Status` (enum `PaymentAccountStatus`: Pending, Active, Rejected), `OnboardingUrl?`, `PlatformFeePercent` (decimal 0–20, default 0),
  `CreatedAt`, `UpdatedAt`. Global tenant filter like the other tenant entities.
- Data Protection: `services.AddDataProtection().SetApplicationName("rolvix-api").PersistKeysToDbContext<PlatformDbContext>()`
  (package `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`; `PlatformDbContext` implements `IDataProtectionKeyContext`,
  `DbSet<DataProtectionKey>` — this table has no TenantId and no tenant filter). Wrapper `PaymentSecretProtector`
  (purpose `"payments.subaccount-api-key.v1"`).
- Migration `AddTenantPaymentAccounts` — additive only: `DataProtectionKeys`, `TenantPaymentAccounts` (unique `TenantId`).
- `Modules/Payments/Services/PaymentAccountService.cs`:
  - `GetAsync()` → DTO `{ status, provider, onboardingUrl, platformFeePercent }` — never the key, the wallet id or the token.
  - `CreateAsync(request)` → fails 409 `PAYMENT_ACCOUNT_EXISTS` if one exists; validates `Tenant.TaxId` (CPF/CNPJ digits) and
    the request (name, e-mail, phone, postal code, address number, income/company type as Asaas requires); calls
    `CreateSubaccountAsync` with the root key; generates a 32-byte random webhook token, registers the webhook
    `{WebhookBaseUrl}/api/webhooks/payments/asaas` on the subaccount with it; stores the encrypted key and the token hash.
    Trial read-only tenants are blocked by `trialGuard.EnsureWritableAsync`.
  - `RefreshStatusAsync()` → `GetAccountStatusAsync`, updates `Status`.
- `Modules/Payments/Controllers/PaymentAccountController.cs` — `api/payments/account`: `GET`, `POST`, `POST refresh-status`.
  New permission `Settings.Payments` in `Constants/Permissions.cs` + `PermissionCatalog.cs` (group Configurações),
  granted to the admin role defaults the same way other settings permissions are.
- Tenant gate helper `IPaymentAvailability.IsOnlinePaymentActiveAsync()` (Status == Active) for slice 3.

Tests (xUnit, `tests/Platform.Api.Tests/Payments/`; fake `HttpMessageHandler`, no real network):
- create → subaccount + webhook calls sent with the root key / subaccount key; stored key is encrypted (≠ plain) and decrypts back.
- GET never contains the API key, wallet id or webhook token (serialize and assert).
- second create → 409; tenant A cannot read tenant B's account (filter).
- missing/invalid TaxId → 400 with a field error; Asaas 400 → 422 `PAYMENT_PROVIDER_REJECTED` without the provider body.
- permission: without `Settings.Payments` → 403.
- OpenAPI snapshot updated.

Done when: tests green, migration listed as additive, `npm run gen:api` diff ready for slice 2.

## Slice 2 — Web: Configurações › Pagamentos
Branch `feat/payments-settings`. Feature folder `vlr-web/src/features/payments/`.
- Route `/configuracoes/pagamentos`, nav item under Configurações gated by `Settings.Payments`.
- Page uses the standard `Page`/`PageHeader` + `FormPage`/`SaveBar` (same as the other settings forms).
  - No account: short explanation ("Receba Pix das reservas direto na sua conta. Taxa Asaas por Pix: …") and the form,
    prefilled from the tenant profile (name, e-mail, phone, CNPJ/CPF read-only from the profile, address).
  - Pending: status chip "Em análise", button "Completar cadastro" → `onboardingUrl` (new tab), "Atualizar status".
  - Active: chip "Ativa"; read-only summary. Rejected: chip "Recusada" + support text.
- Strings in pt-BR i18n files. Tests (vitest): each state renders; submit calls POST with the form; 409 shows the existing account.

## Slice 3 — API: deposit charge, webhook, auto-confirm, expiry
Branch `feat/payments-deposit-pix`. **Opus review required.**
- Entities + migration `AddPaymentCharges` (additive):
  - `PaymentCharge` — `ITenantEntity`: `Id`, `TenantId`, `Provider`, `ExternalId` (unique per provider), `SubjectType`
    (enum Reservation, CatalogOrder), `SubjectId`, `CustomerId?`, `Method` (Pix), `Amount`, `Currency` "BRL", `Status`
    (Pending, Paid, Expired, Failed, Refunded), `PixPayload`, `PixQrImageBase64`, `InvoiceUrl`, `DueAt`, `PaidAt?`,
    `IdempotencyKey` (unique), `PlatformFeeAmount`, `CreatedAt`, `RowVersion`. Index `(TenantId, SubjectType, SubjectId)`.
  - `PaymentWebhookEvent` — platform table (no tenant filter): `Id`, `Provider`, `ExternalEventId` (unique with provider),
    `EventType`, `ExternalChargeId`, `PayloadJson`, `ReceivedAt`, `ProcessedAt?`, `Error?`.
- Deposit amount resolver `DepositAmountCalculator` per ADR 0009 §6 (asset → effective booking policy → pricing → 100;
  reuse the policy resolution already used for `MinimumBookingLeadMinutes`). Update the `BookingPolicy.DepositPercent` and
  `RentalAsset.DepositPercent` XML docs.
- `PaymentChargeService.CreateReservationDepositChargeAsync(reservationId)` (portal, customer must own the reservation):
  - only when online payment is active, reservation is PendingDeposit and the amount > 0; otherwise 409 with a code.
  - idempotent: a Pending, not expired charge for the same reservation is returned as is; key `reservation:{id}:{n}`.
  - creates/reuses the Asaas customer (store the external customer id on the customer–tenant link or in a small
    `PaymentCustomer` table; pick the smaller change and document it), then a Pix charge due in 30 min, split to the
    platform wallet only when `PlatformFeePercent > 0`.
- Endpoints: `POST /api/reservations/mine/{id}/deposit-charge`; `GET /api/payments/portal/charges/{id}` (customer + tenant
  check; returns status, amount, pixPayload, qr, dueAt — never provider ids beyond what the UI needs);
  staff `GET /api/reservations/{id}` gains `payment: { status, amount, paidAt }` (latest charge).
- `AsaasWebhookController` `POST api/webhooks/payments/asaas`, `[AllowAnonymous]`, modelled on
  `Modules/Webhooks/Controllers/WhatsAppWebhookController.cs`: read body; find the charge by `payment.id` (cross-tenant
  lookup inside `PlatformScope`); compare SHA-256 of header `asaas-access-token` with that tenant's `WebhookTokenHash` using
  `CryptographicOperations.FixedTimeEquals` → 401 on mismatch or unknown charge; insert `PaymentWebhookEvent`
  (unique violation → 200, no work); enqueue `PaymentEventProcessor.ProcessAsync(eventId)`; return 200.
  Body size limit 64 KB. Log event type and ids only.
- `PaymentEventProcessor` (Hangfire, `PlatformScope` for the charge tenant): `PAYMENT_RECEIVED`/`PAYMENT_CONFIRMED` → charge
  Paid + `PaidAt`; for a Reservation subject set `DepositPaid = Amount` and, if PendingDeposit, confirm through a shared
  method extracted from `ReservationService.ConfirmAsync` (same lock, same `ReservationConfirmed` event; no trial guard for
  this system path, no staff permission). Already Confirmed/Canceled/Completed → charge Paid, reservation unchanged.
  `PAYMENT_OVERDUE` → Expired. `PAYMENT_REFUNDED` → Refunded. Unknown types → mark processed, no change.
- `PaymentChargeExpiryJob` recurring every 5 min in `Jobs/HangfireExtensions.cs`: Pending with `DueAt < now` → Expired.
- Tests: invalid/missing token → 401 and nothing stored; duplicate event → processed once; Paid → reservation Confirmed,
  `DepositPaid` set, one `ReservationConfirmed` outbox row; Paid on a Canceled reservation → reservation unchanged;
  customer B cannot read/create a charge for customer A's reservation; tenant without active account → 409
  `ONLINE_PAYMENT_UNAVAILABLE` and the booking flow unchanged; deposit percent resolution order; expiry job;
  idempotent create returns the same charge. OpenAPI snapshot updated.

## Slice 4 — Web: Pix step in the portal + payment chip for staff
Branch `feat/payments-portal-pix`.
- `TenantPortalAgendaPage.tsx` (and the reservation detail in "Minhas reservas"): after a PendingDeposit booking, when the
  API offers online payment, show a Pix panel: amount, QR image, "Copiar código Pix" button, countdown to `dueAt`, and poll
  `GET /api/payments/portal/charges/{id}` every 4 s (stop on Paid/Expired or after `dueAt`, pause when the tab is hidden).
  Paid → "Reserva confirmada"; Expired → "Gerar novo Pix". Without online payment, keep today's "aguardando confirmação" text.
- `ReservationsPage.tsx` / detail: chip "Pix pendente" / "Pago" / "Pix expirado" from `payment`; manual Confirm unchanged.
- Tests (vitest): Pix panel renders QR + copy; poll switches to confirmed; expired offers a new charge; staff chip states.

## Later slices (own specs)
5. Cart checkout (Pix on order creation or approval). 6. Refunds on cancel (`RefundAsync`, tenant policy).
7. Rolvix billing of tenants (subscriptions on the root account, replaces the hard-coded R$199 per module).

## Verification on DEV (after slices 3–4)
Asaas sandbox + tenant **zzr14locacao**: create the account in Configurações › Pagamentos → book a rentable with deposit in
the portal → Pix shown → confirm the payment in the sandbox (`POST /v3/sandbox/payment/{id}/confirm` or the sandbox panel) →
webhook reaches the DEV API → reservation Confirmed, chip "Pago", confirmation notification queued. Desktop and mobile.
Never on FICC. PROD only after the owner says "pode promover" and sets the production secrets.
