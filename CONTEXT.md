# Contexto Global e Diretrizes do Projeto (Rolvix — SaaS Modular B2B)

> **Canônico (repo `vlr-api`).** O frontend (`vlr-web`) mantém um espelho em `CONTEXT.md` — ao mudar glossário ou beachhead, atualize **os dois**.

## 1. Visão Global (O "Hub" Corporativo)
Você atua como Arquiteto de Software e Desenvolvedor Full-Stack Sênior. O sistema é a plataforma **Rolvix**: um SaaS B2B modular e multi-tenant. Não é software de um nicho só — pense num sistema operacional / Hub corporativo. O Core é agnóstico ao negócio: gerencia Tenants, Users, Units, Roles e Permissions. Sobre o Core, "Módulos" são aplicativos ativáveis por tenant.

### Beachhead atual (prioridade de execução)
A visão de Hub permanece. O primeiro cliente pagante é um **clube** que precisa (1) avisar clientes sobre o estado das quadras e (2) permitir **reserva de horários**. O módulo **Rentals** (`rentals`) é o foco do ciclo atual. Inventário, PMOC e OS já existem como espinha dorsal e continuam no cardápio; não expandir RH, Financeiro, Estoque etc. enquanto o beachhead não estiver operacional.

**Ordem de etapas (ciclo atual):**
1. **Configuração Resend + WhatsApp (Meta)** — etapa principal de infraestrutura de notificações. Providers e webhook já existem no código; falta concluir a configuração externa (credenciais, template Meta aprovado). **Adiada temporariamente** enquanto a configuração do WhatsApp é finalizada pelo time — não bloquear o restante do beachhead por isso.
2. **Portal B2C do tenant (login + cadastro branded)** — shell e login e-mail+senha já em código; fechar deploy/DNS e SMS real quando a Fase 1.5 voltar. Ver seção **Portal B2C do Tenant**.
3. **Agenda Rentals por Slot** (admin templates/kinds + B2C book por `slotId`) — próximo salto de produto após o portal estável. Ver `ROADMAP.md` §2.6 e `docs/adr/0001-rentals-slot-schedule.md`.
4. Demais itens de Rentals / convite / gating — conforme `ROADMAP.md`.

Detalhe de execução: este `ROADMAP.md` e o `ROADMAP.md` do repo `vlr-web`. Regras do agente: `.cursor/rules/`.

### Portal B2C do Tenant (decisão de produto)

**Objetivo:** notificar e atender os Customers daquela empresa em um ambiente exclusivo. Exemplo: `quadratenis.rolvix.com.br` é a porta de entrada só da Quadra de Tênis — não da Rolvix genérica nem de outro tenant.

**O que a pessoa vê ao acessar o subdomínio:**
- Landing de **login** branded (logo, cores, nome fantasia do tenant).
- Fluxo de **cadastro** no mesmo shell (registro obrigatório na primeira vez). O Customer fica vinculado **apenas** àquele Tenant.

**Campos de cadastro do Customer (obrigatórios no beachhead):**
| Campo | Regra |
|---|---|
| Foto de perfil | Upload (URL persistida após storage) |
| Nome | Texto |
| E-mail | Formato e-mail; único por tenant; usado no **login** |
| Senha | Definida no cadastro; login = e-mail + senha (todos os tenants) |
| CPF | Validação algoritmo + consulta a serviço BR (front UX + back autoridade) |
| CEP | Consulta ViaCEP/BrasilAPI (ou equivalente); preencher endereço derivado no back |
| Celular | E.164 BR; **verificação por SMS** no cadastro (prova de posse). Não autentica. Pré-requisito para avisos futuros via WhatsApp |

**Autenticação B2C (decisão fechada):**
- **Login = e-mail + senha** em todos os tenants/projetos (mesmo padrão do B2B na experiência do usuário: credenciais de conta).
- **Celular** não autentica: serve para **verificação por SMS no cadastro** (prova de posse) e, depois, **avisos via WhatsApp**.
- Fluxo alvo: cadastro completo (inclui senha) → SMS no celular → login com e-mail + senha → JWT `Customer`.
- O OTP-only atual por telefone é legado a aposentar quando o cadastro/login por senha estiver estável.

