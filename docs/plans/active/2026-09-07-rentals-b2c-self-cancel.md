# 2026-09-07-rentals-b2c-self-cancel

Status: approved (human decisions locked — do not reopen)

## Goal / Problem

A logged-in B2C Customer can self-cancel their **own** eligible reservation from the branded portal. Eligible = status `PendingDeposit` or `Confirmed` **and** the true instant `now` is strictly before `reservation.StartDateTime`. On success: status becomes `Canceled`, linked `Slot`s are marked `Available`, occupancy (Location exclusivity and Good quantity) is freed, and the operation is concurrency-safe under the existing row-lock protocol. Staff and customer views refresh. No B2C `Complete`. No cancel of another Customer's reservation.

This is the next product slice after Wave 1 (which remains `PROD_COMPLETE`).

## Visible behavior

- B2C portal "Minhas reservas" shows a **Cancelar** CTA on rows whose `status` is `PendingDeposit` or `Confirmed` **and** `startDateTime` is strictly in the future (true instant).
- Clicking Cancel calls the Customer endpoint; on success a toast is shown, the list is re-fetched, and the schedule day is re-fetched (so slot availability updates).
- On `409` (started or completed) / `404` (not owner) / `403` (module off) / `401` (unauthenticated), an error toast with the real API message is shown; the user stays on the page; the list is re-fetched.
- Staff `POST /api/reservations/{id}/cancel` is unchanged: staff may still cancel after start (no cutoff), gated by `rentals.reservations.cancel`.
- Queue ticket is **not** restored on cancel (current behavior preserved).

## Repositories

- vlr-api
- vlr-web

## Branch (both)

`feat/rentals-b2c-self-cancel`

## Merge order

API first. WEB may merge after the API endpoint exists on `develop`.

## Relevant existing ADR / rules

- `.cursor/rules/30-rentals.mdc`
- `.cursor/rules/10-arquitetura.mdc`
- `.cursor/rules/20-convencoes.mdc`
- `docs/adr/0001-rentals-slot-schedule.md`
- `docs/adr/0003-reservation-waiting-queue.md`
- `docs/plans/active/2026-09-05-rentals-wave1-lifecycle-integrity.md` (Wave 1 remains PROD_COMPLETE)

## Architecture route

- rolvix-architect (this spec)
- rolvix-deep-architect: Fable Merge Risk Gate **required** on the implementation PR (auth + tenant + concurrency). Parent runs it at merge. Do not call Fable during implementation.

## Execution route

- api-implementer (vlr-api)
- web-implementer (vlr-web; not ui-implementer)

## Confirmed decisions

| # | Decision |
|---|---|
| 1 | Distinct Customer endpoint. Do not share staff `POST /api/reservations/{id}/cancel`. |
| 2 | Path: `POST /api/reservations/mine/{id}/cancel`. Policy `Customer`. Inherits class `[RequireActiveModule(Rentals)]`. |
| 3 | Ownership mismatch / missing → 404 `{ "error": "Reservation not found." }` (Catalog pattern; no leak). |
| 4 | New `CancelByCustomerAsync(customerId, reservationId, ct)`. Extract shared occupancy-release tail used by staff `CancelAsync` and Customer. Staff path unchanged (no cutoff). |
| 5 | Cutoff after row lock: `DateTimeOffset.UtcNow >= reservation.StartDateTime` → 409 `"Cannot cancel a reservation that has already started."` |
| 6 | Staff cancel after start remains allowed. |
| 7 | Queue: no restore on cancel. Ticket stays `Completed`. |
| 8 | Owner + already `Canceled` → 200 idempotent. Other customer still 404. |
| 9 | No migration. If required → `B2C_SELF_CANCEL_MIGRATION_GATE_REQUIRED` and stop. |
| 10 | WEB: API-authoritative CTA; no confirm dialog (match staff); no Complete. Clocks via `brazilTimeZone`. |

Locked Human decisions: statuses `PendingDeposit` + `Confirmed`; cutoff before start; PendingDeposit does not need staff; timezone `America/Sao_Paulo`.

