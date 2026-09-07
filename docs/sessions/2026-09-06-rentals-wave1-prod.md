# 2026-09-06 — Rentals Wave 1 production closeout

`RENTALS_WAVE1 = PROD_COMPLETE`  
`RENTALS_WAVE1_PHASE_A = PROD_COMPLETE`  
`RENTALS_WAVE1_PHASE_B = PROD_COMPLETE`

Docs-only closeout. No runtime change, no migration, no PROD mutation in this session.

| | SHA |
|---|---|
| API PROD / `main` | `54b385d5d14d0438fceb0c358872cf7ef1e1f589` (PR #62) |
| WEB PROD / `main` | `0d995955dd56338cc8cbfda6bf8ff6950afb68f6` |

- Railway production matched API `main`.
- Vercel Production matched WEB `main`.
- Last migration: `20260906034111_AddRentalsReservationsCompletePermission`.
- `PENDING_COUNT=0`.
- `PROD_TIMESTAMP_STATE=EMPTY` — no Reservation backfill. Single PROD Slot kept.
- T1 (`America/Sao_Paulo`) live: Slot/Schedule civil; Reservation start/end true instants; API UTC JSON; WEB Brazil clocks.
- Public/read-only smoke passed.
- Customer-tenant write smoke: **not** performed.
- Local unpushed CLOSED_DEV docs (`vlr-api` `6f46710`, `vlr-web` `545277d`) were **not** pushed; superseded by this PROD_COMPLETE closeout.
