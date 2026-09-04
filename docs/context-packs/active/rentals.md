# Rentals Context Pack

Derived context — NOT canonical.

- Scope: Rentals beachhead (spaces/goods; club booking)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0001-rentals-slot-schedule.md`; `docs/adr/0003-reservation-waiting-queue.md`; `docs/adr/0004-module-dependencies-asset-registry.md`; `.cursor/rules/30-rentals.mdc`
- Last verified: 2026-09-04

## Purpose

Load when the question is Reservation, Rentable, Slot, SlotGrid, OpenHours, schedule, pricing, booking conflicts, Layout picker, or the optional Location waiting queue.

## Canonical sources

- `CONTEXT.md` — glossary (Reservation, Slot, OccupancyKind, ScheduleTemplate, OpenHours, SlotGrid, Layout, WaitingQueue, …)
- `docs/adr/0001-rentals-slot-schedule.md` — Slot-first schedule; OccupancyKind catalog; derived days; 2026-08-22 overlap addendum
- `docs/adr/0003-reservation-waiting-queue.md` — optional per-Location daily FIFO; T = QueueOpeningTime
- `docs/adr/0004-module-dependencies-asset-registry.md` — Rentals requires Asset Registry, not Inventory entitlement
- `.cursor/rules/30-rentals.mdc` — invariants and current gaps

## Domain vocabulary

- **Rentable** = `RentalAsset` (Location exclusive / Good with quantity)
- **Reservation** = Customer booking for a concrete time window; N `ReservationItem`s
- **Slot** = dated occupancy cell on one Rentable (kind + status)
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

Reservation is the occupancy fact (start/end + items). Slot is the schedule cell. Link is optional: `Slot.ReservationId` when a persisted cell is booked via `BookSlot`. Derived OpenHours/SlotGrid windows book via create-reservation until a Slot row exists (`PublishDay` optional). Conflict = overlapping reservations, not “must have SlotId”. Weekly apply matches `(RentalAssetId, DayOfWeek, StartTime, EndTime, OccupancyKindId)`.

## Critical invariants

- Location: one blocking reservation per interval; Good: quantity vs `TotalQuantity`
- `RequiresDeposit` on any item → `PendingDeposit`; else `Confirmed`
- B2C login is email+password; phone is SMS/WhatsApp, not login
- Reservation customer snapshots do not follow later Customer edits
- Product UI never shows `OpenHours` / `SlotGrid` as copy
- Same OccupancyKind cannot overlap itself on a Rentable+weekday; different kinds may
- `PublishDay` gap-fills by rentable + start; does not wipe existing slots

## Current contracts

- Public day: `GET /api/public/tenants/{subdomain}/schedule/days/{date}`
- Book persisted slot: `POST /api/schedule/slots/book` (`slotId`)
- Book derived window: `POST /api/reservations` (date + start/end + items)
- Queue (Customer): `GET/POST /api/rental-assets/{id}/queue`, `POST .../queue/join`, `POST .../queue/leave`
- Registry without Ativos (Wave 2): `POST /api/rental-assets`, `PUT /api/rental-assets/{id}`, `GET /api/rental-assets/categories|families` (`rentals.assets.*`)
- Admin day/exceptions: `GET /api/schedule/days/{date}`, `POST /api/schedule/slots/daily-occurrence`

## Important implementation seams

- `Platform.Api/Modules/Rentals/Services/ReservationService.cs`
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs`
- `Platform.Api/Modules/Rentals/Services/ReservationQueueService.cs`
- `Platform.Api/Modules/Rentals/Services/OccupancyPrecedence.cs`
- `Core/Platform.Core.Domain/Entities/Reservation.cs`, `Slot.cs`, `ScheduleTemplate.cs`, `ReservationQueueSession.cs`, `ReservationQueueTicket.cs`

## Known gaps / open constraints

From `30-rentals.mdc`: deposit payment (`DepositPaid` always 0), complete-reservation, real SMS/WhatsApp. Create-reservation can occupy an interval without `MarkBooked` if a persisted Slot already exists (portal prefers `slotId` when persisted). F-10b: rewrite of overlapping persisted Slot rows is out of scope.

## Do not assume

- Reservation must have a required `SlotId`
- `PublishDay` is required before B2C can book a weekly grid
- Hard-coded Lesson/Open/Closed as the only occupancy kinds
- Court-only language in the module core
- Start time alone identifies a weekly template (that caused ApplyWeeklyRule 500s)
- Last-write-wins / timestamps / shorter interval as occupancy precedence
- The Inventory (Ativos) module must be entitled for Rentals to work (Rentable still needs an Asset row; that is Asset Registry, not `tenant_modules.inventory`)
- The Inventory (Ativos) module must be entitled for Rentals to work (Rentable still needs an Asset row; that is Asset Registry, not `tenant_modules.inventory`)
