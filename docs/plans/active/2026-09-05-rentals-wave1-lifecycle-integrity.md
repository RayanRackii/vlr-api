# 2026-09-05-rentals-wave1-lifecycle-integrity

Status: **PROD_COMPLETE** (2026-09-06). Historical spec — do not implement from this file. This closeout does not authorize Wave 2, Layout, or timezone follow-ups.

Shipped: API PROD `54b385d5d14d0438fceb0c358872cf7ef1e1f589`; WEB PROD `0d995955dd56338cc8cbfda6bf8ff6950afb68f6`. Complete permission migration applied (`PENDING_COUNT=0`). T1 live. `PROD_TIMESTAMP_STATE=EMPTY` (no Reservation backfill). Public/read-only smoke passed. No customer write smoke.

## Goal / Problem

Club beachhead Wave 1 from the Rentals Slots/Agenda audit: booking clocks must be **America/Sao_Paulo**; staff must be able to mark a reservation **Completed** under a dedicated permission; cancel must lock occupancy before freeing slots. Customer self-cancel is **out of scope** (Wave 2).

Today `Reservation.StartDateTime`/`EndDateTime` are built with `TimeSpan.Zero` on civil `DateOnly`+`TimeOnly` (`ReservationService.ToDateTimeRange`, `ScheduleService.ToDateTime`). A 10:00 slot is stored as `10:00+00:00`. Queue already uses `BrazilTimeZone`. Admin/B2C lists that call `toLocaleString()` in Brazil can show **07:00**.

## Visible behavior

- New bookings (create-reservation and book-slot) store instants that correspond to civil time in America/Sao_Paulo (`BrazilTimeZone.AtLocal` already exists).
- Admin date filters (`GET /api/reservations?from=&to=`) interpret `from`/`to` as Brazil civil dates, not UTC calendar labels.
- Staff with `rentals.reservations.complete` see **Concluir** on `Confirmed` rows; `POST /api/reservations/{id}/complete` moves `Confirmed → Completed`. Idempotent on `Completed`. Rejects `Canceled` and `PendingDeposit` (confirm first).
- Confirm stays `PendingDeposit → Confirmed` and **keeps** `rentals.reservations.confirm`. Complete does **not** reuse confirm.
- Customers have no complete (or cancel) route in this wave.
- Cancel still frees linked slots, under `RentalAssetLocks` (same lock order as create/book).

## Repositories

- vlr-api
- vlr-web

## Branch (both)

`feat/rentals-wave1-lifecycle-integrity`

## Merge

API first (permission seed + TZ helper usage + Complete + cancel lock). Then WEB (Concluir + i18n + permission label). Coordinated: Complete UI must not ship before the API permission/endpoint exist.

## Relevant existing ADR / rules

- `docs/adr/0001-rentals-slot-schedule.md`
- `docs/adr/0003-reservation-waiting-queue.md` (queue clock is already America/Sao_Paulo)
- `.cursor/rules/30-rentals.mdc`
- `Core/Platform.Core.Infrastructure/Time/BrazilTimeZone.cs`
- Audit: Rentals Slots/Agenda 2026-09-05; architect GLM (`rolvix-architect`)
- Human decisions 2026-09-05: **T1**, staff-only Complete + new permission, B2C cancel deferred

## Architecture route

- rolvix-architect (audit)
- rolvix-deep-architect: **not** for this spec authoring. **Fable Merge Risk Gate required** on the implementation PR (see below)

## Execution route

- api-implementer
- web-implementer (not ui-implementer; this is permission + one staff action, not a visual redesign)

## Confirmed decisions

