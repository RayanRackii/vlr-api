# Customer in-app notifications implementation plan

Repositories: vlr-api (implementation), vlr-web (route verification, read only).

**Goal:** Customers receive and manage order status notifications in their own tenant portal.

**Spec:** User request dated 2026-10-29. HTTP/service and publisher tests are the requested test seams.

**Architecture:** Keep customer inbox authorization separate from staff permissions, reuse the
existing inbox response DTOs and NotificationDelivery.ReadAt, and filter both deliveries and
notifications by the customer and tenant claims. Publishers supply customer navigation in the
existing notification payload; the generic inbox has no business-specific branching.

**Constraints:** No Git commands or metadata writes, no migration, no frontend edits, no remote
services. CustomerJwt/Customer policy only. Customer and staff inboxes remain mutually isolated.

**Routes:** GET /api/me/customer-notifications/summary, GET /api/me/customer-notifications,
POST /api/me/customer-notifications/{id}/read, POST /api/me/customer-notifications/read-all.
List uses cursor, unreadOnly and limit (1–50, default 20). Responses reuse staff DTO JSON.
Foreign or absent delivery IDs return 404. Repeated reads succeed without changing ReadAt.

**Customer navigation:** Verified AppRoutes.tsx and catalog/manifest.tsx in vlr-web:
/t/:subdomain/pedidos/:orderId. Store the resolved targetRoute in the existing payload.

## Tasks

- [x] Publisher regression tests: InApp independent of channel configuration, all six customer
  events including preparing, pt-BR copy and target route. Preserve configured external channels.
- [x] Publisher implementation: unconditional customer InApp, preparing InApp template, route payload.
- [x] Customer inbox service and HTTP tests: customer/tenant/recipient isolation, mark-read 404,
  mark-all scope, ordering/cursors, invalid parameters, 401 and mutually exclusive token schemes.
- [x] Implement customer service/controller and register service, reusing inbox response DTOs.
- [x] Run focused checks, dotnet build and the full dotnet test suite; record counts and limitations.
- [x] Review scoped edits without Git and update ROADMAP.md with implementation status and next steps.

## Review focus

- Tenant and customer identity come exclusively from validated JWT claims.
- A delivery and its notification must both belong to the authenticated tenant.
- Customer inbox never includes B2BUser or external-channel deliveries.
- Cursor ordering is stable for equal timestamps and malformed cursors return 400.
- Preparing adds an InApp notification without enabling a new external provider event.

## Implementation notes

- Preparing is intentionally absent from the existing channel configuration registry. The
  first full-suite run caught three matrix-contract regressions when it was added there;
  publisher payload metadata supplies the customer inbox module instead, preserving settings.
- The sandbox denies local sockets required by VSTest. Validation uses the genuine xUnit
  framework in a temporary in-process runner; the standard dotnet test failure is recorded.

## Validation and handoff

- `dotnet build Platform.slnx --no-restore --verbosity quiet -m:1 -nr:false -p:UseSharedCompilation=false`:
  passed, zero errors, four existing warnings (NuGet vulnerability feed unavailable and unreachable test code).
- Standard full `dotnet test Platform.slnx --no-build --no-restore --verbosity quiet
  --logger "console;verbosity=minimal" -m:1 -nr:false`: aborted before tests because the sandbox
  denies the VSTest local socket. Log: `/tmp/custnotif-dotnet-test-full.log`.
- Genuine xUnit framework, in-process temporary runner: **2232 total, 2102 passed, 0 failed,
  130 skipped** (PostgreSQL/Docker unavailable). Log: `/tmp/custnotif-full-suite-final.log`.
- Focused feature and channel configuration regression run: **45 passed**, including **28 new
  feature cases**. OpenAPI snapshot validated by the complete suite.
- Canonical scoped review: `FABLE_MERGE_REVIEW`, `SAFE_WITH_FOLLOWUP`; Critical/High/Medium = 0.
  Low follow-up: move summary grouping into the database if unread inbox volume grows.
- Production Npgsql execution remains unverified locally; run CI with PostgreSQL and smoke in DEV.
  Frontend next step: consume the new inbox endpoints and validate navigation to the order detail.
- Git commands, Git metadata mutations, migrations, frontend writes and remote service actions: none.
