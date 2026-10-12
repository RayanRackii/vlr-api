# Teacher lessons release blockers

Repositories: `vlr-api`, `vlr-web`
Branch: `fix/teacher-lessons-release-blockers` (same name in both repos)
Base: `origin/develop`
Spec: [`2026-10-08-teacher-lessons-date-only.md`](./2026-10-08-teacher-lessons-date-only.md)

This note is the review contract for the three remaining blockers. Read it before judging the diff. It does not change authentication, authorization, or the frontend/backend contract.

## Withdrawn: web route and navigation

The earlier web review marked as High that `/configuracoes/agenda` and its nav item stay behind `rentals.schedule.read`, so a user who has only `rentals.schedule.lessons.write` cannot open the teacher screen.

That finding used an incomplete spec. The approved plan already decides this:

- Line 30: the teacher screen reads courts and the day through `rentals.assets.read` and `rentals.schedule.read`. Those read keys do not grant schedule mutation. The role needs them in addition to `rentals.schedule.lessons.write`.
- Line 36: the teacher affordance is shown only when the user has `rentals.schedule.lessons.write` and does not have `rentals.schedule.write`. A teacher role receives the new key plus the read keys, and no general schedule-write permission.

A professor who has only `rentals.schedule.lessons.write` is not a valid configuration. Keep the agenda route and the nav item on `rentals.schedule.read`. Do not change policies, permission keys, the migration, or the route table in this fix.

## 1. Web: reservation link and response body

`TeacherLessonsPage` treated `status === "Available"` as enough. A slot can be `Available` and still carry `reservationId`. The admin grid already treats that slot as booked (`status === "Booked"` or `reservationId` set).

The teacher screen now offers an open slot, and lists a lesson for removal, only when `status === "Available"` and `reservationId` is absent. The same rule applies when a selected time disappears after the day reloads.

`useTeacherLessons` toasted success as soon as `createTeacherLesson` / `removeTeacherLesson` resolved. Those functions already accept any body that parses as `scheduleSlotSchema`. Success is now accepted only when the returned slot matches the requested court, civil date, and clock interval, has no `reservationId`, has status `Available`, and has kind `lesson` on create or `open` on remove. Any other parsed body uses the existing `changeError` toast, rethrows, and leaves the form values in place.

Tests in `TeacherLessonsPage.test.tsx`:

- an `Available` open slot with `reservationId` is not an option, and an `Available` lesson with `reservationId` has no remove button
- a create body that is otherwise the requested lesson but still has `reservationId` does not call `toast.success` and keeps the typed label
- a remove body that is still kind `lesson` does not call `toast.success`
- a matching unreserved lesson, then a matching unreserved open slot, does call the success toasts

These tests failed before the page and hook checks. They pass after.

## 2. API: template court chosen before the lock

`ScheduleService.UpdateTemplateAsync` and `DeleteTemplateAsync` read `RentalAssetId` with `AsNoTracking`, then lock that id (and, on update, the requested id). The tracked reload can observe a court that was not in that set. The old code still saved or deleted.

After the reload, and before collision checks, mutation, or `SaveChanges`, the service refuses when the current `RentalAssetId` is not one of the ids already chosen for the lock. The exception is `InvalidOperationException`: "The template court changed before its row was locked." The transaction rolls back. There is no second lock and no retry.

Locking the newly observed id after other ids are already held can deadlock when the new id sorts first. The approved plan locks several assets by ascending GUID and forbids retries that hide failures. The caller repeats the whole operation; that repeat reads the current court and locks it first.

`ScheduleTemplateCourtLockTests` uses one in-memory database and a fresh context so the template is not already tracked. On the first materialization of the template, a second context moves it to another court. The no-tracking copy still has the original court, so the lock set is stale, and the tracked reload sees the new court.

- update throws, the new label is not stored, and the moved court remains
- delete throws, and the template row remains on the moved court

Both tests completed the write before `EnsureCourtStillLocked` existed.

This does not execute `FOR UPDATE`. `TeacherLessonConcurrencyTests` are `[DockerFact]`. On this machine, without Docker and without `ROLVIX_TEST_POSTGRES_CONNECTION`, that class finished `0` passed, `18` skipped. A skipped DockerFact is not a concurrency pass. The skip reason is: "Docker and an isolated local PostgreSQL test connection are not available; concurrency tests were skipped."

## 3. API: canonical source on remove

For a persisted canonical lesson, `sourceExists` used to accept any template of the same tenant, court, and weekday that covered the interval. An inactive template, or an active open template that is not the winner, still let the slot become open when the current winner was open.

The approved plan preserves `SourceTemplateId` when a dated lesson is created from an open window, and remove of that persisted lesson opens it. Requiring every source to be kind `lesson` would reject that valid remove (`Remove_opens_D_and_leaves_D_plus_7_as_lesson`).

The source row must now be active, on the same court and weekday, covering the interval, with an active occupancy kind, and one of:

- the active canonical lesson kind (`key == "lesson"`), or
- the current winning template, when that winner is the active canonical open kind

Otherwise remove throws the existing "The lesson source template is unverifiable." and does not write.

Tests:

- `Remove_inactive_lesson_source_rejects_without_opening` — the slot points at an inactive lesson template while the active open template still wins. Before the check, remove opened the slot.
- `Remove_rejects_when_source_open_template_is_not_the_winner` — an active open template `10:00–12:00` with a greater id loses `10:00–11:00` to the hourly open template. The slot points at the loser. Before the check, remove opened the slot. The exact-duplicate unique index does not forbid this row, because the end times differ.

## Out of scope

No change to route, nav, policies, permission catalog, migration, role grant, RLS, deploy, or merge into `main`. No production data change. The release drafts API #93 and Web #88 stay the develop→main pins until a later gate. This branch is not that merge.

## Evidence already run

- API: `TeacherLessonServiceTests` and `ScheduleTemplateCourtLockTests` — 40 passed, 0 failed, 0 skipped.
- API: `TeacherLessonConcurrencyTests` — 0 passed, 18 skipped. Not a pass.
- Web: `TeacherLessonsPage.test.tsx` — 9 passed.
