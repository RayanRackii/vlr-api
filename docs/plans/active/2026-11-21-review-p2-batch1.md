# Review P2 batch 1 (Fable review follow-ups, 2026-11-21)

Source: `C:\Users\rayan\Rolvix\reviews\` (README.md P2 list, fable-vlr-api-review.md, fable-vlr-web-review.md) +
session findings. Owner said "work independently" (2026-11-21). Generic product; tenant data disposable.

## P2-API-1 (vlr-api, branch `fix/p2-api-batch1`, NO migration)
1. `ProductRequestResponse` gains `customerName` and `customerEmail` (nullable) for staff reads
   (list + any detail), joined from the customer row (tenant-scoped). Update OpenAPI snapshot by hand.
2. `PUT /api/catalog/products/{id}/resource-requirements` with an invalid body returns a misleading
   400 `TAXONOMY_INVALID`. Return a requirement-specific code (e.g. `catalog.resource_requirement_invalid`)
   with the validation message; keep taxonomy errors for taxonomy.
3. PMOC sweep: one bad plan/asset aborts the sweep for all tenants (`Jobs/PmocEngineJob.cs` ~127, 164-179).
   Isolate per tenant and per plan (try/catch with structured log, continue), keep the job's overall result.
4. WhatsApp webhook accepts unsigned payloads when `WhatsApp:AppSecret` is empty
   (`Modules/Webhooks/Controllers/WhatsAppWebhookController.cs` ~84-93): reject (401) when the secret is
   not configured, except in Development/Testing environments; log a startup warning when missing.
5. Reservations can be created on soft-deleted (`ScheduledDeletionAt != null`), `Inactive` or `Maintenance`
   assets (`Modules/Rentals/Services/ReservationService.cs` ~525-531 checks only `IsActive && IsRentable`):
   refuse with the existing unavailable error; same for portal availability listing (do not offer them).
Tests for each (fail without the fix). Existing tests stay green.

## P2-WEB-1 (vlr-web, branch `fix/p2-web-batch1`)
1. Admin screen to edit the allow-listed tenant settings (vlr-api#383): in the admin tenant page, a
   "Configurações avançadas" panel using `GET/PUT /api/admin/tenants/{tenantId}/settings/{key}` for
   `rentals.booking_ui.enabled` (switch) and `rentals.teacher_lessons.rental_asset_ids` (multi-select of that
   tenant's rental assets — use an existing admin endpoint that lists a tenant's rental assets if one exists;
   otherwise a JSON-free list input of ids is NOT acceptable → show only when an endpoint exists and note the gap).
   Update `openapi/openapi.json` from the API snapshot on origin/develop + `npm run gen:api`.
2. `AssetsPage` empty-state "Criar tipos" button links to `/ativos/tipos` even when rentals is off → hide it (or
   point to an existing generic place) when the tenant has no `rentals` module.
3. Stale lazy chunks after a deploy: `React.lazy` caches the rejected import and "Tentar novamente" cannot recover
   (`src/routes/lazyPage.ts:17-20`, `src/components/layout/AppErrorBoundary.tsx:36-38`). On a chunk-load error,
   do one guarded full reload (sessionStorage flag to avoid loops) and make retry re-create the lazy component.
4. Strict Zod enums on critical payloads: `/me` `role` closed enum (`src/features/users/schemas/userSchemas.ts:3-9,25`)
   and `notificationChannelSchema` missing `Push` (`src/features/catalog/schemas/catalogSchemas.ts:33-38`):
   accept unknown strings (tolerant reader) and add `Push`; unknown values render a neutral label.
5. Dashboard chosen by legacy role (`src/features/dashboard/pages/DashboardPage.tsx:124-133`): choose by
   permissions/modules instead (technician view only when the user lacks broader read permissions).
6. Work-order default date uses the UTC day (`src/features/workOrders/pages/CreateWorkOrderPage.tsx:104`):
   use the tenant time zone (`TenantTimeZoneProvider`/existing helper) for "today".
7. Portal: load failures labelled "tenant not found" (`src/features/tenantPortal/components/CustomerAppLayout.tsx:103-112`)
   → show "not found" only for 404, otherwise a retryable load-failed state; cart prints product GUIDs when the
   product list fails (`src/features/catalog/pages/PortalCartPage.tsx:132-136,262`) → neutral placeholder + retry.
Tests (vitest) for each logic change; pt-BR/en/es strings.
