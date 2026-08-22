# 2026-08-22-reservation-waiting-queue

Status: approved

## Goal / Problem

Optional per-Location FIFO waiting room for B2C reservation opening. Default off. When on, Customers cannot book that Location without a valid Active ticket for the **current daily opening session**. Does not replace F-01 row locks.

## Visible behavior

- Queue off: booking identical to today.
- Queue on: Location requires `QueueOpeningTime` (e.g. 07:30). Waiting room opens T−30 min (07:00). At T the first Waiting ticket becomes Active (90s). During Active the Customer picks any currently bookable slot on that Location and completes one reservation. Timeout/rejoin-at-end. Reconnect keeps the same ticket and remaining turn time.

## Repositories

- vlr-api
- vlr-web

## Branch (both)

`feat/reservation-waiting-queue`

## Merge

API first (additive `queueEnabled` default false). Then web. Fable Merge Risk Gate **required**.

## Relevant existing ADR / rules

- `docs/adr/0001-rentals-slot-schedule.md`
- `.cursor/rules/30-rentals.mdc`
- F-01: `RentalAssetLocks` `SELECT … FOR UPDATE`
- Human gate 2026-08-22: T is **daily Location opening**, not per-slot (`BOOKING_RELEASE_RULE_REQUIRED` resolved, Option 1)

## Architecture route

- rolvix-architect (blocked on T, then resumed)
- rolvix-deep-architect: Merge Risk Gate only (after implementation)

## Execution route

- api-implementer
- web-implementer (not ui-implementer)

## Confirmed decisions

- Optional per Location (`RentalAsset.Type == Location`). Not tenant-global. Not Goods.
- `QueueEnabled` default false. When true, `QueueOpeningTime` (`TimeOnly`) required.
- Timezone: `America/Sao_Paulo` (reuse Hangfire Brazil TZ helper; Windows fallback `E. South America Standard Time`). No tenant timezone.
- Waiting room = T − 30 minutes (constant). Turn = 90 seconds (constant). `activeCapacity = 1`.
- QueueSession key: `(TenantId, RentalAssetId, OpeningDate)` where OpeningDate is the **civil date of T** in America/Sao_Paulo. **Not** per slot.
- Current session: the unique OpeningDate D such that `WaitingRoomOpensAt(D) <= now < WaitingRoomOpensAt(D+1)`.
- Before waiting room of the would-be next session: join refused; booking refused (`QUEUE_REQUIRED`).
- Waiting room open and `now < T`: join → Waiting; booking → `QUEUE_WAITING` (or `QUEUE_REQUIRED` if no ticket).
- At/after T: promote first Waiting → Active. Empty queue after T: join may promote immediately to Active.
- FIFO by persisted `Sequence` assigned under F-01 lock (`MAX(sequence)+1`). Never client timestamps.
- Max one Waiting/Active ticket per Customer per session. Join idempotent.
- Active may view/change slot selection; failed booking validation does not consume the ticket while `TurnExpiresAt > now`. Success → Completed + promote next. Second reservation same pass → `QUEUE_TURN_ALREADY_USED`.
- Timeout: Active → Expired, promote next. Rejoin → new Waiting at end.
- Reconnect: same ticket; remaining time; never restart 90s.
- B2B/admin never queued (Customer-policy booking endpoints only).
- Polling only. No WebSocket/SignalR/Redis/Hangfire sweeper in MVP.
- Error `{ "error": "<CODE>" }` with exact codes: `QUEUE_REQUIRED`, `QUEUE_WAITING`, `QUEUE_TURN_EXPIRED`, `QUEUE_TURN_ALREADY_USED`. Additional join-closed code: `QUEUE_WAITING_ROOM_CLOSED` (400).
- No booking horizon in this feature.

## Invariants that must not break

- F-01 `FOR UPDATE` on `rentals.rental_assets` remains on BookSlot and CreateReservation.
- Location exclusive / Good quantity rules unchanged.
- QueueEnabled=false path: no extra failure modes; existing concurrency tests still pass.
- Tenant isolation via `ITenantScoped` + JWT CustomerId only.
- Do not log JWT, names, emails of other queue members.

## Implementation scope

### Session clock

