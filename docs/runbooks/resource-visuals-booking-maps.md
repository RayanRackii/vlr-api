# Resource visual catalog and booking maps

Local API implementation of `ux-map-spec.md` PRs 2, 3, 5, 6 and API/seed 10. No environment has been migrated or deployed by this task. Templates are global; resources, segments, maps and settings remain tenant scoped. Existing taxonomy, pricing, availability, reservations and queue contracts are preserved.

## Endpoints

All paths below start at the API origin. New errors use `{code,error}`. Tenant routes require the Rentals module and the appropriate permission; platform routes require `PlatformAdmin` plus the listed platform action policy.

| Method | Route | Purpose / permission |
|---|---|---|
| GET, POST | `/api/admin/resource-visual-templates` | List/create; `platform.resource_templates.read/manage` respectively |
| GET, PUT, DELETE | `/api/admin/resource-visual-templates/{id}` | Detail/edit/archive; same read/manage split |
| GET, POST | `/api/admin/resource-visual-templates/{id}/revisions` | List/create draft; same read/manage split |
| PUT | `/api/admin/resource-visual-templates/{id}/revisions/{revisionId}` | Edit draft; `platform.resource_templates.manage` |
| POST | `/api/admin/resource-visual-templates/{id}/revisions/{revisionId}/publish` | Sanitize/publish immutable art; `platform.resource_templates.manage` |
| GET, POST | `/api/admin/resource-visual-tags` | List/create; `platform.resource_visual_tags.manage` |
| PUT, DELETE | `/api/admin/resource-visual-tags/{id}` | Edit/delete, or archive when referenced; `platform.resource_visual_tags.manage` |
| GET, PUT | `/api/admin/tenants/{tenantId}/resource-segments` | Explicit tenant; `platform.tenant_segments.manage` |
| GET | `/api/rental-assets/visual-templates` | Picker; `rentals.assets.read` OR `rentals.layouts.read` |
| GET | `/api/rental-assets/visual-template-tags` | Active facets; same picker permissions |
| GET, PUT | `/api/rentals/resource-segments` | `rentals.segments.read/write` respectively |
| POST | `/api/rental-assets` | Existing create flow, with optional visual assignment; `rentals.assets.write` |
| PUT | `/api/rental-assets/{id}` | Existing update flow, with optional visual assignment; `rentals.assets.write` |
| GET, POST | `/api/rental-layouts` | Existing list/create; `rentals.layouts.read/write` |
| GET, PUT, DELETE | `/api/rental-layouts/{id}` | Existing detail/update/delete; `rentals.layouts.read/write` |
| GET, PUT | `/api/rental-layouts/{id}/composition` | Full versioned composition; `rentals.layouts.read/write` |
| GET, PUT | `/api/rentals/booking-map-settings` | `rentals.map_settings.read/write` respectively |
| GET | `/api/public/tenants/{subdomain}/booking-maps` | Active map summaries, default and public settings |
| GET | `/api/public/tenants/{subdomain}/booking-maps/{id}` | Public composition and deduplicated referenced published revisions |
| GET | `/api/public/resource-visual-revisions/{id}/art.svg` | Published sanitized art, immutable cache and ETag |
| POST | `/api/admin/tenants/{tenantId}/booking-maps/demo` | Development only; `platform.booking_maps.seed_demo` |

The existing Asset Registry create/update/bulk consumers also carry the optional visual configuration through their existing permissions. `visualTemplateRevisionId` binds a published revision; `capacityPeople` is informational and does not change `totalQuantity`. Updates distinguish omission (preserve) from explicit null (clear). `confirmVisualSuggestions` explicitly applies eligible suggestions without replacing existing configuration.

Picker parameters: `search`, `segmentIds`/`segmentIds[]`, `tagIds`/`tagIds[]`, `allSegments`, `page`, `pageSize`. Repeated IDs within a facet use OR; different facets and text terms use AND. Default discovery uses the tenant's active segments; no segments means all. `allSegments=true` removes the implicit segment filter. Segments are preferences, not entitlements.

