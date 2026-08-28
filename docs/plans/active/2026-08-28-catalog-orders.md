# 2026-08-28-catalog-orders

Status: approved

## Goal / Problem

Deliver tenant-scoped **Catalog & Orders** (PT-BR: Catálogo & Pedidos) so each tenant offers its own products to its B2C Customers, Customers submit quantity requests (not payments), and B2B users with capabilities manage approval/fulfillment plus a notification center.

## Visible behavior

- B2B (module `catalog` active + permissions): CRUD products, files (public images / private technical), list ProductRequests, manage orders through the state machine, view/resend notifications, configure channels per event.
- B2C (module active): register as Individual (CPF) or Company (CNPJ); browse active catalog; client-side cart; submit order; own orders + cancel while Requested; request a missing product.
- No checkout, stock, freight, Asset coupling, Organization, or auto-approval.

## Repositories

- vlr-api
- vlr-web

Merge order: **API first**, then web. Same branch name: `feat/catalog-orders`.

## Relevant existing ADR / rules

- `CONTEXT.md` (canonical glossary; Customer vs User vs Tenant)
- Tenant RBAC v1 (`docs/plans/active/2026-08-27-tenant-rbac-v1.md`)
- `.cursor/rules/` product, architecture, conventions
- Notification providers: Resend / Meta / Dev; Hangfire already in host
- **Do not** apply `.cursor/rules/30-rentals.mdc` as Catalog rules; reuse only FOR UPDATE + snapshot + RoundMoney patterns

## Architecture route

- rolvix-architect (baseline audit 2026-08-28)
- rolvix-deep-architect: **Merge Risk Gate only** (Fable required for this PR)

## Execution route

- api-implementer (vlr-api)
- web-implementer (vlr-web)
- api-reviewer / web-reviewer
- Parent: PR, GLM dossier, Fable, squash to `develop`

## Confirmed decisions

| ID | Decision |
|---|---|
| HD-1 | Admin system role keeps wildcard. Authorize only via `RequirePermission`. Never `if Role == Admin`. |
| HD-2 | PF/PJ in this v1. `CustomerType` Individual or Company. `Document` digits. No Organization*. 1 account = 1 CPF or 1 CNPJ. |
| HD-3 | B2B `catalog.orders.manage` may cancel Requested, Approved, Preparing, Ready (reason required + notify Customer + History). Customer cancels only Requested. |
| HD-4 | Customer-visible images: **public** bucket URLs. Internal/technical: **private** + short signed URL after auth. Listing still tenant-scoped. |
| HD-5 | Channel defaults: InApp on; Email/WhatsApp/SMS off. `Notifications:AllowExternalDelivery` unset/null = **false** in every environment. |

Technical (not reopened): no Cart entity; no persisted DocumentType; outbox = `NotificationDelivery(Queued)`; resend = new Attempt on same Delivery; currency ISO default BRL; two platform buckets + `{tenantId}/{productId}/{fileId}`; RowVersion + state machine; order number FOR UPDATE sequence.

## Invariants that must not break

1. No global catalog. Every product/order/file/notification is `ITenantScoped`.
2. Catalog is not Inventory (`Asset`) and not Rentals (`Reservation`).
3. B2C identity from JWT (`customer_id`, `tenant_id`) only -- never from body.
4. B2C APIs require **catalog module active** (B2C does not use PermissionResolver).
5. After `Approved`, order items are immutable.
6. Price/name/subtotal from the client are ignored; snapshots from DB.
7. Internal files never get a public URL.
8. External notification send only when `AllowExternalDelivery == true` **and** credentials exist.
9. No automatic channel fallback.
10. Do not drop `Customer.Cpf` in this migration.
11. Do not apply the migration remotely. Do not touch `main` / PROD / provider credentials.
12. Tests must not call real Meta/Resend/SMS/Supabase production.

## Implementation scope

Waves 1-4 in one feature branch per repo. See sections below.

## Module + permissions

- `PlatformModules.Catalog = "catalog"`
- Aliases: `catalog`, `catalogo`, `catálogo`, `orders`, `pedidos`
- Frontend: `MODULE_KEYS` add `"Catalog"`; `MODULE_NAME_TO_KEY.catalog = "Catalog"`; `permissionGroups.MODULE_ORDER` include `catalog`
- Disable module: APIs 403, nav/menu hidden, **data preserved**. No FK/cascade to `TenantModule`. Current admin `SyncTenantModules` **removes** the `TenantModule` row (does not call `Deactivate`); gating = module not in active set.

