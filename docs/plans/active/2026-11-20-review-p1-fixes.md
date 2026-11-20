# Review P1 fixes (Fable 5.2 whole-code review, 2026-11-20)

Source: `C:\Users\rayan\Rolvix\reviews\` (fable-vlr-api-review.md, fable-vlr-web-review.md, README.md).
Owner approved the six P1 items on 2026-11-20. Tenant data is disposable for now (owner, 2026-11-20),
so there is no extra code to preserve legacy rows. Generic product: no tenant ids or resource types in code.

## Slices and order
Two API slices that change the schema must not run in parallel, because the model snapshot would
conflict. The order is:

| Slice | Branch | Repo | Migration | Depends on |
|---|---|---|---|---|
| P1-A | `fix/p1-asset-cascade-otp-throttle` | api | no | — |
| P1-B | `feat/p1-order-idempotency` | api | yes | — |
| P1-C | `fix/p1-offline-queue-and-me-retry` | web | — | — |
| P1-D | `refactor/p1-generic-teacher-lessons` | api | data only, or none | after P1-B merges |
| P1-E | `feat/p1-checkout-idempotency-key` | web | — | P1-B contract |

### P1-A (api): asset deleted through category retention, and OTP throttle
1. **Asset deleted through category retention.**
   - *Problem:* an asset added to (or moved into) a category that is already scheduled for deletion is
     hard-deleted by the DB cascade when `DataRetentionJob` removes the category
     (`Jobs/DataRetentionJob.cs:40-92`, `AssetConfiguration.cs:94-97`, `AssetService.cs:590-606`).
   - *Fix:* reject create/update of an asset into a category with `ScheduledDeletionAt != null`
     (400, stable code `asset.category_pending_deletion`). The retention job also refuses to delete a
     category that still has assets with `ScheduledDeletionAt == null`; it logs and skips them.
   - *Tests:* the assignment is refused; the job skips such a category; existing retention tests stay green.
2. **OTP throttle.**
   - *Problem:* `POST /api/auth/customer/request-otp` and the other anonymous customer-auth endpoints
     that send SMS or e-mail have only a per-phone limit (`PhoneVerificationRateLimitedException`).
     There is no per-IP or global throttle, and a call can create a customer row and send an SMS
     (`CustomerAuthService.cs:51-95`).
   - *Fix:* add an ASP.NET Core rate limiter policy (partitioned by client IP; follow ADR 0008 on
     which header carries the real client IP behind Fly) and apply it to the anonymous
     customer-auth send endpoints. Defaults: 10 requests per 10 minutes per IP and 300 per 10 minutes
     globally, configurable. A rejection returns 429 with `Retry-After` and the same body shape the
     current rate-limited responses use. If the OTP flow creates the customer before the code is
     verified, move that creation after verification only when it is a small, local change; otherwise
     record it as a follow-up.
   - *Tests:* the 11th request from one IP inside the window gets 429; another IP is unaffected; existing
     customer-auth tests stay green.

### P1-B (api): idempotent order and reservation creation
- **Problem:** customer order creation (portal checkout) and customer reservation creation have no
  idempotency. A retry after a timeout creates a duplicate.
- **Fix:**
  - an optional `Idempotency-Key` header (1–100 chars) on the portal order-create endpoint and the
    customer reservation-create endpoint;
  - stored per (tenant, customer, scope, key) with the created resource id, for 24 h;
  - a repeat with the same key returns the original resource (same status code, same body), and does
    not create a new one;
  - concurrent repeats: exactly one creates, the others wait or read the stored result (unique index
    plus catch of the unique violation);
  - the same key with a different payload returns 409 `idempotency.key_reused`.
- **Storage:** a new table `core.idempotency_keys`: `tenant_id`, `scope`, `key`, `principal_id`,
  `request_hash`, `resource_id`, `response_status`, `created_at`, `expires_at`; unique
  `(tenant_id, scope, principal_id, key)`. A daily cleanup of expired rows goes into the existing
  retention job. Additive migration.
- **Tests:** the same key twice gives one order; the same key with a different body gives 409; parallel
  requests with the same key give one order (Postgres); no header behaves as today; tenant isolation.
- **Contract:** document the header in OpenAPI and update the snapshot.

### P1-C (web): offline work-order queue scoping, and `/me` failure
1. **Offline queue.**
   - *Problem:* the queue uses fixed IndexedDB keys (`src/lib/offlineSync.ts:9-10`), is never cleared on
     sign-out, and is flushed under whoever signs in next (`WorkOrderSyncHost.tsx:31-52`). 401, 403, 408
     and 429 become permanent conflicts, and a 5xx is retried every second forever.
   - *Fix:*
     - key the queue and conflicts by `userId + tenantId`;
     - migrate a legacy unscoped queue only for the same user (or drop it);
     - clear the current scope on sign-out (staff sign-out path in `sessionRestoreGuard`);
     - 401: pause and keep the item;
     - 403 and 404: the item becomes a visible conflict;
     - 408, 429 and 5xx: exponential backoff (1 s up to 60 s) without blocking items of other work orders.
   - *Tests:* the scoped keys; sign-out clears; backoff schedule; 401 keeps the item.
2. **`/me` failure.**
   - *Problem:* a failed `/api/users/me` makes every staff route show "Acesso negado", with no retry
     (`PermissionContext.tsx:118-126`, `PermissionRoute.tsx:64`).
   - *Fix:* when the permission context has an error (network/5xx), guards render an error state
     "Não foi possível carregar seu acesso" with a "Tentar novamente" button that reloads the profile,
     instead of the deny page. A 401 keeps the existing session-expired flow.
   - *Tests:* error → retry page; retry success → route renders.

### P1-D (api): generic teacher lessons and booking UI, with no tenant ids in code
- **Problem:**
  - `Modules/Rentals/Services/FiccTeacherLessonScope.cs` hard-codes FICC tenant ids and rental-asset ids;
  - `BookingUiOptions.EnabledTenantIds` (appsettings) gates the booking map UI by tenant id;
  - `ResourceTypeService.cs:136-160` seeds `quadra`/`caçamba` resource types into every tenant.
- **Fix:**
  - replace the scope with a tenant-setting-driven scope. `core.tenant_settings` is already a generic
    key/value table (`TenantSettingKeys`). Add the key `rentals.teacher_lessons.rental_asset_ids`
    (JSON array of rental asset ids; missing = lessons disabled), and add `rentals.booking_ui.enabled`
    (`true`/`false`, default false), replacing the appsettings list;
  - give the platform admin a way to set them: an existing admin tenant settings endpoint if there is
    one, otherwise a small `PUT /api/admin/tenants/{id}/settings/{key}` restricted to an allow-list of
    keys;
  - a data-only migration inserts the settings rows for the tenants and rental assets currently in
    code, so FICC keeps working. A data migration is the one place where these ids may appear;
  - remove the ids from code and from appsettings;
  - stop seeding the domain-specific resource types for new tenants, and keep the existing rows.
- **Tests:** a tenant without the setting cannot book lessons; with the setting, only the listed rental
  assets; booking UI follows the setting; a new tenant gets no `quadra`/`caçamba` types; architecture
  test (if any) still green.

### P1-E (web): checkout idempotency key
- Generate one `Idempotency-Key` (UUID) per checkout attempt in `PortalCartPage.tsx` and keep it
  across retries of that same attempt.
- A new key only after success or after the cart changes.
- Send it from `catalogPortalService.ts:91-104`.
- After a timeout, "Tentar novamente" reuses the key.
- **Tests:** key reuse on retry; new key after the cart changes.

## Rules for every slice
- One PR per slice to `develop`, green CI, reviewed by the Opus session (never the author).
- DEV first; PROD only with the owner's "pode promover".
- Bug fixes come with a test that fails without the fix.