Composition PUT receives the complete map with `expectedVersion`, `name`, optional `unitId`, `isActive`, `sortOrder`, `aspectRatio`, `widthPercent`, `items` and `decorations`. Composition and public detail reads materialize metadata/version, items, decorations and referenced art within one repeatable-read transaction. Read the current composition before editing. Preserve placement IDs; omitted existing IDs are reconciled by resource. Version conflicts return 409 `MAP_VERSION_CONFLICT`; invalid compositions are rejected atomically. Legacy layout PUT keeps decorations and new item fields, and increments the version.

Settings PUT requires `expectedVersion` and four `#RRGGBB` colors: `availableColor`, `takenColor`, `unavailableColor`, `selectedColor`. It also accepts `showPriceOnBlock`, `timeFilterMode` (`Periods`/`FromTime`), `defaultView` (`Map`/`List`), and nullable `defaultLayoutId`. Missing settings read as version 0. Deleting the default map clears the preference in the same transaction.

The new public map routes require the existing per-tenant `Rentals:BookingUi:EnabledTenantIds` flag (`newBookingUiEnabled` in branding). Seeding never enables it. Public geometry uses ETag revalidation; availability still comes from `GET /api/public/tenants/{subdomain}/availability/days/{date}`. The legacy public layout route retains its flag behavior. Archived templates remain renderable through already assigned published revisions.

## Migrations

Apply through [the migration workflow](database-migrations.md); these files were generated and validated locally only:

1. `20261021225838_AddResourceVisualCatalog`: global templates/revisions/tags, links, tenant segments and published-revision update guard.
2. `20261021231855_ExtendRentalMapsAndVisualAssignments`: nullable visual/capacity fields, geometry defaults, ordering/version, settings/decorations and tenant-composite FKs. A locked preflight rejects inconsistent legacy tenant/unit links before altering constraints. No automatic ownership repair.
3. `20261021231941_SeedResourceVisualCatalogV1`: idempotent IDs/slugs, 13 original published SVGs, 15 global tags and pt-BR/en/es translations. SQL data only; never change V1 art or its helpers to update published revisions.
4. `20261021232049_AddBookingMapPermissions`: four tenant permissions plus grants to existing Admin/SuperAdmin roles. Future provisioning uses the permission catalog. Platform action policies are excluded from tenant-grantable permissions.

All have Down. Map Down restores the old layout/item/unit constraints and removes only extensions. Seed Down rejects changed, extended or referenced seeds with `RESOURCE_VISUAL_SEED_DOWN_BLOCKED`; it never deletes resources or reservations. Export new configuration and run a preflight before any schema downgrade. Operational rollback uses the booking UI flag while retaining schema/data.

## Development demo

Authenticate with a platform-admin bearer token. The process must run with `ASPNETCORE_ENVIRONMENT=Development`; other environments return 404 `DEV_ONLY`. `BookingMaps:DemoSeed:AllowedTenantIds` defaults to an empty list, so no tenant may be seeded without explicit deployment configuration. Non-allowlisted tenants return 403 `DEMO_SEED_TENANT_NOT_ALLOWED` before the ambient tenant context is changed or the seed service is invoked.

This repo does not include an `appsettings.Development.json` for the DEV deployment. For the approved FICC DEV tenant, configure the Development process with this environment variable (never add this tenant to the shared `appsettings.json` demo allowlist):

```text
BookingMaps__DemoSeed__AllowedTenantIds__0=b27c9a0a-d585-4626-8c13-1468c5a43519
```

The environment variable is documented here, not applied to any deployment by this task. Use the explicit tenant UUID and existing **RentalAsset** IDs from that tenant and unit. Do not supply Asset IDs, inferred inventory or new resources.

```http
POST /api/admin/tenants/{tenantId}/booking-maps/demo
Authorization: Bearer <platform-admin-token>
Content-Type: application/json

{
  "unitId": "<existing-unit-uuid>",
  "name": "Pavilhão de recursos",
  "aspectRatio": 1.75,
  "items": [
    {
      "rentalAssetId": "<existing-rental-asset-uuid>",
      "xPercent": 5,
      "yPercent": 10,
      "widthPercent": 30,
      "heightPercent": 60,
      "rotation": 0,
      "lockAspectRatio": true,
      "zIndex": 1
    }
  ],
  "decorations": [
    {
      "kind": "Walkway",
      "label": "Entrada",
      "xPercent": 0,
      "yPercent": 85,
      "widthPercent": 100,
      "heightPercent": 10,
      "zIndex": 0,
      "style": { "fill": "#D1D5DB" }
    }
  ]
}
```

