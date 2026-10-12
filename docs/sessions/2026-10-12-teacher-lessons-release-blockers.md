# 2026-10-12 — teacher lessons release blockers

Branch `fix/teacher-lessons-release-blockers` in `vlr-api` and `vlr-web`, from `origin/develop`. Not merged. No migration, role grant, or deploy.

The web route/nav finding is withdrawn. The 2026-10-08 plan (L30, L36) already requires `rentals.schedule.lessons.write` plus `rentals.assets.read` and `rentals.schedule.read`, and withholds `rentals.schedule.write`. Agenda route and nav stay on `rentals.schedule.read`.

Three code fixes, each with a test that failed before the fix:

- Web: do not offer or remove an `Available` slot that has `reservationId`; toast success only when the response matches the requested interval and the expected kind, with status `Available` and no reservation.
- API: template update/delete throws before writing when the reloaded court was not in the lock set chosen from the earlier read.
- API: persisted lesson remove accepts only an active canonical lesson source, or the active canonical open template that currently wins the interval.

`TeacherLessonServiceTests` + `ScheduleTemplateCourtLockTests`: 40 passed. `TeacherLessonConcurrencyTests`: 18 skipped (no Docker, no `ROLVIX_TEST_POSTGRES_CONNECTION`). Skip is not a pass. Web `TeacherLessonsPage.test.tsx`: 9 passed.

Review contract: `docs/plans/active/2026-10-12-teacher-lessons-release-blockers.md`.