Permissions (37 -> 43). Do **not** add to `DefaultUserKeys`:

```
catalog.products.read
catalog.products.manage
catalog.orders.read
catalog.orders.manage
catalog.notifications.read
catalog.notifications.resend
```

`catalog.orders.manage` covers all admin transitions. No `catalog.orders.approve` / `catalog.product_requests.*`.

## Customer PF/PJ

Enum `CustomerType { Individual = 0, Company = 1 }`.

Columns (additive):

- `customer_type` NOT NULL DEFAULT 0 (Individual)
- `document` varchar(14) nullable, digits only

Backfill: `document = cpf` where cpf is not null. Keep `cpf` column + unique index. Add unique partial `(tenant_id, document) WHERE document IS NOT NULL`.

Validation:

- Individual -> CPF check digits (`BrazilianDocumentValidator`; also fix `RegistrationAttributeValidator.NormalizeCpf` to use check digits)
- Company -> CNPJ check digits (new `NormalizeCnpj` / `IsValidCnpjDigits`)
- Register requires `CustomerType` + `Document` (new registers). Legacy OTP rows may still have null document.
- Uniqueness: Document per tenant; also keep Cpf uniqueness. When Individual, write both `Cpf` and `Document`. When Company, `Cpf` stays null, `Document` = CNPJ.
- Profile PATCH: **cannot** change CustomerType or Document.
- Dynamic field type `cnpj` added. Reserved keys add `customerType`, `document`.
- Public schema `CoreFields` include `customerType`, `document` (in addition to name/email/password/phone).

DTOs: `RegisterCustomerRequestDto` gains `CustomerType` + `Document`. `CustomerProfileDto` / auth profile expose `customerType` + `document` (and keep `cpf` for Individual/compat).

## Storage

`IStorageProvider`:

- `UploadAsync(bucket, key, stream, contentType, ct)`
- `GetPublicUrl(bucket, key)` -- only for public bucket
- `CreateSignedUrlAsync(bucket, key, ttl, ct)` -- private
- `DeleteAsync(bucket, key, ct)` optional

Implementations: `SupabaseStorageProvider`, `DevStorageProvider` (local folder under content root, e.g. `App_Data/storage/{bucket}/...`; public URL = app-relative path or fake `https://dev-storage.local/...`). Config:

```
Storage:PublicBucket = catalog-public
Storage:PrivateBucket = catalog-private
Storage:SignedUrlTtlSeconds = 900
Storage:SupabaseUrl / Storage:ServiceRoleKey  (env; never commit secrets)
```

Keys: `{tenantId:N}/{productId:N}/{fileId:N}` (or product-request equivalent).

Visibility:

- CustomerVisible images (jpeg/jpg/png/webp): **public** bucket. Validate MIME **and** extension **and** magic bytes where practical. Max size e.g. 5 MB.
- InternalB2B (pdf, stl, step, stp, dxf, and listed technical MIME): **private** bucket. Max size e.g. 25 MB.
- B2C never receives `storageKey`. Public files: public URL. Private: B2B `catalog.products.manage` -> signed URL endpoint.

Do not store product files as data URLs/JSONB.

## Domain (schema `catalog`)

All `ITenantScoped`. RoundMoney: 2 dp, `MidpointRounding.AwayFromZero`. Currency `char(3)` default `BRL`.

### CatalogProduct

Name required, Code optional unique per tenant when set, Description optional, Price nullable, Currency, IsActive. Deactivate only (no hard delete). No UnitId.

### CatalogProductFile

ProductId, StorageKey, FileName, MimeType, SizeBytes, Visibility { CustomerVisible, InternalB2B }.

### CatalogOrder

CustomerId, OrderNumber (int, unique per tenant, display `#1042`), Status, CustomerNote?, customer snapshots (name required; email/phone optional), TotalAmount? (null if any line is Sob consulta), Currency, RejectedReason?, CancelledReason?, `byte[] RowVersion` concurrency token.

### CatalogOrderItem

ProductId, ProductNameSnapshot, ProductCodeSnapshot?, UnitPriceSnapshot?, Currency, Quantity > 0, SubTotal?. Immutable after Approved.

### CatalogOrderNumberSequence

`(TenantId PK, LastNumber)`. Allocate with `SELECT ... FOR UPDATE` on relational providers (skip lock on InMemory like `RentalAssetLocks`).