1. **TIMEZONE T1.** Booking clocks are America/Sao_Paulo. Reuse `BrazilTimeZone.AtLocal` / `Resolve()`. No per-tenant `TimeZoneId` in this wave.
2. **No blind backfill.** Classify existing rows first. DEV audit (2026-09-05, ref `jzptnjyzijklutinpxag`): **0 reservations, 0 slots**. Nothing to correct on DEV.
3. **PROD timestamps:** not queried in this spec task (no safe read-only PROD SQL in-session). **PROD_TIMESTAMP_STATE: NOT_VERIFIED.** Any PROD SELECT beyond this, and **any** PROD UPDATE/backfill, needs a **separate Human Gate**. Do not deploy the TZ write-path to Railway production until PROD row count/classification is known (see gates).
4. **COMPLETE.** Staff-only. New key `rentals.reservations.complete`. Do not reuse `rentals.reservations.confirm`. Customer cannot mark Completed.
5. **B2C_CANCEL_LAUNCH_REQUIRED: NO.** Defer Customer self-cancel to Wave 2. Do not add a Customer cancel route in this branch.

## Timestamp audit (read-only, this spec)

| Env | Method | Reservations | Slots | Classification |
|---|---|---|---|---|
| DEV | live `SELECT` on `rentals.reservations` / `rentals.slots` | 0 | 0 | **no historical rows** |
| PROD | not queried | unknown | unknown | **NOT_VERIFIED** |

**Update 2026-09-06:** PROD classified **`EMPTY`** (`reservation_count=0`; single Slot kept). No Reservation backfill. T1 shipped. See header.

Classification rules for when rows exist (do not apply now):

- Join `slots.reservation_id = reservations.id`.
- `CIVIL_MISLABELED_AS_UTC`: `(start_date_time AT TIME ZONE 'UTC')::time = slot.start_time` and UTC date = `slot.date`.
- `ALREADY_BRAZIL_CIVIL`: `(start_date_time AT TIME ZONE 'America/Sao_Paulo')::time = slot.start_time` and Brazil date = `slot.date`.
- `OTHER`: inspect before any rewrite (create-reservation path may have no Slot).

If PROD is empty: TZ code deploy is safe (same as DEV).  
If PROD is `CIVIL_MISLABELED_AS_UTC`: **stop**; Human Gate for cutover (backfill-then-deploy vs freeze writes).  
If PROD is `ALREADY_BRAZIL_CIVIL`: code-only TZ alignment; no rewrite.  
If mix/`OTHER`: **stop**; Human Gate.

## Invariants that must not break

- F-01 `FOR UPDATE` on `rentals.rental_assets` for create, book-slot, queue, **and cancel**.
- Location exclusive / Good quantity unchanged.
- Slot remains civil `DateOnly`+`TimeOnly`. Do not add TZ columns to `slots` in this wave.
- `ReservationStatus` spelling `Canceled` unchanged.
- Tenant GQF + `RequireActiveModule(rentals)` on Rentals controllers.
- Queue `BrazilTimeZone` behavior unchanged (already correct).
- Confirm permission/endpoint unchanged.
- No Customer complete/cancel endpoints.
- Do not change pricing resolution (Wave 2).
- Do not change OpenHours/SlotGrid derivation rules.

## Implementation scope

### Phase A — Complete + cancel lock (no TZ, no backfill)

**API**

- `Permissions.Rentals.ReservationsComplete = "rentals.reservations.complete"`.
- Add to `PermissionCatalog.All` (bump catalog tests from **43 → 44**). Do **not** add to `DefaultUserKeys` or `TechnicianLegacyKeys`.
- `TenantAccessBootstrapper.SeedMissingPermissionsAsync` already inserts missing catalog keys; `GrantKeys(admin/superAdmin, AllKeys)` grants Complete to system Admin / SuperAdmin on next `EnsureAsync`. Custom roles stay without it until an admin assigns it.
- EF migration: seed `core.permissions` row for the new key (idempotent insert). Do **not** rewrite historical role grants beyond what bootstrap already does. Follow runbook: **do not** `dotnet ef database update` from the laptop against DEV/PROD; apply via `database-migrations` workflow after Human Gate for apply.
- `POST /api/reservations/{id}/complete` + `[RequirePermission(Permissions.Rentals.ReservationsComplete)]`.
- `CompleteAsync`: load reservation; `PendingDeposit`/`Canceled` → 409; `Completed` → 200 unchanged; `Confirmed` → `Completed`. Does not alter slots (booked stays booked until cancel).
- `CancelAsync`: lock each distinct `RentalAssetId` on the reservation (`OrderBy` id) via `RentalAssetLocks` **before** `MarkAvailable`. Keep current status rules.

