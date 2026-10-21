# AGENTS.md — vlr-api

## Ordem de leitura
1. [`CONTEXT.md`](./CONTEXT.md) — glossário + beachhead (canônico; espelho em `vlr-web`)
2. [`ROADMAP.md`](./ROADMAP.md) — checklist e histórico deste repo
3. [`.cursor/rules/`](./.cursor/rules/) — produto, arquitetura, convenções, rentals
4. [`docs/adr/`](./docs/adr/) — decisões duras (ex.: schedule Slot-first)
5. [`docs/sessions/`](./docs/sessions/) — diários operacionais de sessão
6. [`docs/runbooks/`](./docs/runbooks/) — procedimentos (enter órfão, [entrega autônoma](./docs/runbooks/autonomous-delivery.md), [migrations remotas](./docs/runbooks/database-migrations.md), etc.)

## Repos
- **Este:** `vlr-api` (.NET 10), hospedado na Fly.io (app `rolvix-api-prod`). Banco e Auth: Supabase. API da web: Vercel.
- **Irmão:** `vlr-web` (React/Vite, Vercel) — tem o próprio `CONTEXT.md` / `ROADMAP.md`.
- Workspace Cursor pretendido: roots `backend` + `frontend` (dois Git repos, **não** monorepo). O diretório pai no disco não precisa ser workspace root.

## Ao concluir trabalho
- Atualizar `ROADMAP.md` (checks + Histórico se o plano mudou).
- Se glossário/beachhead mudou → atualizar também o espelho em `vlr-web`.
- No chat: próximo passo deste roadmap **e** do `vlr-web`; **como testar**.

## Human Decision Gate

Agentes podem decidir detalhes técnicos locais, reversíveis e já cobertos por regras/padrões existentes.

Se surgir uma dúvida cuja resposta possa afetar arquitetura, modelo de domínio, outra funcionalidade, contrato frontend/backend, compatibilidade retroativa, autenticação/autorização, isolamento multi-tenant, segurança, dados persistidos ou comportamento em produção, o agente **não assume**.