**Branding do tenant — poucos campos, muita identidade (baixa manutenção):**
Campos no cadastro/edição do Tenant (além de `Subdomain`):
- `TradeName` / nome de exibição — título da página
- **`LogoSvg` (markup SVG inline, sanitizado)** — marca principal; **não** usar URL de imagem
- `PrimaryColor` (hex) — botões, links, foco
- `AccentColor` (hex, opcional) — detalhes
- `SupportWhatsApp` ou telefone de contato do clube (opcional) — rodapé
- `WelcomeTagline` (string curta, opcional, ≤120 chars) — uma frase sob o logo

`LogoUrl` (coluna legada) permanece no banco mas **não** é escrita/lida pelo produto — aposentar em migration futura.

**Paleta padrão Rolvix (fallback, não schema novo):** `#4D6A92` primary, `#5A8FA0` accent e `#A2C6E9` complementary. O formulário de novos tenants inicia com primary/accent dessa paleta; valores persistidos em `PrimaryColor`/`AccentColor` continuam soberanos no portal personalizado. A cor complementar é derivada/aplicada pela UI e não exige terceiro campo no Tenant.

**Derivar automaticamente (zero manutenção extra):**
- Favicon a partir do SVG (futuro)
- Cor de fundo suave / contraste de texto a partir do `PrimaryColor` (não pedir paleta completa)
- Iniciais no placeholder se `LogoSvg` estiver vazio

**Evitar no MVP:** CSS custom, upload de fonte, editor visual, múltiplos temas, HTML livre além do SVG sanitizado — custo de suporte alto e pouco retorno.

**Validações BR (front + back):**
- **CPF:** validar dígitos verificadores localmente; enriquecer/consultar via API pública/comercial BR no back (não confiar só no front). Tratar indisponibilidade da API com falha clara ou fila de retry — nunca aceitar CPF só “bem formatado” sem check de dígitos.
- **CEP:** consultar ViaCEP ou BrasilAPI; back é a fonte da verdade; front usa para autocompletar UX.
- **SMS:** provider a escolher na implementação (ex.: Twilio, Zenvia, AWS SNS via abstração); enfileirar como as demais notificações — não enviar SMS síncrono na request HTTP.

## Language

**Tenant**:
The customer organization that subscribes to and is isolated within the platform.
_Avoid_: Company, account, empresa (in code)

**User**:
A person who accesses the platform on behalf of a Tenant (B2B). Authentication is handled externally (Supabase Auth); the platform stores the profile and authorization data.
_Avoid_: Employee, account holder

**Customer**:
An end consumer registered exclusively under one Tenant (B2C). Logs in with email + password. Profile also includes name, CPF, postal address (via CEP), SMS-verified mobile (for WhatsApp notifications, not login), and optional photo. Not a platform User (B2B).
_Avoid_: Client, member, sócio (in code)

**Unit**:
A physical or logical site belonging to a Tenant, such as a hotel property, club facility, or branch. Module data must always reference a Unit when the domain requires site scope.
_Avoid_: Branch, site, location, unidade (in code)

**Role**:
A named bundle of Permissions scoped to a single Tenant. Users receive capabilities through Role assignments.
_Avoid_: Profile, group

**Permission**:
A global, system-defined capability key (for example, `pmoc.work_orders.read`) that Roles grant to Users. The catalog is shared across all Tenants.
_Avoid_: Right, privilege (in code)

**Reservation**:
A booking of one Rentable by a Customer for a concrete time window, owned by a Tenant. Prefer linking to a Slot when the tenant uses slot schedules.
_Avoid_: Booking, appointment, agendamento (in code)

**Rentable**:
Anything a Tenant offers for time-based rental through the Rentals module — a space, court, room, vehicle, or physical good. In code this is the existing `RentalAsset` (typed as location/good; categories refine the label).
_Avoid_: Court-only language in the module core; Quadra as the only product shape

**Asset**:
A Tenant-scoped inventory resource (space, electrical equipment, good, …). Core fields are shared; family-specific values live in `Attributes` (JSONB). Linked 1:1 to a Rentable when `IsRentable`.
_Avoid_: One physical table per use case; dynamic per-tenant tables

