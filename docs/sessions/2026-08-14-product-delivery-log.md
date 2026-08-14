# DIÁRIO OPERACIONAL — 06 a 14 de agosto de 2026

> Consolidação criada em **14 de agosto de 2026 (UTC−3)** a partir dos históricos Git de `vlr-web` e `vlr-api`, dos diffs locais e dos ROADMAPs.
>
> Este arquivo retoma o diário após [`2026-08-05-platform-admin-membership.md`](./2026-08-05-platform-admin-membership.md). Não substitui `CONTEXT.md` (decisões vigentes) nem `ROADMAP.md` (estado das entregas).

---

## 1. Por que esta consolidação existe

As entregas de 06–14/08 foram registradas parcialmente nos ROADMAPs, mas não ganharam entradas no diário operacional. O histórico Git confirma evolução contínua em portal B2C, Rentals, Assets, trial, recuperação de senha, navegação e loading. Este documento fecha essa lacuna e registra também as mudanças locais de 14/08 ainda não commitadas no momento da auditoria.

## 2. Evidência revisada

### `vlr-web`

Commits recentes revisados:

- `a724c7a` — loading da sidebar.
- `e1a1b92`, `d9604e1`, `4646e0c` — expansão dos padrões de loading.
- `8e2141b`, `322dd37`, `2aee3a8` — módulos e nova experiência de Escala/Agenda.
- `a254eb6` — nova organização da sidebar.
- `24a8d53`, `2832771`, `e9ac89d` — correções na gestão em lote e formulários de Assets.
- `1fea039` — correção no comportamento de exclusão de Tenant.

### `vlr-api`

Commits recentes revisados:

- `76b00af` — novos módulos de schedule.
- `f1197ba`, `a5edcef` — correções de Assets em lote/formulários.
- `88a089e` — correção na exclusão de Tenant.
- `c2708a3`, `6ba57ea` — recuperação de senha.
- `78c3a74` — onboarding/registro de Tenant.
- `6d30bc1` — dashboard.
- `87777c3` — novo formato de Assets.
- `e300908`, `feb840e` — progresso de agenda e documentação do fluxo de dois repositórios.

## 3. Entregas consolidadas

### 3.1 Portal B2C e autenticação

- Portal por subdomínio com branding do Tenant, login por e-mail/senha e cadastro dinâmico.
- Agenda B2C passou a reservar por `slotId`.
- Recuperação B2B usa e-mail Rolvix/Resend e URL first-party com `token_hash`; o frontend valida via `verifyOtp`.
- Shell e menu do Customer passaram a respeitar os módulos configurados para o Tenant.

### 3.2 Rentals — agenda e reservas

- Domínio/API de `OccupancyKind`, `ScheduleTemplate`, `Slot`, publicação do dia e reserva por Slot.
- Admin B2B de reservas com confirmação/cancelamento.
- Agenda administrativa em três abas: Agenda diária, Templates semanais e Tipos de ocupação.
- Editor fino de templates e catálogo de tipos.
- Política de domínio `OpenHours` para horários derivados e `SlotGrid` para grade explícita.
- Seed padrão via uma chamada `POST /api/schedule/templates/seed-default`.

### 3.3 Agenda multi-espaço (mudanças locais de 14/08)

- Seleção de um, vários ou todos os Rentables com cache estável por conjunto ordenado de IDs + data.
- Agenda diária agrupada por espaço/bem, com quantidade de horários e empty state por grupo.
- Consulta de dia/templates, seed e publicação aceitam `rentalAssetIds`.
- Atualização de política em lote é transacional: um ID inválido aborta todo o conjunto.
- A UI não expõe nomes técnicos:
  - `OpenHours` → **Horário padrão**.
  - `SlotGrid` → **Grade personalizada**.
- Configurações mistas exigem escolha explícita antes de sobrescrever os selecionados.
- Edição fina continua singular na aba Templates semanais.

### 3.4 Assets, famílias e onboarding

- Asset ganhou famílias configuráveis e atributos JSONB.
- UX de Recursos/Tipos e copy por família.
- Criação/edição/em lote consolidadas no wizard compartilhado.
- Exclusão em lote de Assets.
- Trial self-service com limites, banner e UX read-only.
- Unidade padrão corrigida para **Matriz**.

### 3.5 Navegação e feedback

- Sidebar B2B reorganizada em **Visão geral**, **Pessoas & portal** e **Operação**.
- Navegação filtrada por `activeModules`.
- Skeleton da sidebar evita flash de navegação incorreta durante carregamento.
- Padrões compartilhados de loading:
  - `Skeleton` com shimmer;
  - `LoadingButton` para mutações;
  - `TopProgressBar` para navegação;
  - skeletons estruturados em páginas e listas.

### 3.6 Paleta Rolvix (14/08)

- Nova paleta global:
  - Primary: `#4D6A92`.
  - Accent: `#5A8FA0`.
  - Complementary: `#A2C6E9`.
- Landing e superfícies de autenticação usam gradiente azul suave.
- Novos Tenants recebem primary/accent da paleta como default.
- `PrimaryColor` e `AccentColor` persistidos continuam soberanos nos portais personalizados; nenhum terceiro campo foi adicionado ao schema.
- O login do Tenant combina as duas cores configuradas no fundo e mantém primary em CTAs/links.

## 4. Decisões vigentes

1. O backend mantém `OpenHours`/`SlotGrid`; a interface usa nomes operacionais.
2. Operações multi-Rentable validam o conjunto antes de alterar dados.
3. Templates semanais são editados individualmente para evitar sobrescritas ambíguas.
4. A paleta Rolvix é fallback/default, nunca substituição automática das cores de Tenants existentes.
5. `PrimaryColor`/`AccentColor` continuam sendo os únicos campos de cor do Tenant; complementary é tratamento visual do frontend.
6. O chrome B2B usa tokens globais; o portal B2C usa o branding persistido do Tenant.

## 5. Documentação atualizada nesta consolidação

- `vlr-web/CONTEXT.md`: Agenda multi-espaço, nomenclatura operacional e paleta/fallback.
- `vlr-api/CONTEXT.md`: contratos bulk e regra de branding por Tenant.
- Ambos os `ROADMAP.md`: histórico das entregas de 14/08.
- Este diário: lacuna operacional entre 06 e 14/08.

## 6. Verificação

- Build da API executado após a Agenda multi-espaço.
- TypeScript e parsing dos três arquivos de locale validados.
- Componentes novos da agenda passaram no lint direcionado.
- Após a atualização de paleta:
  - `npm run build` concluído;
  - lint dos arquivos tocados não introduziu diagnóstico novo (a execução ampla ainda reporta regras preexistentes de hooks/fast-refresh em componentes do portal);
  - `dotnet build Platform.Api/Platform.Api.csproj --no-restore` concluído com 0 warnings/0 errors.

## 7. Pendências que permanecem

- Aplicar migrations pendentes nos ambientes indicados pelos ROADMAPs.
- Canvas de Layout no admin e picker visual B2C.
- Enforcement API 403 para módulos inativos.
- `ModuleGuard` e conclusão da área de Users do Tenant.
- Validar visualmente contraste de cores customizadas muito claras informadas por Tenants.

---

*Consolidação gerada em 2026-08-14 após auditoria dos dois históricos Git e dos diffs locais.*
