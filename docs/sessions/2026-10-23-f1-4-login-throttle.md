# F1-4 — throttle do login B2C (local)

Desenho aprovado pelo usuário. Sem Git, branch, PR, merge, migration remota ou deploy por instrução explícita.

## Implementação

- PostgreSQL compartilhado: uma instrução INSERT/ON CONFLICT consome IP global e tenant/e-mail normalizado em ordem estável, inclusive quando já bloqueado. Janela fixa desde a primeira tentativa, clock do banco; contador saturado; nenhum reset após sucesso.
- Configuração `CustomerLoginThrottle:{EmailLimit,IpLimit,WindowSeconds}`: defaults 5/30/900, validação no startup. IP canônico pelo leitor existente (ADR 0006/0008), unknown compartilhado quando ausente.
- Chaves HMAC-SHA256 com secret existente `Supabase:JwtSecret` e separação de domínio; e-mail/IP nunca persistidos em claro pelo throttle. Rotação do secret inicia novos buckets.
- Gate antes do lookup; tenant explícito; conta ausente/sem hash verifica hash dummy com mesmo custo padrão. Credenciais inválidas preservam resposta genérica. Bloqueio retorna 429 `{error}` + Retry-After em segundos.
- Migration `20261023120000_AddCustomerLoginThrottle`: tabela operacional SQL fora do modelo EF, índice por expires_at, Down remove só a tabela. Limpeza diária no DataRetentionJob usa FOR UPDATE SKIP LOCKED, sem esperar writes de login ou inverter locks.

## Validação e revisão

- Build final: `dotnet build tests/Platform.Api.Tests --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false` — 0 warnings/0 errors.
- `dotnet test` convencional (focado e completo): abortado por SocketException/Permission denied do VSTest; MSBuild paralelo também requer sockets. Restore funcionou; build serial contorna apenas o problema de build.
- Runner xUnit temporário em processo, fora do código versionado: suíte completa 1712 total / 1603 passed / 0 failed / 109 skipped. PostgreSQL/Docker indisponível. A primeira execução do runner em /tmp teve 19 falhas por raiz/fixtures ausentes; executando o mesmo runner no output normal dos testes, todas passaram. Logs em /tmp/f1-4-suite-final.log.
- 13 novos testes: 3 sem banco e 10 PostgreSQL; limites, normalização, tenants, 429/header, resposta/timing, concorrência em ambos budgets, sucesso, janela, retenção/locks e migration Down/Up. Ciclo red/green completo não demonstrado: primeira tentativa sem assets, depois execução convencional bloqueada; não alegar prova PostgreSQL.
- Reviewer Sol independente identificou crescimento permanente (corrigido com retenção) e possível deadlock da limpeza (corrigido com SKIP LOCKED). Verificação independente da correção sem novos findings de código.
- MERGE_BLOCKED: executar PostgreSQL sem skips antes de integrar; nenhum merge tentado. Não inferir aprovação de produção.

## Próximo passo e teste externo

`REQUIRE_POSTGRES_TESTS=true ROLVIX_TEST_POSTGRES_CONNECTION='<conexão local isolada>' dotnet test tests/Platform.Api.Tests` (a fixture cria banco isolado). Exigir testes PostgreSQL sem skips. Validar login repetido com X-Tenant-Subdomain: primeira sequência de credenciais erradas retorna 401, tentativa acima do limite retorna 429 com Retry-After; trocar e-mail continua sujeito ao limite de IP.

Aplicar migration Up antes do código; rollback do código antes de Down. Próxima API: validação PostgreSQL/DEV e fluxo de entrega autorizado. Próxima web: validar apresentação de 429/Retry-After no portal e continuar agenda/Slot do roadmap irmão.

Follow-up pedido em ~/rolvix-ai-docs/followups/F1-4.md bloqueado por filesystem read-only; cópia em docs/followups/F1-4.md para transporte externo.
