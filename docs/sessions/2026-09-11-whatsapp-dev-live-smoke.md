# 2026-09-11 — DEV WhatsApp live smoke CLOSED_DEV

Re-ran Catalog / Rentals status / reminder smokes. No runtime code change. PROD WhatsApp stayed off. `RENTALS_REMINDER_LEAD_TIME = 24_HOURS`.

## Result

Customer WhatsApp deliveries **Sent** (Attempt=1, no error):

1. `catalog.order.created` → `catalog_order_status_update`
2. `rentals.reservation.confirmed` → `rental_reservation_status_update`
3. `rentals.reservation.reminder` → `rental_reservation_reminder`

Reminder reservation `922d4afc-6c10-4725-bce7-518e75dd031e`, `StartDateTime` `2026-09-11T14:00:00+00:00` → expected `{{4}}` **`11/09/2026 11:00`** America/Sao_Paulo. One Notification; second Hangfire sweep did not resend.

Catalog also Failed four staff WhatsApp rows (`WhatsApp recipient is missing`) — B2B users have no phone. Customer row Sent.

Tenant WhatsApp configs restored off. Queued = 0 on this tenant.

2026-09-09 132001 is superseded for DEV.

`NOTIFICATIONS_WHATSAPP = CLOSED_DEV` (not PROD).
