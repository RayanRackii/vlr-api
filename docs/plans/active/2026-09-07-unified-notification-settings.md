# 2026-09-07-unified-notification-settings

Status: approved (`core.notifications.read` / `core.notifications.write` + permission seed migration)

## Goal / Problem

ONE tenant-level Notifications channel-config surface for Catalog + Rentals. No Rentals-only API. No new events. Customer cannot configure. WhatsApp tenant configs default OFF. Tenant channel ON ≠ Meta provider ON.

## Repositories

- vlr-api
- vlr-web

## Architecture route

- rolvix-architect (blocked on permission gate; Human approved 2026-09-07)

## Execution route

- api-implementer then web-implementer
- Branch (both): `feat/unified-notification-settings`
- API first

## Confirmed decisions

- Generic `GET/PUT /api/notifications/channel-configs`
- Catalog `GET/PUT /api/catalog/notification-channels` stay as thin wrappers
- Catalog delivery history + resend stay on Catalog controller/page
- Unified WEB: `/configuracoes/notificacoes`
- `/catalogo/notificacoes` history-only (remove channel matrix)
- Permissions: `core.notifications.read` (GET), `core.notifications.write` (PUT)
- Core module (not module-gated). NOT in DefaultUserKeys / TechnicianLegacyKeys
- ADMIN/SUPER_ADMIN via existing wildcard + `TenantAccessBootstrapper` AllKeys (no extra role SQL in this migration)
- Migration: INSERT two `core.permissions` rows, `module_key` NULL, `ON CONFLICT (key) DO NOTHING`
- `MIGRATION_REQUIRED = YES` (permissions only; no `TenantNotificationChannelConfig` schema change)
- Module filter: `ITenantModuleAccessor` only
- Catalog channels: InApp (`configurable=false`), Email, WhatsApp
- Rentals channels: WhatsApp only
- SMS never exposed; PUT SMS → 400 `"SMS channel is not available."`
- Unified PUT InApp / unsupported channel → 400 `"Channel is not configurable for this event."`
- Inactive module event PUT → 403 `"Module is not active for this tenant."`
- Unknown event → 400 `"Unknown notification event."`
- Errors `{ "error": string }`
- `RENTALS_REMINDER_LEAD_TIME = 24_HOURS` unchanged
- PROD WhatsApp stays off
- `EMAIL_STATUS = OK`

## HTTP contract

### GET `/api/notifications/channel-configs`

Auth: B2B, `core.notifications.read`. No `[RequireActiveModule]`.

200: groups for **active** modules only:

```json
[
  {
    "module": "catalog",
    "events": [
      {
        "eventType": "catalog.order.created",
        "displayKey": "catalog.events.orderCreated",
        "channels": [
          { "channel": "InApp", "isActive": true, "configurable": false },
          { "channel": "Email", "isActive": false, "configurable": true },
          { "channel": "WhatsApp", "isActive": false, "configurable": true }
        ]
      }
    ]
  }
]
```

Catalog notifying events (order): created, approved, ready, rejected, cancelled_by_supplier.  
Rentals notifying events (order): pending_deposit, confirmed, canceled, completed, reminder.

`displayKey` values:

- `catalog.events.orderCreated|orderApproved|orderReady|orderRejected|orderCancelledBySupplier`
- `rentals.events.reservationPendingDeposit|reservationConfirmed|reservationCanceled|reservationCompleted|reservationReminder`

### PUT `/api/notifications/channel-configs`

Auth: B2B, `core.notifications.write`.

Body: `{ "eventType": "rentals.reservation.confirmed", "channel": "WhatsApp", "isActive": true }`

200: `{ "eventType", "channel", "isActive" }`

## Do not

- `/api/rentals/notification-channels`
- New notification events
- SMS UI/API exposure
- Rentals Email/InApp in this slice
- DefaultUserKeys / TechnicianLegacyKeys grants
- PROD WhatsApp enablement
- Schema change to channel config table
- Grant USER/TECHNICIAN in this migration

## Implementation notes (vlr-api)

- Module: `Platform.Api.Modules.Notifications` (distinct from `Platform.Api.Notifications` outbox). DI: `AddTenantNotificationsModule`.
- Permissions seeded by migration `20260907185700_AddCoreNotificationPermissions` (`module_key` NULL, no `role_permissions` INSERT).
- Inactive-module PUT throws `TenantModuleInactiveException` → HTTP 403 `Module is not active for this tenant.`
- Catalog `GET /api/catalog/notification-channels` still returns `CatalogChannelConfigItem` rows, filtered to `CatalogEventTypes.Notifying`. Catalog PUT keeps InApp toggle + `"Unknown catalog notification event."` + SMS reject only when activating.
