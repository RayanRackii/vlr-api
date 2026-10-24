<!-- Movido verbatim de AGENTS.md em 2026-10-24 (economia de tokens). Regras continuam vigentes. -->

## Roteamento multi-agent

Arquivos em [`.cursor/agents/`](../../.cursor/agents/). São roteadores — não copiam produto, arquitetura, convenções nem o corpo dos skills.

Roteamento por capacidade: Luna (`gpt-6-luna`) para mudanças pequenas, testes e docs; Terra (`gpt-5.6-terra`) para endpoints e componentes; Sol (`gpt-6.1-sol`) para mudanças complexas/cross-cutting, segurança e reviews. Se um agente ou modelo nomeado estiver indisponível, escolha o próximo modelo adequado à capacidade necessária e prossiga. O parent/orchestrator coordena Git, gates, PR, Merge Risk Gate e merge em `develop`; um writer ativo por working tree.

- Mudança local/trivial: Luna; pule arquitetura.
- Endpoints/componentes: Terra; peça revisão independente a Sol.
- Arquitetura, cross-repo, segurança, concorrência, persistência e risco alto: Sol prepara spec/dossier; resolva Human Decision Gate quando aplicável.
- Merge Risk Gate: Sol faz análise independente com dossier e diff relevante, sem repo-wide exploration. Mantenha os critérios de segurança, auth/tenant, compatibilidade, rollback e aprovação humana/produção abaixo.
- Frontend visual: Terra implementa; Sol revisa.

Não permita que indisponibilidade de roteadores interrompa trabalho: use o próximo modelo adequado e mantenha ownership separado entre API e web. Prompt cache do provider ≠ memória do projeto ≠ context pack.

