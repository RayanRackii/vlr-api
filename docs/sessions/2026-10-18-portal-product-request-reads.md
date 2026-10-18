# 2026-10-18 — portal product-request reads

Customers who already `POST /api/catalog/portal/product-requests` can read those rows back.

`GET /api/catalog/portal/product-requests` returns `{ items, page, pageSize, total }` for the authenticated customer in the JWT tenant, newest `createdAt` first (`id` descending breaks ties). `page` defaults to 1. `pageSize` defaults to 20 and must be 1–50. A page past the end is 200 with an empty `items` array.

`GET /api/catalog/portal/product-requests/{id}` returns one row. Another customer in the same tenant, or any customer in another tenant, gets 404 with no body. The list for those callers does not include the row.

Each item has `id`, `status`, `description`, `quantity`, `createdAt`, and `files`. A file has `id`, `fileName`, and a signed private `url`. No file means `files: []`. The storage key is not returned. POST still returns `ProductRequestResponse` without a signed URL.

No migration. The catalog module gate and the Customer policy are unchanged.
