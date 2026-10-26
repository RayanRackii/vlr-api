<!-- Movido verbatim de AGENTS.md em 2026-10-24 (economia de tokens). Regras continuam vigentes. -->

## Autonomous Delivery Workflow

O parent/orchestrator é dono do **ciclo técnico completo**. O usuário não precisa pedir separadamente: criar branch, commit, push, abrir PR, review, aprovar ou merge. Isso faz parte do fluxo padrão. “MR” em docs/prompts = Pull Request do GitHub.

Detalhe operacional (ferramentas GitHub, merge methods, cleanup): [`docs/runbooks/autonomous-delivery.md`](../../docs/runbooks/autonomous-delivery.md).

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

