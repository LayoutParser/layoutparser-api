# CLAUDE.md — LayoutParser API

Este arquivo configura o comportamento do Claude Code ao trabalhar neste repositório.
Inspirado no harness **AIOX**, porém **enxuto e focado no domínio .NET** desta API.

> **Idioma:** responda ao usuário em **português (PT-BR)**. Documentação de produto é **bilíngue (PT/EN)**.

---

## 1. O que é este projeto

API **ASP.NET Core (.NET 10)** que parseia documentos posicionais (TXT / MQSeries / IDOC)
contra um **layout XML** (low-code Sysmiddle), com camada de IA/ML que aprende a gerar
transformações (**XSLT/TCL**). É o **hub** de um ecossistema de 4 repositórios:

| Repo | Papel |
|------|-------|
| **layoutparser-api** *(este)* | Orquestra parse, cache, IA, transformação. Source of truth do runtime. |
| **layoutparser-lib** | Criptografia Sysmiddle (DLL referenciada). |
| **layoutparser-decrypt** | `.exe` de descriptografia (processo externo). |
| **layoutparser-portal** | Front-end (Vite + React). |

Contexto completo: [`README.md`](../README.md).

---

## 2. Sistema de Agentes (enxuto)

Ative com `@agent-name` ou via `Task` tool. Personas tailored ao stack .NET:

| Agente | Persona | Escopo principal |
|--------|---------|------------------|
| `@lp-architect` | **Aria** | Arquitetura, decisões técnicas, a visão IA→XSLT, trade-offs. **Não implementa.** |
| `@lp-backend-dev` | **Dex** | Implementação C#/.NET: controllers, services, DI, cache. |
| `@lp-parser-llm` | **Lia** | Domínio: parsing, detecção, Learning/RAG, geração XSLT/TCL, Ollama/Gemini. |
| `@lp-qa` | **Quinn** | Testes, validação de transformação, quality gates. |
| `@lp-devops` | **Gage** | `git push` (EXCLUSIVO), `gh pr create/merge` (EXCLUSIVO), Docker, CI/CD, MCP, configuração de segredos. |
| `@lp-doc` | **Duda** | Documentação bilíngue (README, Swagger/XML docs, diagramas). |
| `@lp-pm` | **Pia** | Converte bug/gate/decisão/feature em PBI/User Story no GitHub Project (`gh issue`, `gh project item-add`). Não prioriza sozinha. |

### Regra de autoridade (resumo)
- **Apenas `@lp-devops` faz `git push`/`gh pr create/merge`** e gerencia MCP/CI. Demais agentes: `git add/commit` local apenas.
- **Apenas `@lp-pm` cria/edita issues e itens do GitHub Project** (`gh issue`, `gh project item-add`) — backlog, não release.
- `@lp-architect` **analisa e recomenda**, não escreve código de produção.
- Detalhe: [`.claude/rules/agent-authority.md`](rules/agent-authority.md).

### Handoff entre agentes
Ao trocar de agente, compacte o contexto anterior num artefato de handoff (~400 tokens):
story/tarefa atual, branch, decisões-chave, arquivos tocados, próximo passo.
Protocolo: [`.claude/rules/agent-handoff.md`](rules/agent-handoff.md).

---

## 3. Padrões de Código (.NET) — resumo

Detalhe completo em [`.claude/rules/dotnet-standards.md`](rules/dotnet-standards.md).

- **Nullable + ImplicitUsings habilitados** (`LangVersion: preview`). Não reintroduza `using` redundante.
- **DI:** registre serviços em `Program.cs` no grupo correto (Cache, Database, Parsing, Transformation…). Prefira `Scoped`; `Singleton` só para estado compartilhado (ex.: LowCode runners).
- **Async:** `async/await` ponta a ponta; sufixo `Async`; nunca `.Result`/`.Wait()`.
- **Resiliência:** dependências externas (Redis, SQL, Ollama, `.exe`) podem falhar — **degrade graciosamente**, nunca derrube o request principal. Veja o padrão de Redis opcional em `Program.cs`.
- **Background work:** aprendizado/transformação é *fire-and-forget* (`Task.Run` / `RunInBackgroundAsync`) e **não pode** quebrar a resposta ao usuário.
- **Logging:** use `ILogger<T>`; mensagens estruturadas (`{Param}`), não interpolação. Preserve o `CorrelationId`.
- **JSON com XML:** mantenha `UnsafeRelaxedJsonEscaping` — não "consertar" o escaping quebra o XML no payload.
- **Comentários:** PT-BR, no estilo já presente no código.

---

## 4. Quality Gates (antes de concluir)

```bash
dotnet build            # deve compilar sem erros
dotnet test             # quando houver testes (Services/Testing + projeto de testes)
```

- Não conclua uma tarefa com build quebrado.
- Mudou contrato de endpoint? Atualize Swagger/XML docs e o README (delegue a `@lp-doc`).
- Mexeu em parsing/transformação? Rode/atualize os testes de `Services/Testing`.

---

## 5. Segurança (NON-NEGOTIABLE)

- **NUNCA** comite segredos. `appsettings.json` **já tem segredos versionados** (pendência crítica — ver [`rules/security.md`](rules/security.md)).
- Ao tocar em config, prefira `dotnet user-secrets` / variáveis de ambiente.
- Sinalize qualquer credencial, connection string ou API key encontrada em texto plano.

---

## 6. Git & Commits

- **Conventional Commits:** `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.
- Trabalhe em branch (`feat/*`, `fix/*`); **não** comite direto na `master` sem pedido.
- **Push só por `@lp-devops`** e só quando o usuário pedir.

---

## 7. Otimização Claude Code

| Tarefa | Use | Não use |
|--------|-----|---------|
| Buscar conteúdo | `Grep` | `grep`/`rg` no bash |
| Ler arquivos | `Read` | `cat`/`head`/`tail` |
| Editar | `Edit` | `sed`/`awk` |
| Buscar arquivos | `Glob` | `find` |

- Chamadas independentes em **paralelo** num só turno.
- Comandos: `dotnet` roda em PowerShell (shell primário) ou Bash.
- **rtk (Rust Token Killer):** instalado localmente (`~/.local/bin/rtk`, sem hook global). Convenções completas
  de uso (git, build/test, filtros) estão no [`CLAUDE.md`](../CLAUDE.md) gerado por `rtk init` na raiz do repo —
  não duplicado aqui. NÃO substitui as tools nativas (Grep/Read/Glob continuam preferidas).
- **better-context (btca):** ao mexer com libs externas, prefira consultar o código-fonte real via btca a confiar em docs desatualizadas.

---

## 8. MCP

Existe um **MCP Server em C#** em `mcp/LayoutParserMcp/` que expõe parse/catálogo/transformação
como *tools*. Gestão de MCP é exclusiva do `@lp-devops`. Regras: [`rules/mcp-usage.md`](rules/mcp-usage.md).

---

*LayoutParser API · Claude Code harness v1 · enxuto, focado em .NET*
