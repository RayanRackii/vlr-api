# 2026-10-14 — FICC DEV teacher lesson “Rentable was not found”

Branch `cursor/fix-ficc-dev-teacher-lesson-rentable-ee37` from `origin/develop` (`da17ee1`). Not merged. No migration, role grant, secrets, data rewrite, or deploy.

## What failed

Staff with the custom role Professor (`rentals.assets.read`, `rentals.occupancy_kinds.read`, `rentals.schedule.read`, `rentals.schedule.lessons.write`) could list Quadra 1 and Quadra 2 on `GET /api/rental-assets` and still got `Rentable was not found.` from `POST /api/schedule/lessons` for 2026-10-15 15:00–16:00.

## What was ruled out

Read-only census on Rolvix DEV (`jzptnjyzijklutinpxag`), 2026-10-14:

| | Tenant | Quadra 1 | Quadra 2 |
|---|---|---|---|
| Rental asset id | `b27c9a0a-d585-4626-8c13-1468c5a43519` | `e82cbc28-0fdc-4d44-a062-2efede0f16b8` | `e56e4f22-f45e-486d-a80b-a3714d6e3d5e` |
| Asset id | — | `388931ba-5233-4da9-b49f-d8304e48c89b` | `2a01d706-2021-43d1-a578-ba0892ca7739` |

Both rows are active Location, SlotGrid, rentable, status Active, same tenant as the asset. Thursday 15:00–16:00 has an active `open` template on each court. Canonical `open` and `lesson` kinds exist with the expected flags. The agenda posts `rentalAsset.id` from `GET /api/rental-assets`, not `assetId`. The create query has no extra permission filter; the Professor already passes `rentals.schedule.lessons.write` or the response would be 403. The EF tenant filter is not hiding the rows.

`FiccTeacherLessonScope` only allowed production tenant `3ae86b53-d43e-42b0-9616-4833d02f7a1e` and the six production rental asset ids (Q-1…Q-6). `EnsurePilotScope` throws before the database lookup, with the same text as a missing row.

## Fix

The allowlist now also accepts the two DEV rental asset ids above. Production ids are unchanged. Asset ids stay rejected. A court that is allowlisted but has no active SlotGrid row still returns 404. Teacher-lesson errors returned to the client are pt-BR (`A quadra não foi encontrada.`).

Do not rewrite DEV primary keys to the production UUIDs. Those keys are already referenced by assets, templates, and the tenant. No SQL repair is required.
