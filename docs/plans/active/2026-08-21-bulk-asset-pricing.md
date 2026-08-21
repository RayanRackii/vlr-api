# 2026-08-21-bulk-asset-pricing

Status: approved (human: option B)

Repositories: vlr-api, vlr-web
Branch (both): `fix/bulk-asset-pricing`
Merge: API first, then web. `FABLE_MERGE_REVIEW_REQUIRED: yes`

## Endpoint

`POST /api/assets/pricing-bulk` `[Authorize]` B2B

```json
{ "assetIds": ["guid"], "pricings": [{ "dayOfWeek", "startTime", "endTime", "pricePerHour", "requiresDeposit", "depositPercentage" }], "replace": true }
```

Response 200: `{ appliedAssetCount, pricingsCreated }`

Keep `POST /api/assets/bulk` and per-asset pricing routes unchanged.

## Rules

- Caps: 1000 assetIds, 100 pricings, product ≤ 10000
- Duplicate assetIds / exact duplicate rows → 400
- Empty pricings + replace=false → 400; replace=true → clear all
- Missing/not-rentable asset → 404 entire batch (no writes)
- Payload or DB overlap → 409
- One transaction; reuse `RentalPricingService` overlap/window/rentable helpers

## FE

One `bulkApplyAssetPricings` after create/bulk/edit. `replace: true`. Stop GET+DELETE+POST loop.
