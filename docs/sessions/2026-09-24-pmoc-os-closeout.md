# 2026-09-24 — PMOC/OS closeout

Documentation only. No product change, migration, or deploy in this closeout.

```
ROLVIX_PMOC_OS_PHASE1 = RELEASED_PROD
ROLVIX_PMOC_OS_PHASE2 = RELEASED_PROD
ROLVIX_PMOC_OS_PHASE3 = RELEASED_PROD
PMOC_OS_PHASE4 = NOT_PLANNED
PMOC_OS_ACTIVE_IMPLEMENTATION = NO
PMOC_OS_PHASE3_DEV_INTEGRATION = PASS
PMOC_OS_PHASE3_PROD_RELEASE = PASS
```

## Release record

API develop `6f1c19b7028c6b967cf95d344f37bba258334259`. Release merge `c837988abfb55255c1c867ca62d9618ec3c6dd18` (PR #90). Railway production deployment `10d4e1af-02dd-45f3-95f6-3e8977895aba`.

WEB develop `775dd16cbd171086d2a6f6bee659831113e12877`. Release merge `254bd333ef2b2c3b4f798ef6327091f85890fd36` (PR #85). Vercel production deployment `dpl_DCXL6RnwsGwgCYiFi87f7ZaxQ2xa`.

Migration `20260920002154_ApplyPmocOsPhase3FinalScheduling` applied on PROD. Pending migrations after apply: 0.

## Validation

DEV integration PASS. Real PostgreSQL concurrency: `P_two_automatic_generators_create_at_most_one_work_order` PASS and `AH_completion_does_not_leave_a_stale_automatic_work_order` PASS. API suite 742 passed, 0 failed, 1 skipped. WEB suite 450 passed, 0 failed, 79 files.

PROD release PASS. Final smoke had no unexpected application 5xx. No unintended Email, WhatsApp, or SMS delivery. ficc was not mutated.

## RELEASE_PROCESS_FOLLOWUP

Not a PMOC product defect.

During the Phase 3 production cutover, Railway did not accept replica count 0. Clearing the region started the Phase 3 deployment. The Phase 2 deployment was removed before the migration finished. The migration completed about three minutes later. No Phase 2 process kept writing against the Phase 3 schema. The Phase 3 process was healthy afterward. Final release validation saw no product application error.

Future breaking schema cutovers need a supported Railway maintenance or traffic-blocking method. Do not implement that here.

## Follow-ups (not active PMOC work)

- Platform: `OperationCanceledException` may be logged as 500 while the proxy reports client 499
- Release process: Railway maintenance gate, as above
- WEB backlog: inactive-plan Gerar OS UX, Novo Plano permission visibility, explicit checklist reorder, expected 409 console noise
- Test infra: WEB Vitest worker-memory history
- DEV cleanup pending: isolated database `pmoc_phase3_concurrency`. The SQL runner cannot `DROP DATABASE` inside its transaction. Do not drop shared DEV.

## Operational deploy settings at closeout

Recorded only. This closeout does not change them.

- Railway production autodeploy: OFF
- Railway development autodeploy: OFF
- Vercel production deployment automation: unchanged (it deployed the WEB merge commit)

## Not promised

MaintenancePlanAsset, per-asset interval override, DueSoon, cron editor, tenant timezone configuration, PMOC state on Inventory/Assets, plan-list summary dashboard, compliance percentage, CREA certification guarantee, regulatory-document management, template CMS for scheduling, offline technician workflow, Technician PMOC administration.