## Invariants that must not break

1. Complete × Cancel exclusive-winner on `Confirmed` (lock reservation first).
2. Cancel then `RentalAsset` locks ascending; `MarkAvailable` on linked Slots.
3. Tenant GQF; cross-tenant id → 404.
4. No B2C Complete.
5. Customer cutoff is Customer-only.
6. Errors `{ "error": string }`.
7. No Wave 1 reopen; no schema change.

## Implementation scope

### vlr-api

`CancelByCustomerAsync`:

1. `EnsureTenantContext` + `trialGuard.EnsureWritableAsync`.
2. Transaction (same as staff Cancel).
3. `ReservationLocks.LockByReservationIdAsync` first.
4. Load with items. Null or `CustomerId != customerId` → `KeyNotFoundException("Reservation not found.")`.
5. `Canceled` → commit + return (idempotent).
6. `Completed` → `InvalidOperationException("Cannot cancel a completed reservation.")`.
7. `DateTimeOffset.UtcNow >= StartDateTime` → `InvalidOperationException("Cannot cancel a reservation that has already started.")`.
8. Shared `ReleaseOccupancyAsync`: distinct rental asset ids ascending, lock each, set `Canceled`, `Touch`, `MarkAvailable` linked slots, save, commit.

Controller:

```
[Authorize(Policy = "Customer")]
[HttpPost("mine/{id:guid}/cancel")]
```

Map KeyNotFound → 404, InvalidOperation → 409, UnauthorizedAccess → 401.

### vlr-web

- `cancelMyPortalReservation(id)` via `customerApi.post(/api/reservations/mine/${id}/cancel)` + Zod + `parseApiError`.
- Agenda "Minhas reservas": Cancel CTA when status is PendingDeposit|Confirmed and start is in the future (`Date.parse` of API instant vs `Date.now()`). UX only; API is authority.
- Per-row busy flag. Success toast + refetch mine + schedule day. 409/404: toast real message, stay, refetch mine.
- i18n `tenantPortal.agenda.cancel` / `cancelSuccess`. Reuse `apiErrors.cancelReservation`.
- No Complete. No staff page change.

## Test seams

### API

Positive: owner PendingDeposit future; owner Confirmed future; status Canceled; Location slot Available; Good `GetReservedQuantityAsync` 0.

Auth: other Customer 404; unauthenticated 401; module off 403.

Cutoff: future succeeds; `StartDateTime` in the past (including equality/`>=`) 409.

Status: Completed 409; Canceled idempotent 200.

Concurrency (DockerFact, reuse Wave 1 patterns): Customer Cancel × Staff Cancel; Customer Cancel × Complete; Customer Cancel × Confirm (PendingDeposit). Occupancy consistent. Staff Cancel after start still succeeds.

Queue: after cancel, ticket remains Completed if one existed.

Do not remove existing Wave 1 tests.

### WEB (Vitest)

CTA visible PendingDeposit/Confirmed future; hidden Completed/Canceled/started; success updates list; 409 toast + refetch; no Complete.

### E2E (Playwright exists — parent correction)

Do **not** claim `TEST_INFRASTRUCTURE_MISSING`. WEB has `e2e/` against Railway DEV + develop Preview.

Required: Customer sees own reservation → cancels → UI Canceled → occupancy bookable again. Ownership of another Customer is API-proven (single E2E Customer identity — do not invent a second browser session unless secrets already provide one). No flaky browser races. DEV only.

## Do not

- Cutoff on staff Cancel
- Change Confirm/Complete unless proven defect
- Restore queue ticket
- B2C Complete
- Share staff cancel route
- 403 for not-owner
- TimeProvider injection in this slice
- Tenant timezone / ±3h
- Confirm dialog (this slice)
- Migration / PROD / refunds / fees / reason / notifications / Queue redesign / Layout / Catalog

## Recommended Customer route

`POST /api/reservations/mine/{id}/cancel`
