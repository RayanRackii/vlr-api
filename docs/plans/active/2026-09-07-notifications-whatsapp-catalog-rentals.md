# 2026-09-07-notifications-whatsapp-catalog-rentals

Status: approved (reminder **scheduler** deferred — `RENTALS_REMINDER_TIMING_HUMAN_GATE_REQUIRED`)

## Goal / Problem

Use approved Meta WhatsApp templates for Catalog order-status and Rentals reservation-status (plus reminder mapping). Durable outbox only. PROD external WhatsApp stays disabled. Email unchanged. SMS branding remains Twilio Console.

## Visible behavior

- Catalog customer WhatsApp status events send `catalog_order_status_update` (`pt_BR`, 4 body params) when tenant WhatsApp channel is on **and** `AllowExternalWhatsApp` is on.
- Rentals customer WhatsApp status events send `rental_reservation_status_update` under the same two gates.
- Reminder EventType/template/rendering exist; **no Hangfire reminder job**.
- Missing template → permanent failure (no `type=text` fallback, no 5 retries).
- Meta `messages[0].id` persisted on `NotificationDelivery.ProviderMessageId`.
- Tenant WhatsApp defaults **off**.

## Repositories

- vlr-api
- WEB_CHANGE_REQUIRED = NO

## Relevant existing ADR / rules

- `.cursor/rules/10-arquitetura.mdc` — providers, no sync Meta in HTTP
- `.cursor/rules/30-rentals.mdc` — locks, Complete×Cancel, timezone T1
- Catalog outbox spec `docs/plans/active/2026-08-28-catalog-orders.md`

## Architecture route

- rolvix-architect (parent materialized this spec from locked Human decisions + targeted code inspection; reminder timing is the only open product gate)

## Execution route

- api-implementer

## Confirmed decisions

### Templates (exact)

| Event family | Meta name | Language | Body params |
|---|---|---|---|
| Catalog order status | `catalog_order_status_update` | `pt_BR` | tenantName, customerName, orderNumber, orderStatus (PT label) |
| Rentals reservation status | `rental_reservation_status_update` | `pt_BR` | tenantName, customerName, reservationReference, reservationStatus (PT label) |
| Rentals reminder | `rental_reservation_reminder` | `pt_BR` | tenantName, customerName, reservationReference, reservationDateTime (America/Sao_Paulo) |

No header / media / buttons. No free-form WhatsApp for these events.

### Internal EventTypes (distinct; one Meta status template)

Catalog (unchanged): `catalog.order.created|approved|ready|rejected|cancelled_by_supplier`

Rentals:

- `rentals.reservation.pending_deposit`
- `rentals.reservation.confirmed`
- `rentals.reservation.canceled`
- `rentals.reservation.completed`
- `rentals.reservation.reminder`

### Portuguese labels (canonical UI copy)

Catalog: Requested=Solicitado, Approved=Aprovado, Ready=Pronto, Rejected=Recusado, Cancelled=Cancelado

Rentals: PendingDeposit=Aguardando depósito, Confirmed=Confirmada, Canceled=Cancelada, Completed=Concluída

### reservationReference

No booking code exists. Use customer-visible `Asset.Name` of reservation items, joined with ` + ` when multiple. Do not send a GUID. Do not add a DB identifier.

### reservationDateTime

`BrazilTimeZone` civil clock: `dd/MM/yyyy HH:mm` (`pt-BR` culture). Convert UTC instant via `TimeZoneInfo.ConvertTimeFromUtc` — never hardcode `-03:00`.

### Pipeline

Same as Catalog: mutation transaction → `Notification` + `NotificationDelivery` (InApp Delivered / external Queued) → commit → `INotificationOutboxScheduler.Schedule`. Never `NotificationQueue` for these events.

### Idempotency (no migration)

Publish only when a **new** lifecycle transition is persisted. Confirm/Complete/Cancel already-idempotent returns must not publish. Create publishes once for the opening status (`PendingDeposit` or `Confirmed`). Reminder publish is explicit (tests / future job). `MIGRATION_REQUIRED = NO`

### Template seed reconciliation (no migration)