**AssetFamily**:
A platform catalog entry (`spaces`, `electrical`, `goods`, `generic`, …) with a FieldSchema describing extra attribute fields. Tenants enable families at onboarding (`TenantAssetFamily`). Drives asset forms and copy tone.
_Avoid_: STI / child tables per family; inventing new CREATE TABLE migrations for each vertical

**ResourceCategory**:
A Tenant-defined label for grouping Rentables (for example padel, society, tennis, meeting room, van). Used for filters, legends, and layout meaning — not a hard-coded enum in the platform. In inventory UI this is **AssetCategory** (Tipo) within an AssetFamily.
_Avoid_: Fixed platform enum of sport types

**OccupancyKind**:
A Tenant-defined kind of time occupancy on a Rentable (for example Open, Closed, Lesson, Event). Controls whether Customers may book that cell and whether it blocks capacity. Catalog is per Tenant, not a global closed set.
_Avoid_: Hard-coded Lesson/Open/Closed-only enums as the only kinds

**Slot**:
One dated occupancy cell on one Rentable: date + start + end + OccupancyKind. The operational unit of a published schedule day. Duration is whatever the admin defined (1h, 2h, 3h, …).
_Avoid_: Free-typed start/end as the only booking path for slot-mode tenants

**ScheduleTemplate**:
The default weekly pattern of Slots (or open-hours rules) used to materialize each Schedule Day. Each template belongs to a `DayOfWeek` and recurs on every occurrence of that weekday (all Mondays, all Tuesdays, etc.); it is not tied to one calendar date. A single day can still be edited after publish.
_Avoid_: Forcing admins to rebuild every day from scratch as the only path

**ScheduleDay**:
The concrete set of Slots for one calendar date (optionally per Unit). Published from templates and/or edited manually for that date.
_Avoid_: Treating the weekly template itself as the live bookings grid

**OpenHours**:
A schedule policy where a Rentable is continuously available between open and close times; bookable windows are derived from that interval (and allowed durations), without requiring the admin to draw every cell. Prefer this for the common club case (~08:00–22:00). Admin: `PUT /api/rental-assets/{id}/schedule-policy` (one) or `PUT /api/rental-assets/schedule-policy` (bulk, transactional — invalid ID aborts all). **UI copy: Horário padrão** — never show `OpenHours` or “80%” in the product UI.
_Avoid_: Forcing explicit Slot drawing when the tenant only needs “18:00–00:00 all open”; seeding dozens of identical SlotGrid templates when OpenHours fits

**SlotGrid**:
Schedule policy that authors the week as explicit **ScheduleTemplate** cells, then **PublishDay** materializes **Slot** rows. Use for fine exceptions (lesson blocks, closed mornings). Default grid seed is a **single** API call: `POST /api/schedule/templates/seed-default` (`rentalAssetIds` for a set). Day query/publish accept the same ID list. **UI copy: Grade personalizada** — never show `SlotGrid` in the product UI. Fine edits stay per rentable on Weekly templates.
_Avoid_: N client-side POSTs per hour×day as the product path

**Admin Daily Agenda UX**:
Operational resource grid: compact toolbar (date navigation, multi-resource selector, apply grid) and a virtualized time × resource matrix as the main surface. Cells open a contextual drawer for day overrides or SlotGrid recurrence edits. Copy stays generic (spaces/goods), never segment-specific.
_Avoid_: Vertical stacks of per-resource cards; mixing weekly policy editors into the day grid; sports-specific labels

**Weekly setup UX**:
Same time × resource matrix as the day agenda, keyed by weekday instead of a calendar date. OpenHours columns render derived repeating windows; SlotGrid columns render that weekday’s templates. Empty SlotGrid cells create a template; OpenHours cells open the window-level schedule setup.
_Avoid_: Loading templates for a single Rentable while the day grid shows the whole selection; a form column that replaces the matrix