**WEB**

- `completeAdminReservation` → `POST /api/reservations/{id}/complete`.
- `ReservationsPage`: Concluir only when `row.status === "Confirmed"` **and** `can("rentals.reservations.complete")`. Confirm remains `PendingDeposit`.
- i18n pt-BR/en/es: action + toasts + `permissions.rentals.reservations.complete` label (roles UI).
- Validity/permission: disable while submitting; trial read-only already disables mutations.

### Phase B — Timezone T1 (after DEV empty proof; PROD gate before production deploy)

Replace every civil→`DateTimeOffset` reservation conversion with `BrazilTimeZone.AtLocal`:

- `ReservationService.ToDateTimeRange`
- `ReservationService.ListAdminAsync` from/to bounds (`MinValue`/`MaxValue` of the Brazil civil day)
- `ScheduleService.ToDateTime` used when creating reservations from slots

Overlap and availability already compare `DateTimeOffset` instants; after T1 they compare real instants. Mixed old/new encodings on the same DB **must not** go to PROD.

WEB: do not invent a second TZ. After T1, ISO instants with real UTC offset display as Brazil civil in `America/Sao_Paulo` browsers. Do not wrap reservation times in extra `+3h`. Document that staff browsers outside Brazil will show local wall time of the instant (correct).

**No SQL UPDATE in Phase B code.** If a future Human Gate approves a backfill, that is a **separate** migration/script and PR.

## Likely affected areas / files

**vlr-api**

