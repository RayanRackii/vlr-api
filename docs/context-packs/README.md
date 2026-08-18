# Context packs

Derived context for agents. **Not canonical.**

```text
CONTEXT.md / ADR / rules / code  →  truth
context-pack                     →  condensed, reusable loading
```

If a pack conflicts with a canonical source, the canonical source wins.

Do not name this folder `cache`. Provider prompt cache is unrelated.

## How to load

1. Read [INDEX.md](./INDEX.md).
2. Load **only** the pack that matches the question.
3. Do not load every pack.
4. Validate critical facts in canonical sources when the decision is architectural or production-sensitive.

## Who updates

Architect is readonly → `CONTEXT_PACK_UPDATE_RECOMMENDED`.

Parent/implementer materializes the pack **after** the canonical source has been updated.

Keep packs small: high signal, low token. Split or cut history if a pack grows.