Extract Brazil TZ to a shared helper used by Hangfire and the queue (do not depend on Hangfire from Rentals). Use `TimeProvider` (register `TimeProvider.System`) so tests freeze time.

`OpensAt(D) = D + QueueOpeningTime` in Sao Paulo, stored as `DateTimeOffset` UTC.
`WaitingRoomOpensAt(D) = OpensAt(D) − 30 minutes` (may be previous civil day).

### Persistence (schema `rentals`, additive)

`RentalAsset`:
- `QueueEnabled` bool not null default false
- `QueueOpeningTime` time null

Goods: `QueueEnabled` must stay false; enabling on Good → 400.

`ReservationQueueSession` (`ITenantScoped`):
- Id, TenantId, RentalAssetId, OpeningDate (date), OpensAt, WaitingRoomOpensAt
- Unique `(tenant_id, rental_asset_id, opening_date)`
- FK rental_assets cascade

`ReservationQueueTicket` (`ITenantScoped`):
- Id, TenantId, QueueSessionId, CustomerId, Sequence (bigint), Status, JoinedAt, TurnStartedAt, TurnExpiresAt, CompletedReservationId
- Status varchar: Waiting, Active, Completed, Expired, Cancelled
- Unique `(queue_session_id, sequence)`
- Partial unique `(queue_session_id, customer_id) WHERE status IN ('Waiting','Active')`
- Index `(queue_session_id, sequence)` for FIFO
- FK customers + sessions cascade

Lazy-create session on first join/status/booking that needs it.

### Promotion (inside F-01 lock; no 90s transaction)

1. Lock rental_asset FOR UPDATE.
2. Expire Active if `TurnExpiresAt <= now`.
3. If `now >= OpensAt` and no Active: promote lowest Waiting sequence → Active, set TurnStartedAt=now, TurnExpiresAt=now+90s.
4. Return.

GET status, join, leave, book all run this advancement.

### Booking enforcement

Shared `IReservationQueueService.EnsureActiveTurnForBookingAsync(customerId, rentalAsset, ct)` **after** the existing lock, **before** creating Reservation.

- Type != Location or `QueueEnabled == false` → no-op.
- Else require current session Active ticket for this Customer with `TurnExpiresAt > now`.
- Throw `InvalidOperationException` whose **Message is exactly** the code (controllers already map IOE → 409 `{ error }`).
- On successful reservation: mark ticket Completed, set CompletedReservationId, promote next (still in same transaction).

Apply in:
- `ScheduleService.BookSlotAsync`
- `ReservationService.CreateReservationAsync` for **each** queued Location item (fail whole reservation)

Availability GET stays public and unqueued (display only).

### API

Admin (existing `[Authorize]` asset write path — not a new complex panel):
- Add `queueEnabled` + `queueOpeningTime` to Create/Update/BulkCreate asset DTOs and `AssetRentalConfigResponse`.
- Add same fields to `RentalAssetResponse` (public catalog + admin list) so B2C can feature-detect.
- Validate: Location + queueEnabled true → opening time required. Good + queueEnabled true → reject. Opening time ignored/cleared when queue off.

Customer (`Authorize(Policy = "Customer")`, customerId from JWT only):

`GET /api/rental-assets/{rentalAssetId}/queue`

```json
{
  "rentalAssetId": "...",
  "queueEnabled": true,
  "openingDate": "2026-08-22",
  "opensAt": "2026-08-22T10:30:00Z",
  "waitingRoomOpensAt": "2026-08-22T10:00:00Z",
  "serverNow": "2026-08-22T10:05:00Z",
  "phase": "WaitingRoom" | "Open" | "Closed",
  "waitingCount": 3,
  "aheadCount": 2,
  "myTicket": {
    "id": "...",
    "status": "Waiting",
    "sequence": 4,
    "position": 3,
    "joinedAt": "...",
    "turnStartedAt": null,
    "turnExpiresAt": null,
    "completedReservationId": null
  }
}
```

`phase`:
- Closed: now < current-or-next waiting room (cannot join)
- WaitingRoom: waiting room ≤ now < T
- Open: now ≥ T (until next waiting room)

Never return other customers' identity.

`POST /api/rental-assets/{rentalAssetId}/queue/join` — idempotent; 400 `QUEUE_WAITING_ROOM_CLOSED` if phase Closed; 404 if not Location or queue off.