Deve parar, explicar a decisão necessária, apresentar alternativas relevantes, recomendar uma opção com motivo e pedir decisão ao usuário antes de continuar. Depois da escolha, o parent retoma o [Autonomous Delivery Workflow](#autonomous-delivery-workflow) sozinho.

Também é gate: regra de produto, semântica de domínio, contrato FE↔BE com alternativas relevantes, comportamento de provider externo, migration destrutiva, comportamento PROD.

Regra: *Escalate uncertainty, not implementation.*

## Git Work Policy

Uma implementação = uma branch própria + N commits + uma review.

**Base:** `develop`. Nunca implementar, commitar ou fazer push diretamente em `main` ou `develop`.

**Branches:** `feat/<slug>`, `fix/<slug>`, `refactor/<slug>`, `test/<slug>`, `chore/<slug>`. Mudança cross-repo usa o **mesmo nome** em `vlr-api` e `vlr-web`.

`main` permanece PROD-ready. `develop` permanece integration/DEV. Implementação só em `feat` / `fix` / `refactor` / `test` / `chore`. Commits diretos em `main` são proibidos.

### Merge methods

| PR | Método |
|---|---|
| `feat` / `fix` / `refactor` / `test` / `chore` → `develop` | **Squash and merge** |
| Production release `develop` → `main` | **Create a merge commit** |

Nunca squash nem rebase-and-merge de um PR `develop` → `main`. Squash de release destrói ancestrais: o Git deixa de reconhecer que os commits de `develop` já foram para `main`, e o próximo release reporta conflitos falsos (ex.: `ReservationService.cs`, `ROADMAP.md`, context packs) mesmo com trees equivalentes.

Depois de um release merge, verificar:

```bash
git merge-base --is-ancestor <released-develop-sha> origin/main
```

Exit 0 esperado. **Não** abrir reconciliação rotineira `main` → `develop` depois de um merge-commit de release. Back-merge `main` → `develop` **somente** se `main` tiver hotfix de emergência que ainda não está em `develop`. Um PR cujo único objetivo é restaurar ancestrais (`merge origin/main` em branch a partir de `develop`) também entra em `develop` com **Create a merge commit** — squash apagaria o parent `main`.

O usuário trabalha nos mesmos repos a partir de mais de um computador. Nenhum agente pode assumir que a branch local, os refs `origin/*` locais, os commits locais ou a working tree representam o estado remoto atual.

**Quem coordena Git:** o parent/orchestrator. Subagentes (architect, implementer, reviewer) **não** repetem `git fetch` nem o bootstrap completo durante a mesma tarefa.

```
parent
  ├─ git bootstrap
  ├─ classify risk / Human Decision Gate
  ├─ Sol (se necessário)
  ├─ implementer
  ├─ build / test
  ├─ reviewer
  ├─ PR
  ├─ Merge Risk Gate (dossier Sol review)
  ├─ approve → merge em develop (quando os gates passarem)
  └─ git checkpoint
```

Detecção por **evento**, não por adivinhar se uma janela do Cursor abriu ou vai fechar:

| Evento | Ação |
|---|---|
| Antes da primeira operação que altere o repo | Automatic Session Bootstrap |
| Depois de implementação concluída e validada | Automatic Task Checkpoint |
| Usuário indica parar / trocar de PC / encerrar sessão | Explicit Session Handoff |

Fluxo: pedido do usuário → parent → Git bootstrap **uma vez** → agentes → implementação → review → PR → Merge Risk Gate → merge em `develop` (se os gates passarem) → checkpoint.

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

Se a implementação estiver concluída, validada, numa branch `feat` / `fix` / `refactor` / `test` / `chore`, e as regras deste arquivo permitirem: **commit**, **push**, e o restante do [Autonomous Delivery Workflow](#autonomous-delivery-workflow) (PR, Merge Risk Gate, merge em `develop` quando autorizado). Trabalho completo e validado não deve ficar só numa máquina.

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

**O implementer NÃO pode autonomamente:** trabalhar/commitar/push em `main` ou `develop`; force push; merge; rebase automático; `reset --hard`; stash silencioso; `clean`; deploy de produção; alterar dados, segredos ou infraestrutura de produção; iniciar um segundo Session Bootstrap (`fetch` + sync de `develop` + criar branch) na mesma tarefa. PR, aprovação e merge em `develop` são do **parent**, depois dos gates.

## Autonomous Delivery Workflow

O parent/orchestrator é dono do **ciclo técnico completo**. O usuário não precisa pedir separadamente: criar branch, commit, push, abrir PR, review, aprovar ou merge. Isso faz parte do fluxo padrão. “MR” em docs/prompts = Pull Request do GitHub.

Detalhe operacional (ferramentas GitHub, merge methods, cleanup): [`docs/runbooks/autonomous-delivery.md`](./docs/runbooks/autonomous-delivery.md).

### Lifecycle por task independente

1. Session Bootstrap (protocolo multi-machine acima).
2. Classificar risco (local vs arquitetural; revisão profunda merge gate vs skip).
3. Human Decision Gate, se a incerteza for de produto/domínio/contrato/auth/tenant/dados/PROD.
4. Criar branch a partir de `origin/develop` (`feat` / `fix` / `refactor` / `test` / `chore`).
5. Invocar o implementer apropriado (um writer por working tree).
6. Build/test do que o repo realmente tem.
7. Reviewer normal (modelo Sol neste repo; modelo Sol no irmão).
8. Corrigir findings; não ignorar em silêncio.
9. Repetir review até o gate passar.
10. Commit na feature branch.
11. Push da feature branch.
12. Abrir PR para `develop` (automação se disponível).
13. Merge Risk Gate (dossier de análise Sol; revisão profunda quando obrigatório).
14. Registrar/aplicar aprovação (`AI_APPROVED`) se a plataforma permitir.
15. Mergear em `develop` **somente** se todos os gates passarem.
16. `git fetch origin --prune` + `git switch develop` + `git pull --ff-only origin develop`.
17. Verificar o estado integrado: feature → `develop` é squash-aware (tree/diff, não o SHA original da feature); release `develop` → `main` é ancestry (`git merge-base --is-ancestor <released-develop-sha> origin/main`).
18. Remover branch remota/local **só** se o merge estiver confirmado e a tree limpa.
19. Checkpoint / handoff.
20. Próxima task da fila.

Fila: `READY` → `IN_PROGRESS` → `BLOCKED_HUMAN` | `BLOCKED_TECHNICAL` | `PR_OPEN` | `MERGE_REVIEW` | `MERGED_DEVELOP` | `VALIDATION_REQUIRED`. Uma task bloqueada **não** encerra o sprint: registrar e seguir para a próxima `READY`. Uma task = uma branch. Não misturar findings.

### Reviewer gate (antes do Merge Risk Gate)

Parent faz `git fetch origin --prune`. Reviewer **não** faz fetch. Diff: `origin/develop...HEAD`.

- **Critical = 0, High = 0** para avançar.
- **Medium:** corrigir **ou** justificar explicitamente como non-blocking/follow-up. Silêncio não conta.
- Backend: modelo Sol. Frontend: modelo Sol. Visual, quando for o centro: modelo Terra (write) depois modelo Sol.

### MERGE_RISK_GATE (revisão profunda)

A revisão profunda **não** navega o repositório.

```text
Do not pay revisão profunda to grep. Use revisão profunda to reason.
```

Antes de qualquer chamada revisão profunda, o modelo Sol prepara um **Merge Review Dossier** compacto: ~1200 palavras, ≤ 10 arquivos/símbolos. Campos: PR title; purpose; base/head; diff summary; files changed; invariants; contratos FE↔BE; auth/tenant; DB; concorrência; backward compatibility; build/test; reviewer findings; PRs relacionados; riscos; rollback.

Se faltar contexto, o revisor devolve `NEED_MORE_CONTEXT`. Sol coleta **somente** o pedido e atualiza o dossier. Sem crawl.

**Revisão profunda obrigatória** (modelo Sol; use o agente disponível mais adequado) se o PR tocar: autenticação; autorização; isolamento de tenant; segurança; concorrência; transação/integridade; migration/schema; dados de domínio persistidos; contrato frontend-backend; clients de API compartilhados; roteamento DEV/PROD; integração Fly.io/Vercel/Supabase; providers de notificação; jobs Hangfire; fronteiras de arquitetura; comportamento cross-repo; refactor de blast radius alto. Também se: confiança do Sol < high; reviewers discordarem; vários PRs relacionados; regressão de impacto amplo.

**Revisão profunda dispensável** (`FABLE_MERGE_REVIEW_NOT_REQUIRED` + reason) para mudança claramente local (copy/i18n/CSS isolado/rename/dead-code/docs/refactor mecânico) **desde que** reviewers limpos, build/test ok, blast radius baixo.

**Custo:** 1 PR de alto risco → no máximo 1 chamada revisão profunda. `BLOCK_MERGE` → implementer corrige → Sol + reviewer normal verificam. Segunda chamada revisão profunda no **mesmo** PR só se a correção alterou materialmente arquitetura/risco. PRs que interagem (ex.: F-07 + F-09 no mesmo `api.ts`) podem ir numa `INTEGRATION_MERGE_REVIEW`: um dossier de análise Sol cobrindo os PRs relacionados, o mesmo contrato `FABLE_MERGE_REVIEW`, uma chamada.

Contrato de chamada — pedir exatamente:

```text
FABLE_MERGE_REVIEW

Examine o dossier e o diff relevante.
Não implemente. Não faça repo-wide exploration.

Verifique: regressões; efeitos colaterais; auth/authz; tenant isolation;
contratos FE↔BE; concorrência; integridade de dados; backward compatibility;
DEV/PROD; PRs pendentes; cenários não cobertos; rollback.

Retorne:
MERGE_VERDICT: SAFE_TO_MERGE | SAFE_WITH_FOLLOWUP | BLOCK_MERGE
BLOCKING_FINDINGS:
NON_BLOCKING_FINDINGS:
MISSING_TESTS:
CROSS_PR_RISKS:
ROLLBACK_RISK:
REQUIRED_ACTIONS_BEFORE_MERGE:
```

Arquitetura profunda **fora** deste gate continua: Sol emite `FABLE_ESCALATION_RECOMMENDED` e o parent só chama revisão profunda com autorização **explícita** do usuário nesta conversa. Silêncio não autoriza esse caminho. O Merge Risk Gate, depois desta política vigente, **é** o caminho autorizado para revisão profunda em PRs que batem os critérios acima — o parent não espera um “pode usar revisão profunda” extra por PR.

### PR, aprovação e merge

Detectar `gh`, GitHub MCP/plugin ou integração autorizada. Abrir PR automaticamente se autenticado. Se não: `PR_AUTOMATION_UNAVAILABLE` + compare URL + título + body. **Não** parar review só porque `gh` falta.

`AI_APPROVED` somente se: Critical/High = 0; nenhum Medium blocking; veredito revisão profunda/Sol permite merge (`SAFE_TO_MERGE` ou `SAFE_WITH_FOLLOWUP`); testes exigidos passaram; Human Decision Gates resolvidos. `BLOCK_MERGE` impede merge. Se o GitHub bloquear self-approval do autor: `PLATFORM_SELF_APPROVAL_NOT_ALLOWED` — o Merge Risk Gate continua como aprovação técnica interna. Não contornar proteção.

**Merge automático permitido:** feature/fix/refactor/test/chore → `develop`, quando **todos** os gates passarem. **Squash and merge**. Sem force push. Sem reescrever `develop`.

**Nunca** merge automático para `main`, production branch, ou promote `develop` → `main`. Quando o Human Gate autorizar o release: **Create a merge commit** (`gh pr merge --merge`). Nunca `gh pr merge --squash` nem rebase-and-merge em `develop` → `main`.

**MERGE_BLOCKED** (não mergear `develop`) se: `BLOCK_MERGE`; Critical/High; build/test required falhou; Human Decision Gate aberto; migration sensível não aprovada; operação destrutiva de dados; impacto DEV/PROD desconhecido; risco auth/tenant aberto; conflito não trivial; branch divergida de forma inesperada. Registrar o motivo e seguir outra task `READY`.

**Cross-repo:** mesmo nome de branch. Ordem de merge: `API first` | `Web first` | `either` | `coordinated`. Não mergear metade se abrir janela incompatível → `COORDINATED_MERGE_REQUIRED`.

**Production:** `PRODUCTION_HUMAN_APPROVAL_REQUIRED` para merge em `main`, promote DEV→PROD, deploy PROD deliberado, secrets PROD, migration PROD destrutiva, DNS PROD.

### Testes

Toda implementação pergunta: *como esta regressão seria detectada automaticamente?* Infra existe → adicionar/atualizar teste. Não existe → `TEST_INFRASTRUCTURE_MISSING`. Bugs de alta severidade (concorrência, auth, tenant isolation, pricing/integridade) **não** podem depender só de build; ausência de teste pode bloquear o merge.

## Roteamento multi-agent

Arquivos em [`.cursor/agents/`](./.cursor/agents/). São roteadores — não copiam produto, arquitetura, convenções nem o corpo dos skills.

Roteamento por capacidade: Luna (`gpt-6-luna`) para mudanças pequenas, testes e docs; Terra (`gpt-5.6-terra`) para endpoints e componentes; Sol (`gpt-6.1-sol`) para mudanças complexas/cross-cutting, segurança e reviews. Se um agente ou modelo nomeado estiver indisponível, escolha o próximo modelo adequado à capacidade necessária e prossiga. O parent/orchestrator coordena Git, gates, PR, Merge Risk Gate e merge em `develop`; um writer ativo por working tree.

- Mudança local/trivial: Luna; pule arquitetura.
- Endpoints/componentes: Terra; peça revisão independente a Sol.
- Arquitetura, cross-repo, segurança, concorrência, persistência e risco alto: Sol prepara spec/dossier; resolva Human Decision Gate quando aplicável.
- Merge Risk Gate: Sol faz análise independente com dossier e diff relevante, sem repo-wide exploration. Mantenha os critérios de segurança, auth/tenant, compatibilidade, rollback e aprovação humana/produção abaixo.
- Frontend visual: Terra implementa; Sol revisa.

Não permita que indisponibilidade de roteadores interrompa trabalho: use o próximo modelo adequado e mantenha ownership separado entre API e web. Prompt cache do provider ≠ memória do projeto ≠ context pack.

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
