# 2026-08-22-bulk-asset-type-quantity

Status: approved

## Goal / Problem

F-16: `BulkCreateAsync` hardcodes `RentalAssetType.Location` and `TotalQuantity = 1`. Wizard collects `rentalType` but bulk POST drops it. Goods become Locations.

## Visible behavior

- Location + prefix Q- + 1..6 → 6 Assets Q-1..Q-6, each Location, TotalQuantity=1.
- Good + quantity 100 → 1 Asset, Type=Good, TotalQuantity=100. Not 100 assets.
- Good + quantity 1 → 1 Asset, Type=Good, TotalQuantity=1.
- No silent Good → Location.
- UX: type drives semantics. No extra “generate individuals vs stock” toggle. Location shows prefix + start/end. Good shows stock quantity; hide start/end.

## Repositories

- vlr-api
- vlr-web

## Branch (both)

`fix/bulk-asset-type-quantity`

## Merge

API first, then web. `FABLE_MERGE_REVIEW_REQUIRED: yes`

## Confirmed decisions

Additive DTO on `BulkCreateAssetsRequest`:
- `RentalType` default `Location` (back-compat)
- `TotalQuantity` default 1
- `StartNumber`/`EndNumber` become `int?` — required for Location, ignored for Good

Location: `createCount = end - start + 1`; tags `BuildTag(baseTag, n)`; `TotalQuantity` forced to 1.
Good: `createCount = 1`; tag = `BaseTag` (no numeric suffix); `TotalQuantity` from request.

Reuse `ValidateRentalFields`. Controller mapping unchanged.

FE: `bulkCreateAssetsRequestSchema` adds rentalType + totalQuantity; start/end optional.
Wizard bulk: rentalType first; Location → start/end; Good → stock quantity. Hide rentalType/totalQuantity on Operation step in bulk (already captured). i18n pt-BR/en/es.

Pricing/reservations/availability: no rewrite. ReservationService and ScheduleService already branch Good vs Location.

No serialized individual Goods. No migration.

## Tests (API)

`tests/Platform.Api.Tests/Assets/AssetServiceBulkCreateTests.cs`:
1. Location 1–6 → 6 assets qty 1
2. Good 100 → 1 asset qty 100
3. Good 1 → 1 asset qty 1
4. RentalType omitted + start/end → Location (back-compat)
5. Never silent Good→Location

Also: Location missing start/end → ArgumentException; Good TotalQuantity 0 → ArgumentException.

## Do not

F-10, F-04, main, PROD, serialized Goods, repo-wide schedule rewrite.

## Documentation

ROADMAP both repos. CONTEXT only if glossary of Asset/Rentable quantity meaning needs a sentence (Location = N entities; Good = stock on one entity).
