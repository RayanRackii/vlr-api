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

## Regras duras (resumo — detalhe nos docs abaixo)
- Base `develop`; nunca commit/push direto em `main` ou `develop`; uma task = uma branch (`feat|fix|refactor|test|chore/<slug>`) = um PR pequeno. Detalhe: [`docs/agents/git-workflow.md`](./docs/agents/git-workflow.md).
- Feature → `develop`: **Squash and merge**, só com CI verde e todos os gates. Release `develop` → `main`: **Create a merge commit**, nunca squash/rebase, nunca automático (`PRODUCTION_HUMAN_APPROVAL_REQUIRED`).
- Proibido sem humano: force push, `reset --hard`, stash/`clean` silencioso, deploy/dados/segredos/infra/DNS de PROD, migration destrutiva, deletar dados.
- Nunca imprimir senhas, tokens ou segredos. Trabalho autônomo só em DEV.
- Sempre `git fetch origin --prune` antes de alterar o repo (refs locais podem estar stale); bloqueios → `SESSION_BOOTSTRAP_BLOCKED`, nunca resolver em silêncio.
- Reviewer gate: Critical = 0, High = 0; Medium corrigido ou justificado. Review só do diff (`origin/develop...HEAD`).
- **Revisão profunda (canônica) obrigatória** se tocar: auth/authz, tenant isolation, segurança, concorrência, transação/integridade, migration/schema, dados persistidos, contrato FE↔BE, clients compartilhados, DEV/PROD, Fly/Vercel/Supabase, notificações, Hangfire, fronteiras de arquitetura, cross-repo, blast radius alto. Dossier ≤ ~1200 palavras, sem crawl; ≤ 1 chamada por PR. Detalhe e contrato `FABLE_MERGE_REVIEW`/`MERGE_BLOCKED`: [`docs/agents/delivery-and-merge-gate.md`](./docs/agents/delivery-and-merge-gate.md).
- Bug de alta severidade (concorrência, auth, tenant, pricing) não pode depender só de build.

## Roteamento de modelo/esforço
Resumo: Luna/mini para mecânico, testes e docs; Terra para endpoints/componentes; Sol medium por padrão, Sol high só em passos de dados/tenant/preço; revisão canônica: Sol (padrão do gate) ou Claude Opus read-only como substituto permitido; um revisor canônico por PR (ambos só em auth/tenant/migration). Não trocar modelo/esforço no meio de uma sessão; sessão nova por task, começando pela nota de handoff. Detalhe: [`docs/agents/routing.md`](./docs/agents/routing.md) e `~/rolvix-ai-docs/ROUTING.md`.

## Skills por tipo de task (não carregar todas sempre)
Skills user-level por nome (`grilling`, `domain-modeling`, `implement`, `tdd`, `code-review`); não duplicar o corpo; não usar caminhos de workspace (`C:\Free\...`, `../.agents/...`); se a skill exigida pela task não for descoberta, parar e informar — não improvisar cópia.
- `implement`: toda task que escreve código.
- `tdd`: mudança de lógica ou correção de bug (não para docs, copy, CSS, rename).
- `grilling` + `domain-modeling`: só passo novo de domínio/design/arquitetura ou spec.
- `code-review`: só no papel de reviewer.
- Docs/config/mecânico: nenhuma skill além de `implement` (ou nenhuma, se for só docs).

## Saída quieta (economia de tokens)
- Testes: `dotnet test --no-build --verbosity quiet --logger "console;verbosity=minimal"`; rodar só o projeto/arquivo afetado antes da suíte inteira.
- CI: `gh run view <id> --log-failed | tail -n 200`; nunca colar log inteiro no contexto.
- Saída grande → redirecionar para arquivo e ler só o trecho necessário.

## Human Decision Gate

Agentes podem decidir detalhes técnicos locais, reversíveis e já cobertos por regras/padrões existentes.

Se surgir uma dúvida cuja resposta possa afetar arquitetura, modelo de domínio, outra funcionalidade, contrato frontend/backend, compatibilidade retroativa, autenticação/autorização, isolamento multi-tenant, segurança, dados persistidos ou comportamento em produção, o agente **não assume**.

Deve parar, explicar a decisão necessária, apresentar alternativas relevantes, recomendar uma opção com motivo e pedir decisão ao usuário antes de continuar. Depois da escolha, o parent retoma o [Autonomous Delivery Workflow](./docs/agents/delivery-and-merge-gate.md) sozinho.

Também é gate: regra de produto, semântica de domínio, contrato FE↔BE com alternativas relevantes, comportamento de provider externo, migration destrutiva, comportamento PROD.

Regra: *Escalate uncertainty, not implementation.*

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

