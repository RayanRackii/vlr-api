# Handoffs / specs

Mecanismo de passagem **rolvix-architect → api-implementer / web-implementer**. Não é um segundo `ROADMAP.md`.

## Onde gravar

| Escopo | Caminho |
|---|---|
| Só `vlr-api` | `docs/plans/active/` (este repo) |
| Só `vlr-web` | `vlr-web/docs/plans/active/` |
| Cross-repo | **Uma** spec neste repo, com `Repositories` listando `vlr-api` e `vlr-web`. Sem espelho no frontend. |

Nome: `YYYY-MM-DD-descricao-curta.md`.

O `rolvix-architect` é read-only: devolve o markdown ao parent. Depois das decisões humanas confirmadas, o parent/`api-implementer` materializa o arquivo aqui.

Uma spec com decisão humana pendente **não** está pronta para implementação.

## Conteúdo mínimo

Ver [HANDOFF-TEMPLATE.md](./HANDOFF-TEMPLATE.md).
