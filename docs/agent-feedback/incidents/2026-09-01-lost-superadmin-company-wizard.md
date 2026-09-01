# Agent Feedback — Lost SuperAdmin company wizard

Date: 2026-09-01
Status: Open
Severity: High
Repository: vlr-web (incident stored here per canonical agent-feedback; no API code change)

Agent: parent / orchestrator (prior session [DEV vs PROD gap](cd0d1802-0a43-4fb0-9a53-cad9d52b273e) + this recovery)
Model: Grok 4.6
Branch: `feat/superadmin-company-wizard`
PR: pending this recovery

## What happened

A SuperAdmin `/admin/tenants/new` redesign (desktop ~1200px, vertical 6-step nav, title “Cadastrar nova empresa”, specific Portuguese validation, `/admin` → `/admin/dashboard`, `SUPER_ADMIN` detection) was described as implemented and manually reviewed on this PC. It is not on `origin/develop` or `origin/main`. Production therefore still shows the older “Novo cliente” wizard (`max-w-3xl`, horizontal stepper).

A prior audit ([DEV vs PROD gap](cd0d1802-0a43-4fb0-9a53-cad9d52b273e)) correctly found the gap vs `main`/`develop`, then inferred the work might have been left on another machine. The human confirms the work was done on this PC. That inference was wrong.

## Expected behavior

UI work the human approved must have an identified commit on a `feat`/`fix` branch and, after delivery, a commit reachable from `origin/develop` before the task is treated as complete. Agents must not report persistence/merge from conversation memory.

```text
Never report MERGED_DEVELOP without verifying origin/develop contains the commit.
After merge/push, verify:
  git fetch
  git merge-base
  git log origin/develop
  expected files/content on origin/develop
```

## Impact

- código: redesign absent from git; reimplementation required
- custo: forensic + rebuild of already-reviewed UI
- arquitetura: none (layout + SuperAdmin routing only)
- produção: none from this incident (PROD never received the redesign)
- tempo: lost implementation + audit confusion
- UX: PROD still “Novo cliente” / horizontal stepper

## Forensic findings (this PC, 2026-09-01)

Proven:

| Check | Result |
|---|---|
| WEB `develop` == `origin/develop` | Yes (`9ebf683` after #22 sync) |
| `origin/main` ancestor of `origin/develop` | Yes (`7c7e685`) |
| Working tree | Clean |
| Stash | Empty |
| Pickaxe `Cadastrar nova empresa` / `max-w-[1200px]` / `Identificação` | No commits |
| `git grep` all refs | No hits |
| Unreachable commits (`git fsck --full --no-reflogs --unreachable`) | Unrelated squash leftovers (catalog, Twilio, queue, RBAC). No wizard redesign |
| Unreachable `common.json` blobs | Still `"title": "Novo cliente"` |
| Reflog | No `reset --hard`, no wizard branch create/delete, no stash; only Twilio/catalog/sync checkouts |
| Cursor local History `TenantOnboardingWizard.tsx` | Six snapshots, all `max-w-3xl` + `WizardPanelsStepper`; dated 2026-08-07 |
| Cursor History `pt-BR/common.json` | All snapshots `"Novo cliente"`; none “Cadastrar nova empresa” |
| Agent transcripts on this PC | No implementation session for this redesign. Sessions on this PC from 2026-08-31 are New PC Bootstrap + Twilio. Redesign appears first as the 2026-09-01 audit prompt |

Classification:

- A) orphan/unreachable commit: **No**
- B) deleted/local branch: **No evidence**
- C) uncommitted working-tree only: **Not proven.** No leftover dirty tree or stash. If it existed uncommitted, it is gone.
- D) overwritten/reset: **No reflog evidence** of reset/force checkout discarding this work
- E) cannot reconstruct from repository evidence: **Yes**

Inference (not proven): work may have existed only in a Cursor composer/preview buffer or an unsaved editor session that never became a git object. Transcripts on this PC do not contain the implementation turn.

Human testimony (accepted as product fact, not git fact): the redesign was implemented and reviewed on this PC.

## Why previous reporting was wrong

The 2026-09-01 gap audit correctly said the redesign is not in `develop`/`main`. It then speculated “another PC”. That speculation is retracted. Git on this PC also has no object. The audit never claimed `MERGED_DEVELOP` for this wizard; the failure mode here is **reporting a reviewed UI as persisted without a commit SHA**.

## Resolution

Reimplement from the approved spec on `feat/superadmin-company-wizard` (vlr-web). Do not invent a recovered blob. Prove persistence on `origin/develop` after merge (`git fetch`, content grep for “Cadastrar nova empresa”).

## Prevention

1. Never report `MERGED_DEVELOP` without `git fetch` + commit present on `origin/develop`.
2. After merge/push verify: `git fetch`; `git merge-base`; `git log origin/develop`; expected strings/files on `origin/develop`.
3. UI work the human approved is incomplete until there is an identified commit and PR.
4. Do not infer “other machine” when local git/history also lacks the objects; say “not in git on this PC”.
5. Parent checkpoint after implementation: `git status`, `git log -5`, tracking, unpushed commits.

## Promotion

AGENTS (verify `origin/develop` before complete; approved UI requires commit/PR)

## References

- `vlr-web` files still on develop: `NewTenantPage.tsx` (`max-w-3xl`), `TenantOnboardingWizard.tsx` (horizontal stepper), `admin.wizard.title` = “Novo cliente”
- Cursor History: `%APPDATA%\Cursor\User\History\-4d444f34` (wizard), `-6d5b32cb` (pt-BR)
- Transcript: [DEV vs PROD gap](cd0d1802-0a43-4fb0-9a53-cad9d52b273e)
