# Rentals Context Pack

Derived context — NOT canonical.

- Scope: Rentals beachhead (spaces/goods; club booking)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0001-rentals-slot-schedule.md`; `docs/adr/0003-reservation-waiting-queue.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `.cursor/rules/30-rentals.mdc`; `ROADMAP.md`
- Last verified: 2026-09-07
- Verified at: Wave 1 **PROD_COMPLETE**; B2C self-cancel **CLOSED_DEV** (not PROD)
  - API PROD / `origin/main`: `54b385d5d14d0438fceb0c358872cf7ef1e1f589`
  - WEB PROD / `origin/main`: `0d995955dd56338cc8cbfda6bf8ff6950afb68f6`
  - B2C self-cancel DEV: API `89b3e6d` / PR #64; WEB `3478355` / PR #59
- Historical spec (delivered, do not re-implement): `docs/plans/active/2026-09-05-rentals-wave1-lifecycle-integrity.md`
- Current spec: `docs/plans/active/2026-09-07-rentals-b2c-self-cancel.md`

## Purpose

Load when the question is Reservation, Rentable, Slot, SlotGrid, OpenHours, schedule, pricing, booking conflicts, Layout picker, the optional Location waiting queue, or reservation Complete/Cancel.

This pack describes **shipped PROD**. It does **not** authorize Wave 2, Layout work, timezone follow-ups, or new product implementation.

## Canonical sources

- `CONTEXT.md` — glossary (Reservation, Slot, OccupancyKind, ScheduleTemplate, OpenHours, SlotGrid, Layout, WaitingQueue, RequiresDeposit, …)
- `docs/adr/0001-rentals-slot-schedule.md` — Slot-first schedule; OccupancyKind catalog; derived days; 2026-08-22 overlap addendum
- `docs/adr/0003-reservation-waiting-queue.md` — optional per-Location daily FIFO; T = QueueOpeningTime
- `docs/adr/0004-module-dependencies-asset-registry.md` — Rentals requires Asset Registry, not Inventory entitlement
- `.cursor/rules/30-rentals.mdc` — invariants and current gaps
- `ROADMAP.md` — Wave 1 closed as **PROD_COMPLETE**

## Wave 1 shipped (PROD)

`RENTALS_WAVE1 = PROD_COMPLETE`  
`RENTALS_WAVE1_PHASE_A = PROD_COMPLETE`  
`RENTALS_WAVE1_PHASE_B = PROD_COMPLETE`

| | Identity |
|---|---|
| API PROD | `54b385d5d14d0438fceb0c358872cf7ef1e1f589` (Railway production matched `main`) |
| WEB PROD | `0d995955dd56338cc8cbfda6bf8ff6950afb68f6` (Vercel Production matched `main`) |
| Last migration | `20260906034111_AddRentalsReservationsCompletePermission` |
| Pending | `PENDING_COUNT=0` |
| Timestamp rows | `PROD_TIMESTAMP_STATE=EMPTY` — no historical Reservation backfill |
| PROD Slot | single Slot **kept** |
| Smoke | public/read-only **passed**; **no** customer-tenant write smoke |

### Lifecycle integrity (Phase A)

- Staff-only Complete; permission `rentals.reservations.complete` (not confirm)
- `Confirmed → Completed` (idempotent on Completed; PendingDeposit/Canceled → 409)
- Complete × Cancel serialized exclusive-winner
- Confirm × Cancel serialized (`PendingDeposit` source; Confirm is not terminal)
- Reservation-row `FOR UPDATE` first (`ReservationLocks`)
- Cancel then `RentalAsset` locks ascending where occupancy is released

### Timezone (Phase B T1 — live)

- Business timezone = `America/Sao_Paulo` (`BrazilTimeZone`; do not hard-code `-03:00`)
- Slot / Schedule / OpenHours / RentalPricing / CreateReservation request Date/StartTime/EndTime remain **civil**
- Reservation `StartDateTime` / `EndDateTime` are true instants via `BrazilTimeZone.AtLocal` → UTC `DateTimeOffset` / `timestamptz`
- API JSON returns UTC instants only (no parallel civil fields)
- WEB formats Rentals reservation clocks and Rentals “today” in `America/Sao_Paulo` (`src/lib/brazilTimeZone.ts`)
- Brazil civil-day query bounds: inclusive `StartOfCivilDay(D)`, exclusive `ExclusiveEndOfCivilDay(D)`
- Pricing remains civil-day/time based
- No schema migration for Phase B
- No historical Reservation backfill required

## Domain vocabulary

