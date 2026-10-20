# Catalog: política de cancelamento por tenant

Implementado em `feat/catalog-cancel-policy`, sem commit, push, deploy ou aplicação de migration. Execução direta com Codex autorizada pelo usuário; agentes e skills dispensados nesta tarefa. Genérico para pedidos e solicitações de produtos de qualquer tenant.

## Default preservado

Antes desta mudança, `CatalogOrder.Cancel(Customer, ...)` aceitava **somente Requested**. Não havia endpoint de cancelamento de `ProductRequest` e seu único status era `Submitted`.

Sem linha de `CatalogPolicy`, a leitura retorna `customerCanCancelOrders=true`, `orderCancelableUpToStatus=Requested`, `customerCanCancelRequests=false`, `requestCancelableUpToStatus=Submitted`, `cancellationPolicyText=null`. Ler não cria linha nem reescreve tenants existentes.

`catalog.catalog_policies`: entidade `ITenantScoped`, Global Query Filter e índice único em `tenant_id`; FK para Tenant com cascade delete. O texto é opcional, até 2000 caracteres, tratado como texto simples. PUT substitui todos os campos; os quatro campos de flags/status são obrigatórios; texto omitido/null apaga o texto.

## Contrato HTTP / JSON

- `GET /api/catalog/policy` → `CatalogPolicyResponse`.
- `PUT /api/catalog/policy` recebe `UpsertCatalogPolicyRequest` → `CatalogPolicyResponse`.
- Ambos exigem B2B e `catalog.products.manage`. Super-Admin utiliza a sessão de tenant existente; sem tenant, falha fechado. Não há tenantId no payload.
- `GET /api/catalog/portal/policy` → `PortalCatalogPolicyResponse`, com `CatalogCancellationUpToStatus` em `upToStatus`. Público com `X-Tenant-Subdomain`; cliente autenticado pode usar o tenant do JWT sem header. Segue o binder/gate de módulo existente.
- `POST /api/catalog/portal/orders/{id}/cancel` → `CatalogOrderResponse` (existente, contrato de resposta preservado).
- `POST /api/catalog/portal/product-requests/{id}/cancel` → `ProductRequestResponse` (DTO existente, mesmo shape do create).
- POSTs sem body, autenticados como Customer. Ausente/outro owner/outro tenant → 404; política desabilitada ou status não permitido → 409 `{ "error": "mensagem" }`. PUT com limite/status/texto inválido → 400. Repetir cancelamento de estado terminal → 409.
- Todas essas superfícies exigem módulo `catalog` ativo.

`UpsertCatalogPolicyRequest` e `CatalogPolicyResponse` (default):

```json
{
  "customerCanCancelOrders": true,
  "orderCancelableUpToStatus": "Requested",
  "customerCanCancelRequests": false,
  "requestCancelableUpToStatus": "Submitted",
  "cancellationPolicyText": null
}
```

`PortalCatalogPolicyResponse` (default):

```json
{
  "canCancelOrders": true,
  "canCancelRequests": false,
  "upToStatus": {
    "orders": "Requested",
    "requests": "Submitted"
  },
  "policyText": null
}
```

`upToStatus` contém os dois limites, pois pedidos e solicitações têm enums diferentes. Limites de pedidos: `Requested` → `Approved` → `Preparing` → `Ready`, inclusivo. `Completed`, `Rejected` e `Cancelled` nunca permitem cancelamento de cliente. Limite de solicitação: somente `Submitted` (Enviada). Solicitação cancelada recebe o novo status `Cancelled` (valor 1; `Submitted=0` preservado). Não se compara um estado terminal somente pela ordem numérica do enum.

`ProductRequestResponse` no cancelamento:

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "customerId": "00000000-0000-0000-0000-000000000002",
  "description": "Item solicitado",
  "quantity": 1,
  "note": null,
  "status": "Cancelled",
  "createdAt": "2026-10-20T12:00:00Z",
  "files": [
    { "id": "00000000-0000-0000-0000-000000000003", "fileName": "item.pdf", "mimeType": "application/pdf", "sizeBytes": 128 }
  ]
}
```

A resposta de pedido continua com os campos existentes: `id`, `displayNumber`, `orderNumber`, `status`, `customerId`, `customerName`, `customerEmail`, `customerPhone`, `customerNote`, `totalAmount`, `currency`, `rejectedReason`, `cancelledReason`, `createdAt`, `items`, `history`. Cancelamento Customer continua gerando histórico com ator Customer e sem exigir motivo. Cancelamento de staff não consulta essa política.

## Persistência e concorrência

Migration gerada pelo comando exigido:

```bash
dotnet ef migrations add AddCatalogPolicy --project Core/Platform.Core.Infrastructure --startup-project Platform.Api --no-build
```

ID: `20261020131737_AddCatalogPolicy`. Cria somente `catalog.catalog_policies`, índice único por tenant, FK cascade e checks dos limites permitidos. Não há backfill. **Não aplicada.** Snapshot também marca `ProductRequest.Status` como concurrency token, sem mudança de coluna. Pedidos mantêm o RowVersion existente; uma transição concorrente que vencer faz o cancelamento stale retornar conflito. Primeiros PUTs concorrentes de política reutilizam a linha vencedora após unique violation.

## Validação e como testar

- `dotnet build -m:1 -p:UseSharedCompilation=false -nodeReuse:false --no-restore`: passou, 0 warnings/erros.
- `dotnet test --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false`: abortou antes dos testes; sandbox bloqueia o socket local do VSTest (`SocketException (13): Permission denied`). A suíte completa permanece pendente.
- Runner temporário em `/tmp/catalog-policy-checks` invocou diretamente Facts/InlineData de `CatalogPolicyTests`, `PortalProductRequestReadTests`, `CatalogServiceTests`, `CatalogOrderStateMachineTests` e `PermissionAuthorizationMatrixTests`: **76 passaram, 0 falhas**. Inclui HTTP em TestServer, persistência em memória, limites inclusivos/terminais, flag desabilitada, default sem linha, owner/tenant, permissões e conflito de status stale. Não substitui suíte completa nem valida PostgreSQL real.

Em ambiente com sockets habilitados: executar `dotnet build && dotnet test`; em particular, `dotnet test --filter 'FullyQualifiedName~CatalogPolicyTests|FullyQualifiedName~PortalProductRequestReadTests|FullyQualifiedName~PermissionAuthorizationMatrixTests'`.

Depois da migration aplicada em etapa autorizada de DEV: GET sem linha deve retornar o default acima. PUT como staff com `catalog.products.manage`, habilitando solicitações e definindo limite `Approved`; GET público deve refletir o tenant; cancelar pedido Requested/Approved deve passar e Preparing deve retornar 409. Solicitação Submitted deve passar e repetir deve retornar 409. Outro cliente/tenant deve receber 404; Customer não deve acessar PUT administrativo.

Próximo API: suíte completa/PostgreSQL em ambiente habilitado e aplicação da migration em etapa autorizada. Próximo `vlr-web`: consumir a política em configuração administrativa e em botões/texto do portal, incluindo `ProductRequestStatus.Cancelled`; há APIs prontas, UI não alterada nesta tarefa.
