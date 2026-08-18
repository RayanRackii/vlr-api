# Rentals Context Pack

Derived context — NOT canonical.

- Scope: Rentals beachhead (spaces/goods; club booking)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/adr/0001-rentals-slot-schedule.md`; `.cursor/rules/30-rentals.mdc`
- Last verified: 2026-08-18
- Verified at commit(s): `7dcb66d` (`chore/multi-agent-foundation`)

## Purpose

Load when the question is Reservation, Rentable, Slot, SlotGrid, OpenHours, schedule, pricing, booking conflicts, or Layout picker.

## Canonical sources

- `CONTEXT.md` — glossary (Reservation, Slot, OpenHours, SlotGrid, Layout, …)
- `docs/adr/0001-rentals-slot-schedule.md` — Slot-first schedule; OccupancyKind catalog; derived days
- `.cursor/rules/30-rentals.mdc` — invariants and current gaps

## Domain vocabulary

- **Rentable** = `RentalAsset` (Location exclusive / Good with quantity)
- **Reservation** = Customer booking for a concrete time window; N `ReservationItem`s
- **Slot** = dated occupancy cell on one Rentable (kind + status)
- **OpenHours** = policy “Horário padrão”; bookable windows derived
- **SlotGrid** = policy “Grade personalizada”; unpublished days derived from weekly templates
- **Layout** = visual map of Rentables; not schedule data

## Current model

Reservation is the occupancy fact (start/end + items). Slot is the schedule cell. Link is optional: `Slot.ReservationId` when a persisted cell is booked via `BookSlot`. Derived OpenHours/SlotGrid windows book via create-reservation until a Slot row exists (`PublishDay` optional). Conflict = overlapping reservations, not “must have SlotId”.

## Critical invariants

- Location: one blocking reservation per interval; Good: quantity vs `TotalQuantity`
- `RequiresDeposit` on any item → `PendingDeposit`; else `Confirmed`
- B2C login is email+password; phone is SMS/WhatsApp, not login
- Reservation customer snapshots do not follow later Customer edits
- Product UI never shows `OpenHours` / `SlotGrid` as copy

## Current contracts

- Public day: `GET /api/public/tenants/{subdomain}/schedule/days/{date}`
- Book persisted slot: `POST /api/schedule/slots/book` (`slotId`)
- Book derived window: `POST /api/reservations` (date + start/end + items)
- Admin day/exceptions: `GET /api/schedule/days/{date}`, `POST /api/schedule/slots/daily-occurrence`

## Important implementation seams

- `Platform.Api/Modules/Rentals/Services/ReservationService.cs`
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs`
- `Core/Platform.Core.Domain/Entities/Reservation.cs`, `Slot.cs`

## Known gaps / open constraints

From `30-rentals.mdc`: deposit payment (`DepositPaid` always 0), complete-reservation, real SMS/WhatsApp. Create-reservation can occupy an interval without `MarkBooked` if a persisted Slot already exists (portal prefers `slotId` when persisted).

## Do not assume

- Reservation must have a required `SlotId`
- `PublishDay` is required before B2C can book a weekly grid
- Hard-coded Lesson/Open/Closed as the only occupancy kinds
- Court-only language in the module core
