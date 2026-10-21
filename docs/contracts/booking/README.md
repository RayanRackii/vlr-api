# Booking UI rollout: API contracts, steps 1, 2 and 4

Implementation in this worktree only; no Git operations, publication or deployment. The canonical proposal is `~/codex-runs/ux-booking-spec.md`. This work adds a read-only offer projection, the default-off UI rollout flag and an optional expected-price guard on the two existing reservation POSTs. Legacy clients keep their behavior when the new field is omitted or null.

Accepted defaults: BRL only, quotes valid for 60 seconds, editable stock quantity starting at one, map optional. The later UI uses “Continue” with contextual explanation and a separate click after queue admission; in-progress reservations belong to Upcoming and canceled reservations sort by usage date. These UI/history decisions do not add runtime flows in this API slice. Paged history/detail/own-day reads belong to step 5.

## Daily availability

`GET /api/public/tenants/{subdomain}/availability/days/{date}`

- `date`: exact civil `yyyy-MM-dd`, excluding `9999-12-31` because the day requires an exclusive next-day bound.
- `rentalAssetIds`: optional repeated UUIDs; the `rentalAssetIds[]` spelling is also supported. Empty scope means all active rentable resources. Foreign, unknown and inactive IDs never expand scope. Unknown units return an empty scope.
- `unitId`: optional UUID, intersected with resource scope.
- `quantity`: integer 1–1,000,000, default 1. It drives stock sufficiency and total price. A Location has physical capacity one; the new UI selects quantity one for it.
- `durationMinutes`: optional integer 1–1440; filters full windows by duration rather than generating arbitrary windows.
- At most 100 requested IDs, 100 matched resources and 10,000 returned windows across all resources. Excess is rejected, never truncated; callers narrow resource/unit/duration scope. More than 10,000 persisted rows or templates in scope is rejected before derivation.

The response matches [the JSON Schema](daily-availability.v1.schema.json). [Fixtures](fixtures/) cover empty scope, available/taken/blocked/past/unpriced cells, stock, zero/null price and mixed horizons. Tests validate both fixtures and the actual controller's JSON against the structural schema and typed DTOs; any Draft 2020-12 validator can also enforce formats.

`serverNow` is captured once. `quoteExpiresAt = serverNow + 60s`; neither a quote nor a selection holds capacity. `timeZone` is the canonical IANA identifier `America/Sao_Paulo` from the existing booking clock. Slot times remain civil, while reservation overlap uses UTC instants and the civil day's exclusive upper bound. Each window's identity is resource + date + full start/end; persisted windows carry `slotId`, derived ones carry null.

States, in precedence order:

| State | Reason code | Meaning |
|---|---|---|
| OutsideHorizon | `booking_horizon_exceeded` | This resource's effective horizon excludes the requested date. |
| Past | `START_IN_THE_PAST` | The window starts strictly before the captured server instant. |
| Blocked | `SCHEDULE_BLOCKED` | Canceled occurrence, nonbookable/inactive/missing kind, or overlapping dated block. |
| Taken | `INSUFFICIENT_CAPACITY` | Physical capacity is below requested quantity, or a persisted slot is already booked. |
| Unpriced | `PRICE_UNAVAILABLE` | No applicable pricing row covers the whole interval. |
| Available | null | Schedule, time, horizon, physical capacity and price allow selection. Queue admission remains separate. |

`availableQuantity` describes physical stock, independently of queue eligibility, quote validity and horizon. Locations cap at one; Goods subtract quantities from overlapping PendingDeposit/Confirmed reservations, with half-open intervals. Completed/Canceled reservations do not consume capacity. A dated capacity block reports zero. A Booked Good slot remains Taken even with positive stock because its existing BookSlot route rejects an already-booked slot. Queue presence never subtracts stock.

`totalAmount` is rounded to two decimal places away from zero, for the whole interval × requested quantity. Zero is valid; null means no quote. Occupied/blocked/past cells retain prices when calculable. Selection matches the eventual booking route: persisted slots choose the covering price with latest StartTime; derived windows choose the cheapest covering hourly price. Both booking services share these pure price-order/total helpers; no legacy precedence is changed.

Weekly template overlaps reuse existing occupancy precedence. OpenHours derives the existing configured durations and keeps occupied cells. Persisted starts, including canceled tombstones, override derived starts. Unknown/foreign occupancy kinds fail closed. Resource horizon overrides tenant horizon; missing/null policy is unlimited; N days includes today + N. Mixed scopes keep OutsideHorizon resources visible; if all matched resources exceed their horizons, the endpoint preserves HTTP 400 `booking_horizon_exceeded`.

Public output excludes reservation/customer/contact/internal labels and block explanations. Route subdomain binding wins over hostile headers and authenticated tenant claims. Every resource/asset/unit join, price, slot, template, kind and reservation query is explicitly tenant-scoped; missing/empty service tenant fails closed. The existing Rentals module gate applies. The normal service path executes eight bulk read queries regardless of cell count, with no default seeding or writes. Binder/module-gate reads add a constant HTTP overhead.

Invalid new query values return HTTP 400 `{ error, code: "AVAILABILITY_INVALID_QUERY" }`; scope/window limits use `AVAILABILITY_LIMIT_EXCEEDED`. Existing module-disabled 403 and unresolved-tenant 401 behavior comes from the shared filter/exception pipeline. The endpoint uses no UI-flag gate, allowing API validation before UI enablement.

