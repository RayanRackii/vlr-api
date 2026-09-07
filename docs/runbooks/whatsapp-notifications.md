# WhatsApp notifications (Catalog + Rentals)

Operational runbook. Canonical product copy lives in `CONTEXT.md` and `docs/plans/active/2026-09-07-notifications-whatsapp-catalog-rentals.md`.

## Current state

| Channel | Runtime | PROD |
|---|---|---|
| Email (Resend) | Unchanged. `EMAIL_STATUS = OK` | Keep `Notifications__AllowExternalEmail=true` |
| SMS (Twilio Verify) | No app branding setting. Operational Catalog SMS remains Dev | Friendly Name must be `Rolvix` (not `ROLVIX PROD`) |
| WhatsApp (Meta Cloud API) | Templates + durable outbox in code | **External delivery stays disabled** |

Do **not** set `Notifications__AllowExternalWhatsApp=true` on Railway production.
Do **not** set `Notifications__AllowExternalDelivery=true` as a shortcut.

## Approved Meta templates (`pt_BR`, body only, no header/media/buttons)

| Event family | Template | Body parameters |
|---|---|---|
| Catalog order status | `catalog_order_status_update` | tenantName, customerName, orderNumber, orderStatus (PT label) |
| Rentals reservation status | `rental_reservation_status_update` | tenantName, customerName, reservationReference, reservationStatus (PT label) |
| Rentals reminder | `rental_reservation_reminder` | tenantName, customerName, reservationReference, reservationDateTime (`America/Sao_Paulo`) |

Internal Catalog EventTypes stay `catalog.order.created|approved|ready|rejected|cancelled_by_supplier`.
Internal Rentals EventTypes: `rentals.reservation.pending_deposit|confirmed|canceled|completed|reminder`.

`reservationReference` is the customer-visible `Asset.Name` of reservation items (joined with ` + `). Not a GUID.

Portuguese status labels: Catalog Solicitado / Aprovado / Pronto / Recusado / Cancelado; Rentals Aguardando depósito / Confirmada / Cancelada / Concluída.

## Two gates (both required for a real send)

1. Environment: `Notifications__AllowExternalWhatsApp=true` **and** `WhatsApp__AccessToken` + `WhatsApp__PhoneNumberId`.
2. Tenant: `TenantNotificationChannelConfig` WhatsApp row `IsActive=true` for that EventType. Seeds default **off**.

Missing approved template name → permanent failure (no `type=text` fallback, no five retries).

## Existing DB template names

Seed now **reconciles** `(EventType, Channel, Language)` and updates `WhatsAppTemplateName`. First Catalog or Rentals `EnsureReady` on DEV/PROD rewrites legacy names such as `catalog_order_created`. No EF migration.

## Human — SMS branding

Twilio **PROD** Verify Service → Friendly Name → `Rolvix`. Also confirm the custom Verify template does not hardcode `ROLVIX PROD`.

`SMS_PROD_BRANDING_CHANGE = PROVIDER_CONFIG`

## Human — Meta reminder body

Re-submit `rental_reservation_reminder` (`pt_BR`):

```
Olá, {{2}}!

A {{1}} lembra que você possui uma reserva {{3}} para {{4}}.

Acesse a plataforma para consultar os detalhes da reserva.
```

Variables: 1 tenantName, 2 customerName, 3 reservationReference, 4 reservationDateTime.

DEV live send of reminders cannot happen until Meta marks this template active.

## Human — reminder timing (locked)

`RENTALS_REMINDER_LEAD_TIME = 24_HOURS`. Hangfire job `rentals-reservation-reminder` every minute. One reminder per reservation. Not 2h. No tenant setting.

Skip Canceled, Completed, and after start. Idempotent via existing `core.notifications` row (`EventType = rentals.reservation.reminder`, `AggregateType = Reservation`).

Job only considers tenants with WhatsApp `rentals.reservation.reminder` **IsActive**. Default remains off.

## DEV enablement (names only — never print values)

On the **development** Railway service only:

- `Notifications__AllowExternalWhatsApp=true`
- `WhatsApp__AccessToken`
- `WhatsApp__PhoneNumberId`
- `WhatsApp__VerifyToken`
- `WhatsApp__AppSecret` (required before treating WhatsApp as production-ready)

Restart and confirm boot: `External WhatsApp delivery enabled`.
If AppSecret is missing in Production host, treat WhatsApp as **BLOCKED**.

Catalog tenant channel: existing UI `/catalogo/notificacoes` (`PUT /api/catalog/notification-channels`).
Rentals tenant channel: no WEB UI in this slice. Enable a single DEV tenant/event with SQL, for example:

```sql
UPDATE core.tenant_notification_channel_configs
SET is_active = true
WHERE tenant_id = '<dev-tenant-id>'
  AND channel = 'WhatsApp'
  AND event_type IN (
    'catalog.order.created',
    'rentals.reservation.confirmed',
    'rentals.reservation.reminder'
  );
```

Enable **one** event for the smoke tenant. Do not activate every tenant.

## Backlog-blast preflight (read-only, before any PROD enablement)

```sql
SELECT status, COUNT(*)
FROM core.notification_deliveries
WHERE channel = 'WhatsApp'
GROUP BY status;
```

If `Queued` > 0, do **not** enable Meta. Drain/decision is a separate Human Gate. Do not resend Failed rows as a blast.

## DEV live smoke (after merge + deploy + Meta active + tenant channel on)

If reminder scheduler is deployed, prove reminder only with a Human-owned reservation whose `StartDateTime` is inside the next 24h (not Canceled/Completed). Enable `rentals.reservation.reminder` WhatsApp for that DEV tenant via SQL. One Notification row; Hangfire sweep must not duplicate.

No PROD send.

## Do not

- Enable WhatsApp on Railway production in this slice
- Mutate PROD outbox / resend Failed
- Use `Notifications__AllowExternalDelivery=true` to turn WhatsApp on
- Invent reminder hours or a new reservation identifier