### CatalogOrderStatusHistory

Status, ActorType { Customer, B2BUser, System }, ActorId?, Reason?, CreatedAt. One row per transition. Domain, not generic AuditLog.

### ProductRequest

CustomerId, Description, Quantity > 0, Note?, Status = Submitted only this v1. No conversion.

### ProductRequestFile

Same file metadata; Customer can see own uploads; B2B `catalog.products.read` can list. v1: store request files in **private** bucket; Customer download via authenticated endpoint for **own** request files only.

## State machine

```
Requested -> Approved -> Preparing -> Ready -> Completed
Requested -> Rejected | Cancelled
Approved | Preparing | Ready -> Cancelled
```

Domain methods on `CatalogOrder`: `Approve()`, `StartPreparing()`, `MarkReady()`, `Complete()`, `Reject(reason)`, `Cancel(actor, reason)`. Invalid -> throw that controllers map to **409**. Empty reason on Reject / B2B Cancel -> 400.

Who:

- Approve / Preparing / Ready / Complete / Reject: B2B `orders.manage`
- Cancel Requested: owner Customer **or** B2B `orders.manage` (B2B still requires reason)
- Cancel Approved/Preparing/Ready: B2B `orders.manage` + reason only
- Customer cannot Approve or cancel after Requested

Concurrency: RowVersion + guard. `DbUpdateConcurrencyException` -> 409.

## Notifications (schema `core`)

Generic platform entities (not Catalog*Notification):

- `Notification`: TenantId, EventType, AggregateType, AggregateId, Payload jsonb, CreatedAt
- `NotificationDelivery`: NotificationId, Channel { InApp, Email, WhatsApp, Sms, Push reserved }, RecipientKind { Customer, B2BUser }, RecipientId?, Name/Email/Phone snapshots, Status { Queued, Sent, Delivered, Failed }, ProviderMessageId?, ErrorMessage?, NextAttemptAt?, AttemptCount
- `NotificationDeliveryAttempt`: DeliveryId, AttemptNumber, StartedAt, FinishedAt, Outcome { Success, TransientFailure, PermanentFailure }, ProviderResponse truncated, ErrorMessage
- `NotificationTemplate`: EventType, Channel, Language (`pt-BR`), SubjectTemplate?, BodyTemplate, WhatsAppTemplateName?, IsActive. Platform-seeded, not tenant-editable
- `TenantNotificationChannelConfig`: TenantId, EventType, Channel, IsActive. Unique (TenantId, EventType, Channel)

Defaults when tenant enables catalog (seed on first use or module add): InApp true; Email/WhatsApp/Sms false.

Same DB transaction as order mutation:

1. mutate order + history
2. insert Notification + Deliveries (Queued) for enabled channels
3. InApp delivery: write row and mark **Delivered** in the same transaction (no provider)
4. commit
5. enqueue Hangfire job / sweep to process Queued **external** deliveries

Do **not** use in-memory `NotificationQueue` as source of truth for catalog events. Invite/OTP may keep the Channel.

Retry: NextAttemptAt 1m, 5m, 30m, 2h, 6h; max 5 attempts. Transient vs Permanent. No cross-channel retry.

Resend: only Failed; new Attempt on **same** Delivery; reset to Queued and process. Not Queued/Sent/Delivered.

SMS: no real provider. Enabling SMS via config PUT -> 400 `{ "error": "SMS channel is not available." }`. If a Queued SMS delivery exists, process as PermanentFailure (do not fake success).

WhatsApp: `SendTemplateAsync` when template name set. Webhook Delivered mapping is **follow-up** (do not over-parse Meta in v1).

Email: existing `IEmailProvider` after `{{var}}` replace.

### Event matrix

| EventType | Recipients | Notes |
|---|---|---|
| `catalog.order.created` | Customer + users with effective `catalog.orders.manage` | resolve capabilities, not role names |
| `catalog.order.approved` | Customer | |
| `catalog.order.preparing` | none | |
| `catalog.order.ready` | Customer | |
| `catalog.order.rejected` | Customer | payload reason |
| `catalog.order.cancelled_by_supplier` | Customer | B2B cancel; payload reason |
| Customer self-cancel Requested | none to that Customer | History only |

Completed: no required notification.

Variables: tenantName, customerName, orderNumber, orderStatus, rejectionReason, cancellationReason.

### AllowExternalDelivery