**Day occurrence**:
A dated Slot (or OpenHours-derived window) for one Rentable. Admin can adjust kind/label, make unavailable, or restore the weekly default for that single date (`OnlyThisDay`). For SlotGrid, `EntireRecurrence` updates the matching weekly template and cascades to future non-booked slots that still match the previous fingerprint. OpenHours entire-window edits stay in Weekly setup. Booked occurrences redirect to the reservation.
_Avoid_: Deleting a weekly template to hide one date; cascading over Booked or intentional DailyOverride rows

**Day read path**:
`GET /api/schedule/days/{date}` must stay at a constant, small number of database round trips regardless of how many Rentables or hours are requested. OpenHours derivation loads the day's blocking reservations once and computes overlap in memory, and reuses the Slots already loaded by the same request to detect starts that are already persisted (including cancelled tombstones so unavailable OpenHours windows stay hidden). Admin reads include cancelled occurrences; public reads stay available+bookable only. Slot DTOs expose `sourceTemplateId`, `schedulePolicy` and `supportsEntireRecurrence`. `POST /api/schedule/slots/daily-occurrence` is the seam for day/recurrence edits; `POST /api/schedule/templates/apply-weekly-rule` expands resources × weekdays × intervals transactionally.
_Avoid_: One query per derived slot; fetching all seven weekdays of templates to answer a single day; canceling a derived OpenHours window without a persisted tombstone; N client POSTs to build a weekly grid

**Occupancy kind**:
Tenant catalog entry for how a schedule cell behaves (label, optional description/icon, color, bookable/blocks flags). Icons are client-resolved Lucide keys, never hardcoded segment enums.
_Avoid_: Closed product enums for lesson/court/clinic; requiring icons for every kind

**Layout**:
A Tenant-authored visual arrangement of Rentables on a 2D canvas (positions and sizes) so Customers pick a resource from a map rather than only from a list. Multiple Layouts are allowed (different venues or views).
_Avoid_: Hard-coding a single FICC court map in the product

**Subdomain**:
The tenant-owned URL slug used to resolve which Tenant a public B2C request belongs to (for example `clube-x` → `clube-x.rolvix.com.br`). It is identity routing, not the branded experience itself.
_Avoid_: custom domain (until real custom hostnames are supported), slug alone without tenant resolution

## 2. Dinâmica de Módulos e Customização (Requisitos Core)
- **O Cardápio de Módulos:** Super Admin habilita/desabilita módulos por Tenant (chaves canônicas: `inventory`, `maintenance`, `pmoc`, `os`, `rentals`). Persistência em `core.tenant_modules`. **Meta:** API e UI devem bloquear módulos inativos (enforcement ainda incompleto — ver ROADMAPs).
- **Extensibilidade de Entidades (Schema Flexível):** Campos base padronizados + `JSONB` no PostgreSQL para customizações por cliente. **Jamais** crie tabelas dinâmicas por cliente.
- **Regra de ouro de segurança:** Administradores **nunca** definem a senha de outros usuários. Fluxo alvo: convite por token → link `/invite?token=` → o próprio usuário define a senha. Super-Admin convida o admin inicial no wizard/edit do tenant. Onboarding público que ainda coleta senha do admin é legado.
- **Modo suporte (Super-Admin):** no painel de clientes, “Abrir ambiente” (mesma aba em `rolvix.com.br`) garante membership Admin + seta `app_metadata.tenant_id` e refresh — console B2B daquele tenant. **Não** é o portal B2C (`{subdomain}.rolvix.com.br`), que usa CustomerAuth. “Voltar à plataforma” limpa o `tenant_id`. No create, e-mails `PlatformAdmin` viram Admin (recria Auth se sumiu). Excluir tenant **não** apaga Auth de PlatformAdmin nem de quem ainda tem membership noutro tenant. Schema: `User` único por `(TenantId, SupabaseAuthId)`. **PlatformAdmin** (allowlist) não conta como assento do tenant, não aparece em listas de usuários/técnicos e não pode ser convidado/promovido/excluído pela UI de users. Detalhe operacional: `docs/sessions/2026-08-05-platform-admin-membership.md` e `docs/runbooks/platform-admin-enter.md`.
- **Domínio personalizado vs portal do tenant:** Cadastrar `Subdomain` (+ branding) é só a chave de roteamento e a identidade visual. A UI B2C (login/cadastro branded → depois reservas) é o portal — ver seção Portal B2C.

