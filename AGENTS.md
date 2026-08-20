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

`main` permanece PROD-ready. `develop` permanece integration/DEV. Implementação só em `feat` / `fix` / `refactor` / `chore`.

O usuário trabalha nos mesmos repos a partir de mais de um computador. Nenhum agente pode assumir que a branch local, os refs `origin/*` locais, os commits locais ou a working tree representam o estado remoto atual.

**Quem coordena Git:** o parent/orchestrator. Subagentes (architect, implementer, reviewer) **não** repetem `git fetch` nem o bootstrap completo durante a mesma tarefa.

```
parent
  ├─ git bootstrap
  ├─ architect
  ├─ implementer
  ├─ reviewer
  └─ git checkpoint
```

Detecção por **evento**, não por adivinhar se uma janela do Cursor abriu ou vai fechar:

| Evento | Ação |
|---|---|
| Antes da primeira operação que altere o repo | Automatic Session Bootstrap |
| Depois de implementação concluída e validada | Automatic Task Checkpoint |
| Usuário indica parar / trocar de PC / encerrar sessão | Explicit Session Handoff |

Fluxo: pedido do usuário → parent → Git bootstrap **uma vez** → agentes → implementação → review/validação → Git checkpoint.

### Automatic Session Bootstrap

Antes da primeira operação que altere o repositório de qualquer tarefa (editar arquivos, criar branch de implementação, continuar uma implementação, chamar agente com escrita, commit, push), o parent executa:

```bash
git status --short --branch
git fetch origin --prune
git status --short --branch
git branch -vv
```

**Nunca confie em refs `origin/*` locais antes do fetch.** `git status` sem fetch pode mostrar a branch "sincronizada" com um `origin/*` stale.

Não repetir este bootstrap a cada subagente da mesma tarefa. Exemplo: se o usuário abre outro PC e diz "vamos continuar Slots", o parent faz o bootstrap **antes** de qualquer alteração — o usuário não precisa perguntar se houve fetch.

**Nova implementação** (working tree limpa; refs já atualizados pelo fetch acima):

```bash
git switch develop
git pull --ff-only origin develop
git switch -c <new-branch>
```

**Continuar uma feature existente noutro PC** (refs já atualizados pelo fetch acima): localizar a branch remota, switch para a local/tracking, atualizar só por fast-forward:

```bash
git switch <feature-branch>
git pull --ff-only origin <feature-branch>
```

Não fazer automaticamente merge de `develop` na feature nem rebase da feature sobre `develop`. Se for necessário, é decisão explícita da tarefa ou Human Decision Gate.

Parar com `SESSION_BOOTSTRAP_BLOCKED` se encontrar: uncommitted changes inesperadas; local e remote divergidos; branch atual inesperada; tracking ausente; fast-forward impossível; conflito; commit local não publicado cuja origem não esteja clara.

Nesses casos: explicar o estado, apresentar opções, pedir decisão humana. Nunca resolver em silêncio com stash, reset, rebase, force, `clean`, restore ou checkout para esconder trabalho.

### Automatic Task Checkpoint

Não depender do usuário dizer que está encerrando a sessão. Depois de cada implementação concluída e validada, **antes** de anunciar que a tarefa terminou, o parent executa:

```bash
git status --short --branch
git log -5 --oneline
```

e verifica: branch atual, working tree, commits locais, commits ainda não no remote, tracking.

Se a implementação estiver concluída, validada, numa branch `feat` / `fix` / `refactor` / `chore`, e as regras deste arquivo permitirem: **commit** e **push da branch**. Trabalho completo e validado não deve ficar só numa máquina.

Não commitar código incompleto só para "limpar" a máquina. Trabalho incompleto deve ser reportado como incompleto.

### Explicit Session Handoff

Quando o usuário indicar que vai parar, trocar de PC, continuar noutro computador, encerrar por hoje, finalizar por hoje, fazer o handoff ou encerrar sessão, o parent faz um handoff completo:

```bash
git status --short --branch
git log -5 --oneline
```

e, quando necessário, `git diff --stat`.

Retornar:

```
SESSION_HANDOFF

Repository:
Branch:
HEAD:
Tracking:
Working tree:
Ahead:
Behind:
Last pushed commit:
Uncommitted changes:
Unpushed commits:
Safe to continue on another PC:
```

Se não estiver seguro continuar noutro PC, dizer o motivo explicitamente.

### Autonomia do implementer

**O implementer pode autonomamente** (na feature branch, depois do Session Bootstrap do parent nesta tarefa): consultar status; editar; stage controlado; commits (incluindo vários coerentes); push da feature branch; configurar upstream.

**O implementer NÃO pode autonomamente:** trabalhar/commitar/push em `main` ou `develop`; force push; merge automático; rebase automático; `reset --hard`; stash silencioso; `clean`; deploy de produção; alterar dados, segredos ou infraestrutura de produção; iniciar um segundo Session Bootstrap (`fetch` + sync de `develop` + criar branch) na mesma tarefa.

## Roteamento multi-agent

Arquivos em [`.cursor/agents/`](./.cursor/agents/). São roteadores — não copiam produto, arquitetura, convenções nem o corpo dos skills.

O parent/orchestrator é **Grok 4.6**. Coordena Git (Session Bootstrap, Task Checkpoint, Session Handoff — ver Git Work Policy). Subagentes não repetem `git fetch` na mesma tarefa. Não substitua modelos em silêncio. Se o subagent configurado não puder rodar, emita `SUBAGENT_UNAVAILABLE` (agent, modelo esperado, **root esperado**, motivo, ação do usuário) e **pare**. Não simule o papel e não use outro agent/modelo no lugar.

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
