# Optional per-Location reservation waiting queue

B2C booking of a Location may optionally pass through a daily FIFO waiting room keyed by **opening time**, not by slot. Default is off so existing tenants are unchanged. The Active ticket (90s server lease) authorizes one completed reservation of any currently bookable window on that Location. F-01 row locks remain the occupancy serialization; the queue only serializes **who may attempt** a booking.

**Status:** accepted (2026-08-22)

**Context:** Rolvix had no booking-release/horizon. `OpenTime`/`CloseTime` are operating hours (OpenHours), not reservation opening. Product needs a club-style scramble at a wall-clock opening without tying a ticket to a slot before the Customer chooses a time.

**Considered options:** (1) per-slot queue session — rejected (conflicts with choosing/changing time during the turn); (2) T = slot start — rejected (example 07:00/07:30 is opening of reservations, not play start); (3) daily Location opening `QueueOpeningTime` in America/Sao_Paulo with session `(Tenant, Location, civil date of T)` — accepted.

**Consequences:** Additive columns and tables in `rentals`. QueueEnabled=false is the compatibility path. Timezone is platform Brazil, not per tenant. Polling, not WebSocket. Promotion is lazy under the existing `FOR UPDATE` on `rental_assets`.
