# 2026-08-22-schedule-template-overlap-rules

Status: approved

## Goal / Problem

F-10: `ApplyWeeklyRuleAsync` uses `ToDictionary` keyed by `RentalAssetId|DayOfWeek|StartTime`. Duplicate StartTime (including legitimate cross-kind overlap) throws 500. Product now allows overlap across OccupancyKinds and rejects exact duplicates.

## Visible behavior

- Open 08:00–22:00 + Lesson 18:00–19:00 on the same Location/weekday → allowed. Derived unpublished SlotGrid: Lesson wins 18:00–19:00 (08:00–18:00 Open, 19:00–22:00 Open).
- Open + Closed overlap → allowed. Closed wins the overlapping interval.
- Exact duplicate (same Tenant, RentalAsset, DayOfWeek, StartTime, EndTime, OccupancyKind) → rejected (400), no 500.
- PublishDay on a day that already has slots: keep existing slots and reservations; insert only missing (RentalAssetId, StartTime) rows. Do not wipe the day.

## Repositories

- vlr-api

## Branch

`fix/schedule-template-overlap-rules`

## Relevant existing ADR / rules

- `docs/adr/0001-rentals-slot-schedule.md` — OccupancyKind is a tenant catalog.
- `.cursor/rules/30-rentals.mdc`
- CONTEXT.md glossary (OccupancyKind, ScheduleTemplate, SlotGrid)

## Architecture route

- rolvix-architect
- FABLE_MERGE_REVIEW_REQUIRED: yes (persisted schedule + occupancy semantics)

## Execution route

- api-implementer

## Confirmed decisions

### Persisted unique key

Current EF index is **non-unique** `(TenantId, RentalAssetId, DayOfWeek, StartTime)`. Do **not** make that unique (would block Open 08–22 + Closed 08–22).

Add UNIQUE `(TenantId, RentalAssetId, DayOfWeek, StartTime, EndTime, OccupancyKindId)`.
Keep the existing 4-column index.

This is not a destructive data rewrite. If apply fails on Railway because exact dupes already exist, that is an ops apply failure, not a silent delete. Parent cannot inspect DEV/PROD from this PC.

### Write-time rules

- Exact duplicate → `ArgumentException`.
- Same OccupancyKind overlapping intervals (start < other.End && end > other.Start) on same Tenant+RentalAsset+DayOfWeek → `ArgumentException`.
- Different kinds overlapping → allowed.
- Apply on CreateTemplate, UpdateTemplate (exclude self), ApplyWeeklyRule, and SeedDefault when it would insert a colliding row.

### ApplyWeeklyRuleAsync

Do **not** use `ToDictionary` on StartTime. Lookup by full tuple `(RentalAssetId, DayOfWeek, StartTime, EndTime, OccupancyKindId)`.

- Exact match → skip (count skipped), never throw.
- Same start + different kind → treat as a **new** template row, do not overwrite the other kind.
- If legacy exact dupes exist in memory, skip extras deterministically (stable Id order); never ToDictionary throw.

### Read-time precedence (unpublished SlotGrid derive only)

`DeriveSlotGridFromTemplatesAsync`: per rentable, split the day at all template Start/End breakpoints. For each atomic segment pick the covering template with highest rank. Merge adjacent segments with the same winner.

Rank by OccupancyKind.Key:
- `closed` = 3
- `lesson` = 2
- `open` = 1
- other: 2 if `BlocksCapacity` else 1
Equal rank → higher `Key` ordinal (deterministic). Never createdAt/updatedAt/shorter-interval.

Do **not** rewrite persisted overlapping Slot rows in this PR (follow-up F-10b / Human Gate). PublishDay stays gap-fill by `(RentalAssetId, StartTime)`.

### Custom kinds

Covered by the rank function above. Not a new product gate.

## Invariants that must not break

- Booked slots stay booked; PublishDay does not delete slots.
- Location exclusive / Good quantity booking rules unchanged.
- OccupancyKind remains a tenant catalog (not a closed C# enum).
- No last-write-wins.

## Implementation scope

- `ScheduleTemplateConfiguration` unique index + EF migration (non-destructive CREATE UNIQUE INDEX).
- `ScheduleService` write validation + ApplyWeeklyRule lookup + derive split.
- Small helper (rank + overlap) — unit-testable.
- Tests, CONTEXT glossary, ADR 0001 addendum, ROADMAP, then rentals context pack.

## Likely affected areas / files

- `Core/.../Configurations/ScheduleTemplateConfiguration.cs`
- `Core/.../Persistence/Migrations/` (new)
- `Platform.Api/Modules/Rentals/Services/ScheduleService.cs`
- new helper under Rentals (e.g. `OccupancyPrecedence.cs`)
- `tests/Platform.Api.Tests/Rentals/` (new)
- `CONTEXT.md` + `vlr-web/CONTEXT.md` glossary
- `docs/adr/0001-rentals-slot-schedule.md` addendum
- `docs/context-packs/active/rentals.md` after CONTEXT
- `ROADMAP.md`

## Test seams

InMemory `AppDbContext` + real `OccupancyKindService` (seed open/lesson/closed). Do **not** use `LocationBookingHarness`'s throwing `UnusedOccupancyKindService` for these tests. Force `SchedulePolicy.SlotGrid` on the rentable.

Required:

1. Create Open 08–22 + Lesson 18–19 → 2 templates. GetDay unpublished Tuesday: Lesson occupies 18–19; Open occupies 08–18 and 19–22 (or equivalent merged segments). Lesson wins 18–19.
2. Open + Closed overlap → Closed wins overlapping interval.
3. Second exact duplicate CreateTemplate → ArgumentException; still 1 row.
4. ApplyWeeklyRule with duplicate StartTime different kinds does not 500; both kinds persist.
5. Partially published day: persist a Slot at 08:00; PublishDay after adding a Lesson 18–19 template → existing 08:00 slot unchanged (including if Booked); Lesson 18–19 inserted; no wipe.

Also: ApplyWeeklyRule with two in-memory exact dupes (seed two identical templates then apply) must not throw.

## Verification strategy

`dotnet ef migrations add ...` then `dotnet test tests/Platform.Api.Tests`.

## Product-level "how to test"

Admin Weekly: add Open 08–22 and Lesson 18–19 Monday. Unpublished B2C day for that Monday shows Lesson 18–19 not bookable as Open. Publish a day that already has morning slots, add afternoon template, publish again → morning stays.

## Do not

- Unique-index the existing 4-column StartTime index.
- Split/rewrite persisted overlapping Slots (F-10b).
- F-16, F-04, main, PROD, secrets, destructive duplicate cleanup SQL.
- last-write-wins / timestamps / shorter-interval as precedence.

## Documentation that may need updating

CONTEXT (canonical + vlr-web mirror), ADR 0001 addendum, rentals context pack, ROADMAP.

## FABLE_MERGE_REVIEW_REQUIRED

Yes.
