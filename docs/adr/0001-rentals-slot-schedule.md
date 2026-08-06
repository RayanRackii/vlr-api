# Rentals schedule is Slot-first, kinds are tenant catalogs

Rentals stays product-agnostic (spaces, goods, vehicles). Weekly **ScheduleTemplate** is the default way to author time; each **ScheduleDay** is materialized as **Slot** rows that can still be edited. **OccupancyKind** is a per-tenant catalog (not a closed Lesson/Open/Closed enum). **OpenHours** is an alternate **SchedulePolicy** on a Rentable that derives bookable windows from open/close + allowed durations. **ResourceCategory** reuses existing **AssetCategory** (no parallel rentals taxonomy table). Visual **Layout** maps Rentables on a canvas for selection UX and is separate from schedule data.

**Status:** accepted

**Considered options:** (1) free DateTime-only booking forever — rejected for FICC-style daily scales; (2) hard-coded slot kinds — rejected for multi-vertical tenants; (3) separate ResourceCategory table — rejected as duplicate of AssetCategory.
