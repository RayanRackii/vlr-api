# Platform Taxonomy + Catalog assignments

Repositories: `vlr-api`, `vlr-web`  
Branch: `feat/taxonomy` in both repositories. Delivery: **API first → Web**.

Status: settled design. API implementation in working tree; migration generated, not applied.
No Git, connection/secret changes, deployment or remote migration in this session; PM owns delivery.
Build passes with zero warnings/errors. In-process xUnit suite: 1411 passed, 0 failed, 88 skipped
(50 passed + 7 skipped new taxonomy cases). Standard VSTest cannot open its socket in this sandbox;
Docker is unavailable. [Session validation log](../../sessions/2026-10-20-platform-taxonomy.md). Web implementation and
CONTEXT/ROADMAP mirror belong to the web task. Portal/Slot remains the product priority.

## Delivery checklist

- [x] API schema, permissions, shared CRUD and typed Catalog assignments/filter/options integration.
- [x] EF migration `AddPlatformTaxonomyAndCatalogAssignments`, designer/snapshot; idempotent permissions; no backfill.
- [x] Unit/HTTP tests and PostgreSQL tests authored (hierarchy, isolation, atomicity, races, cleanup, search branches).
- [ ] Execute required PostgreSQL tests with Docker; run standard `dotnet test` where runner sockets are allowed.
- [ ] Authorized DEV migration and API delivery, followed by Web management/filter/assignment UI.
- [ ] Required delivery reviews and Merge Risk Gate before integration; no release authorization implied.

## Settled spec

**DECISIONS**

- Basis: local API HEAD `633212e`, web HEAD `0eead5b`; remote freshness unverified. Read-only inspection; no files changed or tests run.
- Actual stack: .NET 10, EF Core 10/Npgsql, PostgreSQL; React 19/Vite, Axios, Zod, page-local state.
- Build **Taxonomy** as a shared platform capability, without a new commercial entitlement. Catalog is its first consumer; Rentals integration is a later task.
- Keep existing `AssetCategory` unchanged: it means operational “Tipo” and participates in PMOC/Rentals. Catalog must remain independent of Asset Registry ([ADR 0004](../../adr/0004-module-dependencies-asset-registry.md)).
- Reject polymorphic assignments: `(resourceType, resourceId)` cannot enforce target existence or tenant ownership through ordinary FKs.
- Use shared tenant categories/tags plus **two typed join tables per consumer**, one for categories and one for tags. Future consumers add joins, without replacing resource identity.
- Categories are many-to-many; maximum **3 levels**, counting root as level 1. Parent changes must preserve the depth of the entire subtree.
- Selecting a category includes itself and all descendants. Multiple assigned categories match by membership in that subtree.
- Selected tags use **AND** semantics. Category, tags and existing search/status predicates also combine with AND.
- Categories and tags are shared across the tenant’s consumers; edits affect every consumer. Portal discovery exposes only labels used by that consumer.
- Slugs are immutable, lowercase ASCII, unique per tenant **within each kind**; category uniqueness is tree-wide, not per parent. Queries use UUIDs.
- Delete only unused definitions: category with children or any assignment → 409; assigned tag → 409. No implicit subtree deletion or detachment.
- This settles the requested design; newly discovered product/security uncertainties still require the repository’s Human Decision Gate.

**API SPEC**

