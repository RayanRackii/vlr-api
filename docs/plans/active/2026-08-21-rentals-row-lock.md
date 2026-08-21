# 2026-08-21-rentals-row-lock

Status: approved (human: row lock on RentalAsset; not exclusion/SERIALIZABLE)

## Goal

Close the TOCTOU race in `CreateReservationAsync` and `BookSlotAsync`: two concurrent txs see reserved=0 and both insert blocking reservations.

## Implementation

Shared helper `RentalAssetLocks.LockByRentalAssetIdAsync`:

```
SELECT 1 FROM rentals.rental_assets WHERE id = {id} FOR UPDATE
```

via `ExecuteSqlInterpolatedAsync`. First thing inside the existing transaction. No provider check.

`CreateReservationAsync`: after `BeginTransactionAsync`, resolve all item `AssetId` → `RentalAsset.Id`, lock each in **ascending Id** order, then existing loop (Location and Good).

`BookSlotAsync`: after `BeginTransactionAsync`, resolve `Slot.RentalAssetId` from `SlotId`, lock, then existing slot load.

Keep `GetReservedQuantityAsync` AsNoTracking.

## Tests

`ReservationConcurrencyTests`: Testcontainers PostgreSQL, `MigrateAsync` on real `AppDbContext` (not TestAppDbContext). Two parallel `CreateReservationAsync` same Location same interval → one success, one `InvalidOperationException` conflict; exactly one blocking reservation.

Same for `BookSlotAsync` if seed cost is reasonable.

If Docker is unavailable: skip (custom FactAttribute), do not fail. Do **not** hit Development/PROD Supabase.

Remove `TODO(F-01)` from `LocationBookingConflictTests` once the lock + concurrency tests exist.

## Do not

Exclusion constraint, SERIALIZABLE, Docker-required CI yaml, pollute shared DEV.

## Fable

Required after reviewer (concurrency/integrity).
