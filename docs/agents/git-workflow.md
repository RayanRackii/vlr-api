<!-- Movido verbatim de AGENTS.md em 2026-10-24 (economia de tokens). Regras continuam vigentes. -->

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

