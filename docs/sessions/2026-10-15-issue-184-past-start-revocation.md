# 2026-10-15 — #184 past start and revoked staff writes

`POST /api/reservations`, `POST /api/schedule/slots/book`, and `POST /api/schedule/lessons` return 400 `START_IN_THE_PAST` when the civil start is before the injected clock. The zone is `TenantBookingClock`. `Tenant` still has no timezone, so the fallback is `America/Sao_Paulo`. A start equal to now is allowed. An existing lesson stays on the day read and can be removed.

Staff writes ask GoTrue `GET /auth/v1/user` with the caller's access token. A rejected session is 401. Reads stay on local JWT validation. Auth outages are 500, not 401. ADR `docs/adr/0007-staff-write-session-revocation.md`. No migration.
