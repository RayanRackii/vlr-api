# Teacher date-only lessons (FICC pilot)

## Decision and boundary

Add a distinct `rentals.schedule.lessons.write` permission and dedicated date-only lesson create/remove operations. This is a narrow capability alongside `rentals.schedule.write`; it must not authorize generic slot/template/policy writes, recurring changes, or reservation operations. The pilot is fixed to the FICC tenant and its six reviewed SlotGrid rental asset IDs. No migration, role assignment, deploy, or production/dev write is authorized in this task.

## Create

- Request identifies a rental asset, one civil date, exact start/end, and optional display label. Server derives tenant and canonical lesson/open occupancy kinds; clients cannot choose status, source template, tenant, reservation link, or arbitrary kind.
- Require active tenant, `TrialGuard.EnsureWritableAsync`, `SchedulePolicy.SlotGrid`, tenant-owned active rentable `Location`, valid non-empty interval, and active canonical `lesson` kind. Validate date-only semantics; no recurrence.
- Serialize against reservations and all schedule writers by locking the tenant-owned `rentals.rental_assets` row inside a DB transaction, using a stable lock order for multiple assets. Fix the shared lock helper's missing tenant predicate and ensure all callers pass tenant ID.
- Resolve the exact winning derived SlotGrid segment for that date/asset/time, including precedence/splits. Reject unless the requested interval exactly matches an available window whose winning source is the canonical open kind. A persisted slot is convertible only when it is the sole exact-interval row for that winning open segment, has canonical open kind, `Available` status, no reservation link, and a valid matching source template; preserve its `SourceTemplateId` when turning it into lesson. An exact duplicate lesson is idempotent. Any other overlapping/partial persisted slot rejects without mutation. Reject active non-open template overlap or a blocking reservation discovered directly from `rentals.reservation_items JOIN rentals.reservations` with tenant predicates and statuses `PendingDeposit`/`Confirmed`.
- Exact duplicate lesson request is idempotent: return the existing exact same lesson unchanged. Same target with differing end/label/kind or any ambiguous overlap rejects without mutation.
- Materialize a date-only Slot override with canonical lesson kind and correct source template ID; write only after all checks, commit transaction, return the canonical response.

## Remove

- Dedicated operation names a date, asset and exact start/end interval; it can remove a persisted date-only lesson or a lesson derived from a weekly template. It never accepts a generic slot mutation payload. Require the same permission, writable trial, active tenant, SlotGrid asset and tenant ownership.
- In a transaction, acquire the same tenant-scoped asset row lock used by reservation creation and schedule writers, then reload the exact slot with tenant, date and asset predicates.
- Resolve the exact winning weekly segment again. If the winner is an active canonical lesson and there is no dated slot, insert an open/Available override for that interval with `SourceTemplateId` equal to the winning lesson template. If a persisted canonical lesson exists, update it in place to canonical open/Available and preserve its `SourceTemplateId`. An already-open exact override is idempotent only when it is a valid date override of the current winning lesson. Any reservation overlap from the direct reservation-item join, any other overlapping persisted slot, active non-open template overlap apart from the exact winning lesson being overridden, malformed source/state, inactive/missing kind, or ambiguity rejects with no DB writes. Never modify weekly templates. After success, D is open and D+7 remains lesson.

## Shared concurrency protocol

All reservation-create paths (including Schedule BookSlot and queue paths that create reservations), teacher create/remove, and schedule writes that can affect the result must use the same tenant-scoped `rentals.rental_assets` `FOR UPDATE` lock within their transaction. Audit and lock all ScheduleService write methods (template create/update/delete, seed/apply weekly, publish, slot upsert/cancel/daily occurrence); also lock schedule-policy mutation. Acquire several asset locks by ascending GUID. Read/write tenant predicates must be explicit at the raw SQL boundary and service queries even with EF global filters. Do not introduce retries that hide failures. PostgreSQL concurrency tests are required; in-memory locking is not evidence.

## Authorization and migration

Permission catalog key: `rentals.schedule.lessons.write`. Add permission definition and a reversible migration to insert/delete only that catalog permission (plus any strictly required catalog/module metadata); do not automatically grant it to any persisted role in this migration. System Admin/SuperAdmin retain their existing catalog wildcard at runtime. Other roles receive the lesson capability only when explicitly assigned the new key through a later role configuration gate.

The teacher screen reads courts and day schedules through existing `rentals.assets.read` and `rentals.schedule.read` routes; its role needs those read permissions in addition to `rentals.schedule.lessons.write`. These read keys do not grant schedule mutation.

**Verified fixed pilot scope:** a read-only Supabase census on 2026-10-08 found exactly six active, rentable FICC `Location` assets using `SlotGrid` in production (Q-1 through Q-6); no FICC tenant was found in dev. The server hard-codes the FICC tenant ID and those six `rental_asset` IDs in `FiccTeacherLessonScope`; adding a court requires an explicit reviewed code change. The runtime still checks current tenant ownership, active/rentable status, Location type, and SlotGrid policy before mutation.

## API and UI contract

Dedicated routes under `/api/schedule/lessons` for create and remove require only the new fine-grained `rentals.schedule.lessons.write` permission. Do not fall back to the broader `rentals.schedule.write` permission: custom roles with only that key remain on the administrative schedule UI and cannot call these routes. System Admin and SuperAdmin retain access through the existing runtime catalog wildcard when Rentals is active; the migration does not persist this key into their `role_permissions`. A teacher role receives the new key plus the read keys described above, and no general schedule-write permission. Show the teacher lesson affordance only when the user has `rentals.schedule.lessons.write` and does not have `rentals.schedule.write`; do not expose admin template, recurring, general slot editing, schedule policy, asset, pricing, or reservation actions to a teacher-only user. Use established API error conventions; conflict/unsafe removal should return conflict and leave state untouched. Keep existing admin behavior and add localization.

## Required verification

- API tests for authorization boundaries, exact slot resolution and split precedence, direct reservation-item conflict, duplicate idempotency, tenant isolation, TrialGuard, invalid kinds/states, and no-write rejection.
- PostgreSQL concurrency tests for create/remove vs booking, template writes, and policy writes; assert serial outcomes and no unsafe opening.
- Web tests for teacher-only control visibility and absence of generic schedule mutation affordances.
- End-to-end behavior to establish via tests: remove D => D open, D+7 still lesson; reservation create D succeeds and D+7 remains blocked; template edits/cascades preserve dated override; weekly default restore returns to lesson; closed/custom partial overlaps reject without writes; open template beneath lesson is allowed.

## RLS finding (separate)

Record, without changing, the 11 `rentals` tables previously observed with RLS disabled: `rental_assets`, `reservations`, `reservation_items`, `rental_pricings`, `layouts`, `layout_items`, `occupancy_kinds`, `schedule_templates`, `slots`, `reservation_queue_sessions`, `reservation_queue_tickets`. This fact alone does not establish public exposure or backend credentials. Inspect source/configuration statically and report what is evidenced; never print secrets. Do not enable RLS as part of this feature.

## Out of scope

No live DB inspection that writes, no real migration application, role creation/assignment, deployment, Gmail work, or production mutation. Do not touch original dirty checkouts. Work only in the clean isolated feature worktrees.
