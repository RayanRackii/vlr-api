# Runbook — Autonomous delivery (GitHub)

Procedimento do parent. A política vive em [`AGENTS.md`](../../AGENTS.md) (**Autonomous Delivery Workflow**). Não duplicar gates aqui.

MR = Pull Request.

## Ferramentas neste ambiente

Ordem de tentativa:

1. **GitHub CLI `gh`** — create, review, merge. Feature → `develop`: `gh pr merge --squash`. Release `develop` → `main` (só com Human Gate): `gh pr merge --merge`. Nunca squash de release.
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

Feature / fix / refactor / test / chore → `develop`: **Squash and merge**.

Exceção: PR de restauração de ancestrais (`merge origin/main` na feature) → `develop` usa **Create a merge commit** (`gh pr merge --merge`). Squash apagaria o parent `main`.

```powershell
gh pr merge --squash
```

Sem `--admin` para furar proteção. Sem force push. Sem rebase de `develop`.

## Release `develop` → `main`

Somente com `PRODUCTION_HUMAN_APPROVAL_REQUIRED`. **Create a merge commit** — nunca squash, nunca rebase-and-merge.

```powershell
gh pr merge --merge
git fetch origin --prune
git merge-base --is-ancestor <released-develop-sha> origin/main
```

Exit 0 esperado. Não abrir reconciliação rotineira `main` → `develop` depois disto. Back-merge só se `main` tiver hotfix ausente em `develop`.

## Pós-merge em develop (squash-aware)

```powershell
git fetch origin --prune
git switch develop
git pull --ff-only origin develop
```

Não concluir integração só porque o SHA original da feature não está em `develop`. Conferir que o **tree** do PR está em `origin/develop` (`git diff origin/develop -- <paths do PR>` vazio no conjunto esperado, ou o squash commit contém as mudanças).

Cleanup da branch só se: PR merged, `develop` sincronizado, conteúdo confirmado, working tree limpa. Nunca apagar branch não mergeada.

## PRs cross-repo

Mesmo nome de branch. Antes do merge: `API first` | `Web first` | `either` | `COORDINATED_MERGE_REQUIRED`.
