# AGENTS.md — vlr-api

## Ordem de leitura
1. [`CONTEXT.md`](./CONTEXT.md) — glossário + beachhead (canônico; espelho em `vlr-web`)
2. [`ROADMAP.md`](./ROADMAP.md) — checklist e histórico deste repo
3. [`.cursor/rules/`](./.cursor/rules/) — produto, arquitetura, convenções, rentals
4. [`docs/adr/`](./docs/adr/) — decisões duras (ex.: schedule Slot-first)
5. [`docs/sessions/`](./docs/sessions/) — diários operacionais de sessão
6. [`docs/runbooks/`](./docs/runbooks/) — procedimentos (enter órfão, etc.)

## Repos
- **Este:** `vlr-api` (.NET 10, Railway).
- **Irmão:** `vlr-web` (React/Vite, Vercel) — tem o próprio `CONTEXT.md` / `ROADMAP.md`.
- Workspace Cursor pretendido: roots `backend` + `frontend` (dois Git repos, **não** monorepo). O diretório pai no disco não precisa ser workspace root.

## Ao concluir trabalho
- Atualizar `ROADMAP.md` (checks + Histórico se o plano mudou).
- Se glossário/beachhead mudou → atualizar também o espelho em `vlr-web`.
- No chat: próximo passo deste roadmap **e** do `vlr-web`; **como testar**.

## Human Decision Gate

Agentes podem decidir detalhes técnicos locais, reversíveis e já cobertos por regras/padrões existentes.

Se surgir uma dúvida cuja resposta possa afetar arquitetura, modelo de domínio, outra funcionalidade, contrato frontend/backend, compatibilidade retroativa, autenticação/autorização, isolamento multi-tenant, segurança, dados persistidos ou comportamento em produção, o agente **não assume**.

Deve parar, explicar a decisão necessária, apresentar alternativas relevantes, recomendar uma opção com motivo e pedir decisão ao usuário antes de continuar.

Regra: *Escalate uncertainty, not implementation.*

## Git Work Policy

Uma implementação = uma branch própria + N commits + uma review.

**Base:** `develop`. Nunca implementar, commitar ou fazer push diretamente em `main` ou `develop`.

**Branches:** `feat/<slug>`, `fix/<slug>`, `refactor/<slug>`, `chore/<slug>`. Mudança cross-repo usa o **mesmo nome** em `vlr-api` e `vlr-web`.

**Preflight** (antes de implementar):

```bash
git status --short --branch
git fetch --prune origin
git switch develop
git pull --ff-only origin develop
git switch -c <branch>
```

Adapte só se a branch já existir por motivo conhecido. Working tree suja por trabalho não reconhecido: **PARE E PERGUNTE.** Não usar stash, reset, clean, restore ou checkout para esconder trabalho.

**O implementer pode autonomamente** (na feature branch): consultar status; fetch; `pull --ff-only`; criar a branch; editar; stage controlado; commits (incluindo vários coerentes); push da feature branch; configurar upstream.

**O implementer NÃO pode autonomamente:** trabalhar/commitar/push em `main` ou `develop`; force push; merge; rebase destrutivo de trabalho desconhecido; `reset --hard` de trabalho desconhecido; stash silencioso de trabalho desconhecido; deploy de produção; alterar dados, segredos ou infraestrutura de produção.

## Roteamento multi-agent

Arquivos em [`.cursor/agents/`](./.cursor/agents/). São roteadores — não copiam produto, arquitetura, convenções nem o corpo dos skills.

O parent/orchestrator é **Grok 4.6**. Não substitua modelos em silêncio. Se o subagent configurado não puder rodar, emita `SUBAGENT_UNAVAILABLE` (agent, modelo esperado, **root esperado**, motivo, ação do usuário) e **pare**. Não simule o papel e não use outro agent/modelo no lugar.

Architects canônicos deste produto (este repo):

1. **rolvix-architect** (`glm-5.2`, readonly) — arquitetura do Rolvix (API + web). Investigação focada. Não chama Fable; se excepcional, devolve `FABLE_ESCALATION_RECOMMENDED`.
2. **rolvix-deep-architect** (`claude-fable-5`, readonly) — só após autorização **explícita** do usuário **nesta** conversa, com o dossier do GLM. Silêncio não autoriza.

Ownership de implementação **neste** repo:

3. **api-implementer** (`grok-4.6`, write) — implementação em `vlr-api`. Segue esta Git Work Policy. Não assume o frontend.
4. **api-reviewer** (`grok-4.6`, readonly) — Standards × Spec no diff `origin/develop...HEAD` de `vlr-api`. O parent faz `git fetch --prune origin` **antes**; o reviewer não faz fetch.

Cross-repo: `rolvix-architect` → uma spec → `api-implementer` **e** `web-implementer` / `ui-implementer` (ownership separado por repo) → `api-reviewer` **e** `web-reviewer`. Um architect; dois ownerships de implementação. Um writer ativo por working tree.

Tarefa trivial/localizada neste repo: pular architect. Tarefa arquitetural: `rolvix-architect` → Human Decision Gate e/ou Fable se o usuário autorizar → spec em `docs/plans` → `api-implementer` → `api-reviewer`.

Prompt cache do provider ≠ memória do projeto ≠ context pack. Nenhuma decisão do fluxo depende de cache hit.

## Context packs

[`docs/context-packs/`](./docs/context-packs/) — resumo derivado para carregar contexto. **Não** é fonte da verdade. Comece pelo [`INDEX.md`](./docs/context-packs/INDEX.md); carregue só o pack relevante. Canônico (CONTEXT / ADR / rule / código) vence o pack. Pack desatualizado → `CONTEXT_PACK_STALE`. Architect readonly recomenda `CONTEXT_PACK_UPDATE_RECOMMENDED`; parent/implementer materializa **depois** da fonte canônica.

## Agent feedback

[`docs/agent-feedback/`](./docs/agent-feedback/) — histórico canônico de erros/aprendizados do sistema de agentes (API + web). **Não** é rule até promotion. Não ler `incidents/**` no início de toda tarefa; use o [`INDEX.md`](./docs/agent-feedback/INDEX.md). Reviewer readonly devolve `AGENT_FEEDBACK_RECOMMENDED`; parent registra só se confirmado.

## Handoffs (`docs/plans`)

Specs de implementação: [`docs/plans/`](./docs/plans/). Não substituem `ROADMAP.md` nem `CONTEXT.md`.

- Só API: `docs/plans/active/`
- Só web: `vlr-web/docs/plans/active/`
- Cross-repo: **uma** spec neste repo, com `Repositories: vlr-api` e `vlr-web`. Sem espelho no frontend.

Nome: `YYYY-MM-DD-descricao-curta.md`. Spec com decisão humana pendente **não** está pronta para implementar.

## User-level skills

Required user-level Cursor skills (descoberta do Cursor, tipicamente `~/.agents/skills/`): `grilling`, `domain-modeling`, `implement`, `tdd`, `code-review`.

Agents referem skills **por nome**. Não duplicar o corpo. Não usar caminhos de workspace (`C:\Free\...`, `../.agents/...`). Se a skill não for descoberta, parar e informar — não improvisar cópia.
