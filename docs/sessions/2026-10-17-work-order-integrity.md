# 2026-10-17 — work-order integrity and stable conflict codes

Closed or canceled work orders reject task writes with 409 `WORK_ORDER_CLOSED` after a Postgres row lock and reload. Invalid task values return 400 `INVALID_TASK_VALUE` and a `reason`. Completing an order with unfilled mandatory tasks returns 400 `MANDATORY_TASKS_INCOMPLETE` and `tasks` (`id`, `title`). A checkbox counts as filled only when the stored value is `true`.

`POST /api/work-orders` with `maintenancePlanId` uses the same active-plan, asset-eligibility, and duplicate checks as from-plan. A duplicate is 409 `DUPLICATE_WORK_ORDER`. Client tasks are kept.

`POST /api/work-orders/{id}/cancel` accepts `os.work_orders.execute` or `os.work_orders.create`, clears `CompletedDate`, and is the only exit from `Completed`. Reopening to `Pending` or `InProgress` is 409 `WORK_ORDER_REOPEN_NOT_ALLOWED`. Canceled orders stay out of coverage.

`PUT /api/work-orders/{id}/assignee` requires `os.work_orders.assign`. The assignee must have `os.work_orders.execute`. Null clears the assignee. `GET /api/users/technicians` accepts `core.users.read`, `os.work_orders.create`, or `os.work_orders.assign`.

A duplicate asset tag is 409 `ASSET_TAG_DUPLICATE`. Permanent delete of an asset that has work orders is 409 `ASSET_IN_USE`. Permanent delete of a category used by a plan or by an asset that has work orders is 409 `CATEGORY_IN_USE`. `data-retention-gc` skips those rows and continues the batch.

`assets.inMaintenance` remains the count of assets whose status is `Maintenance`. `workOrders.totalThisMonth` uses the America/Sao_Paulo civil month of `ScheduledDate`. The work-order list is not month-filtered. A customer-visible PNG over 5 MB returns 400 `FILE_TOO_LARGE` (the portal limit is 25 MB, so 6 MB is not 413). Other upload failures use `FILE_TYPE_NOT_ALLOWED` and `FILE_CONTENT_MISMATCH`.

No migration.
