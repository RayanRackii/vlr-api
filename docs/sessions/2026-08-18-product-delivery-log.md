# 2026-08-18 — wizard de recursos

## Passo Operação + presets de preço

O passo 2 do cadastro (create/lote/edit) passou a se chamar **Operação** (i18n pt-BR / en / es).

Preços deixam de ser uma linha por dia como caminho padrão. Três presets na UI:

- Mesmo preço todos os dias
- Preço especial no fim de semana (úteis vs sáb/dom)
- Preço específico por dia

A API continua com `RentalPricing` por `DayOfWeek`; o cliente expande o preset em 7 faixas.

## Formulário que zerava entre passos

O `useEffect` de init do `AssetWizard` reexecutava quando `resetWizard` / `loadEditAsset` mudavam de identidade (ex.: `onOpenChange` novo a cada render do pai) e também quando um Select portaled disparava close do Dialog (`outside-press` / `focus-out`), o que desmontava o wizard (`wizardMode = null`).

Correção: inicializar só na abertura da sessão; ignorar close por clique fora / perda de foco; `disablePointerDismissal` no Dialog do wizard.
