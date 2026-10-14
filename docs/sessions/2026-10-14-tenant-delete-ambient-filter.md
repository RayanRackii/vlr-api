# 2026-10-14 — Super-Admin delete under another tenant

`DELETE /api/admin/tenants/{id}` loaded and removed child rows through the ambient global query filter. After `POST /api/admin/tenants/{A}/enter`, deleting tenant B matched no children of B. Users, units, and roles (`ON DELETE RESTRICT`) blocked the tenant delete. Tables without a tenant foreign key would remain after the tenant row was removed.

`DeleteAsync` now ignores that filter and keeps `TenantId == id` on every child delete, including the membership read that decides which Supabase auth ids to remove. An auth id still used by another tenant is left in place. No migration.

Follow-up #136: the same delete also removes Catalog (order, item, history, product, file, number sequence), ProductRequest and its files, Notification, NotificationDelivery, delivery attempts for those deliveries, TenantNotificationChannelConfig, and TrialSignupClaim rows linked to that tenant. Catalog orders and product requests are removed before the customer because those foreign keys are `ON DELETE RESTRICT`. Attempts have no `TenantId`; the delete joins them to deliveries of the target tenant. Global `NotificationTemplate` rows and trial claims with a null `TenantId` stay. Other tenants' rows stay. No migration.