```
effectiveAllowExternal = allowExternalDelivery == true
```

Log at startup: `External notification delivery enabled` or `disabled`. Update `NotificationsServiceCollectionExtensionsTests`: Production + null + credentials -> **Dev** providers.

## HTTP API (errors `{ "error": string }`)

### B2B -- `[Authorize]` + `[RequirePermission]`

| Method | Path | Permission |
|---|---|---|
| GET | `/api/catalog/products` | products.read |
| POST | `/api/catalog/products` | products.manage |
| GET | `/api/catalog/products/{id}` | products.read |
| PUT | `/api/catalog/products/{id}` | products.manage |
| POST | `/api/catalog/products/{id}/deactivate` | products.manage |
| POST | `/api/catalog/products/{id}/activate` | products.manage |
| POST | `/api/catalog/products/{id}/files` multipart | products.manage |
| DELETE | `/api/catalog/products/{id}/files/{fileId}` | products.manage |
| GET | `/api/catalog/products/{id}/files/{fileId}/url` | products.manage (signed or public url) |
| GET | `/api/catalog/product-requests` | products.read |
| GET | `/api/catalog/product-requests/{id}` | products.read |
| GET | `/api/catalog/orders` | orders.read |
| GET | `/api/catalog/orders/{id}` | orders.read |
| POST | `/api/catalog/orders/{id}/approve` | orders.manage |
| POST | `/api/catalog/orders/{id}/reject` body `{ reason }` | orders.manage |
| POST | `/api/catalog/orders/{id}/preparing` | orders.manage |
| POST | `/api/catalog/orders/{id}/ready` | orders.manage |
| POST | `/api/catalog/orders/{id}/complete` | orders.manage |
| POST | `/api/catalog/orders/{id}/cancel` body `{ reason }` | orders.manage |
| GET | `/api/catalog/notifications` | notifications.read |
| POST | `/api/catalog/notifications/deliveries/{id}/resend` | notifications.resend |
| GET | `/api/catalog/notification-channels` | notifications.read |
| PUT | `/api/catalog/notification-channels` | notifications.resend |

Channel config write uses `catalog.notifications.resend` (no seventh permission). GET remains `notifications.read`.

Query filters: products name/code/active; orders number/status/customer/from/to; notifications date/event/recipient/channel/status.

### B2C -- `[Authorize(Policy = "Customer")]` + explicit catalog module gate (403 if inactive)

| Method | Path |
|---|---|
| GET | `/api/catalog/portal/products` (active only; search name/code) |
| GET | `/api/catalog/portal/products/{id}` |
| POST | `/api/catalog/portal/orders` `{ items: [{ productId, quantity }], customerNote? }` |
| GET | `/api/catalog/portal/orders` |
| GET | `/api/catalog/portal/orders/{id}` |
| POST | `/api/catalog/portal/orders/{id}/cancel` (no reason required for Customer) |
| POST | `/api/catalog/portal/product-requests` multipart or JSON + separate file posts |
| GET | `/api/catalog/portal/product-requests/{id}/files/{fileId}/url` own only |

Portal product DTO: customer-visible files only, public image URLs, never InternalB2B, never storageKey.

Create order: ignore client prices; re-load active products; snapshot; allocate number under lock; status Requested; history; notifications.

## Frontend B2B

Nav Operacao: **Catalogo & Pedidos** `modules: ["catalog"]` children:

- Produtos `/catalogo/produtos` `catalog.products.read`
- Pedidos `/catalogo/pedidos` `catalog.orders.read`
- Notificacoes `/catalogo/notificacoes` `catalog.notifications.read`

`PermissionRoute` + existing `filterNavigationItemsByAccess`. i18n all strings. TanStack tables with column filter headers. Zod `safeParse`. `parseApiError`. LoadingButton / Skeleton.

Channel config UI on notifications page: per-event InApp/Email/WhatsApp/SMS checkboxes; SMS disabled + copy that channel is unavailable; externals default off.

## Frontend B2C

`CustomerAppLayout.modulePath`: `catalog` maps to catalog list `/t/:subdomain/catalogo` (and host `/catalogo`) and orders `/t/:subdomain/pedidos`. **Never** combined "Catalogo & Pedidos" for Customer. Host-mode routes too.

Cart: `sessionStorage` keyed by tenant+customer; not an entity.

Register: core toggle Pessoa fisica / Empresa; CPF vs CNPJ mask; same other fields. Frontend + backend validation. Mask in UI; digits persisted. Do not log full document.

