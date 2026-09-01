# Catalog & Orders Context Pack

Derived context — NOT canonical.

- Scope: Catalog & Orders module (tenant-owned product catalog + customer requests)
- Repositories: vlr-api (canonical domain); vlr-web (UI)
- Canonical sources: `CONTEXT.md`; `docs/plans/active/2026-08-28-catalog-orders.md`
- Last verified: 2026-08-28

## Purpose

Load when the question is CatalogProduct, CatalogOrder, ProductRequest, catalog files/storage, catalog notifications, or B2C catalog/cart/my-orders.

## Canonical sources

- `CONTEXT.md` — glossary
- `docs/plans/active/2026-08-28-catalog-orders.md` — approved v1 spec
- `Core/Platform.Core.Domain/Constants/PlatformModules.cs` — key `catalog`
- `Core/Platform.Core.Domain/Constants/Permissions.cs` — `Permissions.Catalog`

## Domain vocabulary

- **CatalogProduct** — tenant-owned offering to Customers. Not Asset (inventory) and not Rentable.
- **CatalogOrder** — Customer request for N products × quantities. Not payment. Not Reservation.
- **CatalogOrderItem** — line with name/price snapshots at submit time.
- **ProductRequest** — Customer ask for something not in the catalog. Does not auto-create a product.
- **CustomerType** — Individual (CPF) or Company (CNPJ). One Customer account = one document.
- **Document** — digits-only CPF (11) or CNPJ (14) on Customer. Distinct from `Tenant.TaxId`.
- **Notification** — persisted platform event. Distinct from in-process `NotificationMessage`.

## Current model

Tenant-wide catalog (no Unit). Price nullable = “Sob consulta”. Orders start Requested and need approval. After Approved, items freeze. B2B `catalog.orders.manage` can cancel through Ready. Customer cancels only Requested. Cart is client-side only.

Files: public bucket for customer-visible images; private bucket + signed URL for technical files. Keys `{tenantId}/{productId}/{fileId}`.

Notifications: generic Notification + Delivery + Attempt. Outbox = Delivery(Queued). InApp committed with the order. External channels default off. `AllowExternalDelivery` unset = false. Per-channel `AllowExternalEmail` / `AllowExternalWhatsApp` override the global flag.

## Critical invariants

- Tenant isolation via ITenantScoped + GQF; B2C also needs explicit catalog module gate
- Never authorize with role name
- Internal files never public
- Client-supplied prices ignored
- Do not drop `Customer.Cpf` in v1
- Tests must not hit real Meta/Resend/SMS/Supabase prod

## Current contracts

See spec HTTP tables: `/api/catalog/*` B2B; `/api/catalog/portal/*` B2C.

## Important implementation seams

- `Platform.Api/Modules/Catalog/`
- `Platform.Api/Storage/`
- `Platform.Api/Notifications/`
- `vlr-web/src/features/catalog/`
- `vlr-web` navigation + `CustomerAppLayout.modulePath`

## Known gaps / open constraints

- WhatsApp webhook → Delivered persistence is follow-up
- No real SMS provider (activating SMS is rejected)
- ProductRequest conversion is follow-up
- Remote migration not applied in the implementation PR

## Do not assume

- Global/shared Rolvix catalog
- Per-customer pricing
- Checkout/payment
- CatalogProduct linked to Asset
- In-memory NotificationQueue as source of truth for orders
