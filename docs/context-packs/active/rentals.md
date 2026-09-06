# Rentals Context Pack

Derived context — NOT canonical.

- Scope: Rentals beachhead (spaces/goods; club booking)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0001-rentals-slot-schedule.md`; `docs/adr/0003-reservation-waiting-queue.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `.cursor/rules/30-rentals.mdc`; spec `docs/plans/active/2026-09-05-rentals-wave1-lifecycle-integrity.md`
- Last verified: 2026-09-06

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
- Create/book/cancel serialize occupancy with `RentalAssetLocks` `FOR UPDATE` on `rentals.rental_assets` ordered by `RentalAssetId`
- Complete is staff-only: `Confirmed → Completed`; `Completed` is idempotent 200; `PendingDeposit`/`Canceled` → 409. Does not free slots.
- Customer JWT cannot Complete (no B2C Complete UI; no B2C cancel in Wave 1)

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
- Admin cancel: `POST /api/reservations/{id}/cancel` — locks rentables then `MarkAvailable` on linked slots
- WEB admin Concluir: `/configuracoes/reservas` when status is Confirmed **and** `can("rentals.reservations.complete")`

## Important implementation seams

- `Platform.Api/Modules/Rentals/Services/ReservationService.cs` (`ToDateTimeRange`, `CompleteAsync`, `CancelAsync` locks)
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs` (`ToDateTime` still `TimeSpan.Zero`)
- `Platform.Api/Modules/Rentals/Services/ReservationQueueService.cs` / `ReservationQueueClock.cs` (`BrazilTimeZone`)
- `Platform.Api/Modules/Rentals/Services/OccupancyPrecedence.cs`
- `Core/Platform.Core.Domain/Entities/Reservation.cs`, `Slot.cs`, `ScheduleTemplate.cs`, `ReservationQueueSession.cs`, `ReservationQueueTicket.cs`
- `Core/Platform.Core.Domain/Constants/Permissions.cs` — `rentals.reservations.complete`
- WEB: `src/features/rentals/pages/ReservationsPage.tsx`, `src/features/rentals/services/reservationsService.ts`

## Known gaps / open constraints

From `30-rentals.mdc`: deposit payment (`DepositPaid` always 0), real SMS/WhatsApp. Create-reservation can occupy an interval without `MarkBooked` if a persisted Slot already exists (portal prefers `slotId` when persisted). F-10b: rewrite of overlapping persisted Slot rows is out of scope.

**Phase B timezone** is blocked until PROD reservation timestamps are classified read-only (`EMPTY` / `CIVIL_MISLABELED_AS_UTC` / `ALREADY_BRAZIL_INSTANT` / `MIXED_OR_OTHER` / `INSUFFICIENT_EVIDENCE`). No Phase B code until that Human Gate.

**Complete × Cancel:** Complete does not lock the reservation row; Cancel locks rentables then writes status. Concurrent Complete+Cancel can both return success while persistence last-write-wins (observed: Canceled + slot Available). Occupancy stayed consistent in sampled runs; dedicated fix (serialize status transitions) is a follow-up, not Phase B.

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
