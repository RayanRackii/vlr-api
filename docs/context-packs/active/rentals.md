# Rentals Context Pack

Derived context — NOT canonical.

- Scope: Rentals beachhead (spaces/goods; club booking)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0001-rentals-slot-schedule.md`; `docs/adr/0003-reservation-waiting-queue.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `.cursor/rules/30-rentals.mdc`; spec `docs/plans/active/2026-09-05-rentals-wave1-lifecycle-integrity.md`
- Last verified: 2026-09-06
- Verified at commit(s): `vlr-api` `034b051` (`fix/rentals-complete-cancel-concurrency`); `vlr-web` `5359942ace3daf1f6ff72e7cbe73678e78d7ec02`

## Purpose

Load when the question is Reservation, Rentable, Slot, SlotGrid, OpenHours, schedule, pricing, booking conflicts, Layout picker, the optional Location waiting queue, or reservation Complete/Cancel.

## Canonical sources

- `CONTEXT.md` — glossary (Reservation, Slot, OccupancyKind, ScheduleTemplate, OpenHours, SlotGrid, Layout, WaitingQueue, RequiresDeposit, …)
- `docs/adr/0001-rentals-slot-schedule.md` — Slot-first schedule; OccupancyKind catalog; derived days; 2026-08-22 overlap addendum
- `docs/adr/0003-reservation-waiting-queue.md` — optional per-Location daily FIFO; T = QueueOpeningTime
- `docs/adr/0004-module-dependencies-asset-registry.md` — Rentals requires Asset Registry, not Inventory entitlement
- `.cursor/rules/30-rentals.mdc` — invariants and current gaps
- `docs/plans/active/2026-09-05-rentals-wave1-lifecycle-integrity.md` — Wave 1 Phase A (Complete + cancel locks) vs Phase B (timezone T1)

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

### Timezone split (Phase B gated)

- **Queue clock:** `ReservationQueueClock` uses `BrazilTimeZone.AtLocal` / civil date in America/Sao_Paulo.
- **Reservation writes:** `ReservationService.ToDateTimeRange` and `ScheduleService.ToDateTime` still stamp civil `DateOnly`+`TimeOnly` with `TimeSpan.Zero` (10:00 stored as `10:00+00:00`).
- **Phase B** (switch writers to `BrazilTimeZone.AtLocal`, list bounds, optional backfill) is **not started**. Requires PROD timestamp classification + a separate Human Gate. No backfill SQL in tree.

## Critical invariants

- Location: one blocking reservation per interval; Good: quantity vs `TotalQuantity`
- `RequiresDeposit` on any item → `PendingDeposit`; else `Confirmed`
- B2C login is email+password; phone is SMS/WhatsApp, not login
- Reservation customer snapshots do not follow later Customer edits
- Product UI never shows `OpenHours` / `SlotGrid` as copy
- Same OccupancyKind cannot overlap itself on a Rentable+weekday; different kinds may
- `PublishDay` gap-fills by rentable + start; does not wipe existing slots
- Create/book serialize occupancy with `RentalAssetLocks` `FOR UPDATE` on `rentals.rental_assets` ordered by `RentalAssetId` (reservation rows are not locked on create/book)
- Complete/Cancel serialize terminal transitions with `ReservationLocks` `FOR UPDATE` on `rentals.reservations` **first**. Cancel then locks distinct `RentalAssetId`s ascending via `RentalAssetLocks` before `MarkAvailable`. Complete does not lock rentables and does not free slots
- Complete is staff-only: `Confirmed → Completed`; `Completed` is idempotent 200; `PendingDeposit`/`Canceled` → 409. Does not free slots.
- Customer JWT cannot Complete (no B2C Complete UI; no B2C cancel in Wave 1)
- **Complete × Cancel (human):** `Confirmed` is the source. Complete and Cancel are competing terminals with **no priority**. Exactly one may succeed (first serialized commit). The loser must see non-`Confirmed` and fail with the existing invalid-transition contract (`InvalidOperationException` → HTTP 409). If Cancel wins: status `Canceled` and occupancy-release (`MarkAvailable`) runs. If Complete wins: status `Completed`, Cancel fails, `MarkAvailable` must **not** run. Both returning success is invalid. Serialized; `PHASE_A_CONCURRENCY_DEFECT` closed.

## Current contracts