## 3. Estrutura de Domínio (O Mapa Mental da Arquitetura)
Separação rigorosa entre fundação ("Core") e aplicativos ("Módulos"). Os módulos listados são o objetivo de longo prazo; **execute apenas o beachhead e as fases ativas** — não antecipe Estoque, RH ou Financeiro.

**Árvore de Arquitetura:**
```
Core (Fundação Multi-Tenant e Agnóstica)
├── Tenants (+ Subdomain, LogoSvg, brand colors, tagline)
├── Users (B2B)
├── Customers (B2C — por Tenant; CPF, CEP, phone verified via SMS)
├── Permissions / Roles
└── Units

Módulos (Fatias Verticais Plugáveis)
├── Inventory / Ativos          (entregue; base para Rentals)
├── Maintenance / PMOC / OS    (entregue; espinha de conformidade)
├── Rentals                    (FOCO ATUAL — agnóstico: espaços, bens, veículos; beachhead clube)
├── Checklists                 (Futuro)
├── Documentos                 (Futuro)
├── Estoque                    (Futuro)
├── Financeiro                 (Futuro)
├── RH                         (Futuro)
├── Dashboard
└── Notifications              (fila + Resend / Meta WhatsApp)
```

**Dois repositórios Git (não monorepo versionado):**
```
vlr-api (este repo)                 vlr-web (repo irmão)
├── CONTEXT.md  ← canônico          ├── CONTEXT.md  ← espelho
├── ROADMAP.md                      ├── ROADMAP.md
├── AGENTS.md                       ├── AGENTS.md
├── docs/adr|sessions|runbooks      ├── docs/sessions (FE)
├── .cursor/rules/                  ├── .cursor/rules/
├── Core/                           └── src/
└── Platform.Api/
```

Workspace local pode agrupar os dois clones numa pasta; **nunca** assumir `CONTEXT.md` na pasta pai — cada repo versiona o seu.

*Regra de Ouro Arquitetural:* Todo dado de módulo referencia o Core (`TenantId`; `UnitId` quando o domínio exige escopo de site) para isolamento total entre clientes.

## 4. Fases de Desenvolvimento
Avance de fase só quando a atual estiver estável o bastante para o beachhead. ROADMAPs detalham o trabalho aberto.

- **Fase 1: O Core (Esqueleto Agnóstico)** — em andamento
  - Multi-tenant via Global Query Filters no EF Core (RLS Postgres ainda não é requisito bloqueante do beachhead).
  - Tenants, Units, Users, RBAC (Permissions modeladas; enforcement fino ainda incompleto).
  - Catálogo de módulos por tenant (persistido; gating de API/UI pendente).
  - Subdomain + branding (`LogoSvg`, cores, tagline) no cadastro do tenant; portal UI em uso.

- **Fase 1.5: Notificações reais (Resend + WhatsApp)** — etapa principal de infra; **adiada**
  - Providers Resend/Meta + webhook já no código; falta fechar configuração externa (credenciais, template de autenticação Meta, OTP/avisos enfileirados de verdade).
  - Status: **em pausa** enquanto o time finaliza a configuração do WhatsApp. Retomar antes de depender de OTP/avisos em produção.

- **Fase 2a: MVP operacional (PMOC + OS + Inventário)** — entregue como base
  - Planos PMOC, geração de OS (Hangfire), inventário de ativos.
  - Mantém-se no cardápio; não é o foco de feature do ciclo atual.

- **Fase 2b: Beachhead Rentals (clube)** — foco atual de produto
  - Portal B2C branded — login **e-mail + senha** + cadastro (foto, nome, e-mail, senha, CPF, CEP, celular com SMS) — em código; fechar ops.
  - Branding no Tenant: **`LogoSvg` (único canal de marca)** + cores + tagline.
  - Em seguida: disponibilidade por **Slot**, reserva, estado das quadras, gestão admin.
  - Avisos WhatsApp usam o celular **já verificado por SMS** (celular ≠ login).
  - Convite B2B real + `/invite` (regra de ouro de senha) permanece no Core, separado do cadastro B2C.

