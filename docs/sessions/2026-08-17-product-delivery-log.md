# 2026-08-17 — entrega de produto

## RequiresDeposit no rentable

Pagamento prévio deixou de ser obrigatório para todo aluguel. Cada `RentalAsset` tem `RequiresDeposit` (default `true`, migration `AddRentalAssetRequiresDeposit`).

- Qualquer item da reserva com a flag ligada → status `PendingDeposit` (confirmação no admin).
- Nenhum item exige depósito → status `Confirmed` (ocupa o horário; `DepositPaid` permanece 0).
- UI no wizard de ativos: “É necessário pagamento prévio?” (pt-BR / en / es). Vale para espaço e bem.

Não usar `RentalPricing.RequiresDeposit` como gate — esse campo é percentual por faixa e não entra no fluxo de reserva.

## Layout canvas

Admin posiciona Rentables em **Operação → Layout**. O mapa pode ser redimensionado (canto do canvas) e os espaços organizados em grade igual. Save ajusta percentuais que saíram do 0–100 após o arraste.

## Escala: templates semanais visíveis na reserva

`GET .../schedule/days/{date}` (admin e público) passa a derivar células SlotGrid a partir dos `ScheduleTemplate` daquele weekday quando ainda não há `Slot` persistido (tombstone cancelado continua ganhando). `POST .../templates/seed-default` também força `SchedulePolicy.SlotGrid`. Reserva B2C de janela derivada usa create-reservation; `PublishDay` continua opcional para exceções e cascata de recorrência.

A grade admin (Agenda do dia / Configuração semanal) deixa de clipar em `68vh`: colunas preenchem a largura e a altura segue os horários do dia.