- **Rentable** = `RentalAsset` (Location exclusive / Good with quantity)
- **Reservation** = Customer booking for a concrete time window; N `ReservationItem`s; occupancy fact
- **Slot** = dated occupancy cell on one Rentable (kind + status); optional `ReservationId`
- **OccupancyKind** = tenant catalog; overlapping kinds on a weekday: Closed > Lesson > Open on unpublished days
- **ScheduleTemplate** = weekly pattern; cross-kind overlap allowed; exact interval+kind is unique
- **OpenHours** = policy “Horário padrão”; bookable windows derived
- **SlotGrid** = policy “Grade personalizada”; unpublished days derived from weekly templates (split by precedence)
- **Layout** = visual map of Rentables; not schedule data
- **WaitingQueue** = optional per-Location FIFO at daily `QueueOpeningTime` (America/Sao_Paulo); default off
- **QueueSession** = `(Tenant, Location, civil date of T)`; not per Slot
- **QueueTicket** = FIFO `Sequence`; Waiting → Active (90s Turn) → Completed | Expired | Cancelled
- **Turn** = 90s Active lease authorizing one reservation; F-01 still serializes occupancy

## Current model

Reservation is the occupancy fact (start/end + items). Slot is the schedule cell. Link is optional: `Slot.ReservationId` when a persisted cell is booked via `BookSlot`. Derived OpenHours/SlotGrid windows book via `POST /api/reservations` until a Slot row exists (`PublishDay` optional). Conflict = overlapping reservations, not “must have SlotId”. Weekly apply matches `(RentalAssetId, DayOfWeek, StartTime, EndTime, OccupancyKindId)`.

### Pricing vs deposit gate (split)

- **Price:** `RentalPricing` window (`DayOfWeek` + `Start`/`End` + `PricePerHour`) computes `TotalAmount`.
- **Deposit gate:** `RentalAsset.RequiresDeposit` — if any item’s rentable has it, reservation starts `PendingDeposit`; else `Confirmed`.
- Pricing-row `RequiresDeposit` / `DepositPercentage` do **not** gate the reservation (see `CONTEXT.md` RequiresDeposit). `DepositPaid` is still always 0 (payment not implemented).

## Critical invariants

- Location: one blocking reservation per interval; Good: quantity vs `TotalQuantity`
- `RequiresDeposit` on any item → `PendingDeposit`; else `Confirmed`
- B2C login is email+password; phone is SMS/WhatsApp, not login
- Reservation customer snapshots do not follow later Customer edits
- Product UI never shows `OpenHours` / `SlotGrid` as copy
- Same OccupancyKind cannot overlap itself on a Rentable+weekday; different kinds may
- `PublishDay` gap-fills by rentable + start; does not wipe existing slots
- Create/book serialize occupancy with `RentalAssetLocks` `FOR UPDATE` on `rentals.rental_assets` ordered by `RentalAssetId` (reservation rows are not locked on create/book)
- Confirm/Complete/Cancel serialize on `ReservationLocks` `FOR UPDATE` **first** (load only after lock). Cancel then locks distinct `RentalAssetId`s ascending via `RentalAssetLocks` before `MarkAvailable`. Confirm and Complete lock the reservation only and do not free slots
- Confirm is not terminal: `PendingDeposit → Confirmed` (idempotent 200 on Confirmed); `Canceled`/`Completed` → 409. Sequential Confirm then Cancel both succeed
- Complete is staff-only: `Confirmed → Completed`; `Completed` is idempotent 200; `PendingDeposit`/`Canceled` → 409. Does not free slots.
- Customer JWT cannot Complete. Customer can self-cancel own eligible reservation (`POST /api/reservations/mine/{id}/cancel`) when status is `PendingDeposit`/`Confirmed` and `UtcNow < StartDateTime`. Non-owner → 404 `"Reservation not found."`. Staff Cancel has no cutoff.
- **Complete × Cancel (human):** `Confirmed` is the source. Competing terminals, no priority. Exactly one succeeds. Serialized. Customer Cancel uses the same occupancy-release tail as staff Cancel.
- **Confirm × Cancel:** shared source `PendingDeposit`. Confirm is not terminal. Legal: Confirm→Cancel both succeed, final `Canceled` + occupancy released. Legal: Cancel→Confirm, Confirm 409, stays `Canceled`. Invalid: stale Confirm overwrites `Canceled` after occupancy release (`Confirmed` + slot `Available`).

## Current contracts

- Public day: `GET /api/public/tenants/{subdomain}/schedule/days/{date}`
- Book persisted slot: `POST /api/schedule/slots/book` (`slotId`)
- Book derived window: `POST /api/reservations` (date + start/end + items)
- Customer list: `GET /api/reservations/mine` (Customer JWT)
- Customer self-cancel: `POST /api/reservations/mine/{id}/cancel` (Customer JWT; cutoff before start; 404 if not owner)
- Queue (Customer): `GET/POST /api/rental-assets/{id}/queue`, `POST .../queue/join`, `POST .../queue/leave`
- Registry without Ativos: `POST /api/rental-assets`, `PUT /api/rental-assets/{id}`, `GET /api/rental-assets/categories|families` (`rentals.assets.*`)
- Admin day/exceptions: `GET /api/schedule/days/{date}`, `POST /api/schedule/slots/daily-occurrence`
- Admin list: `GET /api/reservations`
- Admin confirm: `POST /api/reservations/{id}/confirm` — permission `rentals.reservations.confirm`; `ReservationLocks` then status decision
- Admin complete: `POST /api/reservations/{id}/complete` — permission `rentals.reservations.complete` (not confirm)
- Admin cancel: `POST /api/reservations/{id}/cancel` — `ReservationLocks` then rentables (`OrderBy Id`) then `MarkAvailable` on linked slots
- WEB admin Concluir: `/configuracoes/reservas` when status is Confirmed **and** `can("rentals.reservations.complete")`