Product request: CTA on catalog; form description/qty/note/files; toast; no B2C tracking list required in v1.

## Test seams (confirmed)

API (xUnit; InMemory/SQLite where enough; Testcontainers Postgres for concurrency / unique order numbers like ReservationConcurrencyTests):

- Cross-tenant product/order access
- B2C accessing another Customer's order
- Module disabled B2B (permission filtered) and B2C (explicit gate)
- Internal file inaccessible to Customer; unauthorized B2B file access
- `orders.manage` / `products.manage` enforcement
- Customer cannot approve; Customer cannot cancel Approved
- B2B cancel Preparing and Ready; reason required
- Invalid transition 409
- Concurrent transition 409
- Duplicate order numbers under concurrency
- CPF + CNPJ validators; Document uniqueness per tenant; CPF backfill (document = cpf)
- `AllowExternalDelivery` null -> Dev providers even in Production
- Notification retry idempotency; resend appends Attempt, keeps history
- RegistrationAttributeValidator CPF uses check digits
- SMS activate rejected; SMS delivery does not fake success

Web (vitest): navigation filter for catalog; register schema PF/PJ; cart helpers if extracted; permission groups MODULE_ORDER.

No real Meta/Resend/SMS/Supabase prod in tests.

## Migration

Name e.g. `AddCatalogOrdersAndCustomerDocument`. Additive: schema `catalog`, core notification tables, customer columns, indexes. **Do not drop cpf. Do not apply remotely.**

`TestAppDbContext` jsonb remap must cover Notification.Payload.

Hangfire: recurring sweep `notification-outbox` (e.g. every minute) plus fire-and-forget after commit when Hangfire is available; tests use a fake dispatcher/outbox processor without Hangfire.

## Likely affected areas / files

**vlr-api:** `PlatformModules`, `Permissions`, `PermissionCatalog`, `Customer`, `BrazilianDocumentValidator`, `RegistrationAttributeValidator`, `RegistrationFieldTypes`, `CustomerAuth*`, `AppDbContext`, `NotificationsServiceCollectionExtensions`, `NotificationsOptions`, `Program.cs`, `HangfireExtensions`, new `Modules/Catalog/`, `Storage/`, `Notifications/` entities/dispatcher/outbox, EF configurations + migration, tests under `tests/Platform.Api.Tests/Catalog|Notifications|CustomerAuth|Storage`.

**vlr-web:** `adminTenantSchemas`, `permissionGroups`, `navigation.ts`, `AppRoutes.tsx`, `CustomerAppLayout`, tenant portal register/schemas/i18n, `features/catalog/` pages/services/schemas, locales pt-BR/en/es.

## Do not

- Cart/Quote/Payment/Subscription/Contract/Organization/Asset FK/Shipment/Invoice
- Hard delete products
- Role-name authorization
- Data URL storage for catalog files
- In-memory queue as catalog notification source of truth
- `IsDevelopment()` as the AllowExternalDelivery default
- Apply migration; merge to main; enable external delivery in Railway
- Hardcode FICC / motorcycle / 3D-print / Brazil beyond CPF/CNPJ

## Documentation that may need updating

- `CONTEXT.md` both repos (Customer + new terms) -- already updated by parent
- `ROADMAP.md` both repos -- already updated by parent
- `docs/context-packs/INDEX.md` + `active/catalog.md` -- already updated by parent
- This spec

## Product-level how to test (after DEV migration -- not this PR)

1. Enable module `catalog` on a tenant. Admin sees Catalogo & Pedidos. User without keys does not.
2. Create product with price and "sob consulta"; public image; private STL. Customer sees image, not STL.
3. B2C PF and PJ register (CPF/CNPJ). Profile cannot change type/document.
4. Cart -> submit -> `#` number, Requested. B2B approve -> Preparing -> Ready -> Complete. Reject/cancel with reason notify only when externals enabled (they stay off).
5. Disable module: APIs 403, menus gone, rows remain in DB.
6. Confirm `Notifications:AllowExternalDelivery` unset does not register Resend/Meta.

## Follow-ups (explicit, out of v1)

- WhatsApp webhook -> Delivery Delivered/Failed persistence
- Real SMS provider
- ProductRequest -> CatalogProduct conversion
- In-app bell UI
- Tenant-editable templates
- Drop/rename `cpf` column
- Push channel provider