The response is the composition. The seed creates one deterministic demo map per tenant. Repeating it returns that map without overwriting edits. It validates the supplied manifest, does not assign unconfirmed modalities/art, and does not create assets, pricing, schedules or reservations. Use the regular composition endpoint and current version for later edits.

To inspect customer output after enabling the flag for an approved DEV tenant, GET the public list and returned map ID. Confirm inactive resources are absent, foreign map IDs return no composition, and `If-None-Match` returns 304 for unchanged geometry. Saving with a stale version must return 409.

## Validation

Build: `dotnet build --no-restore -m:1 -p:UseSharedCompilation=false`. Tests: `dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj --no-build --no-restore`. Configure `ROLVIX_TEST_POSTGRES_CONNECTION` to an isolated loopback PostgreSQL, or provide Docker, for the relational/concurrency tests.

In this sandbox the SDK is `/home/rolvixpilot/.dotnet/dotnet`; use `DOTNET_CLI_HOME=/tmp/mapi-dotnet`. VSTest's socket is blocked. The xUnit in-process runner under `/tmp/mapi-map-inproc` was executed from `tests/Platform.Api.Tests/bin/Debug/net10.0` so repository-relative tests resolve correctly. Final suite: **1675 total, 1578 passed, 0 failed, 97 skipped**. Feature-focused tests plus permission catalog regressions: **96 total, 93 passed, 0 failed, 3 skipped**. The three feature skips cover registry/FK PostgreSQL and concurrent segment replacement.

PostgreSQL 16 single-backend mode, using the isolated `/tmp/mapi-map-pg-single` cluster without sockets, separately validated Up→Down→Up, legacy asset/resource/layout/item preservation, seed idempotency/counts, admin grants, seed Down guards for references and modified metadata, published-art immutability, composite tenant FKs, missing revision FKs and the legacy-link preflight. This does not execute multi-connection concurrency tests.


## PR #248 review corrections

The two reported CI failures were fixture errors before the tested operation: the segment concurrency tenant had a 41-character TaxId for a `varchar(20)` column, and the registry tests reused a TaxId under a class-scoped PostgreSQL database, violating its unique index. Fixtures now generate separate IDs within the mapped length; the FK assertions remain intact.

`BookingMapReadConcurrencyTests` pauses an admin/public reader after metadata, commits a composition save on another connection, then checks that the pending read contains only the original version and a subsequent read contains only the new version. It also exercises fresh/idempotent demo seed responses and write-transaction cleanup. `ResourceVisualPostgresMigrationTests` executes all four migrations Up→Down→Up against its own fixture database, compares legacy row snapshots, checks seeded catalog/admin grants and verifies actual seed rollback refusal for referenced or edited seeds.

Both classes use `DockerFact` and run in the existing PostgreSQL CI job (`REQUIRE_POSTGRES_TESTS=true`). To reproduce all affected relational tests locally, configure an isolated loopback `ROLVIX_TEST_POSTGRES_CONNECTION`, then run:

```sh
dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj --configuration Release --filter 'FullyQualifiedName~BookingMapReadConcurrencyTests|FullyQualifiedName~ResourceVisualPostgresMigrationTests|FullyQualifiedName~ResourceSegmentsConcurrencyTests|FullyQualifiedName~AssetVisualAssignmentPostgresTests'
```

HTTP regression coverage in `BookingMapHttpTests` verifies the absent/empty allowlist, a different allowed tenant, authorized seeding, production denial and tenant-manifest rejection. No remote migration, configuration or deployment is performed by these tests.

PR #248 final local validation: solution build **0 warnings/errors**; in-process xUnit suite **1679 total / 1580 passed / 0 failed / 99 skipped**, focused **97 / 92 / 0 / 5 PostgreSQL skipped**. VSTest and loopback PostgreSQL sockets are blocked by the sandbox; the real PostgreSQL regressions above require CI or a local environment with sockets.
