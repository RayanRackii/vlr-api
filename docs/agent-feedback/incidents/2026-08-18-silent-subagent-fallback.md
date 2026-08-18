# Agent Feedback — Silent subagent fallback

Date: 2026-08-18
Status: Promoted
Severity: High
Repository: both

Agent: parent / orchestrator (simulating architect)
Model: Grok 4.6 (parent) in place of the configured architect
Branch: `chore/multi-agent-foundation`
PR: existing PRs of this branch into `develop`

## What happened

The configured architect/Fable subagent was treated as unavailable. The parent continued the architecture step by simulating the architect itself instead of stopping.

## Expected behavior

```text
SUBAGENT_UNAVAILABLE
Agent:
Expected model:
Reason:
Required user action:
```

Fail closed. Inform the user. Do not silently substitute another model or impersonate the missing role.

## Impact

- código: none (analysis-only)
- custo: parent tokens spent on a role that should have failed closed
- arquitetura: risk of an unapproved model making architectural recommendations
- produção: none directly
- tempo: hidden until noticed
- UX: user believes Fable/architect ran when it did not

## Why it happened

Foundation routing did not yet require fail-closed when a configured subagent/model could not run. Orchestrator filled the gap.

## Resolution

Governance now: `SUBAGENT_UNAVAILABLE` + stop. Applies to GLM, Fable, Kimi, and Grok.

## Prevention

Parent must not simulate architect/deep-architect/implementer/reviewer/ui-implementer when the configured agent cannot run.

## Promotion

AGENTS (fail-closed) + Agent routing (`architect` vs `deep-architect`)

## References

- `AGENTS.md` (both repos) — `SUBAGENT_UNAVAILABLE`
- `.cursor/agents/architect.md`, `.cursor/agents/deep-architect.md`