## Important implementation seams

- `Platform.Api/Modules/Rentals/Services/ReservationService.cs` (`ToDateTimeRange`, Confirm/Complete/Cancel lock-then-load)
- `Platform.Api/Modules/Rentals/Services/ReservationLocks.cs` / `RentalAssetLocks.cs` (`FOR UPDATE`; no-op when `!IsRelational()`)
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs` (`ToDateTime` → `BrazilTimeZone.AtLocal`; `LoadReservedWindowsAsync` civil-day bounds)
- `Platform.Api/Modules/Rentals/Services/ReservationQueueService.cs` / `ReservationQueueClock.cs` (`BrazilTimeZone`)
- `Platform.Api/Modules/Rentals/Services/OccupancyPrecedence.cs`
- `Core/Platform.Core.Domain/Entities/Reservation.cs`, `Slot.cs`, `ScheduleTemplate.cs`, `ReservationQueueSession.cs`, `ReservationQueueTicket.cs`
- `Core/Platform.Core.Domain/Constants/Permissions.cs` — `rentals.reservations.complete`
- WEB: `src/lib/brazilTimeZone.ts`, `src/features/rentals/pages/ReservationsPage.tsx`, `src/features/rentals/services/reservationsService.ts`, `src/features/tenantPortal/pages/TenantPortalAgendaPage.tsx`

## Known gaps / open constraints

From `30-rentals.mdc`: deposit payment (`DepositPaid` always 0), real SMS. Create-reservation can occupy an interval without `MarkBooked` if a persisted Slot already exists (portal prefers `slotId` when persisted). F-10b: rewrite of overlapping persisted Slot rows is out of scope.

WhatsApp (2026-09-07, API): reservation lifecycle publishes durable outbox events (`rentals.reservation.pending_deposit|confirmed|canceled|completed|reminder`) using `rental_reservation_status_update` / `rental_reservation_reminder`. Tenant WhatsApp default off. Reminder **scheduler not implemented** (`RENTALS_REMINDER_TIMING_HUMAN_GATE_REQUIRED`). Notifications must not drive Confirm/Complete/Cancel. Spec `docs/plans/active/2026-09-07-notifications-whatsapp-catalog-rentals.md`.

**Non-blocking follow-ups** (not blockers; not authorized by Wave 1 closeout):

- ListAdmin D+1 absence test
- Dedicated OpenHours wrap-guard regression
- WEB today-boundary
- Latent `AtLocal` DST midnight
- Concurrent Confirm × Confirm coverage
- Tenant predicate on raw lock SQL

**Next product work is not automatically authorized.** Remaining roadmap items (deposit provider, Goods quantity B2C, multi-item booking, agenda booked-by overlay, reminder scheduler after Human timing) stay backlog until explicitly started. B2C self-cancel is **CLOSED_DEV** (not PROD). WhatsApp PROD enablement is a separate Human Gate.

## Do not assume

- Reservation must have a required `SlotId`
- `PublishDay` is required before B2C can book a weekly grid
- Hard-coded Lesson/Open/Closed as the only occupancy kinds
- Court-only language in the module core
- Start time alone identifies a weekly template (that caused ApplyWeeklyRule 500s)
- Last-write-wins / timestamps / shorter interval as occupancy precedence
- The Inventory (Ativos) module must be entitled for Rentals to work (Rentable still needs an Asset row; that is Asset Registry, not `tenant_modules.inventory`)
- Booking clocks are browser-local (WEB formats Reservation instants in `America/Sao_Paulo`)
- Reservation JSON includes civil `date`/`startTime`/`endTime` in this phase (it does not; UTC instant only)
- Complete reuses `rentals.reservations.confirm`
- Customer portal can Complete (forbidden). Wave 1 had no B2C cancel; Customer self-cancel is `POST /api/reservations/mine/{id}/cancel` only (not staff `POST /{id}/cancel`)
- Complete and Cancel may both return success on the same Confirmed reservation (forbidden; now serialized)
- Stale Confirm may overwrite `Canceled` after occupancy release (forbidden; Confirm now lock-then-load)
- Wave 1 is still DEV-only / blocked for PROD / waiting on timestamp classification or a pending migration
- This closeout starts Wave 2, Layout, or timezone follow-up implementation
