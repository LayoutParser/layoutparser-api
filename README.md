# LayoutParser API

> **PT-BR** · API .NET 10 que faz o *parsing* de documentos posicionais (TXT / MQSeries / IDOC) contra um **layout XML** (gerado no low-code Sysmiddle), com uma camada de IA/ML que aprende a gerar transformações (**XSLT/TCL**) automaticamente — caminho para eliminar o XML low-code.
>
> **EN** · .NET 10 API that *parses* positional documents (TXT / MQSeries / IDOC) against an **XML layout** (authored in the Sysmiddle low-code platform), with an AI/ML layer that learns to generate transformations (**XSLT/TCL**) automatically — the path to retiring the low-code XML.

<p>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" />
  <img alt="ASP.NET Core" src="https://img.shields.io/badge/ASP.NET%20Core-Web%20API-512BD4?style=flat-square" />
  <img alt="Redis" src="https://img.shields.io/badge/Redis-cache-DC382D?style=flat-square" />
  <img alt="SQL Server" src="https://img.shields.io/badge/SQL%20Server-source%20of%20truth-CC2927?style=flat-square" />
  <img alt="Serilog" src="https://img.shields.io/badge/Serilog%20%2B%20Elastic-observability-005571?style=flat-square" />
  <img alt="LLM" src="https://img.shields.io/badge/LLM-Ollama%20%7C%20Gemini%20%7C%20OpenAI-7E57C2?style=flat-square" />
</p>

---

## 📑 Índice / Table of Contents

