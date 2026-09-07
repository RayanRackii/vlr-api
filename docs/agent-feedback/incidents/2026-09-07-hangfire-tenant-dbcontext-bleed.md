# Agent Feedback — Hangfire tenant loop reused tracked DbContext

Date: 2026-09-07
Status: Open
Severity: High
Repository: vlr-api

Agent: api-implementer
Model: grok-4.6
Branch: `feat/rentals-whatsapp-reminder-24h`
PR: pending into `develop`

## What happened

`ReservationReminderJob` set `AmbientTenantContext.TenantId` per tenant while reusing one scoped `AppDbContext` and `RentalsNotificationPublisher`. The publisher merges `TenantNotificationChannelConfigs.Local` by `Id` (and seeds defaults keyed `EventType|Channel` without `TenantId`). After tenant A, tenant B inherited A's tracked WhatsApp config and could get a second WhatsApp delivery in the same sweep.

Tests injected a different `AmbientTenantContext` than `FakeTenantProvider`, so the Hangfire tenant-switch path was a no-op in the fixture.

## Expected behavior

Hangfire jobs that fan out across tenants create one DI scope per tenant (`IServiceScopeFactory`) so `AppDbContext`, `AmbientTenantContext`, and publishers do not share a ChangeTracker. A two-tenant sweep test asserts one WhatsApp delivery per tenant.

## Impact

- código: extra WhatsApp `NotificationDelivery` rows for the 2nd+ tenant in one sweep
- isolamento: B's deliveries could carry A's channel flags
- produção: none (PROD WhatsApp off; reminder not yet merged)
- tempo: one High review-fix before merge

## Why it happened

Hangfire has null HTTP tenant, so the job switched ambient tenant on a long-lived context. Publisher `.Local` merge was written for HTTP request uniqueness, not multi-tenant sweeps.

## Resolution

`ReservationReminderJob` opens `CreateAsyncScope()` per tenant, sets ambient tenant inside that scope, then disposes. Test `Two_tenants_in_one_sweep_each_get_one_whatsapp_delivery` uses `AmbientBackedTenantProvider`.

## Prevention

Hangfire loops that set `AmbientTenantContext` must not reuse a tracked `AppDbContext` with publishers that merge `.Local` channel configs. Reviewer/implementer checklist: two-tenant job fixture when a job fans out by tenant.

## Suggested promotion

Hangfire multi-tenant isolation rule: scope-per-tenant or `ChangeTracker.Clear()` after each tenant. Do not promote until a second occurrence.