`EnsurePlatformTemplates` **updates** existing `(EventType, Channel, Language)` rows’ `WhatsAppTemplateName` (and WhatsApp `BodyTemplate` if needed). Insert-only is insufficient for DEV/PROD rows already seeded as `catalog_order_*`.

### Channel defaults

Seed InApp on, Email/WhatsApp/Sms off (Catalog pattern). Rentals WhatsApp stays off until explicitly enabled per tenant/event. No global blast.

DEV enablement without WEB: Catalog uses existing `/api/catalog/notification-channels`. Rentals: tests set `IsActive`; Human DEV smoke uses documented SQL (no new permission row / no migration).

### Provider

`IWhatsAppProvider.SendTemplateAsync` returns Meta message id (`string?`). Outbox `MarkSent(id)`. `SendAsync` remains for 24h window elsewhere; outbox **must not** call it for company-initiated catalog/rentals WhatsApp.

### Errors

- Missing/blank `WhatsAppTemplateName` → permanent, no HTTP.
- Meta 429 / 5xx / timeout → transient.
- Meta 4xx template/param/language/recipient-invalid → permanent (`WhatsAppSendException`).

### Logging

Mask phone to last-4. Do not log tokens, AppSecret, full inbound WhatsApp text, or full Meta error JSON when a code/title suffices.

### Webhook

Receipts remain follow-up. Production readiness: `WhatsApp__AppSecret` required when external WhatsApp is on; LogError in Production if missing. DEV may skip signature if unset (existing warning).

## Invariants that must not break

Reservation locks, occupancy, Confirm/Complete/Cancel semantics, queue, timezone T1. Failed reservation/order must not notify. Provider failure must not roll back the business transaction. Email Resend path unchanged. Operational SMS remains unavailable.

## Implementation scope

- Catalog WhatsApp template consolidation + PT status in payload
- RentalsNotificationPublisher + wire Create/Book/Confirm/Complete/Cancel (staff + B2C)
- Reminder publish API for tests; **no scheduler**
- Meta id + error classification + PII logs
- Move outbox processor DI to `AddNotificationInfrastructure`
- Tests as requested
- Docs/ROADMAP; PROD WhatsApp flag untouched

## Likely affected areas / files

- `Platform.Api/Notifications/**`
- `Platform.Api/Modules/Catalog/Services/CatalogNotificationPublisher.cs`
- `Platform.Api/Modules/Rentals/Services/ReservationService.cs`, `ScheduleService.cs`
- `Platform.Api/Modules/Catalog/CatalogModuleExtensions.cs`, `RentalsModuleExtensions.cs`
- `Core/Platform.Core.Domain/Constants/`
- `Core/Platform.Core.Infrastructure/Time/BrazilTimeZone.cs`
- `tests/Platform.Api.Tests/Notifications/**`, `Catalog/**`, `Rentals/**`
- `CONTEXT.md`, `ROADMAP.md`, `docs/runbooks/password-recovery-resend.md`

## Test seams

CatalogHarness, LocationBookingHarness, HttpMessageHandler fake for Meta, existing DI gate tests.

## Do not

Enable PROD WhatsApp; invent reminder hours; create EF migration; change email/SMS runtime; WEB UI; drive reservation lifecycle from notifications.

## Reminder timing (Human Gate)

No canonical lead time in ROADMAP/CONTEXT/plans/jobs.

Options:

1. **24h before `StartDateTime`** (one shot) — simple, common for clubs.
2. **Same civil morning in America/Sao_Paulo** (e.g. 08:00 on reservation date) — aligns with club ops day.
3. **2h before start** — last-minute only; easy to miss if job cadence is 1 minute but noisy.

Recommendation: **(1) 24h before start**, one reminder, skip if start is already within 24h at booking time. Do **not** implement until Human picks.

`RENTALS_REMINDER_TIMING_HUMAN_GATE_REQUIRED`

## PROD blast preflight (later, read-only)

```sql
SELECT status, COUNT(*) 
FROM core.notification_deliveries
WHERE channel = 'WhatsApp'
GROUP BY status;
```

Do not enable `Notifications__AllowExternalWhatsApp` on PROD if `Queued` > 0 without an explicit drain decision.

`MIGRATION_REQUIRED = NO`  
`WEB_CHANGE_REQUIRED = NO`
