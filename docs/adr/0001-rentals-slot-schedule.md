# Rentals schedule is Slot-first, kinds are tenant catalogs

Rentals stays product-agnostic (spaces, goods, vehicles). Weekly **ScheduleTemplate** is the default way to author time; each **ScheduleDay** is materialized as **Slot** rows that can still be edited. **OccupancyKind** is a per-tenant catalog (not a closed Lesson/Open/Closed enum). **OpenHours** is an alternate **SchedulePolicy** on a Rentable that derives bookable windows from open/close + allowed durations. **ResourceCategory** reuses existing **AssetCategory** (no parallel rentals taxonomy table). Visual **Layout** maps Rentables on a canvas for selection UX and is separate from schedule data.

**Status:** accepted

**Considered options:** (1) free DateTime-only booking forever — rejected for FICC-style daily scales; (2) hard-coded slot kinds — rejected for multi-vertical tenants; (3) separate ResourceCategory table — rejected as duplicate of AssetCategory.

**Addendum 2026-08-14:** list/publish/seed/policy endpoints accept a set of `rentalAssetIds` (singular contracts remain). Bulk policy update is transactional. Product UI never shows `OpenHours`/`SlotGrid`; those stay domain names. Fine template edits remain per Rentable.

Because a day can span many Rentables × many hours, deriving OpenHours windows must not query per slot: blocking reservations for the date are loaded once and overlap is computed in memory, and persisted starts come from the Slots the same request already loaded. Template listing accepts `dayOfWeek` so a single day never pays for the whole week.

**Addendum 2026-08-17:** unpublished SlotGrid days are derived from that weekday’s `ScheduleTemplate` rows on `GetDay` (same persisted-start + reservation overlap rules as OpenHours). Seed-default also forces `SchedulePolicy.SlotGrid`. `PublishDay` remains optional materialization for dated exceptions and EntireRecurrence cascade. B2C books a derived window through create-reservation until a Slot row exists.

Day exceptions reuse `Slot` as a dated override (update kind/label, cancel/unavailable tombstone, restore weekly default). Admin day reads include cancelled occurrences; OpenHours cancelled starts remain tombstones so the derived window does not return. Product UI separates **Day agenda** (one date, resource grid) from **Weekly setup** (recurring rules).

`EntireRecurrence` on SlotGrid updates the matching `ScheduleTemplate` and cascades to future non-booked slots that still match the previous fingerprint; OpenHours entire edits remain in Weekly setup. Bulk weekly grids use `POST /api/schedule/templates/apply-weekly-rule`. Occupancy kinds may carry optional description and client-resolved icon keys.
