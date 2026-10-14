# 2026-10-14 — Tenant predicate on row locks

`ReservationLocks` and `PmocPlanAssetLock` `FOR UPDATE` statements now require `tenant_id` as well as the row id. An empty tenant id throws. `TryGenerateAutomaticAsync` takes the plan tenant from `PmocEngineJob` and reloads the plan and asset with that predicate.

No migration. `tenant_id` already exists on `rentals.reservations`, `pmoc.maintenance_plans`, and `assets.assets`. The advisory lock in `PmocPlanAssetLock.AcquireAsync` is unchanged.

Proof: `TenantScopedForUpdateLockTests`. A held row stays locked for its owner (`55P03` on the matching tenant). `pgrowlocks` shows the foreign backend is not on that row when the same id is locked with the other tenant id.