- **Fase 3: Motor de Extensibilidade**
  - Campos dinâmicos (`JSONB`) em formulários existentes.

- **Fase 4+: Expansão do Hub e Mobile**
  - Novos módulos (RH, Financeiro, Documentos) e app mobile — só após o beachhead gerar receita/aprendizado.

## 5. Idioma e Padrões de Nomenclatura
- **Idioma Universal:** Todo o código em **Inglês** (variáveis, classes, tabelas, rotas, comentários, commits). Textos de UI ao usuário final: Português (via i18n no frontend).
- **C# / .NET:** Types/métodos `PascalCase`; locals/params `camelCase`; interfaces com prefixo `I`.
- **PostgreSQL (EF Core):** `snake_case` em tabelas/colunas (naming conventions automáticas).
- **React / TypeScript:** Componentes/types `PascalCase`; funções/hooks `camelCase`.

## 6. Stack Tecnológico e Padrões (Regras Inegociáveis)
- **Backend (`vlr-api`):** .NET 10, REST. Organização dominante: `Platform.Api/Modules/<Área>/` (Controller + Service + DTOs). Features Minimal API + MediatR só onde já existem (`CreateTenant`, `InviteUser`) — não expandir MediatR sem decisão explícita. Deploy: **Docker no Railway**.
- **Frontend (`vlr-web`):** React + Vite, shadcn/ui, TailwindCSS. Deploy: **Vercel**. Consome a API com JWT Bearer (`VITE_API_URL`).
- **Dados e Auth (PaaS):** **Supabase** = PostgreSQL + Supabase Auth (B2B). Proibido provisionar AWS “pura” (RDS/Cognito/EC2) neste momento. EF Core permanece agnóstico à connection string.
- **B2C:** Customer registrado por Tenant. **Login: e-mail + senha** (todos os tenants). Celular verificado por **SMS** no cadastro; WhatsApp só para avisos operacionais. Resolução pública por subdomain (`X-Tenant-Subdomain` / host). JWT próprio (`Customer`) após login — não Supabase Auth.
- **Notificações:** Fila em memória (`NotificationQueue` + `BackgroundService`). Providers: **Resend** (e-mail), **Meta WhatsApp** (avisos), **SMS** (verificação de celular — provider a plugar na mesma fila), **Dev** como fallback. Nunca enviar e-mail/WhatsApp/SMS de forma síncrona dentro da request HTTP. WA iniciado pela empresa exige template Meta aprovado. Config WA/Resend: Fase 1.5 (adiada).
- **TypeScript:** Zero `any`; validação Zod espelhando DTOs da API. Frontend **nunca** consulta o banco via SDK Supabase — só auth.
- **Isolamento:** Dados de módulo com `TenantId` (e `UnitId` quando aplicável).

## 7. Disciplina do agente (ROADMAP = fonte viva)
O agente **deve** manter os ROADMAPs atualizados em toda tarefa relevante:

1. **Ao iniciar ou concluir trabalho:** atualizar o `ROADMAP.md` deste repo — marcar itens feitos, mover foco, acrescentar lacunas descobertas. Se a mudança exige UI, mencionar o próximo passo no ROADMAP do `vlr-web` (repo irmão).
2. **Se a equipe decidir mudar prioridade, escopo ou abordagem:** atualizar o ROADMAP **na mesma mudança** e registrar a decisão na seção **Histórico** (data + o que mudou + por quê). O ROADMAP não é só backlog; é o diário curto do projeto.
3. Não apagar contexto útil: itens cancelados ou adiados ficam no histórico (ou riscados com nota), não desaparecem sem rastreio.
4. Glossário/beachhead: atualizar este `CONTEXT.md` **e** o espelho em `vlr-web`.

## 8. Instrução de Ação
Antes de implementar, pergunte-se:
1. Isso respeita o **beachhead atual** (Rentals / clube) e a ordem de etapas (WA/Resend adiado → portal estável → slots)?
2. Respeita o Core multi-tenant e as regras em `.cursor/rules`?
3. Atualizei o `ROADMAP.md` correspondente (checklist + histórico, se houve mudança de plano)?
