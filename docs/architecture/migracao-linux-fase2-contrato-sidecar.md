# Fase 2 — Contrato do sidecar Windows (issue #577, Epic #575)

Serviço fino numa VM Windows mínima que expõe os dois processos legados .NET Framework 4.8.1.
**Contrato apenas — o serviço e o cliente ainda não foram implementados.**

## Autenticação
Por **rede isolada** (a VM só aceita conexões da(s) sub-rede(s) da API, via firewall). Sem segredo
compartilhado (não repetir o `ApiKeyGateFilter` removido — ver `.claude/rules/security.md`).

## Endpoints (HTTP interno, JSON)

| Método | Rota | Substitui | Request | Response |
|--------|------|-----------|---------|----------|
| POST | `/decrypt` | `Process.Start` de `LayoutParserDecrypt.exe` em `Services/Database/DecryptionService.cs` | `{ "content": "<cifra>", "correlationId": "..." }` | `200 { "content": "<claro>" }` · `422 { "error": "..." }` · `500` |
| POST | `/lowcode/transform` | `LowCodeRunner` (x86, DLLs Sysmiddle) | `{ "layoutXml", "mapperXml", "document", "correlationId" }` | `200 { "output", "warnings": [] }` · `422 { "error" }` |
| GET | `/health` | — | — | `200 { "status": "ok" }` |

Regras: falha explícita (nunca devolver a cifra como se fosse texto claro — vale o P1.1 atual); timeout
no cliente (default 60 s); `correlationId` propagado no header `x-correlation-id`; conteúdo de documento
nunca logado.

## Integração na API (a implementar)
- `IDecryptionService` ganha implementação `HttpDecryptionService` selecionada por config
  (`Sidecar:BaseUrl`); em Windows continua o `Process.Start` local. Resiliência: degrada com erro
  explícito, sem derrubar o request.
- Mesmo padrão para o LowCodeRunner (`LowCodeTransformationService`).

## Pendências
- VM Windows mínima: `@lp-devops`. LayoutParserLib no sidecar: decidir após Fase 1.