`POST /api/rental-assets/{rentalAssetId}/queue/leave` — Cancelled; if was Active, promote next.

Controller: ArgumentException 400, InvalidOperationException 409, KeyNotFound 404, Unauthorized 401.

### Logging

Structured ILogger events: join, activate, expire, complete, rejoin, booking rejection (code + rentalAssetId + sessionId + customerId guid, **no** email/name/JWT).

### Frontend B2C (`TenantPortalAgendaPage`)

If selected Location `queueEnabled !== true`: unchanged.

If enabled:
- Poll GET queue every 4s while page visible; pause on `document.hidden`; resume on focus; clear on unmount.
- Closed: copy “fila abre / reservas abrem às HH:mm” (from waitingRoomOpensAt / opensAt). Disable reserve.
- WaitingRoom without ticket: “Entrar na fila”.
- Waiting: “Você está na fila / Sua posição: N” (+ aheadCount). Disable reserve.
- Active: enable normal slot pick; show “Sua vez” + countdown from `turnExpiresAt` vs `serverNow` (display only). Reserve uses existing book/create endpoints.
- Completed: existing success path.
- Expired: leave selection UI; “Seu tempo terminou” + “Entrar novamente na fila”.
- Reconnect: GET restores ticket; countdown uses remaining `turnExpiresAt`.
- Map 409 `error` codes to states. `parseApiError` already returns the `error` string — compare to codes before toasting generic text.

### Frontend B2B

Asset wizard Operação, Location only, next to `requiresDeposit`:
- Toggle fila (default off)
- Time input HH:mm required when on
- Hide for Good
- Persist via existing asset create/update (`queueEnabled`, `queueOpeningTime`)
- i18n pt-BR / en / es
- Zod on asset + portal rental-asset schemas (`queueEnabled` default false)

### Tests (API)

InMemory / service tests OK for clock/phase/idempotent join/codes.

**Testcontainers (`DockerFact` + `PostgresContainerFixture`) required** for:
- 10 concurrent joins → 10 unique sequences, deterministic order
- 2 concurrent promotions → exactly one Active
- same Customer two concurrent joins → one ticket
- Active timeout → exactly the next Waiting becomes Active
- direct BookSlot / CreateReservation bypass without Active → QUEUE_*
- QueueEnabled=false regression (existing ReservationConcurrencyTests still pass; add explicit queue-off create still works)
- tenant isolation (ticket of A invisible to B)

Also: waiting room closed cannot join; Open+Lesson-style not relevant; Good cannot enable queue; Active books once; Expired cannot book; rejoin after Expired is new higher sequence; reconnect returns same ticket id.

Use `FakeTimeProvider` (or TimeProvider subclass) — do not sleep 90s.

### Documentation

- CONTEXT.md glossary (canonical) + vlr-web mirror: WaitingQueue, QueueSession, QueueTicket, QueueOpeningTime, Turn
- ADR `docs/adr/0003-reservation-waiting-queue.md`
- ROADMAP both repos
- rentals context pack after canonical

## Likely affected areas / files

API: `RentalAsset`, Asset DTOs/service, `RentalAssetResponse`, new entities/configs/migration, `ReservationQueueService`, inject into ReservationService + ScheduleService, new controller actions on `RentalAssetsController` (Customer policy on those actions only), Hangfire TZ extract, Program DI.

Web: `AssetWizard.tsx`, `assetSchemas.ts`, `tenantPortalService.ts`, `TenantPortalAgendaPage.tsx`, locales.

## Do not

- F-04, F-10b, main, PROD, apply PROD migrations
- WebSocket/Redis/Hangfire sweeper
- Configurable 90s/30min
- Multi-active, points, raffle, Goods queue, tenant-global queue
- Booking horizon
- Per-slot QueueSession
- Generic queue platform

## Product-level how to test

1. Location queue off: portal reserve as today.
2. Enable fila 07:30 on a Location. Before 07:00 SP: cannot join. 07:00–07:30: join, cannot book. After 07:30: first Active, books one slot in 90s.
3. Second customer sees position 2; after first completes or times out, they become Active.
4. Refresh / other device: same position / remaining seconds.
5. Direct POST book without ticket → 409 QUEUE_REQUIRED.
6. Two browsers, two customers: unique positions; only one Active.
