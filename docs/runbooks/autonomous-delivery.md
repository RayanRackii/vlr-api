# Runbook — Autonomous delivery (GitHub)

Procedimento do parent. A política vive em [`AGENTS.md`](../../AGENTS.md) (**Autonomous Delivery Workflow**). Não duplicar gates aqui.

MR = Pull Request.

## Ferramentas neste ambiente

Ordem de tentativa:

1. **GitHub CLI `gh`** — create, review, merge (`gh pr merge --squash --delete-branch` quando o cleanup for seguro).
2. **GitLens / GitKraken MCP** (`user-eamodio.gitlens-extension-GitKraken`) — neste workspace: `pull_request_create`, `pull_request_create_review` (`approve: true` quando o gate interno passou). **Não há ferramenta de merge** neste MCP.
3. Sem integração autenticada → `PR_AUTOMATION_UNAVAILABLE` + compare URL + título + body. Continuar review/Merge Risk Gate.

Nunca gravar PAT em repo, `.env` versionado, prompts ou docs. Nunca imprimir token.

## Setup se `gh` faltar (ação do usuário)

No Windows (exemplo):

```powershell
winget install --id GitHub.cli
gh auth login
```

Usar o fluxo oficial do GitHub (browser ou SSH). Não colar PAT no chat.

Opcional: MCP GitHub oficial com scope de merge, se a org exigir merge via API em vez de `gh`.

## Abrir PR

Base: `develop`. Head: a feature branch já pushed.

Se `gh`:

```powershell
gh pr create --base develop --title "<title>" --body "<body>"
```

Se só GitLens MCP: `pull_request_create` com `provider=github`, org/repo reais, `source_branch`, `target_branch=develop`.

## Aprovar

Tentativa via `gh pr review --approve` ou MCP `pull_request_create_review` com `approve=true`.

Se o GitHub recusar self-approval do autor do PR:

```text
PLATFORM_SELF_APPROVAL_NOT_ALLOWED
```

O Merge Risk Gate continua como aprovação técnica interna. Não contornar branch protection.

## Merge em develop

Somente depois dos gates de `AGENTS.md`. Nunca `main`.

Preferir squash (padrão deste runbook se o GitHub do repo não definir outro):

```powershell
gh pr merge --squash
```

Sem `--admin` para furar proteção. Sem force push. Sem rebase de `develop`.

## Pós-merge (squash-aware)

```powershell
git fetch origin --prune
git switch develop
git pull --ff-only origin develop
```

Não concluir integração só porque o SHA original da feature não está em `develop`. Conferir que o **tree** do PR está em `origin/develop` (`git diff origin/develop -- <paths do PR>` vazio no conjunto esperado, ou o squash commit contém as mudanças).

Cleanup da branch só se: PR merged, `develop` sincronizado, conteúdo confirmado, working tree limpa. Nunca apagar branch não mergeada.

## PRs cross-repo

Mesmo nome de branch. Antes do merge: `API first` | `Web first` | `either` | `COORDINATED_MERGE_REQUIRED`.
