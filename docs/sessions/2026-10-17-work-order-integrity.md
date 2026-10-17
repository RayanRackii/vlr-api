# 2026-10-17 — work-order integrity and stable conflict codes

Closed or canceled work orders reject task writes with 409 `WORK_ORDER_CLOSED` after a Postgres row lock and reload. Invalid task values return 400 `INVALID_TASK_VALUE` and a `reason`. Completing an order with unfilled mandatory tasks returns 400 `MANDATORY_TASKS_INCOMPLETE` and `tasks` (`id`, `title`). A checkbox counts as filled only when the stored value is `true`.

`POST /api/work-orders` with `maintenancePlanId` uses the same active-plan, asset-eligibility, and duplicate checks as from-plan. A duplicate is 409 `DUPLICATE_WORK_ORDER`. Client tasks are kept.

`POST /api/work-orders/{id}/cancel` accepts `os.work_orders.execute` or `os.work_orders.create`, clears `CompletedDate`, and is the only exit from `Completed`. Reopening to `Pending` or `InProgress` is 409 `WORK_ORDER_REOPEN_NOT_ALLOWED`. Canceled orders stay out of coverage.

`PUT /api/work-orders/{id}/assignee` requires `os.work_orders.assign`. The assignee must have `os.work_orders.execute`. Null clears the assignee. `GET /api/users/technicians` accepts `core.users.read`, `os.work_orders.create`, or `os.work_orders.assign`.

A duplicate asset tag is 409 `ASSET_TAG_DUPLICATE`. Permanent delete of an asset that has work orders is 409 `ASSET_IN_USE`. Permanent delete of a category used by a plan or by an asset that has work orders is 409 `CATEGORY_IN_USE`. `data-retention-gc` skips those rows and continues the batch.

`assets.inMaintenance` remains the count of assets whose status is `Maintenance`. `workOrders.totalThisMonth` uses the America/Sao_Paulo civil month of `ScheduledDate`. The work-order list is not month-filtered. A customer-visible PNG over 5 MB returns 400 `FILE_TOO_LARGE` (the portal limit is 25 MB, so 6 MB is not 413). Other upload failures use `FILE_TYPE_NOT_ALLOWED` and `FILE_CONTENT_MISMATCH`.

No migration.

## Review follow-up

A mandatory `Image` task does not block completion. There is no work-order upload, so `IsFilled` treats `Image` as filled until that upload exists. No upload endpoint was added.

`UpdateAssigneeAsync` locks the row, reloads it, and returns `409 WORK_ORDER_CLOSED` for `Completed` and `Canceled` orders. The assignee is left unchanged.

`os.work_orders.assign` is already in the permission catalog and is not explicit-grant-only. `TenantAccessBootstrapper.EnsureAsync` adds it to existing Admin and SuperAdmin roles without removing grants those roles already have. User, Technician, and custom roles (including a role named Manager) do not receive it. There is no system role named Manager. No new migration.

## Closed-order write ordering

DEV `67ce7e5` saved an offline text value on work order `0d8c9364` after the operator had completed it in another tab. The value row was updated about 1.6s before `completed_date`. Status and the task write both read the order before taking `FOR UPDATE`, so the task transaction could commit while completion was still outside the lock.

`UpdateTaskValue`, `UpdateStatus`, and `Cancel` now take the `os.work_orders` row lock as the first statement in the transaction, then load the caller and reload the order. `UpdateAssignee` does the same and still does not apply the assigned-only filter. A value that acquires the lock and commits while the order is open stays on the order when completion runs next. A value that waits on a completion or cancel already holding the lock reloads `Completed` or `Canceled` and returns 409 `WORK_ORDER_CLOSED`.

vlr-web `develop` has one task-value write: `PATCH /api/work-orders/{id}/tasks/{taskId}` with `{ value }`. `updateTaskValue` and `processSyncQueue` in `src/lib/offlineSync.ts` both call it. There is no bulk or batch task route.

The second `DELETE /api/assets/{id}` (the permanent one, after `ScheduledDeletionAt` is set) returns 409 `ASSET_IN_USE` when any work order references the asset. The first delete still schedules removal and returns 200. There is no bulk asset delete. The second category delete returns 409 `CATEGORY_IN_USE` when a plan or a work order still references it. No API change was required on those delete paths.
