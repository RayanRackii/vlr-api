# Agent Feedback — Premium architect cost explosion

Date: 2026-08-18
Status: Promoted
Severity: High
Repository: both

Agent: architect (used as default)
Model: Fable 5 (`claude-fable-5`)
Branch: `chore/multi-agent-foundation`
PR: existing PRs of this branch into `develop`

## What happened

A routine Reservation × Slot domain question triggered a broad Fable investigation (canonical docs plus substantial code search) and an unexpectedly high on-demand cost for that run. Specific dollar amounts from that execution are not recorded here; they are observations of one session, not guaranteed prices.

## Expected behavior

Cheap, focused discovery first (GLM 5.2 + context pack + directed search). Fable only after explicit user approval for that escalation, with a GLM dossier — reason, do not grep the whole repo.

## Impact

- código: none
- custo: premium model used for discovery that cheaper models can do
- arquitetura: none broken; process was too expensive
- produção: none
- tempo: slower and costlier than needed
- UX: user surprised by usage

## Why it happened

Foundation originally pinned Fable as the default architect and did not constrain investigation breadth or require a condensed pack/dossier before premium reasoning.

## Resolution

- GLM 5.2 = default architect
- Fable 5 = `deep-architect`, explicit per-occurrence approval
- Context packs + GLM dossier before Fable
- Fable returns `NEED_MORE_CONTEXT` instead of wide exploration
- Prompt cache is not treated as project memory

## Prevention

Do not invoke Fable because a task is “hard.” Do not pay Fable to grep.

## Promotion

Agent routing (`architect` → `glm-5.2`, `deep-architect` → `claude-fable-5`) + Context Pack (`docs/context-packs/`) + AGENTS (escalation + cache note)

## References

- `AGENTS.md` routing
- `.cursor/agents/architect.md`, `.cursor/agents/deep-architect.md`
- `docs/context-packs/active/rentals.md`