1. [Visão geral / Overview](#1-visão-geral--overview)
2. [Ecossistema de projetos / Project ecosystem](#2-ecossistema-de-projetos--project-ecosystem)
3. [Arquitetura / Architecture](#3-arquitetura--architecture)
4. [Como o parse funciona / How parsing works](#4-como-o-parse-funciona--how-parsing-works)
5. [A visão de IA / The AI vision](#5-a-visão-de-ia--the-ai-vision)
6. [Stack tecnológica / Tech stack](#6-stack-tecnológica--tech-stack)
7. [API & Endpoints](#7-api--endpoints)
8. [Fundação da plataforma fiscal / Fiscal platform foundation](#8-fundação-da-plataforma-fiscal--fiscal-platform-foundation)
9. [Configuração / Configuration](#9-configuração--configuration)
10. [Como rodar / Getting started](#10-como-rodar--getting-started)
11. [Segurança / Security](#11-segurança--security-)
12. [Observabilidade / Observability](#12-observabilidade--observability)
13. [Estrutura de pastas / Project structure](#13-estrutura-de-pastas--project-structure)
14. [Harness Claude Code & MCP](#14-harness-claude-code--mcp)
15. [Roadmap](#15-roadmap)

---

## 1. Visão geral / Overview

**🇧🇷** O LayoutParser API é o back-end de uma plataforma de **leitura, validação e transformação de documentos de integração** (notas fiscais eletrônicas e mensagens corporativas). O usuário, pelo front-end ([layoutparser-portal](#2-ecossistema-de-projetos--project-ecosystem)), anexa **dois arquivos**:

- um **layout XML** — a "planta" que descreve as linhas, campos, posições e tamanhos do documento (modelado no low-code **Sysmiddle**);
- um **documento** posicional (`.txt`, `.mq_series`, `.idoc`) — o dado bruto a ser interpretado.

A API casa os dois, devolve a **estrutura parseada** (linhas → campos → valores) para o front renderizar, e — em background — **aprende** com cada arquivo processado para evoluir até gerar as transformações sozinha.

**🇺🇸** LayoutParser API is the back-end of a platform for **reading, validating and transforming integration documents** (electronic fiscal notes and corporate messages). Through the front-end, the user uploads **two files**: an **XML layout** (the blueprint describing rows, fields, positions and sizes — authored in the **Sysmiddle** low-code tool) and a **positional document** (`.txt`, `.mq_series`, `.idoc`). The API matches them, returns the **parsed structure** for the front-end to render, and — in the background — **learns** from every processed file to eventually generate the transformations on its own.

> **Contexto acadêmico / Academic note:** este repositório é a base de back-end de um projeto de faculdade (TCC). A documentação é mantida bilíngue propositadamente. / This repository is the back-end base of a college project; documentation is intentionally bilingual.

---

## 2. Ecossistema de projetos / Project ecosystem

**🇧🇷** Esta API é o **ponto de conexão** de quatro repositórios. **🇺🇸** This API is the **connection hub** of four repositories.

| Repositório | Tipo | Papel / Role |
|-------------|------|--------------|
| **layoutparser-api** *(este)* | ASP.NET Core 10 Web API | Orquestra parse, cache, IA/ML, transformação e logging. **Source of truth do runtime.** |
| **layoutparser-lib** | .NET Class Library (DLL) | Criptografia Sysmiddle (`CryptographySysMiddle`) e utilitários compartilhados. Referenciada pela API via `HintPath`. |
| **layoutparser-decrypt** | .NET Console (`.exe`) | Descriptografa os layouts/pacotes Sysmiddle. Invocado pela API como processo externo. |
| **layoutparser-portal** | Vite + React + TypeScript | Front-end: upload de arquivos, render da estrutura parseada, edição de layouts. |

```
                         ┌───────────────────────────┐
                         │     layoutparser-portal    │  (front-end / Vite + React)
                         │  upload .xml + documento   │
                         └─────────────┬──────────────┘
                                       │  HTTP (CORS)
                                       ▼
        ┌──────────────────────────────────────────────────────────┐
        │                    layoutparser-api (.NET 10)              │
        │                                                            │
        │  Parse ── Cache(Redis) ── Learning/RAG ── Transformation   │
        │     │           │              │                │          │
        └─────┼───────────┼──────────────┼────────────────┼─────────┘
              │           │              │                │
   ┌──────────┘   ┌───────┘        ┌─────┘          ┌─────┘
   ▼              ▼                ▼                ▼
layoutparser-lib Redis        SQL Server        LLM (Ollama /
(crypto .dll)   (layouts/   (ConnectUS_Macgyver  Gemini / OpenAI)
                 mappers)     — source of truth)
   │
   ▼
LayoutParserDecrypt.exe  (descriptografia Sysmiddle)
```

> **🔌 MCP** · Um **MCP Server em C#** (ver [§14](#14-harness-claude-code--mcp)) expõe as operações da API como *tools* para agentes de IA, transformando este ecossistema num conjunto de ferramentas operáveis por LLMs.

---

## 3. Arquitetura / Architecture

**🇧🇷** A API segue uma arquitetura em camadas com **injeção de dependência** (registrada em [`Program.cs`](Program.cs)). **🇺🇸** Layered architecture with **dependency injection** wired in [`Program.cs`](Program.cs).

| Camada / Layer | Pasta / Folder | Responsabilidade / Responsibility |
|----------------|----------------|-----------------------------------|
| **API / Controllers** | `Controllers/` | Endpoints HTTP, validação de request, orquestração. |
| **Parsing** | `Services/Parsing/` | Detecção de tipo, *split* de linhas, normalização e validação do layout. |
| **Cache** | `Services/Cache/` + `Services/Database/Cached*` | Camada Redis sobre os dados do SQL (layouts e mappers). |
| **Database** | `Services/Database/` | Acesso ao SQL Server, descriptografia (`DecryptionService`). |
| **Learning / RAG** | `Services/Learning/`, `Services/Generation/` | Aprende padrões de cada documento; RAG sobre exemplos. |
| **Transformation** | `Services/Transformation/`, `Services/XmlAnalysis/` | Geração de **XSLT/TCL**, pipeline low-code, validação por XSD. |
| **Testing** | `Services/Testing/` | Testes automatizados de transformação (aplica XSLT e compara). |
| **Logging / Audit** | `Services/Logging/` | Serilog → arquivo + Elasticsearch, `CorrelationId`, auditoria. |

### Princípios de design / Design principles

- **Resiliência primeiro:** a aplicação **sobe mesmo sem Redis** (cache degrada graciosamente) — ver `Program.cs:171`.
- **SQL é a fonte da verdade; Redis é cache.** O cache é populado no startup via `RefreshCacheFromDatabaseAsync()`.
- **Background learning:** o parse responde rápido ao usuário e dispara aprendizado/transformação em *fire-and-forget* (`Task.Run` / `RunInBackgroundAsync`).
- **CorrelationId por request:** header `X-Correlation-ID` propagado para todos os logs.

---

## 4. Como o parse funciona / How parsing works

**🇧🇷** Fluxo do endpoint principal `POST /api/parse/upload` ([`ParseController`](Controllers/ParseController.cs)):

**🇺🇸** Flow of the main endpoint `POST /api/parse/upload`:

```
1. Recebe layoutFile (.xml) + txtFile (documento)
2. DetectType(sample)  ──► xml | mqseries | idoc | txt
   └─ override por extensão (.mq_series, .idoc) ou nome do layout (contém "MQ")
3. Se for XML puro ► devolve conteúdo para o front processar (xmltools.js)
4. Senão:
   a. Salva o arquivo p/ aprendizado (SaveFileForLearningAsync) — assíncrono
   b. ParseAsync(layoutStream, txtStream)  ──► Layout + ParsedFields + RawText
   c. ReestruturarLayout ► ReordenarSequences ► BuildDocumentStructure
   d. CalculateLineValidations (se o layout tem tamanho de linha configurado)
   e. Dispara LowCodeAuto.RunInBackgroundAsync (aprendizado contínuo, MQSeries)
5. Retorna { success, detectedType, layout, fields, text, summary,
             documentStructure, lineValidations, validationErrors }
```

**🇧🇷** Tipos de documento suportados: **XML**, **MQSeries**, **IDOC** e **TXT** posicional. A detecção combina conteúdo + extensão + layout selecionado (o conteúdo sozinho pode falhar em MQSeries com 601 chars/linha — daí os *overrides*).

**🇺🇸** Supported document types: **XML**, **MQSeries**, **IDOC** and positional **TXT**. Detection combines content + extension + selected layout (content alone can misfire on 601-char MQSeries lines — hence the overrides).

### Sinais aditivos de linha (2026-08-27) / Additive line signals (2026-08-27)

**🇧🇷** Design completo: [`docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md`](docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md). Dois booleanos novos em [`LineInfo`](Models/Entities/LineInfo.cs), ortogonais ao `Status` por campo:

| Campo | Significado | Uso sugerido no front |
|-------|-------------|------------------------|
| `IsDeclaredEmpty` | A linha foi identificada no layout (`matchingLineConfig != null`), mas o conteúdo bruto é vazio/whitespace. Diferencia "linha declarada e vazia" de "erro de parsing". | Renderizar como estado neutro, não como erro. |
| `PositionalAlignmentFailed` | ≥2 campos consecutivos da mesma ocorrência colapsaram na mesma posição inicial (sintoma de degradação posicional, ex.: bug reportado na LINHA006 de um layout `.mqseries`). É observacional — não é erro fatal, nem aponta o mapeador de origem (agnóstico a `sysmiddle`/`tcl`, por decisão de produto). | Exibir aviso visual de "atenção" na linha. |

```json
{
  "lineName": "LINHA006",
  "occurrence": 1,
  "isDeclaredEmpty": false,
  "positionalAlignmentFailed": true
}
```

> ⚠️ **Gap conhecido:** `ParsingResult.LineInfos` já é preenchido internamente pelo parser com
> esses dois sinais, mas o payload de `POST /api/parse/upload` (`ParseController.Upload`) **ainda
> não os serializa** — hoje esse objeto não é incluído na resposta HTTP. Consumidores que hoje
> inspecionam o payload podem notar isso caso o campo passe a existir em versão futura; até lá,
> os dois sinais **não estão acessíveis pelo front** via este endpoint. Fechar esse gap é trabalho
> pendente de `@lp-backend-dev`.

**🇺🇸** Full design at the file above. Two new booleans on [`LineInfo`](Models/Entities/LineInfo.cs), orthogonal to per-field `Status`:

| Field | Meaning | Suggested front-end use |
|-------|---------|--------------------------|
| `IsDeclaredEmpty` | The line was identified in the layout (`matchingLineConfig != null`), but its raw content is empty/whitespace. Distinguishes "declared and empty" from a parsing error. | Render as a neutral state, not an error. |
| `PositionalAlignmentFailed` | ≥2 consecutive fields in the same line occurrence collapsed onto the same start position (positional-degradation symptom, e.g. the LINHA006 bug on an `.mqseries` layout). Observational — not fatal, and intentionally agnostic to the source mapper (`sysmiddle` vs. `tcl`). | Show a visual warning on the line. |

> ⚠️ **Known gap:** `ParsingResult.LineInfos` is already populated internally by the parser with
> both signals, but the `POST /api/parse/upload` response (`ParseController.Upload`) does **not**
> serialize it yet — the object isn't included in the HTTP payload today. Both signals are
> **not reachable by the front-end** through this endpoint until that gap is closed
> (pending `@lp-backend-dev` work).

---

## 5. A visão de IA / The AI vision

**🇧🇷** O objetivo de longo prazo é **eliminar o XML low-code do Sysmiddle**: hoje um analista desenha o mapeamento no low-code, que produz um XML intermediário; queremos que o back-end **gere sozinho o XSLT** que transforma o documento original no XML final.

**🇺🇸** The long-term goal is to **retire the Sysmiddle low-code XML**: today an analyst designs the mapping in the low-code tool, producing an intermediate XML; we want the back-end to **generate the XSLT itself** that transforms the original document into the final XML.

### O "trio de ouro" / The golden triple

```
   TXT (original)  ──►  XML low-code (intermediário)  ──►  XML final (esperado)
   ▲                                                              ▲
   └──────────────  aprender a ponte direta via XSLT  ────────────┘
                    learn the direct bridge via XSLT
```

**🇧🇷** Cada documento processado gera um triplo **(TXT, XML low-code, XML final)** — ou seja, um **dataset de tradução supervisionada já rotulado**. A abordagem recomendada **não é fine-tuning** de um modelo Llama, e sim **RAG + few-shot com loop de auto-correção**:

**🇺🇸** Every processed document yields a triple **(TXT, low-code XML, final XML)** — i.e. a **pre-labeled supervised translation dataset**. The recommended approach is **not fine-tuning** a Llama model, but **RAG + few-shot with a self-correction loop**:

```
┌─ 1. INDEX ─────────────────────────────────────────────────────────┐
│  Indexa pares (layout → XSLT) num vector store (embeddings).        │
├─ 2. RETRIEVE ──────────────────────────────────────────────────────┤
│  Para um novo layout, recupera os k exemplos mais similares.        │
├─ 3. GENERATE ──────────────────────────────────────────────────────┤
│  LLM local (Ollama / Llama) gera um XSLT candidato (few-shot).      │
├─ 4. VALIDATE ──────────────────────────────────────────────────────┤
│  Aplica o XSLT ► compara com o XML final esperado (XSD + diff).     │
│  (XsdValidationService + AutomatedTransformationTestService)        │
├─ 5. CORRECT ───────────────────────────────────────────────────────┤
│  Realimenta os erros no prompt e repete 3-4 até convergir.          │
└────────────────────────────────────────────────────────────────────┘
```

**🇧🇷** **Por que não fine-tuning?** Você já tem validadores determinísticos (XSD, comparação com o XML final). Um loop *gerar → validar → corrigir* é mais barato, auditável e confiável que treinar um modelo, e melhora sozinho conforme a base de exemplos cresce. O **Llama via Ollama** roda no seu servidor (config `Ollama` em [`appsettings.json`](appsettings.json)), mantendo os dados on-premise.

**🇺🇸** **Why not fine-tuning?** You already have deterministic validators (XSD, comparison against the final XML). A *generate → validate → correct* loop is cheaper, auditable and more reliable than training a model, and improves on its own as the example base grows. **Llama via Ollama** runs on your server (`Ollama` config), keeping data on-premise.

> Os serviços que já materializam essa visão: `TransformationLearningService`, `ImprovedXslGeneratorService`, `ImprovedTclGeneratorService`, `RAGService`, `AutomatedTransformationTestService`, `XsdValidationService`.

### Contexto estruturado para a IA — a DSL bruta nunca chega ao Ollama / Structured context for the AI — the raw DSL never reaches Ollama

**🇧🇷** O mapeamento do Mapper Sysmiddle (`ContentValue` de cada `Rule`) usa uma DSL proprietária com prefixos (`#.` variável local, `$.` variável global, `I.` campo de origem, `T.` campo de destino, `F.` função, `N.`/`S.` menos comuns) e estruturas de controle (`if/else`, `for/foreach/while`) com sintaxe própria (ex.: `begin/end`, `=` como comparação — **não é C#/Roslyn válido**, hipótese investigada e refutada nas DLLs do Sysmiddle). Decisão estratégica do dono: reduzir a carga de interpretação da IA e viabilizar fine-tuning futuro do Ollama, evitando que o modelo precise aprender a gramática Sysmiddle inteira a cada chamada.

Por isso existe uma **Camada 0**, determinística e sem IA, que traduz `ContentValue` bruto para um JSON estruturado (`branches`/`condition`/`sources`/`target`/`functions`) **antes** de qualquer coisa chegar ao prompt:

```
ContentValue (DSL bruta) ──► DslStructuredParser (Camada 0, 100% código) ──► StructuredRule (JSON)
                                                                                    │
                                                                                    ▼
                                                        Ollama só vê o JSON — nunca a DSL bruta
```

- `DslStructuredParser` interpreta a árvore de decisão real (`if/else` aninhado, funções como `ConcatString`/`CalculateVerifierDigit`) e produz um `StructuredRule` (`SchemaVersion`, `Target`, `Branches`, `AllSources`, `AllFunctions`).
- Esse trabalho pesado vive hoje em **`ai/XslSynth.Contracts`** — um projeto novo, extraído de `ai/XslSynth.Core`, que contém só o núcleo determinístico e sem I/O externo (`DslStructuredParser`, `StructuredRuleSchema`, `FunctionCatalog`, `GuidXPathCatalog`, `RealMapperParser`). É referenciado tanto pelo lado de pesquisa (`ai/XslSynth.Core`, que mantém Ollama/RAG/XSD validator isolados) quanto pela **API em runtime**, via `Services/Transformation/MappingStructureService.cs` (registrado `Scoped` em `Program.cs`) — ou seja, uma parte real desse trabalho **já conecta ao runtime da API**, não é mais só ferramenta offline/CLI.
- **Estado atual (issues #138-#141 entregues):** a exposição via HTTP deixou de ser trabalho futuro — `POST /api/transformationexecution/execute-candidates` já retorna, por candidato, tanto `sectionMappings`/`xmlNamespaces` (rastreabilidade linha/seção, issue #138) quanto `fieldMappings` (rastreabilidade campo-a-campo, issue #141, sobre o motor de resolução estrutural da issue #140); há também um endpoint dedicado equivalente, `POST /api/transformationexecution/field-mappings`. Contrato completo, exemplos de payload e a ressalva de validação comportamental ainda pendente: [§7 "`fieldMappings` em `execute-candidates`"](#7-api--endpoints) e ["Rastreabilidade TXT↔XML por linha/seção"](#7-api--endpoints).
- **Consolidação do parser de MapperVO (issue #139):** `XslSynth.Model.MapperVo` + `RealMapperParser` (`ai/XslSynth.Contracts`) é agora o parser canônico em todo o runtime, inclusive no caminho de geração de XSL legado (`Services/XmlAnalysis/XslGeneratorService.cs`, que passou a usá-lo em vez do parser antigo). O parser antigo (`Models/Entities/MapperVo.cs`/`MapperRule.cs`/`LinkMappingItem.cs`) está marcado `[Obsolete]`, mantido apenas por rastreabilidade. Detalhe completo, inclusive a limitação conhecida de que nenhum dos dois parsers captura elementos aninhados: [`docs/architecture/inventario-parsers-mapperVo-issue-139.md`](docs/architecture/inventario-parsers-mapperVo-issue-139.md).

**🇧🇷 Visão de longo prazo (declarada, não entregue por completo):** a meta de fundo continua sendo eliminar a dependência do XML low-code Sysmiddle. O plano de mapeamento campo TXT↔XML (issues #137-141) já entregou a **extração** estruturada (interpretar `ContentValue` real e expor as coordenadas TXT↔XML via HTTP, sem a IA decidir a lógica condicional). O que segue como visão declarada, não capacidade real, é a **geração** (a IA aprender a produzir o JSON estruturado, com um transpilador determinístico convertendo de volta para `ContentValue`/XSLT) e a reversibilidade (XML SEFAZ → TXT original), que é **investigação em fase de desenho** (Fase 4, ver roadmap) — funções como dígito verificador têm perda e não são inversíveis sem heurística.

**🇺🇸** The Sysmiddle Mapper's `ContentValue` uses a proprietary DSL with prefixes (`#.` local var, `$.` global var, `I.` source field, `T.` target field, `F.` function) and control structures with their own syntax (`begin/end`, `=` as comparison — confirmed **not** valid C#/Roslyn, a hypothesis investigated and refuted against the Sysmiddle DLLs). Strategic decision: reduce the AI's interpretation burden and enable future Ollama fine-tuning by never asking the model to learn the full Sysmiddle grammar per call.

A deterministic, AI-free **Layer 0** translates raw `ContentValue` into structured JSON (`branches`/`condition`/`sources`/`target`/`functions`) before anything reaches the prompt. That logic now lives in **`ai/XslSynth.Contracts`**, a new project extracted from `ai/XslSynth.Core` containing only the deterministic, I/O-free core (`DslStructuredParser`, `StructuredRuleSchema`, `FunctionCatalog`, `GuidXPathCatalog`, `RealMapperParser`). It's referenced by both the research side (`ai/XslSynth.Core`, which keeps Ollama/RAG/XSD validation isolated) and the **API at runtime**, via `Services/Transformation/MappingStructureService.cs` (`Scoped` in `Program.cs`) — a real slice of this work now connects to the API's actual runtime, not just an offline/CLI tool. **Current state (issues #138-#141 shipped):** HTTP exposure is no longer future work — `POST /api/transformationexecution/execute-candidates` now returns, per candidate, both `sectionMappings`/`xmlNamespaces` (row/section traceability, issue #138) and `fieldMappings` (field-level traceability, issue #141, built on the structural resolution engine from issue #140); there is also an equivalent dedicated endpoint, `POST /api/transformationexecution/field-mappings`. Full contract, payload examples and the still-pending behavioral-validation caveat: see [§7](#7-api--endpoints). The long-term goal of retiring the low-code XML remains a declared vision for the *generation* side and for reversibility (XML → original TXT), which is early-stage design investigation, not a real feature, since lossy functions (check digits) aren't cleanly invertible — but the *extraction* side (structural TXT↔XML coordinates over HTTP) is now shipped.

Design completo: [`docs/architecture/design-dsl-mapper-prompt-ia-2026-08-16.md`](docs/architecture/design-dsl-mapper-prompt-ia-2026-08-16.md), [`docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md`](docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md), [`docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md`](docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md) e [`docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md`](docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md).

### Fallback automático de IA em produção / Automatic AI fallback in production

**🇧🇷** Esse loop já roda **automaticamente em produção**, não só via CLI offline: `POST /api/transformationexecution/execute-candidates` (o pathway que o front-end de fato chama) dispara a IA local (Ollama) em background quando os dois pathways síncronos (`sysmiddle`, `tcl-xsl`) não produzem **nenhum** candidato. A resposta síncrona (200) não espera o job — ela devolve um *warning* com o *ticket* do job assíncrono, consultável em `GET /api/transformationexecution/execute-candidates/{ticket}/ia-status` (mesmo endpoint/mecanismo do pathway com gabarito, particionado por usuário).

O gatilho distingue dois estados para não desperdiçar geração num problema que não é de transformação:

| Estado | Sintoma | Dispara IA? |
|--------|---------|-------------|
| **A — não encontrado/não modelado** | Não existe mapper cadastrado para o layout, ou nenhuma heurística `tcl-xsl` se aplica | **Sim** — gap real de cobertura |
| **B — encontrado, falhou por infra** | O mapper existe (`sysmiddle` reconhece o layout) mas a execução falhou por config/runner/timeout | **Não** — a transformação já existe e está correta; o problema é operacional, não de geração |

Sem `groundTruthXml` (Estado A, "gerar do zero"), o critério de convergência muda de *diff canônico == 0* para **XSD válido + validação de negócio** (mais fraco), o teto de iterações é mais conservador (`MaxIterationsFallback`, default 2, contra 3 do modo com gabarito), e um **cooldown de 4h por `LayoutGuid`** (`AiFallbackSuppressionGate`, cross-usuário) evita reprocessar o Ollama repetidamente para um layout que a IA já tentou e não resolveu sozinha. O candidato resultante vem marcado com `HasGroundTruth: false` em `AiCandidateStatus.Diagnostics` — **é sugestão para revisão humana, nunca aplicado à produção sem validação**. Design completo: [`docs/architecture/design-fallback-ia-automatico-2026-08-16.md`](docs/architecture/design-fallback-ia-automatico-2026-08-16.md).

**🇺🇸** This loop already runs **automatically in production**, not only via the offline CLI: `POST /api/transformationexecution/execute-candidates` (the pathway the front-end actually calls) dispatches the local AI (Ollama) in the background when neither synchronous pathway (`sysmiddle`, `tcl-xsl`) produces **any** candidate. The synchronous (200) response doesn't wait on the job — it returns a warning with the async job's *ticket*, pollable via `GET /api/transformationexecution/execute-candidates/{ticket}/ia-status` (same endpoint/mechanism as the ground-truth pathway, partitioned per user).

The trigger distinguishes two states so generation isn't wasted on a problem that isn't a transformation gap:

| State | Symptom | Triggers AI? |
|-------|---------|---------------|
| **A — not found / not modeled** | No mapper registered for the layout, or no `tcl-xsl` heuristic applies | **Yes** — genuine coverage gap |
| **B — found, failed due to infra** | The mapper exists (`sysmiddle` recognizes the layout) but execution failed due to config/runner/timeout | **No** — the transformation already exists and is correct; the problem is operational, not generation |

Without a `groundTruthXml` (State A, "generate from scratch"), the convergence criterion shifts from *canonical diff == 0* to **valid XSD + business validation** (weaker), the iteration cap is more conservative (`MaxIterationsFallback`, default 2, vs. 3 for the ground-truth mode), and a **4h cooldown per `LayoutGuid`** (`AiFallbackSuppressionGate`, cross-user) prevents repeatedly re-hitting Ollama for a layout the AI already tried and failed to solve on its own. The resulting candidate is marked `HasGroundTruth: false` in `AiCandidateStatus.Diagnostics` — **it is a suggestion for human review, never applied to production without validation**. Full design: [`docs/architecture/design-fallback-ia-automatico-2026-08-16.md`](docs/architecture/design-fallback-ia-automatico-2026-08-16.md).

---

## 6. Stack tecnológica / Tech stack

| Categoria | Tecnologia | Uso |
|-----------|-----------|-----|
| Runtime | **.NET 10** / ASP.NET Core Web API | `LangVersion: preview`, nullable + implicit usings |
| Cache | **Redis** (`StackExchange.Redis`) | Cache de layouts e mappers |
| Banco | **SQL Server** (`Microsoft.Data.SqlClient`) | Fonte da verdade (layouts, mappers) |
| Logging | **Serilog** + Sinks (File, Async, Elasticsearch) | Logs estruturados + correlação |
| Serialização | `System.Text.Json` + **Newtonsoft.Json** | JSON com XML preservado (`UnsafeRelaxedJsonEscaping`) |
| Docs | **Swashbuckle / Swagger** | OpenAPI em Development |
| LLM | **Ollama** (deepseek-coder/Llama), **Gemini**, **OpenAI** | Geração e aprendizado |
| Container | **Docker** (`Dockerfile`, target Linux) | Deploy |
| Crypto | **LayoutParserLib.dll** | Criptografia Sysmiddle |

---

## 7. API & Endpoints

**🇧🇷** Todos os controllers seguem a convenção `/api/[controller]`. Swagger UI disponível em Development (`/swagger`). Abaixo, os grupos por capacidade:

**🇺🇸** All controllers follow the `/api/[controller]` convention. Swagger UI is available in Development (`/swagger`). Grouped by capability:

| Grupo / Group | Controllers | O que faz / What it does |
|---------------|-------------|--------------------------|
| **Parse** | `Parse`, `Document` | Parseia documento contra layout; valida estrutura. Ex.: `POST /api/parse/upload`. |
| **Catálogo / Catalog** | `LayoutDatabase`, `MapperDatabase` | Lista/busca layouts e mappers (com cache Redis). |
| **Transformação / Transformation** | `Transformation`, `TransformationExecution`, `AutoTransformation` | Gera e executa XSLT/TCL; pipeline low-code. |
| **Análise XML / XML analysis** | `XmlAnalysis` | Analisa estrutura/tipo de documentos XML (NFe, CTe, MDFe, NFCom). |
| **IA/ML** | `Learning`, `RAG`, `DataGeneration` | Aprende padrões; RAG; gera dados sintéticos. |
| **Qualidade / Quality** | `Test`, `Testing` | Testes automatizados de transformação. |
| **Observabilidade / Observability** | `Metrics`, `Monitoring` | Métricas e healthchecks. |
| **Métricas de IA / AI metrics** | `AiMetrics` | `GET /api/ai-metrics/generations` e `GET /api/ai-metrics/summary` — expõem em JSON tipado as gerações do job `ai/XslSynth --mode=metrics-batch` (rodando via cron em produção), sem exigir parsing de log no cliente. Contrato completo em [`docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md`](docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md). |

> Detalhe completo de rotas em runtime via Swagger. / Full route detail at runtime via Swagger.

### Detecção automática de layout / Automatic layout detection

**🇧🇷** `POST /api/parse/auto` recebe `multipart/form-data` com `documentFile` e, após uma
escolha explícita, `layoutGuidOverride`. O probe consulta o catálogo interno, mantém o XML
descriptografado fora do navegador e devolve:

- `unique`: exatamente um layout passou todos os gates; `parseResult` já vem preenchido;
- `ambiguous`: dois ou mais layouts continuam compatíveis; `candidates` traz no máximo cinco e
  nenhum é aplicado sem ação do usuário;
- `not_found`: nenhum layout passou os gates; `suggestedCandidates` pode trazer aproximações não
  confirmadas, separadas dos candidatos compatíveis.

`matchScore` é um índice determinístico de equivalência de `0` a `100`, não probabilidade. O
override é normalizado e aceito somente se o GUID pertencer ao ranking da detecção atual; caso
contrário a API responde `422`. A seleção registra status, origem, GUID/rank,
`algorithmVersion`, `catalogVersion` e `correlationId`, sem conteúdo do documento. Em MQSeries, a
ordem hierárquica cadastrada no XML é evidência informativa porque grupos/filhos não representam
necessariamente a ordem física do stream; no IDoc ela permanece gate autoritativo.
Se o catálogo atingir o teto de segurança ou algum layout não puder ser avaliado, a API falha
fechado como `not_found`, anota `catalog_incomplete`/`authoritative_selection_disabled` nas
sugestões e não aceita override. A normalização do documento e a divisão por largura são
reutilizadas dentro da requisição para limitar alocações ao comparar layouts próximos.

**🇺🇸** `POST /api/parse/auto` accepts multipart `documentFile` plus an optional, explicitly
chosen `layoutGuidOverride`. It returns `unique`, `ambiguous` (up to five ranked compatible
layouts, no implicit choice), or `not_found` (optional unconfirmed suggestions). The score is a
deterministic equivalence index rather than a probability. Overrides are accepted only when they
belong to the current ranking, and the audit trail preserves correlation/algorithm/catalog
versions without logging raw document content or exposing decrypted layout XML.
An incomplete/truncated catalog disables authoritative selection and overrides, returning
`not_found` with explicit limitations. Document normalization and fixed-width splitting are reused
per request to avoid repeating large allocations for every catalog entry.

### Fases de status da transformação low-code (2026-08-27) / Low-code transformation status phases (2026-08-27)

**🇧🇷** `POST /api/parse/upload` (campo `transformationsStatus`) e `GET /api/parse/transformations/{ticket}` (campo `status`, [`LowCodeTransformationIndexEntry`](Models/Transformation/LowCodeTransformationIndex.cs)) compartilham o mesmo vocabulário de fases. Design completo: [`docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md`](docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md) §2.

| Fase | Emitida pelo back-end? | Significado |
|------|------------------------|-------------|
| `uploaded`, `layout_selected`, `parsing` | **Não** — client-side only | O ticket só existe a partir do momento em que o documento **já foi parseado** (é derivado do `RawText` pós-parse); antes disso não há entrada de índice para consultar. O front já sabe que fez upload/selecionou o layout, não precisa perguntar à API por essas fases. |
| `processing` | Sim | Transformação em andamento (alias interno `TransformingStatus`, mesmo valor de fio). |
| `completed` | Sim | Ao menos um candidato de transformação teve sucesso. |
| `failed` | Sim — **novo neste contrato** | Existem candidatos, mas **nenhum** teve sucesso — falha estrutural do conjunto. Antes disso, esse caso vinha como `completed` com todos os candidatos `success=false`, obrigando o front a varrer o array para inferir o fracasso. |
| `not_applicable` / `error` | Sim (só na resposta síncrona de `/api/parse/upload`) | `not_applicable`: pathway não elegível (sem mapper, tipo não posicional, entrada vazia). `error`: falha estrutural ao processar transformações (ex.: banco fora do ar) — não derruba o parse principal. |

**🇺🇸** Both endpoints above share the same phase vocabulary.

| Phase | Emitted by the back-end? | Meaning |
|-------|---------------------------|---------|
| `uploaded`, `layout_selected`, `parsing` | **No** — client-side only | The ticket only exists once the document has **already been parsed** (it's derived from the post-parse `RawText`); before that there's no index entry to query. The front already knows it uploaded/selected a layout — no need to ask the API for these phases. |
| `processing` | Yes | Transformation in progress (internal alias `TransformingStatus`, same wire value). |
| `completed` | Yes | At least one transformation candidate succeeded. |
| `failed` | Yes — **new in this contract** | Candidates exist, but **none** succeeded — structural failure of the set. Previously this came back as `completed` with every candidate `success=false`, forcing the front to scan the array to infer failure. |
| `not_applicable` / `error` | Yes (only in `/api/parse/upload`'s synchronous response) | `not_applicable`: pathway not eligible (no mapper, non-positional type, empty input). `error`: structural failure processing transformations (e.g. database down) — does not fail the main parse. |

### Contrato de referência: `transformationsTicket` para polling do front (Issue #99) / Reference contract: `transformationsTicket` for front-end polling (Issue #99)

**🇧🇷** Resposta formal a uma proposta cross-team do `layoutparser-portal` pedindo um endpoint novo de job assíncrono + polling para resolver a barra de progresso travando em "100%". Decisão da arquitetura ([`docs/architecture/resposta-proposta-frontend-progresso-parse-2026-08-14.md`](docs/architecture/resposta-proposta-frontend-progresso-parse-2026-08-14.md)): **nenhum endpoint novo** — o mecanismo já existente, `GET /api/parse/transformations/{ticket}`, é o contrato oficial que o front deve consumir. Nenhuma mudança de contrato foi feita para atender esta issue; esta seção só torna explícito, num único lugar, o que já era verdade no código.

- **Gargalo real:** é a **transformação** (pathway low-code/IA), não o parse do documento em si. O parse tem instrumentação de duração (`Stopwatch` em `ParseController.Upload`, log estruturado `Parse concluído em {ParseDurationMs}ms`), mas não emite ticket próprio — hoje não há evidência de que precise, porque o `transformationsTicket` já cobre a parte demorada do fluxo.
- **Como consumir:** o `POST /api/parse/upload` sempre devolve `transformationsStatus` + `transformationsTicket` (ticket só vem nulo quando o pathway não é elegível, `transformationsStatus="not_applicable"`). Quando `transformationsStatus="processing"`, faça polling em `GET /api/parse/transformations/{ticket}` até `status` virar `"completed"` ou `"failed"` — ver tabela de fases logo acima para o vocabulário completo.
- **Intervalo de polling:** não há um número de referência fixo recomendado por esta issue — falta medição de duração real em produção por tempo suficiente para propor um intervalo com dado, não achismo. Até lá, sugerimos um polling com backoff (ex.: 2s → 5s → 10s) em vez de um intervalo fixo curto.
- **SSE/WebSocket foi descartado com evidência de código:** o proxy Fastify do BFF (`layoutparser-portal/server/dist/src/app.js`) está registrado com `websocket: false` explícito — decisão deliberada do próprio front, não omissão da API.
- **Ação de custo zero recomendada ao front (fora deste repo):** trocar o rótulo da barra de progresso para algo como "Processando arquivo" com indicador indeterminado assim que o upload terminar, em vez de tentar prever 100% antes da hora.

**🇺🇸** Formal response to a cross-team proposal from `layoutparser-portal` asking for a new async job + polling endpoint to fix the progress bar getting stuck at "100%". Architecture decision: **no new endpoint** — the existing `GET /api/parse/transformations/{ticket}` mechanism is the official contract the front should consume. No contract change was made for this issue; this section just makes explicit, in one place, what was already true in the code.

- **The real bottleneck** is the **transformation** (low-code/AI pathway), not the document parse itself. Parse now has duration instrumentation (`Stopwatch` in `ParseController.Upload`, structured log `Parse concluído em {ParseDurationMs}ms`), but does not emit its own ticket — there's currently no evidence it needs one, since `transformationsTicket` already covers the slow part of the flow.
- **How to consume it:** `POST /api/parse/upload` always returns `transformationsStatus` + `transformationsTicket` (ticket is only null when the pathway isn't eligible, `transformationsStatus="not_applicable"`). When `transformationsStatus="processing"`, poll `GET /api/parse/transformations/{ticket}` until `status` becomes `"completed"` or `"failed"` — see the phase table above for the full vocabulary.
- **Polling interval:** this issue doesn't recommend a fixed number — real production duration measurement is still missing to propose an interval backed by data instead of guesswork. Until then, we suggest polling with backoff (e.g. 2s → 5s → 10s) instead of a short fixed interval.
- **SSE/WebSocket was ruled out with code evidence:** the BFF's Fastify proxy (`layoutparser-portal/server/dist/src/app.js`) is registered with an explicit `websocket: false` — a deliberate decision from the front itself, not an oversight from the API.
- **Zero-cost action recommended for the front (outside this repo):** switch the progress bar label to something like "Processing file" with an indeterminate indicator as soon as the upload finishes, instead of trying to predict 100% too early.

### Diagnóstico estruturado de `execute-candidates` (Issue layoutparser-portal#86) / Structured diagnostics for `execute-candidates`

**🇧🇷** `POST /api/transformationexecution/execute-candidates` ganhou dois campos **aditivos** na resposta (não quebram clientes existentes que ignoram campos desconhecidos): [`pathwayDiagnostics`](Models/Transformation/PathwayDiagnostic.cs) e `correlationId`. Design completo: [`docs/architecture/diagnostico-issue-86-diagnostico-estruturado-execute-candidates.md`](docs/architecture/diagnostico-issue-86-diagnostico-estruturado-execute-candidates.md).

```jsonc
{
  "success": true,
  "candidates": [],
  "recommendedCandidateId": null,
  "warnings": ["..."],
  "pathwayDiagnostics": [
    { "pathway": "sysmiddle", "status": "not_applicable", "code": "no_mapper", "message": "..." },
    { "pathway": "tcl-xsl", "status": "failed", "code": "map_not_found", "message": "..." }
  ],
  "correlationId": "..."
}
```

**Semântica principal:** `candidates: []` nunca fica sem causa quando a API sabe o motivo — cada pathway avaliado (`sysmiddle`, `tcl-xsl`, e `ai-fallback` quando o fallback automático de IA é disparado) entra em `pathwayDiagnostics` com um veredito, mesmo quando não produz candidato. `warnings` continua populado exatamente como antes, por compatibilidade — `pathwayDiagnostics` é estruturado, não substitui.

| Campo | Valores | Significado |
|-------|---------|-------------|
| `pathway` | `sysmiddle` \| `tcl-xsl` \| `ai-fallback` | Qual dos pathways gerou este diagnóstico. |
| `status` | `candidate_generated` \| `not_applicable` \| `failed` | `candidate_generated`: o pathway produziu ao menos um candidato. `not_applicable`: o pathway não é elegível para este layout/entrada (não é falha). `failed`: o pathway era elegível mas não conseguiu produzir candidato. |
| `code` | `no_mapper` \| `map_not_found` \| `xsl_not_found` \| `configuration_error` \| `runner_unavailable` \| `timeout` \| `not_applicable` \| `execution_error` | Taxonomia estável (string, não enum — permite adicionar valores sem quebrar o contrato). |
| `message` | texto livre | Mensagem legível para exibição no front. |

**Regra de sanitização:** toda `message` em `pathwayDiagnostics` passa por [`LowCodeErrorSanitizer`](Services/Transformation/LowCode/LowCodeErrorSanitizer.cs) antes de chegar ao payload HTTP — **nunca** contém caminho físico de disco nem detalhe interno cru. O detalhe completo (não sanitizado) só existe no log estruturado, correlacionável via `correlationId`.

**🇺🇸** `POST /api/transformationexecution/execute-candidates` gained two **additive** response fields (safe for existing clients that ignore unknown fields): [`pathwayDiagnostics`](Models/Transformation/PathwayDiagnostic.cs) and `correlationId`. Full design: [`docs/architecture/diagnostico-issue-86-diagnostico-estruturado-execute-candidates.md`](docs/architecture/diagnostico-issue-86-diagnostico-estruturado-execute-candidates.md).

**Core semantics:** `candidates: []` is never left without a cause when the API knows the reason — every pathway evaluated (`sysmiddle`, `tcl-xsl`, and `ai-fallback` when the automatic AI fallback fires) gets an entry in `pathwayDiagnostics` with a verdict, even when it produces no candidate. `warnings` remains populated exactly as before for backward compatibility — `pathwayDiagnostics` is structured, it doesn't replace it.

| Field | Values | Meaning |
|-------|--------|---------|
| `pathway` | `sysmiddle` \| `tcl-xsl` \| `ai-fallback` | Which pathway produced this diagnostic. |
| `status` | `candidate_generated` \| `not_applicable` \| `failed` | `candidate_generated`: the pathway produced at least one candidate. `not_applicable`: the pathway isn't eligible for this layout/input (not a failure). `failed`: the pathway was eligible but couldn't produce a candidate. |
| `code` | `no_mapper` \| `map_not_found` \| `xsl_not_found` \| `configuration_error` \| `runner_unavailable` \| `timeout` \| `not_applicable` \| `execution_error` | Stable taxonomy (string, not an exposed enum — new values can be added without breaking the contract). |
| `message` | free text | Human-readable message for front-end display. |

**Sanitization rule:** every `message` in `pathwayDiagnostics` goes through [`LowCodeErrorSanitizer`](Services/Transformation/LowCode/LowCodeErrorSanitizer.cs) before reaching the HTTP payload — it **never** contains a physical disk path or raw internal detail. The full (unsanitized) detail only exists in the structured log, correlatable via `correlationId`.

### `fieldMappings` em `execute-candidates` (Issue #141) / `fieldMappings` in `execute-candidates` (Issue #141)

> ⚠️ **Ressalva ativa — leia antes de confiar no campo / Active caveat — read before trusting this field**
>
> **🇧🇷** A validação comportamental (rodar 20 execuções reais contra o `LowCodeRunner` e comparar o `fieldMappings` resolvido com o comportamento real do runner) **não foi feita neste ambiente** — o `LowCodeRunner.exe` é um processo Windows-only (x86, interop nativo) que não roda em WSL/Linux. O que existe hoje é **só validação estrutural**, com fixtures sintéticas (20 cenários cobrindo `direct`/`transformed`/`concatenated`/`static`/N:1/1:N/repetição). O dono do projeto autorizou seguir mesmo assim. Na prática: `fieldMappings` é funcional e testado estruturalmente, **mas ainda não confirmado contra a saída real do `LowCodeRunner` em produção**. Trate `confidence: "best-effort"` com cautela reforçada — e mesmo `"authoritative"` deve ser lido como "resolução estrutural correta segundo as regras declaradas no mapper", não como "validado contra execução real", até essa validação pendente ser concluída.
>
> **🇺🇸** Behavioral validation (running 20 real executions against `LowCodeRunner` and comparing the resolved `fieldMappings` to the runner's actual behavior) **has not been done in this environment** — `LowCodeRunner.exe` is a Windows-only process (x86, native interop) that does not run on WSL/Linux. What exists today is **structural validation only**, via synthetic fixtures (20 scenarios covering `direct`/`transformed`/`concatenated`/`static`/N:1/1:N/repetition). The project owner authorized proceeding anyway. In practice: `fieldMappings` is functional and structurally tested, **but not yet confirmed against `LowCodeRunner`'s real production output**. Treat `confidence: "best-effort"` with extra caution — and even `"authoritative"` should be read as "structurally correct per the rules declared in the mapper", not "validated against real execution", until this pending validation is completed.

**🇧🇷** `POST /api/transformationexecution/execute-candidates` ganha um terceiro campo **aditivo** por candidato (issue #141, não quebra clientes existentes): [`fieldMappings`](Models/Transformation/TransformationCandidate.cs), o mapeamento **campo-a-campo** entre o layout posicional de origem (TXT/MQSeries/IDOC) e o XML de destino (hoje só NF-e — escopo do motor de resolução estrutural, issue #140). Reaproveita, sem custo adicional de I/O, o mesmo mapper decifrado e o mesmo parse posicional já usados para gerar `transformedXml` no pathway `sysmiddle`.

**Não confunda com `sectionMappings`/`segmentMappings` (issue #138, ver seção "Rastreabilidade TXT↔XML por linha/seção" abaixo):** aquele é um mapeamento em nível de **linha/seção** (qual seção do layout corresponde a qual bloco do XML), já existente antes da #141. `fieldMappings` é um nível de granularidade abaixo — **campo individual** dentro de uma linha, com coordenada estrutural precisa (posição, ocorrência, XPath). Os dois são **complementares**, não substitutos: um front pode usar `sectionMappings` para navegação em bloco e `fieldMappings` para destacar/editar um campo específico. Juntos, os dois campos desbloqueiam a PBI [layoutparser-portal#128](https://github.com/LayoutParser/layoutparser-portal/issues/128) (highlight de campo).

Exemplo completo — resolução do CNPJ do emitente:

```jsonc
// POST /api/transformationexecution/execute-candidates → response
{
  "success": true,
  "candidates": [
    {
      "candidateId": "sysmiddle-{mapperGuid}",
      "pathway": "sysmiddle",
      "transformedXml": "<nfeProc>...</nfeProc>",
      "fieldMappings": [
        {
          "mappingId": "...",
          "sources": [
            {
              "lineGuid": "{guid-da-linha-C100}",
              "lineName": "C100",
              "fieldGuid": "{guid-do-campo-CNPJ}",
              "fieldName": "CNPJ_EMITENTE",
              "lineOccurrence": 0,
              "startPosition": 12,
              "length": 14
            }
          ],
          "targets": [
            {
              "xpath": "/nfe:NFe/nfe:infNFe/nfe:emit/nfe:CNPJ",
              "nodeKind": "Text",
              "xmlOccurrence": null
            }
          ],
          "kind": "Direct",
          "confidence": "Authoritative",
          "limitations": null
        }
      ]
    }
  ],
  "warnings": [],
  "pathwayDiagnostics": [],
  "correlationId": "..."
}
```

| Campo | Semântica |
|-------|-----------|
| `fieldMappings: null` | Pathway `tcl-xsl` (decisão categórica — sem fonte estrutural equivalente hoje, mesma decisão já tomada para `sectionMappings` nesse pathway); **ou** falha isolada na composição (parse compartilhado indisponível, mapper decifrado ausente, exceção do motor) — nunca derruba o candidato, vira `warning` textual em vez de erro 500. |
| `fieldMappings: []` | Pathway `sysmiddle`, mapper existe e foi decifrado, mas o motor de composição não resolveu **nenhum** `FieldToXmlMapping` — resultado válido, não é falha. |
| `fieldMappings: [...]` | Um ou mais mapeamentos resolvidos — ver estrutura abaixo. |
| `sources[].lineOccurrence`/`startPosition`/`length` | Coordenadas do campo de origem no fragmento **físico** (`ParsedField.Occurrence`, nunca a ocorrência agregada) — nunca o valor do documento. |
| `targets[].xpath` | Convenção **sempre com prefixo de namespace** (`nfe:`), nunca XPath sem prefixo — o XML da NF-e é namespaced e um XPath sem prefixo não resolveria contra o documento real. |
| `targets[].xmlOccurrence` | `null` quando não há repetição confirmada no ancestral; inteiro quando há (ex.: N-ésimo item de uma lista repetida). |
| `kind` | `Direct` (1:1 sem DSL, veio de `LinkMappings`) \| `Transformed` (regra DSL com função não-concatenadora, condicional ou loop) \| `Concatenated` (múltiplas origens combinadas) \| `Static` (valor literal, sem origem `I.`; `sources: []` nesse caso). |
| `confidence` | `Authoritative` (as 5 condições objetivas do design foram atendidas) \| `BestEffort` (qualquer outro caso, inclusive fallback heurístico) — **ver a ressalva de validação pendente acima antes de tratar como verdade absoluta**. |
| `limitations` | Populado (nunca `null`) quando `confidence: "BestEffort"` — motivo(s) legível(is) da degradação. Inclui o caso em que a linha TXT de origem está declarada vazia ou com degradação posicional (ver §4 "Sinais aditivos de linha"). |

**🇺🇸** `POST /api/transformationexecution/execute-candidates` gains a third **additive** per-candidate field (issue #141, does not break existing clients): [`fieldMappings`](Models/Transformation/TransformationCandidate.cs), the **field-to-field** mapping between the source positional layout (TXT/MQSeries/IDOC) and the destination XML (NF-e only today — scope of the structural resolution engine, issue #140). It reuses, at no extra I/O cost, the same decrypted mapper and positional parse already used to produce `transformedXml` on the `sysmiddle` pathway.

**Do not confuse with `sectionMappings`/`segmentMappings` (issue #138, see the "Row/section TXT↔XML traceability" section below):** that one is a **line/section**-level mapping (which layout section corresponds to which XML block), predating #141. `fieldMappings` is one granularity level below — an **individual field** inside a line, with a precise structural coordinate (position, occurrence, XPath). The two are **complementary**, not substitutes: a front-end can use `sectionMappings` for block-level navigation and `fieldMappings` to highlight/edit one specific field. Together, the two fields unblock PBI [layoutparser-portal#128](https://github.com/LayoutParser/layoutparser-portal/issues/128) (field highlight).

| Field | Semantics |
|-------|-----------|
| `fieldMappings: null` | `tcl-xsl` pathway (categorical decision — no equivalent structural source today, same decision already made for `sectionMappings` on that pathway); **or** an isolated composition failure (shared parse unavailable, decrypted mapper missing, engine exception) — never fails the candidate, becomes a textual `warning` instead of a 500. |
| `fieldMappings: []` | `sysmiddle` pathway, mapper exists and was decrypted, but the composition engine resolved **no** `FieldToXmlMapping` — a valid result, not a failure. |
| `fieldMappings: [...]` | One or more resolved mappings — see structure above. |
| `sources[].lineOccurrence`/`startPosition`/`length` | Coordinates of the source field in the **physical** fragment (`ParsedField.Occurrence`, never the aggregated occurrence) — never the document's actual value. |
| `targets[].xpath` | Convention is **always namespace-prefixed** (`nfe:`), never a bare XPath — the NF-e XML is namespaced and a bare XPath would not resolve against the real document. |
| `targets[].xmlOccurrence` | `null` when no repetition is confirmed on the ancestor; an integer when there is (e.g. the Nth item of a repeated list). |
| `kind` | `Direct` (1:1, no DSL, came from `LinkMappings`) \| `Transformed` (DSL rule with a non-concatenating function, conditional, or loop) \| `Concatenated` (multiple sources combined) \| `Static` (literal value, no `I.` source; `sources: []` in this case). |
| `confidence` | `Authoritative` (all 5 objective design conditions met) \| `BestEffort` (any other case, including heuristic fallback) — **see the pending-validation caveat above before treating this as absolute truth**. |
| `limitations` | Populated (never `null`) when `confidence: "BestEffort"` — human-readable reason(s) for the degradation. Includes the case where the source TXT line is declared empty or positionally degraded (see §4 "Additive line signals"). |

Design completo / Full design: [`docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md`](docs/architecture/design-contrato-fieldmappings-execute-candidates-issue-141.md) · [`docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md`](docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md). Endpoint isolado equivalente (mesmo motor, mesmo tipo de dado, não embutido em `execute-candidates`): `POST /api/transformationexecution/field-mappings`.

### Rastreabilidade TXT↔XML por linha/seção — Fase 0 (Issue layoutparser-api#138 / layoutparser-portal#126) / Row/section TXT↔XML traceability — Phase 0

**🇧🇷** `POST /api/transformationexecution/execute-candidates` ganhou dois campos **aditivos** por candidato (não quebram clientes existentes): [`sectionMappings`](Models/Transformation/SectionMapping.cs) e `xmlNamespaces`. Eles mapeiam **de qual linha/seção do TXT** veio **qual nó do XML** gerado — granularidade de **LINHA/SEÇÃO, não de CAMPO**. Rastreabilidade campo-a-campo (`fieldMappings`) já foi entregue nas issues #140/#141 — ver seção acima. `sectionMappings` continua existindo como o nível linha/seção, complementar a `fieldMappings` (não substituído por ele): juntos, os dois campos desbloqueiam a PBI [layoutparser-portal#128](https://github.com/LayoutParser/layoutparser-portal/issues/128) (highlight de campo).

Exemplo de payload (linha `ZRSDM_NFE_400_EMIT` mapeada estruturalmente para o nó de emitente do XML):

```jsonc
{
  "candidateId": "...",
  "pathway": "sysmiddle",
  "transformedXml": "...",
  "sectionMappings": [
    {
      "source": { "lineGuid": "a1b2c3d4-...", "lineName": "ZRSDM_NFE_400_EMIT", "lineOccurrence": 1 },
      "targets": [
        { "xPath": "/nfe:NFe/nfe:infNFe/nfe:emit", "nodeKind": "element", "xmlOccurrence": 1 }
      ],
      "confidence": "authoritative"
    }
  ],
  "xmlNamespaces": { "nfe": "http://www.portalfiscal.inf.br/nfe" }
}
```

**Semântica obrigatória de `sectionMappings`:**

| Valor | Significado |
|-------|-------------|
| `null` | Este pathway ainda **não suporta** rastreabilidade. Hoje: `tcl-xsl` (retorna sempre `null`; `xmlNamespaces` também `null`). |
| `[]` (lista vazia) | O pathway suporta, mas **não encontrou** mapeamentos estruturais resolvíveis para este candidato específico. |
| lista preenchida | Mapeamentos disponíveis, cada um com XPath absoluto (`targets[].xPath`, com prefixo de namespace resolvido via `xmlNamespaces`) e nível de confiança (`confidence`). |

- **Resolução sempre ESTRUTURAL**, nunca por comparação de valor textual do documento: hoje só o pathway `sysmiddle` resolve, e só emite `confidence: "authoritative"` (100% via estrutura declarada no mapper — atribuição `T.<path>` da DSL Sysmiddle) — **nunca inventa `best-effort`** por aproximação.
- `xmlNamespaces` é reportado **uma vez por candidato** (não repetido por mapping) e é `null` sempre que `sectionMappings` também é `null`/vazio.
- `source.lineOccurrence` distingue ocorrências quando a mesma linha alimenta múltiplos destinos estruturalmente distintos dentro do mesmo mapper — não é a ocorrência física real dentro do TXT recebido nesta chamada (fora do escopo da Fase 0).

**🇺🇸** `POST /api/transformationexecution/execute-candidates` gained two **additive** per-candidate fields (safe for existing clients): [`sectionMappings`](Models/Transformation/SectionMapping.cs) and `xmlNamespaces`. They map **which TXT row/section** produced **which XML node** — **row/section granularity, not field-level**. Field-level traceability (`fieldMappings`) has already shipped as issues #140/#141 — see the section above. `sectionMappings` remains the row/section level, complementary to (not replaced by) `fieldMappings`: together, the two fields unblock PBI [layoutparser-portal#128](https://github.com/LayoutParser/layoutparser-portal/issues/128) (field highlight).

**Mandatory semantics of `sectionMappings`:**

| Value | Meaning |
|-------|---------|
| `null` | This pathway does **not support** traceability yet. Today: `tcl-xsl` (always returns `null`; `xmlNamespaces` is also `null`). |
| `[]` (empty list) | The pathway supports it, but **found no** resolvable structural mappings for this specific candidate. |
| populated list | Mappings available, each with an absolute XPath (`targets[].xPath`, namespace-prefixed via `xmlNamespaces`) and a confidence level (`confidence`). |

- Resolution is always **STRUCTURAL**, never by comparing the document's textual value: today only the `sysmiddle` pathway resolves, and only ever emits `confidence: "authoritative"` (100% via structure declared in the mapper — the Sysmiddle DSL's `T.<path>` assignment) — it **never fabricates `best-effort`** by approximation.
- `xmlNamespaces` is reported **once per candidate** (not repeated per mapping) and is `null` whenever `sectionMappings` is also `null`/empty.
- `source.lineOccurrence` distinguishes occurrences when the same row feeds multiple structurally distinct destinations within the same mapper — it is not the actual physical occurrence within the TXT received on this call (out of scope for Phase 0).

---

## 8. Fundação da plataforma fiscal / Fiscal platform foundation

**🇧🇷** Em 2026-08-31/09-01, sete *slices* incrementais foram entregues estabelecendo a **fundação
de uma plataforma fiscal multi-tenant** dentro desta API — o objetivo declarado é permitir, no
futuro, **migrar mapeamentos hoje presos ao low-code Sysmiddle para TCL/XSLT geridos pela própria
API**, com **humano no loop** em cada etapa sensível (revisão de sugestões de IA, aprovação e
publicação de releases) e com uma regra transversal inegociável: **nenhuma dessas capacidades
autoriza escrita/mutação no Sysmiddle** — o Sysmiddle permanece somente leitura/execução em
qualquer rota, presente ou futura.

**🇺🇸** Between 2026-08-31 and 2026-09-01, seven incremental *slices* shipped, laying the
**foundation of a multi-tenant fiscal platform** inside this API — the declared goal is to
eventually **migrate mappings currently locked into the Sysmiddle low-code tool to TCL/XSLT
managed by the API itself**, with a **human in the loop** at every sensitive step (reviewing AI
suggestions, approving and publishing releases), and one non-negotiable cross-cutting rule:
**none of this capability authorizes writing/mutating Sysmiddle** — Sysmiddle remains read-only
execution across every route, present or future.

> ⚠️ **Estado / Status:** capacidade nova, ainda **não documentada em `sectionMappings`/`fieldMappings`
> nem consumida pelo front-end** ([layoutparser-portal](#2-ecossistema-de-projetos--project-ecosystem)).
> Validação de contrato cross-repo (`@lp-contract-qa`) para os slices já mesclados **ainda não
> ocorreu** — não tratar como "pronto para consumo" até essa validação existir. Detalhe completo,
> decisões e histórico de auditoria: [`docs/architecture/resumo-sessao-2026-08-31.md`](docs/architecture/resumo-sessao-2026-08-31.md).

| Slice | Capacidade / Capability | Resumo / Summary |
|-------|--------------------------|-------------------|
| **1 — Identidade & Workspace** | Multi-tenant | **🇧🇷** `ExternalIdentity`, `FiscalUser`, `FiscalWorkspace`, `WorkspaceMembership` — isola dados por workspace, com identidade confiável vinda do BFF ([§11.1](#111-identidade-e-autenticação-bff--api)). **🇺🇸** Multi-tenant identity/workspace model isolating data per workspace, trusting BFF-forwarded identity. Design: [`docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md`](docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md). |
| **2 — `FiscalMappingPackage`** | Upload versionado | **🇧🇷** Upload versionado dos insumos de mapeamento (layout, planilha, XSD) com hardening de segurança (validação de conteúdo/MIME, revisão imutável por hash/autor/instante). **🇺🇸** Versioned upload of mapping inputs (layout, spreadsheet, XSD) with security hardening (content/MIME validation, immutable hash/author/timestamp revisions). Design: [`docs/architecture/design-slice2-fiscalmappingpackage-2026-08-31.md`](docs/architecture/design-slice2-fiscalmappingpackage-2026-08-31.md). |
| **3 — `MappingDraft`** | Human-in-the-loop | **🇧🇷** Sugestões de IA para regras de mapeamento, sempre revisadas por humano (aceitar/editar/rejeitar) antes de virarem geração real; concorrência otimista via ETag/`If-Match`. **🇺🇸** AI-suggested mapping rules, always human-reviewed (accept/edit/reject) before feeding real generation; optimistic concurrency via ETag/`If-Match`. Design: [`docs/architecture/design-slice3-mappingdraft-2026-08-31.md`](docs/architecture/design-slice3-mappingdraft-2026-08-31.md). |
| **4 — `MappingExplanation`** | Explicabilidade | **🇧🇷** Contrato canônico de explicabilidade (regras, origens/destinos, condições, nível de suporte, limitações), com adapters específicos para Sysmiddle, TCL e XSLT. **🇺🇸** Canonical explainability contract (rules, sources/targets, conditions, support level, limitations), with dedicated Sysmiddle/TCL/XSLT adapters. Design: [`docs/architecture/design-slice4-mappingexplanation-2026-08-31.md`](docs/architecture/design-slice4-mappingexplanation-2026-08-31.md). |
| **5 — Compilação determinística + Fiscal Test Lab** | Compilação/Teste | **🇧🇷** `MappingDraftRuleTranspiler` compila regras revisadas em TCL/XSL/XSLT de forma determinística; laboratório de testes fiscais roda fixtures, valida XSD e faz diff canônico contra o XML esperado. **🇺🇸** `MappingDraftRuleTranspiler` deterministically compiles reviewed rules into TCL/XSL/XSLT; a fiscal test lab runs fixtures, validates XSD, and does canonical diffing against expected XML. Design: [`docs/architecture/design-slice5-compilacao-test-lab-2026-08-31.md`](docs/architecture/design-slice5-compilacao-test-lab-2026-08-31.md). |
| **6 — Gate transversal Sysmiddle** | Segurança/Governança | **🇧🇷** Garantia técnica, aplicada em toda rota (presente e futura), de que **nenhuma mutação no Sysmiddle é permitida** — Sysmiddle é somente leitura/execução. **🇺🇸** Technical guarantee, enforced across every route (present and future), that **no Sysmiddle mutation is ever permitted** — Sysmiddle is read/execute-only. Design: [`docs/architecture/design-slice6-gate-sysmiddle-2026-09-01.md`](docs/architecture/design-slice6-gate-sysmiddle-2026-09-01.md). |
| **7 — Governança & publicação** | RBAC/Release | **🇧🇷** RBAC mínimo (papéis com permissão de aprovar/publicar/reverter), `MappingRelease` com fluxo approve/publish/rollback, e `MappingTransition` como trilha de auditoria de cada mudança de estado. **🇺🇸** Minimal RBAC (roles authorized to approve/publish/rollback), `MappingRelease` with an approve/publish/rollback flow, and `MappingTransition` as the audit trail for every state change. Design: [`docs/architecture/design-slice7-governanca-piloto-fiat-2026-09-01.md`](docs/architecture/design-slice7-governanca-piloto-fiat-2026-09-01.md). |

**🇧🇷** Contexto adicional e a auditoria honesta de status real (o que foi mesclado vs. o que ainda
é backlog no momento em que cada slice foi escrito) está em
[`docs/architecture/resumo-sessao-2026-08-31.md`](docs/architecture/resumo-sessao-2026-08-31.md).
O texto original do prompt que originou os 7 slices está preservado em
[`docs/architecture/spec-plataforma-fiscal-prompt-original-2026-08-31.md`](docs/architecture/spec-plataforma-fiscal-prompt-original-2026-08-31.md).

**🇺🇸** Additional context and an honest status audit (what was merged vs. what was still backlog
at the time each slice doc was written) lives in the two docs linked above.

### 8.1 Contrato para o front: RBAC, erro otimista e diff estruturado / Front-end contract

**🇧🇷** Referência do que a API **já expõe hoje** para o [layoutparser-portal](#2-ecossistema-de-projetos--project-ecosystem)
consumir sem esperar backend novo (issue #376, derivada do cross-check #226/#198):

- **Matriz de RBAC** dos endpoints de mapping (`approve` = `reviewer`/`fiscal_admin`;
  `publish`/`rollback` = `fiscal_admin`/`owner`; `List` = todos os membros; draft/compile/edição
  de regra = só membership). **Não existe `401`** — "não autenticado" responde **404** fail-closed.
- **Vocabulário de erro otimista** do `PATCH .../mapping-drafts/{id}/rules/{ruleId}`
  (200 + `eTag` no corpo, 412 com `{ current }`, 428 sem `If-Match`, 400 malformado, 422 semântico).
- **Diff estruturado** já existente (`NodeDiff` → `MappingTestRunDivergence` com `Kind`/`XPath`/
  `Expected`/`Actual` + provenance `RuleId`/`SourceRefs`/`Evidence`), exposto em
  `testRunSummary.divergences` no `GET .../releases/{releaseId}`.

**🇺🇸** Reference for what the API **already exposes today** for the front-end to consume without
waiting on new backend work (issue #376). Full detail, JSON examples and file/line pointers:
[`docs/architecture/contrato-rbac-erro-diff-mapping-fiscal-2026-09-10.md`](docs/architecture/contrato-rbac-erro-diff-mapping-fiscal-2026-09-10.md).

### 8.2 Gate React#200 / epic #368 — 6 itens já fecham só documentando / Gate React#200 / epic #368 — 6 items close by documenting only

**🇧🇷** A tabela original do gate #200 (aberta em agosto contra a epic #368) listava 8 itens.
Um cross-check de 2026-09-15 (issue #413) confirmou que **6 já estão implementados**:
`GET /api/workspaces/me`, pacote fiscal versionado, `MappingDraft` (com `FiscalProfile`/editor
manual/filtros/arquivamento), contrato de saída TCL/XSL/XSLT (hash, status, imutabilidade
pós-`publish`), `MappingExplanation` (era "não existe" em agosto, hoje completo) e o Test Lab
(diff por regra, diff release×release, cobertura de obrigatórios). Só **1 gap pequeno** ficou
confirmado: capability Sysmiddle read-only tem enforcement (`MappingEngineGuardFilter`, issue
#232) mas **não** tem payload consultável tipo `GET .../capabilities` — rastreado na issue #415,
não tratar como resolvido.

**🇺🇸** The original gate #200 table listed 8 items. A 2026-09-15 cross-check confirmed **6 are
already implemented** — see detail below. Only **1 small gap** remains: Sysmiddle read-only
capability has server-side enforcement but no queryable capability payload (tracked as issue
#415).

Detalhe completo, contratos JSON e ponteiros de arquivo/linha:
[`docs/architecture/contratos-gate-200-368-2026-09-15.md`](docs/architecture/contratos-gate-200-368-2026-09-15.md).
Análise de delta que originou este fechamento:
[`docs/architecture/cross-check-gate-200-368-2026-09-15.md`](docs/architecture/cross-check-gate-200-368-2026-09-15.md).

### 8.3 `GET .../mappings/{mappingId}/layout-tree` — árvore dupla origem/destino / dual source/target layout tree

**🇧🇷** Issue #425 (endpoint) + #430 (correção de identidade de nó). Devolve, para um `mappingId`
Sysmiddle já conhecido, a árvore completa dos layouts de **origem** e **destino** (hierarquia,
atributos, cardinalidade) mais os vínculos diretos campo→campo entre as duas árvores — pensado
para o [layoutparser-portal](#2-ecossistema-de-projetos--project-ecosystem) replicar a UI de
dupla-árvore do Connect Us. Rota de leitura: `[ApiController]`
[`Controllers/LayoutTreeController.cs`](Controllers/LayoutTreeController.cs), DTOs em
[`Models/Dtos/Fiscal/LayoutTree.cs`](Models/Dtos/Fiscal/LayoutTree.cs).

```
GET /api/workspaces/{workspaceId}/mappings/{mappingId}/layout-tree
```

- **RBAC:** qualquer papel de membro do workspace (`Owner`, `FiscalAdmin`, `Mapper`, `Reviewer`,
  `Operator`, `Viewer`). Sem identidade ou não-membro → `404` (fail-closed, mesmo padrão do
  restante da API — **não existe `401`** aqui). `mappingId` não resolvido no catálogo `tbMapper`
  → `404` também, indistinguível do caso de RBAC.
- **Não** passa por `MappingEngineGuardFilter` (Slice 6) — ler/explicar a árvore de um mapper
  Sysmiddle é sempre permitido, só escrita é bloqueada.

**Resposta (resumida):**

```json
{
  "mapperGuid": "GRT_0001",
  "source": {
    "layoutGuid": "LAY_ORIGEM",
    "kind": "text",
    "roots": [
      { "elementGuid": "GRT_0010", "name": "Cabecalho", "kind": "group",
        "cardinality": { "min": 1, "max": 1 },
        "children": [
          { "elementGuid": "FLD_0011", "name": "cnpjEmitente", "kind": "element",
            "cardinality": null, "children": [] }
        ] }
    ]
  },
  "target": { "layoutGuid": "LAY_DESTINO", "kind": "xml", "roots": [ "..." ] },
  "rules": [
    { "ruleId": "RULE_0001", "sourceElementGuid": "FLD_0011", "targetElementGuid": "ATT_0022" }
  ],
  "limitations": [
    "Este mapper tem regras condicionais (DSL) que não aparecem em rules[] — consulte GET .../explanation."
  ]
}
```

- `source`/`target`: `kind` é `"text"` ou `"xml"`, cada nó de `roots[]`/`children[]` tem
  `kind` `"group"` (tem filhos), `"element"` (folha) ou `"attribute"`. `elementGuid` é o GUID
  estável do catálogo (`TAG_`/`GRT_`/`ATT_`/`FLD_`/`LIN_…`), `null` quando o nó não tem GUID no
  XML de layout.
- `rules[]`: cobre **só vínculo direto campo→campo** (`LinkMappingItemVO` real do Sysmiddle),
  casando `sourceElementGuid`/`targetElementGuid` com os `elementGuid` das árvores acima.
- **`limitations[]` (novo na issue #430):** ⚠️ **divergência de cobertura importante para o
  consumidor.** `rules[]` aqui **não inclui** regras condicionais/DSL (`MapperRule`/branches) —
  essas só aparecem em `GET .../mappings/{mappingId}/explanation` (Slice 4,
  [§8](#8-fundação-da-plataforma-fiscal--fiscal-platform-foundation)), com `sourceRefs`/`targetRefs`
  prefixados `I.`/`T.`. A origem dessas regras é texto da DSL (ex. `I.xMun`), não um GUID de nó do
  catálogo — o catálogo GUID→XPath (`GuidXPathCatalog`) resolve por GUID, não por nome de campo,
  então reconstruir esse vínculo aqui exigiria "inventar" um GUID sem garantia de unicidade
  (decisão deliberada de escopo, não pendência). Quando o mapper tem regras DSL, `limitations[]`
  sinaliza isso em texto explicativo; o front **deve tratar essas regras como "sem linha desenhável
  no layout-tree, só lista"** e completá-las consultando `explanation.rules` separadamente — não
  assumir que `layout-tree.rules[]` é a lista completa de vínculos do mapper.

**🇺🇸** Issue #425 (endpoint) + #430 (node identity fix). Returns the full **source** and
**destination** layout trees (hierarchy, attributes, cardinality) plus direct field-to-field links
between them, for a known `mappingId` — built for the React front-end's dual-tree UI (Connect Us
parity). Same RBAC as above (any member role, fail-closed `404`, no `401`).

⚠️ **Important consumer note on `limitations[]`:** `rules[]` in this endpoint covers **only**
direct field-to-field links (`LinkMappingItemVO`) — conditional/DSL rules (`MapperRule`/branches)
never appear here, only in `GET .../explanation` (`sourceRefs`/`targetRefs` prefixed `I.`/`T.`).
That's a deliberate scope decision (the DSL rule origin is text, not a catalog GUID — synthesizing
one would risk fabricating a non-unique identifier), not a bug to be fixed later. When present,
`limitations[]` names this gap explicitly; the front-end should treat those DSL rules as
"listable but not drawable" on the layout-tree and merge them in from `explanation.rules`
separately — `layout-tree.rules[]` alone is not the complete link list for a mapper.

### 8.4 Estado de produção vs `develop` / Production vs `develop` status

**🇧🇷** Os contratos das seções 8.5 a 8.10 abaixo estão **mesclados em `develop`** (referência:
commit `5b0f103`). O deploy de produção é disparado por push em `master`
([`.github/workflows/deploy.yml`](.github/workflows/deploy.yml)), portanto o que está em `develop`
**não implica** que esteja rodando no servidor: confira o último deploy antes de assumir que um
endpoint existe lá. Este README não registra datas de deploy.

**🇺🇸** The contracts in 8.5 to 8.10 are **merged into `develop`** (reference: commit `5b0f103`).
Production deploys are triggered by a push to `master`, so being in `develop` **does not imply** the
endpoint is live on the server: check the latest deploy first. This README records no deploy dates.

### 8.5 `GET .../mappings/{mapperGuid}/generated-transformation` — candidato TCL/XSLT gerado automaticamente / auto-generated TCL/XSLT candidate

**🇧🇷** Issue #438. Decisão de arquitetura: [`docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md`](docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md) (geração automática e gabarito = o DSL declarado do Sysmiddle; a seção "Estado da implementação" do ADR lista os desvios da implementação).
Devolve o candidato TCL/XSL/XSLT sintetizado a partir do mapeador Sysmiddle, para que operações vejam o
gerado sem rodar o CLI `ai/XslSynth` à mão. Rota de **leitura**: [`Controllers/GeneratedMapperArtifactController.cs`](Controllers/GeneratedMapperArtifactController.cs)
(o segmento de rota se chama `{mappingId}` no código; é o GUID do mapeador em `tbMapper`).

```
GET /api/workspaces/{workspaceId}/mappings/{mapperGuid}/generated-transformation
```

- **RBAC:** qualquer papel de membro. Sem identidade, não-membro ou mapeador inexistente no catálogo → `404` (fail-closed). Catálogo indisponível → `503`.
- **Geração lazy, sem bloqueio:** se o candidato não existe ou está defasado, **este próprio GET** dispara a geração em background e responde `status: "generating"`; o front deve repetir a consulta até `ready`.
- **`status`** devolvido: `generating` ou `ready`. `stale` (existia um candidato `ready`, mas o hash do DSL do mapeador ou a `generatorVersion` mudou) é calculado só na leitura e **nunca volta como valor final**: vira regeneração e o GET responde `generating`. `none` só aparece no caso residual em que o MapeadorVO do catálogo não é XML bem-formado.
- **`validationBasis` é sempre `declared_dsl`:** a cobertura é medida contra a regra **declarada** no mapeador, **não** contra execução real do Sysmiddle (o runner está bloqueado por licença do host FiatMQ). Não apresente `linkPct`/`rulePct` como "validado contra o motor real".

```json
{
  "mapperGuid": "GRT_0001",
  "status": "ready",
  "content": "<xsl:stylesheet ...>",
  "coverageJson": "{\"generatorVersion\":\"2\",\"shell\":{...},\"limitations\":[...],\"linkPct\":...}",
  "validationBasis": "declared_dsl",
  "generatedAt": "2026-09-21T12:00:00Z",
  "correlationId": "..."
}
```

`coverageJson` é uma **string** JSON (não objeto) com campos aditivos desta rodada:

| Campo | Significado |
|-------|-------------|
| `generatorVersion` | Versão do **gerador** (hoje `"2"`). Entra no hash usado para `stale`: artefatos gerados pela versão anterior são regenerados sob demanda ao abrir o detalhe. |
| `shell` | Casca do documento: `{ rootElement, namespace, attributes }`. |
| `limitations[]` | Limites conhecidos deste gerador, em texto (raiz não determinada, namespace não constante, destinos `ATT_` sem elemento-pai resolvido etc.). Exibir ao usuário. |
| `compiles`, `compileError`, `linksCovered/linksTotal/linkPct`, `rulesCovered/rulesTotal/rulePct`, `provenanceEntries`, `linkMappingsSemFolha` | Cobertura e proveniência (já existentes). |

Como a casca é gerada: atributos do elemento raiz (`versao`, `Id`…) saem como `xsl:attribute`; o namespace
(`xmlns`) **só** é declarado quando é constante no próprio mapeador (nunca é inventado; se o layout de
destino tem `xmlns` e nenhuma regra constante o define, entra uma linha em `limitations[]`). Na tradução
da DSL, `Concat`, `Substring` (base 0 → `substring` base 1) e `GetLength` viram XPath; **`Trim`, `Replace` e
demais funções não são traduzidas** (a regra fica de fora, sem aproximação).

**🇺🇸** Issue #438. Architecture decision: [`docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md`](docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md)
(automatic generation; the reference oracle is Sysmiddle's *declared* DSL; the ADR's "Estado da implementação" section lists the implementation deviations).
Returns the TCL/XSL/XSLT candidate synthesized from a Sysmiddle mapper. Read-only
route, any member role, fail-closed `404`. The **GET itself triggers lazy background generation** when
the candidate is missing or stale and answers `generating`; poll until `ready`. `stale` is computed at
read time only (mapper DSL hash or `generatorVersion` changed) and is never returned as a final value.
`validationBasis` is always `declared_dsl`: coverage is measured against the rule **declared** in the
mapper, **not** against real Sysmiddle execution (runner blocked by licensing). `coverageJson` is a JSON
**string** with additive fields `generatorVersion` (currently `"2"`, part of the `stale` hash, so
artifacts from the previous version regenerate on demand), `shell { rootElement, namespace, attributes }`
and `limitations[]`. Root attributes are emitted as `xsl:attribute`; the namespace is emitted only when
it is constant in the mapper itself; the DSL's `Concat`/`Substring`/`GetLength` are translated,
`Trim`/`Replace` are not.

### 8.6 `GET .../mapping-releases` — listagem unificada / unified listing

**🇧🇷** Issue #438 (ADR [`adr-unificacao-generated-artifact-mapping-release.md`](docs/architecture/adr-unificacao-generated-artifact-mapping-release.md),
opção b: nada migra de tabela). A lista de releases do workspace passa a incluir também os candidatos
auto-gerados de 8.5. Código: `MappingGovernanceController.List` ([`Controllers/MappingGovernanceController.cs`](Controllers/MappingGovernanceController.cs)).

```
GET /api/workspaces/{workspaceId}/mapping-releases?page=1&pageSize=20&status=&draftId=&environment=&origin=
```

Resposta: `{ items, page, pageSize, totalCount, autoGeneratedUnavailable }`. `pageSize` vai de 1 a 100
(fora disso `400`). Qualquer papel de membro; não-membro → `404`.

| Aspecto | Comportamento |
|---------|---------------|
| `origin` (por item) | `draft_compile` (release real) ou `auto_generated`. Filtro `?origin=` aceita os mesmos dois valores; ausente = ambos. |
| Item `auto_generated` | Campos: `origin`, `mapperGuid`, `mapperName`, `status`, `validationBasis`, `coverage`, `generatedAt`, `correlationId`, `detailUrl`. **Sem** `releaseId`, `draftId`, `testRunSummary`, `approvedByUserId`, `environment`, `engine`, `content` (campos **omitidos**, não `null`). `coverage` aqui é objeto JSON (no detalhe 8.5 é a string `coverageJson`). O conteúdo TCL/XSLT vem só pelo `detailUrl`. |
| `status` | Aceita o ciclo de vida da release (`draft_compiled`, `test_passed`, `test_failed`, `in_review`, `approved`, `published`, `deprecated`, `archived`) **ou** `ready`/`generating`/`stale`. `stale` **não é persistido**: a lista não devolve nada para ele (só o detalhe o calcula). Valor fora do vocabulário → `400`. |
| `draftId` / `environment` | Existem só em release real: quando informados, os auto-gerados são **excluídos**. |
| Ordem e paginação | Sequência única: releases reais (mais recentes primeiro) e depois os auto-gerados; `totalCount` soma as duas fontes. |
| Degradação | Se o store de auto-gerados falha, a lista devolve só as releases e `autoGeneratedUnavailable: true`. |
| Ações | Auto-gerado **não** é elegível a approve/publish/rollback (essas rotas exigem `releaseId`). |

⚠️ **Vínculo mapper→workspace é PROVISÓRIO (issue #417):** todo workspace enxerga **todos** os
artefatos gerados, porque o catálogo Sysmiddle é org-wide. Não há isolamento por workspace nos itens
`auto_generated`.

**🇺🇸** Issue #438. The workspace release list now also includes the auto-generated candidates from 8.5,
with a per-item `origin` (`draft_compile` | `auto_generated`) and an `origin` filter. Auto-generated items
omit (not `null`) `releaseId`/`draftId`/`testRunSummary`/`approvedByUserId`/`environment`/`engine`/`content`
and carry `detailUrl`. `draftId`/`environment` filters exclude them; `status` accepts the release
lifecycle or `ready`/`generating`/`stale` (`stale` is never persisted, so the list returns nothing for
it). Order: real releases first, then generated ones. ⚠️ The mapper-to-workspace link is **provisional**
(issue #417): every workspace sees every generated artifact.

### 8.7 `GET /api/reference-examples` — catálogo de exemplos reais da Neogrid / Neogrid real-example catalog

**🇧🇷** Corpus de referência (pares TCL/XSL reais da Neogrid) servido em rota **própria e global**
([`Controllers/ReferenceExamplesController.cs`](Controllers/ReferenceExamplesController.cs)), deliberadamente
**separada** de `mapping-releases`: é material de apoio/oráculo, não release compilada pelo pipeline nem
dado de cliente. Sem `workspaceId` na rota e sem `RequireWorkspaceRole` neste controller.

- `GET /api/reference-examples?docType=NFe` → lista de metadados (`id`, `docType`, `version`, `scenario`, `direction`, `tclFileName`, `xslFileName`), sem conteúdo. `docType` é opcional (sem diferenciar maiúsculas).
- `GET /api/reference-examples/{id}` → `{ id, tclContent?, xslContent? }`. `404` se o `id` não existe (também se a leitura do arquivo falhar).
- **Configuração:** `ReferenceExamples:BasePath` (vazio por padrão em [`appsettings.json`](appsettings.json)). Estrutura esperada: `{BasePath}/tcl/{DocType}/{Versao}/{Arquivo}.tcl` e `{BasePath}/xsl/{DocType}/{Versao}/{Arquivo}.xsl`. Sem `BasePath`, pasta inexistente ou erro de leitura, a **lista vem vazia** (degrada, nunca erro). O catálogo é reconstruído do disco a cada chamada.
- `scenario`/`direction` são extraídos do nome do arquivo em melhor esforço (sufixo `_XxxToYyy`).

**🇺🇸** Reference corpus (real Neogrid TCL/XSL pairs) on its own global route, intentionally separate from
`mapping-releases`. Configure `ReferenceExamples:BasePath` (empty by default); when unset/missing, the
list is empty rather than an error.

### 8.8 Suíte de teste versionada e runner TCL do Fiscal Test Lab / Versioned test suite and TCL runner

**🇧🇷** Issue #423 (suíte) e #421 (runner TCL). Código: [`Controllers/TestSuiteController.cs`](Controllers/TestSuiteController.cs).
Agrupa fixtures (pares XML de entrada/gabarito) sob um `MappingDraft` e roda todas contra uma release, com
histórico persistido para comparar regressão entre versões. Base:
`api/workspaces/{ws}/mapping-drafts/{draftId}/test-suites`.

| Método e rota (relativa à base) | Papel | Resultado |
|---------------------------------|-------|-----------|
| `POST` (base) `{ name, description? }` | `mapper`/`fiscal_admin`/`owner` | `201` suíte; `422` sem `name`. |
| `GET` (base) | membro | Lista as suítes do draft. |
| `GET {suiteId}` | membro | Uma suíte. |
| `POST {suiteId}/fixtures` `{ name, inputXml, expectedXml, xsdVersion? }` | `mapper`/`fiscal_admin`/`owner` | `201` fixture; `422` se faltar campo. Só **cria e lista**: não há edição/remoção. |
| `GET {suiteId}/fixtures` | membro | Fixtures na ordem de execução (`sortOrder`). |
| `POST {suiteId}/run` `{ releaseId }` | `mapper`/`fiscal_admin`/`owner` | `200` com a execução completa; `422` (sem `releaseId`, release/suíte fora do workspace/draft, ou suíte sem fixtures); `503` falha inesperada. |
| `GET {suiteId}/runs?page=&pageSize=` | membro | `{ items, totalCount }`, mais recente primeiro (defaults: página 1, 20 itens). |

- **`run` é SÍNCRONO:** o `200` já traz o resultado (`totalFixtures`, `passed`, `failed`, `requiredGatesPassed`, `durationMs`, `fixtureResults[]`); não há job para consultar. Uma fixture com falha interna vira "falhou" e as demais continuam. `requiredGatesPassed` é verdadeiro só se **todas** passam.
- Não-membro/sem identidade → `404` (não existe `401`). As rotas de escrita passam pelo `MappingEngineGuardFilter` (Sysmiddle nunca é mutado).
- **Fora do escopo:** endpoint de comparação entre duas execuções (o front monta a comparação a partir do histórico).
- **Runner TCL (#421):** `engine=tcl` agora **executa de verdade** no Test Lab, com o mesmo contrato `MappingTestRunSummary` do XSLT (XSD, divergências com proveniência). Implementação: `TclRuleApplier`, que **interpreta as `MappingDraftRule` aceitas/editadas** contra o XML de entrada. Não há motor Tcl externo (o "TCL" aqui é o dialeto declarativo `<MAP><LINE><FIELD>` interno, não a linguagem Tcl), e o runner Sysmiddle segue fora de alcance.

**🇺🇸** Issues #423 (suite) and #421 (TCL runner). Fixtures (input/expected XML pairs) grouped under a
`MappingDraft`, run against a release with persisted history. `POST {suiteId}/run` (`{ releaseId }`) is
**synchronous** and returns the full result; `GET {suiteId}/runs` is the paginated history. Create/list
only for suites and fixtures (no edit/delete). `engine=tcl` now really executes in the Test Lab through
`TclRuleApplier`, which interprets the accepted/edited `MappingDraftRule`s (no external Tcl engine), with
the same `MappingTestRunSummary` contract as XSLT.

### 8.9 Resposta livre do revisor às perguntas abertas da IA / Reviewer free-text answers

**🇧🇷** Issue #422. Código: [`Controllers/MappingRuleAnswersController.cs`](Controllers/MappingRuleAnswersController.cs).
O revisor responde às perguntas em aberto (`OpenQuestions`) de uma regra. **Só persistência:** a resposta
não alimenta nenhum dataset de treino (se um dia virar exemplo, passa pela curadoria da issue #346).
Base: `api/workspaces/{ws}/mapping-drafts/{draftId}`.

| Método e rota (relativa à base) | Papel | Resultado |
|---------------------------------|-------|-----------|
| `PUT rules/{ruleId}/questions/{questionIndex}/answer` `{ answer }` | `owner`/`fiscal_admin`/`mapper`/`reviewer` | `200` resposta vigente; `400` texto vazio ou > 4000 caracteres; `404` regra/pergunta inexistente; `503` falha ao gravar. |
| `GET question-answers?includeHistory=` | qualquer membro | Respostas do draft, todas as regras. |
| `GET rules/{ruleId}/question-answers?includeHistory=` | qualquer membro | Respostas de uma regra. |

- **Chave e limitação:** a pergunta não tem id estável (é uma `string` numa lista), então a chave é
  `(draft, regra, questionIndex)`, com `questionIndex` **0-based** em `OpenQuestions`. Isso só é seguro
  porque a lista é **imutável depois que a regra é criada** (o PATCH não a altera; uma regra nova
  substitui a antiga via `superseded`). Cada resposta guarda um **snapshot do texto da pergunta**
  (`question`), então um índice defasado seria detectável na leitura.
- **Versionamento (append-only):** reenviar o **mesmo** texto (após `Trim`) não cria versão nova (`200`, mesma `version`); texto **diferente** cria a versão N+1. Nada é sobrescrito.
- **Leitura:** por padrão só a versão mais recente de cada pergunta; `includeHistory=true` traz todas. Resposta: `{ draftId, ruleId, includeHistory, items[] }`, com cada item `{ answerId, draftId, ruleId, questionIndex, question, answer, answeredByUserId, answeredByName, answeredAt, version }`.

**🇺🇸** Issue #422. Reviewer answers to a rule's `OpenQuestions`; persistence only, no training dataset.
Key is `(draft, rule, questionIndex)` with a **0-based** index into `OpenQuestions`, safe only because that
list is immutable after the rule is created; each answer stores a snapshot of the question text. Re-sending
identical text creates no version; different text creates version N+1 (append-only). Reads return the latest
version per question unless `includeHistory=true`.

### 8.10 Histórico de análises fiscais / Fiscal analysis history

**🇧🇷** Issue #366 (ADR [`adr-historico-analises-fiscais-366.md`](docs/architecture/adr-historico-analises-fiscais-366.md)).
Códigos: [`Controllers/FiscalAnalysesController.cs`](Controllers/FiscalAnalysesController.cs) e `ParseController`.

**Registro (opt-in, dentro do parse):**

- `POST /api/parse/upload` e `POST /api/parse/auto` aceitam o campo de formulário **opcional** `workspaceId`. Só registra se o GUID for válido, houver usuário identificado e ele for **membro** do workspace; arquivo acima do limite de artefato (50 MB) também não é registrado.
- Campos aditivos na resposta: `analysisId` (**omitido** quando não registrado) e `historyRegistered` (bool). Em `/auto` ficam dentro de `parseResult` (`parseResult.analysisId`).
- **Nunca derruba o parse:** o registro é aguardado com **timeout de 5 s**; falha ou estouro apenas resultam em `historyRegistered: false`. Em `/auto` o layout do catálogo é guardado só como GUID (o XML descriptografado não vai para o histórico).

**Consulta** (`api/workspaces/{ws}/analyses`, qualquer papel de membro):

| Rota (relativa a `.../analyses`) | Resultado |
|----------------------------------|-----------|
| `GET ?page=&pageSize=` | `{ page, pageSize, total, items[] }`, mais recente primeiro (`pageSize` máx. 100; inválido → `400`). |
| `GET {analysisId}` | Detalhe: layout usado e `files[]` (`fileId`, `role`, `fileName`, `sizeBytes`, `sha256`, `downloadUrl`), sem caminho de disco. |
| `GET {analysisId}/files/{fileId}` | Download (`application/octet-stream`, `attachment`, `nosniff`), com **sha256 conferido** na leitura; hash divergente → `500` sem servir o conteúdo. |
| `DELETE {analysisId}` | `204`; apaga a linha SQL e os arquivos em disco. |

- **Isolamento por DONO:** só quem criou a análise a vê, baixa ou apaga. Outro membro do workspace recebe `404` (indistinguível de inexistente).
- **Retenção:** TTL de **90 dias** com purga automática em background. Configuração: `FiscalAnalysisHistory:RetentionDays` (default 90; valor <= 0 volta ao default) e `FiscalAnalysisHistory:CleanupIntervalMinutes` (default 360).
- **Armazenamento:** metadados no banco de identidade (`IdentityDatabase:*`), arquivos em disco sob `ML:FiscalAnalysesPath` (raiz própria, separada da pasta de exemplos de aprendizado: histórico do usuário **não** alimenta dataset de IA). A chave não está no `appsettings.json`; sem ela o default é `{pasta da aplicação}/MLData/FiscalAnalyses`.

> ⚠️ **PENDENTE DE PRODUÇÃO:** definir **caminho, permissões e tamanho da pasta** de `ML:FiscalAnalysesPath`.
> A pasta guarda **documento fiscal de cliente** (LGPD): precisa de ACL restrita à conta do serviço e de
> capacidade dimensionada para 90 dias de retenção. Ainda não há decisão registrada sobre esses três itens.

**🇺🇸** Issue #366. `POST /api/parse/upload` and `/auto` accept an optional `workspaceId` form field; on
success the response gains `analysisId` (omitted when not registered; in `/auto` under
`parseResult.analysisId`) and `historyRegistered`. Registration never breaks the parse (5 s timeout; failure
just yields `historyRegistered: false`). `GET/DELETE api/workspaces/{ws}/analyses[/{analysisId}]` and
`GET .../files/{fileId}` are **owner-only** (other members get `404`); downloads verify the stored sha256.
90-day TTL with automatic purge (`FiscalAnalysisHistory:RetentionDays`, `CleanupIntervalMinutes`); files
live under `ML:FiscalAnalysesPath`. ⚠️ **Production pending:** decide path, permissions and size of that
folder (it stores customer fiscal documents).

---

## 9. Configuração / Configuration

**🇧🇷** Configuração em [`appsettings.json`](appsettings.json). Chaves principais:

| Seção | Descrição |
|-------|-----------|
| `Redis:ConnectionString` | Endpoint do Redis (default `localhost:6379`). |
| `Database` | SQL Server (`Server`, `Database`, `UserId`, `Password`). **Use secrets!** |
| `Ollama:Url` / `Ollama:Model` | LLM local (`http://localhost:11434`, `deepseek-coder:6.7b`). |
| `Gemini` / `OpenAI` | Provedores de LLM em nuvem. **Use secrets!** |
| `LowCode` | Runner Sysmiddle (`RunnerPath`, `SysmiddleDir`, `AllowedPackageGuids`). |
| `LayoutParserDecrypt:BaseUrl` | URL base do serviço de descriptografia. |
| `TransformationPipeline` | Caminhos de TCL/XSL/exemplos/modelos aprendidos. |
| `XsdValidation` | XSDs por tipo de documento fiscal (NFe, CTe, NFCom, MDFe). |
| `ReferenceExamples:BasePath` | Corpus de exemplos reais TCL/XSL da Neogrid (vazio por padrão; lista vazia se ausente). Ver §8.7. |
| `ML:FiscalAnalysesPath` | Pasta dos arquivos do histórico de análises fiscais (**pendente de definição em produção**). Ver §8.10. |
| `FiscalAnalysisHistory` | `RetentionDays` (default 90) e `CleanupIntervalMinutes` (default 360) do histórico de análises. Ver §8.10. |
| `Kestrel:Endpoints:Http:Url` | Porta de escuta (default `http://0.0.0.0:5000`). |

> ⚠️ **Nunca** comite credenciais. Ver [§11 Segurança](#11-segurança--security-).

---

## 10. Como rodar / Getting started

### Pré-requisitos / Prerequisites

- **.NET 10 SDK**
- **Redis** (opcional — a API sobe sem ele, sem cache)
- **SQL Server** acessível (string em `Database`)
- **Ollama** rodando (opcional, para features de IA local)
- **layoutparser-lib** buildada (a API referencia `..\layoutparser-lib\bin\Debug\LayoutParserLib.dll`)

### Local

```bash
# 1. Restaurar e buildar a lib referenciada primeiro
dotnet build ../layoutparser-lib/LayoutParserLib.sln

# 2. Configurar segredos (OBRIGATÓRIO — o appsettings.json tem placeholders vazios, ver §11)
#    O UserSecretsId já está no .csproj; basta setar os valores:
dotnet user-secrets set "Database:Password" "<senha-do-sql>"
dotnet user-secrets set "Gemini:ApiKey" "<key-do-gemini>"
dotnet user-secrets list                                               # conferir

# 3. Restaurar, buildar e rodar a API
dotnet restore
dotnet build
dotnet run                       # http://0.0.0.0:5000  (Swagger em /swagger)
```

> **🔑 Como os segredos são lidos / How secrets are resolved.** A API usa `IConfiguration`,
> então qualquer chave do `appsettings.json` pode ser sobrescrita (precedência crescente):
> **`appsettings.json` → `dotnet user-secrets` (Development) → variáveis de ambiente → args**.
> Em ambiente/produção, use **variáveis de ambiente** no formato `Section__Key` (duplo underscore):
>
> ```bash
> export Database__Password="<senha-do-sql>"
> export Gemini__ApiKey="<key-do-gemini>"
> ```
>
> Os valores secretos foram **removidos do código e do `appsettings.json`** (placeholders vazios);
> se nenhum segredo for fornecido, o recurso correspondente apenas degrada (ex.: Gemini fica inativo).

### Docker

```bash
docker build -t layoutparser-api .
docker run -p 5000:5000 \
  -e Redis__ConnectionString=host.docker.internal:6379 \
  layoutparser-api
```

> Em ambiente, o CORS já libera as origens do front (`localhost:81`, `172.25.32.42:*` etc.) — ver `Program.cs:149`.

### CI: check de contrato OpenAPI (issue #414) / OpenAPI contract check

**🇧🇷** O workflow [`.github/workflows/pr-validate.yml`](.github/workflows/pr-validate.yml) (PRs contra
`develop`) compara o OpenAPI gerado pela PR com o da base (`develop`) usando o
[`oasdiff`](https://github.com/oasdiff/oasdiff) e relata *breaking changes* (endpoint removido, campo
obrigatório novo em request, tipo alterado, campo de resposta removido) no resumo do job. Mudança **aditiva**
(endpoint/campo opcional novo) aparece só como informativa.

- **Hoje está em modo advisory:** `OPENAPI_CONTRACT_BLOCKING: 'false'` (bloco `env` no topo do workflow). O step reporta, mas **nunca reprova a PR**, nem por falha de infraestrutura (download, build da base, extração).
- **Como funciona:** o OpenAPI é extraído com `swagger tofile` (Swashbuckle CLI) a partir do assembly compilado, sem subir o serviço, com `ASPNETCORE_ENVIRONMENT=Testing`. O `oasdiff` é baixado uma vez, com versão e SHA256 fixados, e cacheado no `RUNNER_TOOL_CACHE`.
- **Como promover para bloqueante:** trocar `OPENAPI_CONTRACT_BLOCKING` para `'true'`. Nada mais muda: o step passa a sair com código 1 quando houver breaking change de nível erro. Antes de promover, observar algumas PRs em advisory para medir falso positivo.
- ⚠️ **Limite:** sem branch protection nativa (repositório privado no plano free), um check vermelho **não impede o merge** por si só. Para o gate valer de fato é preciso exigi-lo como *required status check* (exige GitHub Pro/Team) ou manter a convenção de não mesclar PR vermelha. Mudança breaking **intencional** passa a exigir aprovação explícita do dono.

**🇺🇸** `pr-validate.yml` diffs the PR's OpenAPI against `develop` with `oasdiff` and reports breaking changes
in the job summary; additive changes are informational only. It is **advisory today**
(`OPENAPI_CONTRACT_BLOCKING: 'false'`): it never fails the PR. To make it blocking, set that variable to
`'true'` (after watching a few PRs for false positives). Without native branch protection (private repo on
the free plan) a red check does not block a merge by itself: require it as a status check (GitHub Pro/Team)
or keep the convention of not merging red PRs.

---

## 11. Segurança / Security ⚠️

**🇧🇷 Remediação no código — FEITO ✅.** Os segredos foram **removidos** do [`appsettings.json`](appsettings.json) (placeholders vazios) **e dos fallbacks hardcoded no código** (`GeminiAIService`, `LayoutDatabaseService`, `ElasticSearchLogger`). O `.gitignore` ignora `appsettings.*.local.json`. Os segredos agora vêm de `dotnet user-secrets` (dev) / variáveis de ambiente `Section__Key` (produção) — ver [§10](#10-como-rodar--getting-started).

**🇺🇸 Code-side remediation — DONE ✅.** Secrets were **removed** from [`appsettings.json`](appsettings.json) (empty placeholders) **and from the hardcoded code fallbacks**. Secrets now come from `dotnet user-secrets` (dev) / `Section__Key` environment variables (prod) — see [§10](#10-como-rodar--getting-started).

**🔴 Ainda pendente (ação do operador / @lp-devops):**

1. **ROTACIONAR** as chaves expostas — a **key do Gemini** e a **senha do SQL** devem ser tratadas como **comprometidas** (estiveram em texto plano no repo e persistem no histórico). Gere novas no provedor/banco.
2. **Limpar o histórico do git** (BFG / `git filter-repo`), pois os segredos antigos continuam em commits passados mesmo após este commit. Rewrite de história exige force-push e coordenação com clones/forks — ver plano em [`.claude/rules/security.md`](.claude/rules/security.md).

> **⚠️ Rotacionar é obrigatório mesmo após limpar a história:** qualquer clone feito antes da limpeza ainda contém os segredos. A limpeza reduz exposição futura; só a rotação invalida o que vazou.

### 11.0 Hook de pre-commit anti-segredo

Para evitar reincidência (a senha do SQL já vazou uma vez para o `appsettings.json`
comitado — ver [`.claude/rules/security.md`](.claude/rules/security.md)), o repo tem
um hook de pre-commit versionado em [`.githooks/`](.githooks/) que roda o
[gitleaks](https://github.com/gitleaks/gitleaks) contra os arquivos staged e bloqueia
o commit se achar padrão de segredo. **Configure uma vez por clone:**

```bash
git config core.hooksPath .githooks
```

Instruções de instalação do binário `gitleaks` e detalhes do hook:
[`.githooks/README.md`](.githooks/README.md).

### 11.1 Identidade e autenticação (BFF → API)

**🇧🇷** A API **não autentica ninguém diretamente** — ela **confia** na identidade que chega de um
**BFF Fastify** (repo `layoutparser-portal/server/`), que faz login via **Microsoft Entra ID (OIDC)**
e faz proxy de `/api` para esta API. Arquitetura em 3 camadas:

```
Browser  ──(Entra OIDC, sessão cifrada)──►  BFF Fastify  ──(proxy /api + headers de identidade)──►  API .NET
```

- O BFF remove quaisquer headers de identidade que vierem do próprio browser (anti-spoofing na
  camada dele) e injeta `x-iis-user` / `x-iis-roles` confiáveis a partir da sessão Entra.
- Na API, [`Services/Security/TrustedIdentityMiddleware.cs`](Services/Security/TrustedIdentityMiddleware.cs)
  lê esses headers (nomes configuráveis via `Security:TrustedUserHeader` / `Security:TrustedRolesHeader`)
  e popula `ICurrentUser` / `HttpContext.User`.
- **Guarda de loopback:** a API só confia nesses headers se a requisição vier de `127.0.0.1`
  (`TrustIdentityFromLoopbackOnly`, default `true`, deliberadamente **fora** do `appsettings.json`).
  Isso fecha a forja de identidade mesmo com a API respondendo em todas as interfaces.
- **Auditoria** (`AuditActionFilter`) já grava o usuário real (ou `anon`), não mais um IP genérico.
- O antigo mecanismo de **chave compartilhada** (`ApiKeyGateFilter` / `ApiKeyGatePolicy`,
  configuração `Security:ApiKey` / `Security:AnonymousPaths`) foi **removido** — não é mais o
  mecanismo de defesa da fronteira BFF↔API.

**🇺🇸** The API does **not** authenticate anyone directly — it **trusts** the identity forwarded by
a **Fastify BFF** (`layoutparser-portal/server/`), which handles login via **Microsoft Entra ID
(OIDC)** and proxies `/api` to this API. See the PT-BR diagram above for the 3-layer flow. The old
shared-API-key mechanism (`ApiKeyGateFilter`/`Security:ApiKey`) has been **removed**.

**🔴 Pendências conhecidas (não documentar como prontas):**

1. **Trava de rede (`127.0.0.1`)** — a API ainda pode estar escutando `0.0.0.0`; o binding em
   loopback (2ª camada de defesa) está sendo aplicado por `@lp-devops`, condicionado a confirmar
   que o painel de produção passa pelo BFF (e não mais direto na porta da API).
2. **Sem `[Authorize]` em nenhum endpoint** — todos os endpoints continuam acessíveis sem checagem
   de papel; qual endpoint vira privilegiado é decisão de produto ainda em aberto.

Detalhe completo (decisão, sequência, evidência de teste): [`docs/architecture/rollout-p2-autenticacao.md`](docs/architecture/rollout-p2-autenticacao.md).

---

## 12. Observabilidade / Observability

- **Serilog** escreve para console + arquivo (`Logging:File:Directory`) com *rolling* por tamanho, e opcionalmente para **Elasticsearch**.
- Todo log carrega **`CorrelationId`** (`X-Correlation-ID`), permitindo rastrear um arquivo do upload ao parse.
- **Auditoria** via `AuditActionFilter` + `AuditLogger` em endpoints sensíveis (`[ServiceFilter(typeof(AuditActionFilter))]`).
- Controllers `Metrics` e `Monitoring` expõem métricas e estado.

---

## 13. Estrutura de pastas / Project structure

```
layoutparser-api/
├── Controllers/            # Endpoints HTTP (Parse, Transformation, Learning, RAG, ...)
├── Services/
│   ├── Parsing/            # Detecção, split, normalização, validação de layout
│   ├── Cache/              # LayoutCacheService, MapperCacheService (Redis)
│   ├── Database/           # SQL Server + DecryptionService + Cached*
│   ├── Learning/           # Aprendizado de padrões a partir dos arquivos
│   ├── Generation/         # IA (Gemini/Ollama), RAG, geração de dados sintéticos
│   ├── Transformation/     # XSLT/TCL, pipeline low-code, validação
│   ├── XmlAnalysis/        # Análise de estrutura XML + XSD
│   ├── Testing/            # Testes automatizados de transformação
│   └── Logging/            # Serilog, Elastic, correlação, auditoria
├── Models/                 # Entities, DTOs, ML, RAG, Validation, ...
├── Enum/ · Scripts/ · Properties/
├── Program.cs              # Bootstrap + DI + pipeline + cache warmup
├── appsettings.json        # Configuração (⚠️ ver §11)
├── Dockerfile
├── .claude/                # Harness Claude Code (agents, rules, commands) — ver §14
└── README.md               # este arquivo
```

---

## 14. Harness Claude Code & MCP

**🇧🇷** Este projeto vem equipado com um **harness de IA** (pasta [`.claude/`](.claude)) para potencializar o desenvolvimento assistido por LLM, e um **MCP Server em C#** que expõe as operações da API como *tools* para agentes.

**🇺🇸** This project ships with an **AI harness** ([`.claude/`](.claude)) to boost LLM-assisted development, plus a **C# MCP Server** that exposes the API operations as agent *tools*.

| Componente | Local | Função |
|------------|-------|--------|
| **Agents** | `.claude/agents/` | Personas enxutas focadas em .NET (arquiteto, dev, parser/LLM, QA, devops, doc). |
| **Rules** | `.claude/rules/` | Handoff, autoridade, padrões .NET, segurança, MCP. |
| **Commands** | `.claude/commands/` | Slash commands (`/security-scan`, `/new-endpoint`, `/learn-xslt`...). |
| **Hooks** | `.claude/hooks/` | Autoridade de `git push`, varredura de segredos. |
| **MCP Server** | `mcp/LayoutParserMcp/` | Servidor MCP (C#) — *tools* de parse, catálogo e transformação. |

> Setup e detalhes em [`.claude/README.md`](.claude/README.md) e [`mcp/LayoutParserMcp/README.md`](mcp/LayoutParserMcp/README.md).

---

## 15. Roadmap

- [ ] **Segurança:** remover segredos do `appsettings.json`, rotacionar chaves, migrar para secrets/env.
- [ ] **RAG vetorial:** indexar pares (layout → XSLT) num vector store (Redis Stack / RediSearch).
- [x] **Loop de auto-correção XSLT:** fechado em produção — com gabarito (Issue #40, `sysmiddle` bem-sucedido) e sem gabarito (fallback automático Estado A, [§5](#5-a-visão-de-ia--the-ai-vision)). Falta ampliar a base de exemplos/RAG acima.
- [ ] **Eliminar o XML low-code:** validar a geração autônoma de XSLT contra os XMLs finais esperados — hoje a IA só entra quando o low-code falha (fallback), ainda não substitui o pathway sysmiddle bem-sucedido.
- [x] **`XslSynth.Contracts` extraído:** núcleo determinístico (parser DSL→JSON, catálogo de funções) isolado de `ai/XslSynth.Core` e referenciado pela API em runtime via `MappingStructureService` — ver [§5](#5-a-visão-de-ia--the-ai-vision). **Já com consumidor HTTP real** (item abaixo).
- [x] **Mapeamento campo TXT↔XML (issues #137-141) — entregue:** parser MapperVO canônico (#139, PR #201) → `sectionMappings`/`xmlNamespaces` linha/seção (#138, PR #203) → motor de resolução estrutural TXT↔XML via XSD NF-e (#140, PR #205) → `fieldMappings` campo-a-campo em `execute-candidates` + endpoint dedicado `POST /api/transformationexecution/field-mappings` (#141, PR #207). Contrato completo: [§7](#7-api--endpoints). **Ressalva:** validação comportamental contra o `LowCodeRunner` real ainda pendente (WSL/Linux não roda o runner Windows-only) — hoje só há validação estrutural sintética (20 fixtures), ver ressalva ativa em §7.
- [ ] **Fase 4 — reconstrução reversa best-effort (XML→TXT):** investigação de desenho apenas, escopo ainda não confirmado com o dono; funções com perda (dígito verificador) não são inversíveis sem heurística — não prometer "reversão garantida".
- [ ] **Testes automatizados:** ampliar cobertura de `Services/Testing`.
- [ ] **MCP Server:** expandir o conjunto de *tools* e publicar o registro em `.mcp.json`.
- [x] **Fundação da plataforma fiscal (Slices 1-7):** identidade/workspace, `FiscalMappingPackage`, `MappingDraft` human-in-the-loop, `MappingExplanation`/explicabilidade Sysmiddle, compilação determinística + Fiscal Test Lab, gate transversal Sysmiddle e governança/publicação (RBAC, `MappingRelease`, `MappingTransition`) mesclados — ver [§8](#8-fundação-da-plataforma-fiscal--fiscal-platform-foundation).
- [ ] **Validação `@lp-contract-qa`:** nenhum slice da plataforma fiscal ainda teve validação de contrato cross-repo para consumo pelo `layoutparser-portal`; falta também rodar o runbook manual com dado real do Slice 7 (gate FIAT síntetico já PASS).

---

<p align="center"><sub>LayoutParser API · .NET 10 · Documentação bilíngue mantida para fins acadêmicos e operacionais.</sub></p>
