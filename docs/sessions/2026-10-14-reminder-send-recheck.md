# 2026-10-14 — Reminder send recheck

FICC DEV QA: Quadra 2 booked for 2026-10-15 16:00–17:00 at 2026-10-14 20:49:17 BRT (~19h ahead), cancelled at 20:49:32, and the email "Lembrete de reserva Quadra 2" arrived at 20:49:38. The cancellation email had not arrived yet.

The minute sweep `rentals-reservation-reminder` queues a reminder when the start is already inside 24h. That is the locked schedule, not a create-time enqueue. `NotificationOutboxProcessor` sent whatever was queued and did not read the reservation again. Hangfire runs one worker, so the reminder delivery can occupy it while the cancellation delivery waits.

Send path now re-reads status and start. Cancelled, completed, missing, or already started reminders are marked Failed and not sent. No migration.
