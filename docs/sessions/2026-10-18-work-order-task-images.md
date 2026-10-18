# 2026-10-18 — work-order task photos

A mandatory image task now blocks completion until the answer is a file stored by this API. `POST /api/work-orders/{id}/tasks/{taskId}/files` accepts one multipart field, `file`, for jpeg, png, or webp up to 5 MB. Content type and magic bytes are both checked. The object goes to the existing private bucket (`Storage:PrivateBucket`, default `catalog-private`) under `{tenantId}/work-orders/{workOrderId}/tasks/{taskId}/{objectId}`. No new storage secret or bucket.

The task `value` becomes the id of `os.work_order_task_files`. `GET /api/work-orders/{id}/tasks/{taskId}/files/{fileId}/url` returns `{ url, isPublic: false }` with the configured signed-url TTL. Both routes require `os.work_orders.execute` and the same work-order visibility as task updates: another tenant, or an order the caller cannot access, is 404. Missing permission is 403 `FORBIDDEN`.

Oversize is 400 `FILE_TOO_LARGE`. A disallowed media type is 400 `FILE_TYPE_NOT_ALLOWED`. Declared type that does not match the bytes is 400 `FILE_CONTENT_MISMATCH`. A closed order is 409 `WORK_ORDER_CLOSED`. Patching an id that is not a file of that task is 400 `INVALID_TASK_VALUE` with reason `INVALID_IMAGE_REFERENCE`. Completion of a required photo without that id is 400 `MANDATORY_TASKS_INCOMPLETE`.

Replacing the photo keeps the same file id and removes the previous object after the database commit. Clearing `value` does not delete the blob. Migration `20261018163615_AddWorkOrderTaskFiles` is included and not applied.

vlr-web still shows that image upload is unsupported. It should post the file, then treat `value` as the file id when it loads or completes the order.
