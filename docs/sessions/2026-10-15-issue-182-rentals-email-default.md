# 2026-10-15 — Rentals email channel default (#182)

Branch `fix/notification-email-default` in `vlr-api`, from `develop`. Draft PR. No migration, no deploy, no env change.

`RentalsNotificationPublisher.EnsureTenantChannelDefaultsAsync` seeded every missing Rentals channel with `IsActive` only for InApp. `PublishReservationEventAsync` queues a delivery only for an active config, so a new tenant never enqueued reservation or cancellation email.

A missing row for an event in `RentalEventTypes.Notifying` now seeds Email active. WhatsApp and SMS stay inactive. A row that already exists is left as stored, so tenants that already ran the old seed (including FICC) stay off until `PUT /api/notifications/channel-configs`. There is no backfill migration. `Notifications__AllowExternalDelivery` and `AllowExternalEmail` are unchanged: the outbox still uses `DevEmailProvider` unless external email is explicitly allowed.
