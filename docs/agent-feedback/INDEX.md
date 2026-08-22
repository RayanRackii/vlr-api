# Agent feedback index

Searchable history. **Not** always-loaded context.

Read an incident only when it matches this agent/model, the same domain, a similar risk, a recurrence, or a reviewer comparing a known failure.

## Open

None.

## Promoted

| Id | Title | Promotion |
|---|---|---|
| [2026-08-18-silent-subagent-fallback](./incidents/2026-08-18-silent-subagent-fallback.md) | Configured subagent unavailable → parent simulated the role | `AGENTS.md` fail-closed (`SUBAGENT_UNAVAILABLE`) |
| [2026-08-18-premium-architect-cost](./incidents/2026-08-18-premium-architect-cost.md) | Routine architecture used premium model + broad exploration | GLM default architect; Fable architecture path still needs explicit approval + dossier; **Merge Risk Gate** is the second authorized Fable path (`AGENTS.md` 2026-08-21) |

## How to promote

Recurring confirmed pattern → CONTEXT (product), ADR (architecture), rule (technical), skill (procedure), `.cursor/agents/` (routing), `AGENTS.md` (git/process), context pack (condensed facts). Isolated case stays here.