- Follow existing Controller → Service → DTO structure, scoped DI and asynchronous EF access; register `AddTaxonomyModule()` in `Platform.Api/Program.cs`.
- New entities: `TaxonomyCategory`, `TaxonomyTag`, `CatalogProductCategory`, `CatalogProductTag`; all implement `ITenantScoped`.
- Definitions live in `core.taxonomy_categories` and `core.taxonomy_tags`; joins live in `catalog.catalog_product_categories` and `catalog.catalog_product_tags`.
- Category fields: existing Entity identity/timestamps, `TenantId`, `ParentId?`, `Name`, `Slug`, `Icon?`, `SortOrder`.
- Tag fields: existing Entity identity/timestamps, `TenantId`, `Name`, `NormalizedName`, `Slug`; free tenant-defined labels, without a platform enum.
- Validation: trimmed category name 1–100 characters; tag name 1–50; slug 1–80 matching `^[a-z0-9]+(-[a-z0-9]+)*$`; sortOrder 0–100000.
- Tag normalized name = Unicode NFC, collapsed whitespace, invariant lowercase; unique `(TenantId, NormalizedName)`.
- Category names may repeat; slugs distinguish them. Both kinds require explicit slug on creation; reject slug changes on update.
- Icon is an optional key from a small shared Lucide allowlist (`folder`, `tag`, `package`, `map-pin`, `calendar`, `star`); no SVG markup or URLs.
- Add unique `(TenantId, Id)` principal keys on definitions and CatalogProduct.
- Join PKs: `(TenantId, ProductId, CategoryId)` / `(TenantId, ProductId, TagId)`.
- Composite FKs enforce same-tenant product, definition and parent references; parent FK uses `(TenantId, ParentId)`.
- Product deletion cascades its joins; definition and parent deletion use RESTRICT. Add tenant FKs and reverse indexes `(TenantId, CategoryId, ProductId)` / tag equivalent.
- Reject self-parenting, cycles and subtree depth >3. Do not persist derived depth/path.
- Serialize taxonomy mutations and catalog assignment changes with a transaction-scoped `SELECT … FOR UPDATE` on the current `core.tenants` row; lock before validation, hold through commit.
- This serializes hierarchy changes, deletion and assignment writes within a tenant. Keep storage uploads outside this transaction.
- Require non-null ambient tenant in every taxonomy service; add explicit tenant predicates as well as GQFs. The existing GQF becomes permissive when tenant is null ([AppDbContext](../../../Core/Platform.Core.Infrastructure/Persistence/AppDbContext.cs)).
- Resolve referenced UUIDs only within the current tenant; cross-tenant/missing write references receive the same 400 validation response.
- Add `Permissions.Core.TaxonomyRead = "core.taxonomy.read"` and `TaxonomyManage = "core.taxonomy.manage"` to Permissions/PermissionCatalog, moduleKey null.
- Follow current Admin wildcard/bootstrap behavior; no new default User/Technician grants. Custom roles receive explicit permissions; never authorize by role name.
- `GET /api/taxonomy/categories` and `/tags`: `core.taxonomy.read`; return flat DTO arrays.
- `POST /api/taxonomy/categories|tags`: manage permission, 201 plus Location; `GET /{id}`: read; `PUT /{id}`: manage, 200; `DELETE /{id}`: manage, 204.
- Category create DTO `{name,slug,parentId?,icon?,sortOrder}`; update `{name,parentId,icon,sortOrder}` fully replaces mutable fields.
- Tag create DTO `{name,slug}`; update `{name}`. Category response `{id,name,slug,parentId,icon,sortOrder}`; tag response `{id,name,slug}`.
- Sort categories by `sortOrder,name,id` within each parent; tags by `name,id`. No drag-and-drop/reorder endpoint.
- Errors retain `{error,code}`: 400 `TAXONOMY_INVALID`; 409 `TAXONOMY_DUPLICATE` or `TAXONOMY_IN_USE`; missing addressed entity 404. Translate matching FK/unique violations.
- Add optional `taxonomy:{categoryIds:Guid[],tagIds:Guid[]}` to existing catalog create/update DTOs; maximum 20 distinct IDs per array.
- Omitted/null taxonomy preserves existing assignments on update and creates empty assignments on create; supplied object requires both arrays; `[]` clears that kind.
- Save product changes and assignment replacement atomically in CatalogProductService. Invalid taxonomy must leave product and joins unchanged.
- Append `taxonomy:{categories:CategoryDto[],tags:TagDto[]}` to every staff/portal product response; direct assignments only, with empty arrays by default.
- Extend existing staff `GET /api/catalog/products` and Customer `GET /api/catalog/portal/products` with shared `category=<uuid>&tags=<uuid>,<uuid>`.
- Preserve existing array responses, ordering, staff `name/code/isActive`, portal `search`, active-only visibility and customer-visible file projection.
- Empty query values mean absent; malformed UUIDs, empty CSV elements or >20 distinct tags → 400; duplicate UUIDs deduplicate.
- Unknown/cross-tenant filter UUIDs produce an empty result, never silently remove the filter.
- Resolve descendants from the tenant tree; apply join `EXISTS` predicates in SQL **before** portal search’s existing unaccent/fallback branches. Batch assignment loading; avoid N+1.
- Add consumer endpoints `GET /api/catalog/taxonomy-options` and `/api/catalog/portal/taxonomy-options`, returning `{categories,tags}`.
- Staff options: existing Catalog module gate + products.read, all tenant definitions; portal options: Customer policy + module gate, only active-product assignments and category ancestor closure.
- Shared taxonomy endpoints have no Catalog entitlement dependency. Customers cannot access management endpoints; no generic public endpoint or arbitrary resource assignment route.
- Future consumers reuse DTOs, parsing, hierarchy validation and transaction helper; own their typed joins, query predicates, authorization and discovery endpoints.
- Extend AdminTenantService deletion: remove this tenant’s joins before products/definitions; delete categories deepest-first, using its existing explicit tenant-scoped cleanup pattern.
- Migration: `AddPlatformTaxonomyAndCatalogAssignments`, generated with EF; include designer/snapshot and idempotent permission inserts. Existing products remain unclassified; no AssetCategory backfill.
- New files: Domain `Entities/{TaxonomyCategory,TaxonomyTag,CatalogProductCategory,CatalogProductTag}.cs`; matching Infrastructure `Persistence/Configurations/*Configuration.cs`.
- New files under `Platform.Api/Modules/Taxonomy/`: `TaxonomyModuleExtensions.cs`, `Controllers/TaxonomyController.cs`, `Dtos/TaxonomyDtos.cs`, `Services/TaxonomyService.cs`, `Services/TaxonomyMutationScope.cs`.
- Modify `AppDbContext.cs`, `CatalogProductConfiguration.cs`, Permissions/PermissionCatalog, Program, Catalog DTOs/controllers/product+portal services/module registration, AdminTenantService.
- Tests under `tests/Platform.Api.Tests/Taxonomy/`: hierarchy/depth/cycles, normalization, deletion, omitted/null/empty writes, atomic rollback, tenant absence and foreign references.
- Extend Catalog and authorization tests: descendants, tags AND, combined filters, inactive visibility, both search branches, portal metadata isolation, permission/module matrix and old request compatibility.
- PostgreSQL Testcontainers tests must prove composite FKs, uniqueness races, concurrent reparenting/deletion/assignment and tenant cleanup; InMemory alone is insufficient.
- Validate `dotnet build` and `dotnet test tests/Platform.Api.Tests/Platform.Api.Tests.csproj`; required Docker tests must execute, not merely skip.

