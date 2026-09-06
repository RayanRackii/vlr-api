# Rentals Context Pack

Derived context — NOT canonical.

- Scope: Rentals beachhead (spaces/goods; club booking)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0001-rentals-slot-schedule.md`; `docs/adr/0003-reservation-waiting-queue.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `.cursor/rules/30-rentals.mdc`; spec `docs/plans/active/2026-09-05-rentals-wave1-lifecycle-integrity.md`
- Last verified: 2026-09-06
- Verified at commit(s): `vlr-api` `fix/rentals-timezone-t1`; `vlr-web` `fix/rentals-timezone-t1`

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

### Timezone (T1 implemented in DEV code)

- **Business clock:** `America/Sao_Paulo` via `BrazilTimeZone` (Windows `E. South America Standard Time`). Do not hard-code `-03:00`.
- **Civil (unchanged):** Slot `Date`/`StartTime`/`EndTime`, ScheduleTemplate, OpenHours, RentalPricing windows, CreateReservation request Date/StartTime/EndTime, day URLs, booking pickers.
- **Instant:** `Reservation.StartDateTime` / `EndDateTime` are real UTC instants (`DateTimeOffset` / `timestamptz`). Writers: `BrazilTimeZone.AtLocal`. Example: civil `2026-09-10 10:00` SP → API `2026-09-10T13:00:00Z`.
- **Civil-day queries:** inclusive `StartOfCivilDay(D)`, exclusive `ExclusiveEndOfCivilDay(D)` (`AtLocal(D+1, 00:00)`). Do not use UTC midnight or `TimeOnly.MaxValue`.
- **API JSON:** UTC instant only on Reservation DTOs. No parallel `date`/`startTime`/`endTime` fields in this phase.
- **WEB:** format reservation clocks and Rentals “today” with `timeZone: "America/Sao_Paulo"` (`src/lib/brazilTimeZone.ts`).
- **Queue:** already used `BrazilTimeZone.AtLocal`; unchanged.
- **Confirm / Cancel / Complete / CreatedAt / UpdatedAt:** do not timezone-convert.
- **Schema:** no migration. Columns were already `timestamptz`.
- **PROD:** `reservation_count = 0` → no historical backfill. Keep the single PROD Slot. Not rolled out to `main`/PROD.

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
- Customer JWT cannot Complete (no B2C Complete UI; no B2C cancel in Wave 1)
- **Complete × Cancel (human):** `Confirmed` is the source. Competing terminals, no priority. Exactly one succeeds. Serialized.
- **Confirm × Cancel:** shared source `PendingDeposit`. Confirm is not terminal. Legal: Confirm→Cancel both succeed, final `Canceled` + occupancy released. Legal: Cancel→Confirm, Confirm 409, stays `Canceled`. Invalid: stale Confirm overwrites `Canceled` after occupancy release (`Confirmed` + slot `Available`).

## Current contracts

- Public day: `GET /api/public/tenants/{subdomain}/schedule/days/{date}`
- Book persisted slot: `POST /api/schedule/slots/book` (`slotId`)
- Book derived window: `POST /api/reservations` (date + start/end + items)
- Customer list: `GET /api/reservations/mine` (Customer JWT)
- Queue (Customer): `GET/POST /api/rental-assets/{id}/queue`, `POST .../queue/join`, `POST .../queue/leave`
- Registry without Ativos (Wave 2): `POST /api/rental-assets`, `PUT /api/rental-assets/{id}`, `GET /api/rental-assets/categories|families` (`rentals.assets.*`)
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

From `30-rentals.mdc`: deposit payment (`DepositPaid` always 0), real SMS/WhatsApp. Create-reservation can occupy an interval without `MarkBooked` if a persisted Slot already exists (portal prefers `slotId` when persisted). F-10b: rewrite of overlapping persisted Slot rows is out of scope.

**Phase B T1** is implemented on `fix/rentals-timezone-t1` (not PROD). PROD classify remains **`EMPTY`** (`reservation_count = 0`, `slot_count = 1`). No Reservation backfill. Do not delete the PROD Slot. No schema migration.

**DEV permission seed:** `20260906034111_AddRentalsReservationsCompletePermission` applied on development (`PENDING_COUNT=0`, `rentals.reservations.complete` exists once). Not applied on PROD.

**Complete × Cancel:** serialized exclusive-winner on `Confirmed`.

**Confirm × Cancel:** `ConfirmAsync` uses the same reservation `FOR UPDATE` (lock then load). Dual success is legal only as Confirm→Cancel. Occupancy split (`Confirmed` + slot `Available`) is invalid.

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
- Customer portal can Complete or Cancel in Wave 1
- Complete and Cancel may both return success on the same Confirmed reservation (forbidden; now serialized)
- Stale Confirm may overwrite `Canceled` after occupancy release (forbidden; Confirm now lock-then-load)