- `Core/Platform.Core.Domain/Constants/Permissions.cs`
- `Core/Platform.Core.Domain/Constants/PermissionCatalog.cs`
- `Platform.Api/Modules/Rentals/Controllers/ReservationsController.cs`
- `Platform.Api/Modules/Rentals/Services/ReservationService.cs`
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs`
- `Core/Platform.Core.Infrastructure/Time/BrazilTimeZone.cs` (reuse)
- `tests/Platform.Api.Tests/Catalog/PermissionCatalogCatalogTests.cs`
- `tests/Platform.Api.Tests/Authorization/*` (matrix/catalog loops)
- New: Complete tests; cancel-lock concurrency (DockerFact); TZ civil 10:00 → instant that is 10:00 in America/Sao_Paulo
- Migration `AddRentalsReservationsCompletePermission`

**vlr-web**

- `src/features/rentals/pages/ReservationsPage.tsx`
- `src/features/rentals/services/reservationsService.ts`
- `src/locales/{pt-BR,en,es}/common.json`

## Test seams

- Catalog length 44 + key present.
- Complete: Confirmed→Completed; Completed idempotent; Canceled/PendingDeposit 409; Customer JWT 403/401 (no route).
- Confirm still requires `rentals.reservations.confirm` only.
- Cancel: two concurrent cancels leave slots `Available` (Testcontainers).
- TZ: book/create 10:00–11:00 civil → `StartDateTime` equals `BrazilTimeZone.AtLocal(date, 10:00)`; ListAdmin `from=to=that Brazil date` returns the row.
- Existing `ReservationConcurrencyTests` still pass.

## Verification strategy

- `dotnet test --filter FullyQualifiedName~Rentals|FullyQualifiedName~PermissionCatalog`
- WEB: typecheck; staff UI: PendingDeposit still Confirmar; Confirmed shows Concluir only with complete permission.
- DEV: `list` then `apply` for the permission migration via GitHub workflow (Human Gate for apply). TZ Phase B: DEV has zero reservation rows today — re-count immediately before apply/deploy.
- PROD: **read-only** count/classify (safe inspector or approved SQL) **before** deploying Phase B. No UPDATE.

## Product-level "how to test"

1. B2B: create/book a 10:00 Brazil slot; reservation list for that civil date shows 10:00 (not 07:00) for a Brazil browser.
2. PendingDeposit: Confirmar still works; Concluir hidden.
3. Confirmed: Concluir → Completed; second Concluir succeeds (idempotent); Cancel hidden or still allowed only if product keeps cancel-from-completed **false** (current cancel rejects Completed — keep that).
4. User role without complete: no Concluir.
5. Customer portal: no complete/cancel controls (unchanged this wave).

## Do not

- Blind `UPDATE rentals.reservations` on DEV or PROD.
- Deploy Phase B to Railway production while `PROD_TIMESTAMP_STATE=NOT_VERIFIED` or while classification is mixed/`OTHER`.
- Per-tenant timezone.
- Customer complete or Customer cancel.
- Reuse confirm permission for Complete.
- Pricing unification, agenda overlay, queue admin, Goods B2C (later waves).
- Laptop `ef database update` against hosted DBs.
- Call Fable during spec-only work.

## Documentation that may need updating (at implementation, not this spec PR)

- `.cursor/rules/30-rentals.mdc` — Complete exists; Customer listing already exists (`GET /api/reservations/mine`).
- `docs/context-packs/active/rentals.md` — TZ, Complete, `BrazilTimeZone.AtLocal` seam (`CONTEXT_PACK_UPDATE_RECOMMENDED`).
- `ROADMAP.md` (api + web) Histórico + Complete checkbox.
- `CONTEXT.md` only if glossary gains a timezone sentence (optional; keep glossary free of implementation).

## Implementation gates (Human)

| Gate | Status | Blocks |
|---|---|---|
| Product T1 / Complete / no B2C cancel | **done** 2026-09-05 | — |
| DEV timestamp classify | **done** — empty | DEV backfill (none) |
| PROD timestamp classify (read-only) | **done** — `EMPTY` (0 reservations; Slot kept) | — |
| PROD data correction | **not required** | any UPDATE |
| Permission migration apply DEV | **done** | — |
| Permission migration apply PROD | **done** (`PENDING_COUNT=0`) | — |
| Fable Merge Risk Gate | **done** at implementation PRs | — |
| Wave 1 PROD release | **PROD_COMPLETE** 2026-09-06 | — |

## Migration / backfill impact

| Change | Migration? | DEV | PROD |
|---|---|---|---|
| `rentals.reservations.complete` permission row | **Yes** (seed `core.permissions`) | apply via workflow after PR | apply via workflow + production confirm; bootstrap grants Admin/SuperAdmin |
| TZ storage meaning | **No schema change** | 0 rows — code-only | **EMPTY** — no rewrite |
| Historical timestamp rewrite | **Not in this wave** | N/A | **not required** (`EMPTY`) |
| Cancel lock | No | — | — |
| Complete status | No (enum already exists) | — | — |

## Fable Merge Risk Gate (implementation PR)

**FABLE_MERGE_REVIEW: REQUIRED**

Reasons (`AGENTS.md`): authorization (new permission + endpoint); reservation lifecycle / persisted domain data; concurrency (cancel lock); FE↔BE contract (`complete` + ISO instants); optional hosted migration apply.

Not required to invoke Fable to **author** this spec. Parent invokes `rolvix-deep-architect` only at Merge Risk Gate after implementation, with a compact GLM dossier. One Fable call per high-risk PR.

`FABLE_ESCALATION_NOT_RECOMMENDED` for extra pre-implementation architecture (T1 already decided).

## Wave 2 (explicitly not this spec)

- Customer `POST /api/reservations/{id}/cancel` (own row only).
- Unify `ResolveHourlyPriceAsync` sort.
- B2B agenda overlay of who booked.