- Public day: `GET /api/public/tenants/{subdomain}/schedule/days/{date}`
- Book persisted slot: `POST /api/schedule/slots/book` (`slotId`)
- Book derived window: `POST /api/reservations` (date + start/end + items)
- Customer list: `GET /api/reservations/mine` (Customer JWT)
- Queue (Customer): `GET/POST /api/rental-assets/{id}/queue`, `POST .../queue/join`, `POST .../queue/leave`
- Registry without Ativos (Wave 2): `POST /api/rental-assets`, `PUT /api/rental-assets/{id}`, `GET /api/rental-assets/categories|families` (`rentals.assets.*`)
- Admin day/exceptions: `GET /api/schedule/days/{date}`, `POST /api/schedule/slots/daily-occurrence`
- Admin list: `GET /api/reservations`
- Admin confirm: `POST /api/reservations/{id}/confirm` — permission `rentals.reservations.confirm`
- Admin complete: `POST /api/reservations/{id}/complete` — permission `rentals.reservations.complete` (not confirm)
- Admin cancel: `POST /api/reservations/{id}/cancel` — `ReservationLocks` then rentables (`OrderBy Id`) then `MarkAvailable` on linked slots
- WEB admin Concluir: `/configuracoes/reservas` when status is Confirmed **and** `can("rentals.reservations.complete")`

## Important implementation seams

- `Platform.Api/Modules/Rentals/Services/ReservationService.cs` (`ToDateTimeRange`, `CompleteAsync`/`CancelAsync` reservation-row then optional rentable locks)
- `Platform.Api/Modules/Rentals/Services/ReservationLocks.cs` / `RentalAssetLocks.cs` (`FOR UPDATE`; no-op when `!IsRelational()`)
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs` (`ToDateTime` still `TimeSpan.Zero`)
- `Platform.Api/Modules/Rentals/Services/ReservationQueueService.cs` / `ReservationQueueClock.cs` (`BrazilTimeZone`)
- `Platform.Api/Modules/Rentals/Services/OccupancyPrecedence.cs`
- `Core/Platform.Core.Domain/Entities/Reservation.cs`, `Slot.cs`, `ScheduleTemplate.cs`, `ReservationQueueSession.cs`, `ReservationQueueTicket.cs`
- `Core/Platform.Core.Domain/Constants/Permissions.cs` — `rentals.reservations.complete`
- WEB: `src/features/rentals/pages/ReservationsPage.tsx`, `src/features/rentals/services/reservationsService.ts`

## Known gaps / open constraints

From `30-rentals.mdc`: deposit payment (`DepositPaid` always 0), real SMS/WhatsApp. Create-reservation can occupy an interval without `MarkBooked` if a persisted Slot already exists (portal prefers `slotId` when persisted). F-10b: rewrite of overlapping persisted Slot rows is out of scope.

**Phase B timezone** is blocked. PROD timestamp classification this gate: **INSUFFICIENT_EVIDENCE** (no Dashboard/SQL Editor session to `kbptdzfbngelzdhriyhf`; inspector list does not SELECT rentals rows). No Phase B code until a conclusive read-only classify + Human Gate.

**DEV permission seed:** `20260906034111_AddRentalsReservationsCompletePermission` applied on development (`PENDING_COUNT=0`, `rentals.reservations.complete` exists once). Not applied on PROD.

**Complete × Cancel:** serialized. `CompleteAsync`/`CancelAsync` begin a relational transaction, `FOR UPDATE` the reservation row, then evaluate status. Cancel acquires `RentalAssetLocks` only after winning the reservation lock. Dual success is invalid and covered by `ReservationConcurrencyTests` (8 independent races + sequential occupancy).

## Do not assume

- Reservation must have a required `SlotId`
- `PublishDay` is required before B2C can book a weekly grid
- Hard-coded Lesson/Open/Closed as the only occupancy kinds
- Court-only language in the module core
- Start time alone identifies a weekly template (that caused ApplyWeeklyRule 500s)
- Last-write-wins / timestamps / shorter interval as occupancy precedence
- The Inventory (Ativos) module must be entitled for Rentals to work (Rentable still needs an Asset row; that is Asset Registry, not `tenant_modules.inventory`)
- Booking clocks already use `BrazilTimeZone.AtLocal` on reservation writes (they do not)
- Complete reuses `rentals.reservations.confirm`
- Customer portal can Complete or Cancel in Wave 1
- Complete and Cancel may both return success on the same Confirmed reservation (forbidden; now serialized)
