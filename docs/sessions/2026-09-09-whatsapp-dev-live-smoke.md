# 2026-09-09 — DEV WhatsApp live smoke (reminder unskipped)

Human: `rental_reservation_reminder` ACTIVE/APPROVED in Meta. Reminder live smoke is required. No runtime code change. PROD WhatsApp stayed off. `RENTALS_REMINDER_LEAD_TIME = 24_HOURS`.

## Deploy

- API `develop` `51b7306` on Railway development (`vlr-api-development.up.railway.app`)
- WEB `develop` `fcc9982` on Vercel Preview
- Unified `GET/PUT /api/notifications/channel-configs` 200 on DEV

## Smoke (one DEV tenant, Human-owned E2E customer)

Enablement: unified PUT, one event at a time, then off.

1. `catalog.order.created` → new portal order
2. `rentals.reservation.confirmed` → Confirmed reservation starting 2026-09-09 16:00 America/Sao_Paulo (`StartDateTime` `2026-09-09T19:00:00+00:00`)
3. `rentals.reservation.reminder` after blast preflight (1 due reservation, 0 others)

## Result

DEV is talking to Meta (not `DevWhatsAppProvider`).

All three customer WhatsApp deliveries **Failed** Graph `404` / **`132001`** (template name/language not found on that WABA). Permanent; Attempt=1; no five retries.

Reminder: one `Notification`; second Hangfire sweep did not resend. Expected `{{4}}` civil clock `09/09/2026 16:00`.

Catalog also queued staff WhatsApp rows (no phone) that Failed missing recipient. Tenant WhatsApp configs restored off. No leftover Queued on this tenant.

`NOTIFICATIONS_WHATSAPP = BLOCKED_DEV_SMOKE` until Human confirms the three `pt_BR` templates on the WABA bound to DEV `WhatsApp__PhoneNumberId`.