**WEB SPEC**

- New generic feature `src/features/taxonomy/`; use existing Axios, Zod, React Hook Form, shadcn and page-local state patterns.
- New files: `schemas/taxonomySchemas.ts`, `services/taxonomyService.ts`, `lib/taxonomyQuery.ts`, `components/TaxonomyFilters.tsx`, `components/TaxonomyAssignmentFields.tsx`, `pages/TaxonomyPage.tsx`.
- `TaxonomyFilters` is controlled: options, selected category UUID, tag UUIDs, loading/disabled and onChange; no resource names, API calls or auth knowledge.
- Category selector renders indented hierarchy; tag checkbox multiselect explains “Matches all selected tags”; include clear controls, accessible labels and the shared icon allowlist.
- Add `/configuracoes/categorias-tags` to AppRoutes/navigation under `core.taxonomy.read`; mutations require manage. No Catalog entitlement on this shared page.
- Page supports category/tag create, rename, parent selection, icon/order editing and confirmed delete; explain that changes affect all tenant consumers.
- Use RHF+Zod validity gates and FormPrimaryButton; preserve values after server errors; translate duplicate/in-use errors. No optimistic deletion.
- Catalog management stays at `/catalogo/produtos`; add assignment controls to its existing create/edit dialog, populated from staff consumer options.
- Include taxonomy in the existing product save, including `lib/createCatalogProductWithFiles.ts`; preserve its retry/product-ID behavior so file failures cannot duplicate products.
- Add category/tag table columns. Put their filters inside column-header popovers, per repository convention; mount reusable filter controls there.
- Portal mounts filters beside existing search in PortalCatalogPage. Keep branded tenant shell, host/path routing and customerApi transport.
- Both lists send taxonomy filters to the API; retain existing unrelated filters. Serialize tags explicitly as comma-separated UUIDs, avoiding Axios array brackets.
- Persist `category`, `tags` and portal `search` in URL search params; preserve unrelated parameters and support browser Back/Forward.
- Debounce text search 300ms; category/tag changes fetch immediately. Cancel requests and use generation checks so stale responses cannot overwrite current tenant/filter state.
- Clear tenant-scoped options/results on session or tenant change; distinguish empty catalog, no matches and request failure; provide retry.
- Portal options remain independent of current filter selection; no dynamically shrinking facets or counts.
- Preserve cart quantities/items when filtering. Resolve missing cart-product summaries through product detail reads, independently of filtered results; never treat a hidden product as “Sob consulta”.
- Update `features/catalog/{schemas/catalogSchemas.ts,services/catalogService.ts,services/catalogPortalService.ts,pages/CatalogProductsPage.tsx,pages/PortalCatalogPage.tsx}` and the product-save helper.
- Update `routes/AppRoutes.tsx`, `components/layout/navigation.ts`, locale `common.json` and permissionCopy files for pt-BR/en/es; keep system/user-entered labels untranslated.
- Add colocated Vitest tests for serialization, schemas, filter keyboard use, URL restoration, stale responses, permission gating, assignment retries, empty/error states and cart preservation.
- Validate `npm run build`, `npm test`, `npm run lint`; manually test root→child filtering plus two required tags in staff and both portal URL forms.
- Delivery: same branch name in both repos; migration through the authorized DEV workflow, then **API first → Web**. Required reviews and Merge Risk Gate apply.
- Implementers record this spec in the API’s cross-repo plan, update both ROADMAPs and distinguish Taxonomy from existing ResourceCategory in both CONTEXT mirrors.
- Next API step: schema, permissions and tested Catalog integration. Next Web step: shared management/filter UI and Catalog consumption; preserve the roadmaps’ portal/Slot priority.