## Expected-price guard (step 4, API only)

Both existing Customer-authorized POSTs accept optional `expectedTotalAmount: number | null`:

- `POST /api/reservations`: civil date/start/end, `unitId`, `items[].assetId` and quantity; expected amount is the **whole reservation total**, the sum of rounded item subtotals.
- `POST /api/schedule/slots/book`: `slotId`, `unitId`, quantity; expected amount is the whole slot interval × quantity.

The client copies the selected daily availability window's `totalAmount`, using the same resource, full interval and quantity. For multiple items it sums their rounded totals. The server independently resolves current pricing and capacity within the existing transaction, after the customer lock (when the daily cap applies) and RentalAsset locks. Existing route-specific pricing precedence and two-decimal rounding away from zero are unchanged. A zero quote is valid. Comparison uses exact decimal equality against that rounded server total; the supplied amount is never used as the price or rounded to conceal a mismatch.

If present and different, the POST returns **HTTP 409**:

```json
{
  "error": "O preço mudou. Revise antes de continuar.",
  "code": "PRICE_CHANGED",
  "currentTotalAmount": 125.00
}
```

See [the error schema](price-changed.v1.schema.json). `currentTotalAmount` is BRL for this rollout. A mismatch creates no persisted reservation/items, does not mark a slot Booked, does not publish the reservation notification and does not complete/consume the queue turn. A valid active turn keeps its existing expiry; it is never renewed by a price error. Corrected submission requires an explicit new client action. Capacity/queue/start/horizon/daily-cap validation still applies before price comparison; unavailable or unpriced intervals do not promise a current bookable offer. Existing `QUEUE_*`, `START_IN_THE_PAST`, `booking_horizon_exceeded` and `DAILY_BOOKING_LIMIT_REACHED` responses remain.

Omitted or explicit null skips comparison and preserves legacy behavior, including success status (create 201, slot book 200). Negative or over-precision numeric values cannot match a normal rounded total and return PRICE_CHANGED after the other validations; malformed decimal input returns the existing model-binding 400. No new quote token, server-side expiry field or capacity hold is added: `quoteExpiresAt = serverNow + 60s` tells the UI to refresh; every guarded POST recomputes regardless of quote age. The guard compares prices, not quote authenticity.

Queue join/leave/status keep their existing contract. Joining a per-resource queue has no selected interval or price; the guard runs when its active customer submits either reservation POST. Tenant/customer are resolved through Customer JWT, and existing tenant-scoped unit/resource/slot validation precedes returning any current amount. The optional guard applies independently of the UI rollout flag. No entity, schema change or migration is needed. Rollback of the new UI may omit the field and continue through the existing routes.

## Tenant flag

The existing `GET /api/public/tenants/{subdomain}/branding` adds `newBookingUiEnabled: boolean`. Legacy fields remain. Missing configuration, an empty allowlist or a tenant not explicitly listed returns false. Matching uses immutable tenant UUIDs, not the caller's subdomain/header/JWT choices.

Reuse the application's existing `IConfiguration`/options mechanism:

```json
{
  "Rentals": {
    "BookingUi": {
      "EnabledTenantIds": ["00000000-0000-4000-8000-000000000001"]
    }
  }
}
```

Equivalent environment key: `Rentals__BookingUi__EnabledTenantIds__0`. The committed default array is empty. No tenant data model or migration is required. No tenant was enabled here. Rollback removes a tenant UUID from deployment configuration and reloads/restarts the application according to its existing configuration provider. Production activation remains an explicit operational action.

## Validation and next steps

Run `dotnet build` and `dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj`; target `DailyAvailability|BookingAvailabilityContract|ExpectedPrice` for this rollout. `ExpectedPriceTests` and `ExpectedPriceHttpTests` cover quote/quantity/rounding, zero/null/legacy, mismatch/retry, queue turn preservation and tenant/negative cases through services and real Customer-JWT HTTP requests. `ExpectedPriceConcurrencyTests` uses PostgreSQL to test price updates while booking waits for its lock, queue rollback and competing guarded requests for exclusive capacity, stock and daily cap. PostgreSQL tests (including the fixed-query/no-write availability test) use Docker or `ROLVIX_TEST_POSTGRES_CONNECTION` with the repo's isolated loopback database fixture, and skip where neither is available.

Manual read-only smoke: query the new day route with two resource IDs, quantity and duration; confirm unavailable cells remain, and check branding defaults false. Repeat with a foreign resource/unit ID and a different tenant's valid token/header. Validate DEV before any UI rollout. No migration in this slice.

Guarded POST smoke in DEV: quote a window, change its hourly price through staff pricing, submit the original total and verify 409 PRICE_CHANGED/currentTotalAmount without a reservation or completed queue ticket. Review the new amount and submit again explicitly. Repeat with a persisted slot and a derived stock window; omit the field to verify legacy creation. No production write smoke is authorized here.

Next API work: step 5's paged/detail/own-day reads. Next `vlr-web` work: step 4's summary bar/queue/confirmation and sending expectedTotalAmount from the daily quote, behind `newBookingUiEnabled`, building on step 3's grid/date/selection; no frontend changes in this slice.
