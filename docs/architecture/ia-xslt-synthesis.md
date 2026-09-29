# Arquitetura — Síntese de XSLT assistida por IA

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

> **PT-BR** · Como o LayoutParser aprende a **reproduzir a transformação low-code do Sysmiddle como um XSLT gerado**, a partir do trio TXT/input → XML final (NF-e), usando **síntese guiada por verificador** com um LLM local (Llama/Qwen) — eliminando a dependência do runtime low-code.
>
> **EN** · How LayoutParser learns to **reproduce the Sysmiddle low-code transformation as a generated XSLT**, from the input→final-NF-e-XML pairs, using **verifier-guided synthesis** with a local LLM — removing the dependency on the low-code runtime.

> Documento de arquitetura (autoria: `@lp-architect`). Implementação: `@lp-parser-llm`. Material de TCC.

---

## 1. Objetivo

Substituir o mapeamento **low-code do Sysmiddle (Connect US)** por um **XSLT gerado automaticamente**, que produza **o mesmo XML final de NF-e** que o Sysmiddle produz hoje. Escopo inicial: os **~5 mapeadores que têm layout de _input_** (`Mapper.InputLayoutGuid`).

Ganhos: elimina o runtime proprietário Windows-only (`LayoutParserLowCodeRunner.exe`), torna a transformação **auditável** (XSLT é texto legível), **versionável** e **portável para Linux**.

---

## 2. O insight central: **síntese guiada por verificador, não "ML pesada"**

O que torna este problema **fácil** (e barato) é que temos duas coisas raras:

1. **Oráculo determinístico** — o Sysmiddle produz o XML de NF-e **correto**. Gabarito grátis, sem rotulagem.
2. **Verificador determinístico** — dá para **aplicar** um XSLT candidato e **comparar node-a-node** com o XML do Sysmiddle, e **validar contra o XSD** da NF-e (já existente no projeto).

Com oráculo + verificador, **não se treina um modelo** (precisaria de milhares de exemplos, não é auditável, e "95% certo" gera XSLT quebrado). Faz-se **síntese guiada por verificador**: o LLM **propõe**, o verificador **reprova com o diff exato**, o LLM **corrige**. Converge porque o erro é concreto.

E há um facilitador decisivo: **o mapeamento Sysmiddle já É a transformação**, só que noutra representação. Logo, isto é majoritariamente **transpilação verificada** — não aprendizado do zero.

> **Decisão arquitetural NÃO-NEGOCIÁVEL:** o verificador determinístico é o coração. Nenhuma saída do LLM é aceita sem `diff == 0` contra o gabarito e validação XSD.

---

## 3. Anatomia da transformação Sysmiddle (o que precisamos transpilar)

A transformação vive em `MapperVo` (extraída do XML descriptografado do `Mapper`). Ela tem **três naturezas**, com dificuldades bem diferentes:

| Fonte | Natureza | Dificuldade → XSLT | Estratégia |
|-------|----------|--------------------|------------|
| **`LinkMappingItem`** | Mapeamento **declarativo** campo→campo (`InputLayoutGuid`→`TargetLayoutGuid`), com `IsToTruncateValue`, `RemoveWhiteSpaceType`, `DefaultValue`, `AllowEmpty` | **Baixa** | **Transpilador determinístico** (código, sem IA) |
| **`MapperRule.ContentValue`** | **DSL do Sysmiddle** por regra (NÃO é C#, apesar do comentário no código). Sintaxe própria: `#.tmp = I.LINHA000/Campo` (path de input), `$.build` (variável de saída), `GetLength()`, `if(...) begin ... end`. Lógica: condicionais, cálculo, formatação | **Alta** | **LLM** traduz **DSL→XSLT/XPath**, validado pelo loop |
| **`Mapper.XslContent`** | XSL **já existente** em alguns mapeadores | — | **Semente few-shot / referência** para o LLM |

> **Números reais** (mapeador de referência `MAP_MQSERIES_SEND_ENV_TXT_XML_NFE`, NF-e envio 4.00): **237 LinkMappings + 98 Rules DSL**. A maior parte (os 237) é resolvida pelo transpilador determinístico; o LLM foca nas 98 regras DSL.

> **Descriptografia (Windows-only):** o `Mapper.ValueContent`/`DecryptedContent` vem **cifrado** do SQL. A descriptografia é feita pelo `LayoutParserDecrypt.exe` (.NET Framework, **Windows**) — ver `DecryptionService`. No dev, aponte `LayoutParserDecrypt:Path` para o build local (`..\LayoutParserDecrypt\bin\Release\LayoutParserDecrypt.exe`) **ou** rode o exe direto no ciphertext exportado (`decrypt in <cipher> <out>`). O WSL/Linux **não** descriptografa — por isso o export do MapperVo descriptografado é feito no host Windows e só o resultado (XML limpo) vai pro loop.

**Implicação de design:** a maior parte dos campos (`LinkMappings`) é resolvida **por código**, barato e 100% confiável. O LLM concentra-se só nas **regras C#** e nos **diffs residuais**. Isso reduz drasticamente a superfície onde o LLM pode errar.

---

## 4. A arquitetura — o loop fechado

```
 Para cada mapeador com input (dos ~5):
 ┌────────────────────────────────────────────────────────────────────────┐
 │ 0. CORPUS      Pares (input → XML final Sysmiddle) = GABARITO + teste.  │
 │                (dados exportados; NÃO exige rodar o runtime Sysmiddle)  │
 ├────────────────────────────────────────────────────────────────────────┤
 │ 1. EXTRAIR     MapperVo → LinkMappings (diretos) + Rules (C#) + XSD.    │
 │   (código)     Briefing estruturado.                                    │
 ├────────────────────────────────────────────────────────────────────────┤
 │ 2. TRANSPILAR  LinkMappings → XSLT por CÓDIGO (baseline determinístico).│
 │   (código)                                                              │
 ├────────────────────────────────────────────────────────────────────────┤
 │ 3. GERAR       LLM traduz as Rules C# → templates XSLT e completa os    │
 │   (LLM)        buracos, semeado por XslContent existente + exemplos.    │
 ├────────────────────────────────────────────────────────────────────────┤
 │ 4. APLICAR     Roda o XSLT no input (Saxon). Determinístico.           │
 │   (código)                                                              │
 ├────────────────────────────────────────────────────────────────────────┤
 │ 5. VERIFICAR   Diff canônico vs XML Sysmiddle + validação XSD.         │
 │   (código)     Lista os XPaths divergentes.                            │
 ├────────────────────────────────────────────────────────────────────────┤
 │ 6. CORRIGIR    Realimenta os diffs → LLM conserta só o que falhou.     │
 │   (LLM)        Volta ao 4.                                              │
 └────────────────────────────────────────────────────────────────────────┘
        Repete até diff==0 e XSD válido → XSLT aprovado, versionado e testado.
```

Passos **1, 2, 4, 5 são código determinístico** (barato/confiável). O LLM só entra em **3 e 6**, guiado por erro concreto.

---

## 5. Componentes

| Componente | Papel | Reusa (existente) |
|------------|-------|-------------------|
| **CorpusBuilder** | Coleta/organiza pares input→XML-final por mapeador | `TransformationPipeline:ExamplesPath`, `ExpectedOutputsPath` |
| **MapperExtractor** | `MapperVo.FromXml` → LinkMappings/Rules/XSLcontent | `MapperVo`, `MapperRule`, `LinkMappingItem` |
| **DeterministicXslTranspiler** | LinkMappings → XSLT | `XslGeneratorService`, `ImprovedXslGeneratorService` |
| **LlmXslSynthesizer** | Traduz Rules C#→XSLT e completa (Ollama) | `RAGService`, config `Ollama` |
| **XsltApplier** | Aplica XSLT (Saxon) | *(novo — ver §8)* |
| **CanonicalDiffer** | Diff node-a-node por XPath | `PatternComparisonService`, `ComparisonResult` |
| **XsdValidator** | Valida NF-e/CT-e/… | `XsdValidationService` + XSDs em config |
| **RepairOrchestrator** | Loop gerar→aplicar→diff→corrigir | `TransformationLearningService`, `AutomatedTransformationTestService` |
| **ExampleStore (RAG)** | Indexa (features do layout → XSLT aprovado) | `RAGService`, `FileStorageService` |

O **esqueleto já existe** — o trabalho é fechar o loop, não recomeçar.

---

## 6. Modelo LLM (recomendação)

- **Local, no WSL-Ubuntu via Ollama.** Dado fiscal é sensível (regra de segurança do projeto → preferir local; nuvem só com dado anonimizado/sintético e autorização).
- Use modelo **code-tuned**, dimensionado à VRAM:

| Modelo | Tamanhos | Nota |
|--------|----------|------|
| **Qwen3-Coder / Qwen2.5-Coder** | 7B / 14B / 32B | 1ª escolha (melhor família aberta de código). Substitui o `deepseek-coder:6.7b` atual. |
| **DeepSeek-Coder-V2** | 16B | Alternativa sólida. |
| **Codestral / Devstral** | ~22-24B | Boas em código; conferir licença comercial. |

**VRAM (q4):** 7B ≈ 6-8 GB · 14B ≈ 10-12 GB · 32B ≈ 20-24 GB. A síntese é **offline/batch** → tolera CPU (lento, ok). Config via seção `Ollama`.

---

## 7. Fases de construção

- **Fase 0 — Corpus:** organizar pares input→XML-final dos ~5 mapeadores (dados exportados).
- **Fase 1 — Transpilador determinístico:** LinkMappings → XSLT por código. Medir cobertura só com isto (esperado: maioria dos campos).
- **Fase 2 — Loop de reparo com LLM:** traduzir Rules C# e fechar os diffs. **Provar ponta-a-ponta em UM mapeador.**
- **Fase 3 — Generalizar (ML leve = embeddings/RAG):** indexar XSLTs aprovados; semear o LLM para layouts novos.
- **Fase 4 — Rede de segurança:** testes-ouro de regressão (todo XSLT aprovado mantém `diff==0`).

### 7.1 Exportação de dados (Fase 0 na prática)

O loop (no WSL/Linux) precisa, **por mapeador**: (a) o **MapperVo descriptografado** (LinkMappings + Rules + XslContent) e (b) N pares **(input → XML final NF-e = gabarito)**.

**Restrição-chave:** o `Mapper.ValueContent` no SQL é **criptografado** (cripto Sysmiddle via `LayoutParserLib`, .NET Framework, **Windows-only**). O **WSL/Linux não descriptografa.** Logo, exporte o conteúdo **já descriptografado** (a API descriptografa no host Windows e guarda no cache/Redis).

| Caminho | Como | Quando |
|---------|------|--------|
| **Via API (preferido)** | Endpoint tipo `GET /api/MapperDatabase/{guid}/export` → devolve MapperVo descriptografado + pares de exemplo (reusa `CachedMapperService`/`MapperDatabaseService` + `DecryptionService`, que rodam no Windows) | Fonte da verdade, formato pronto pro loop |
| **Direto do Redis** | Dump da chave `mapper:{guid}` (já descriptografada no cache) | *One-off* rápido; é cache, não fonte da verdade |
| **SQL via VPN** | Enumerar quais mapeadores têm `InputLayoutGuid` (os ~5) | Descobrir o alvo; conteúdo descriptografado vem da API/Redis |

**Pares gabarito (input→XML final):** vêm de execuções passadas do Sysmiddle — em `TransformationPipeline:ExamplesPath`/`ExpectedOutputsPath`, ou gerados rodando o pipeline atual no host Windows para alguns inputs. O loop consome esses arquivos direto.

> **Sobre o cache (Redis vs alternativas):** para o **catálogo** (layouts/mappers) — poucos, pequenos, raramente alterados, read-heavy — um **cache em memória na API** (`IMemoryCache`) já basta e é mais simples/rápido (sem hop de rede, uma dependência a menos), com o "atualizar layouts" chamando um *reload*. **Redis se justifica** se houver múltiplas instâncias da API **ou** — o motivo forte — se o **Redis Stack (RediSearch)** virar o **vector store do RAG** (Fase 3): aí ele serve cache **e** embeddings, valendo a pena manter.

---

## 8. O verificador (invista aqui)

- **Engine XSLT:** o `XslCompiledTransform` do .NET é **XSLT 1.0**. NF-e costuma exigir 2.0/3.0 → adotar **Saxon** (Saxon-HE, grátis; `SaxonCS`/`Saxon-HE` para .NET). **Decisão de arquitetura.**
- **Diff canônico:** normalizar namespaces, ordem de atributos e espaços; comparar por XPath e **reportar quais nós divergem** — é o que faz o loop convergir.
- **XSD:** `XsdValidationService` + XSDs de NF-e/CT-e/NFCom/MDFe já configurados.

---

## 9. Execução em Linux/WSL e a visão "tudo em Linux"

**Restrição arquitetural importante** (levantada na análise): o app atual tem **partes Windows-only** que **não rodam em Linux**:

| Componente | Runtime | Linux? |
|------------|---------|--------|
| **LayoutParserApi** | .NET 10 | ✅ nativo |
| **Redis / SQL Server** | — | ✅ (container/Linux) |
| **LayoutParserLib** (cripto Sysmiddle) | **.NET Framework 4.8.1** | ❌ Windows |
| **LayoutParserDecrypt.exe** | .NET FW / exe | ❌ Windows |
| **LayoutParserLowCodeRunner.exe** (Sysmiddle) | Windows | ❌ Windows |

**Consequência:** portar o runtime **atual** inteiro para Linux esbarra no Sysmiddle. **Porém**, a **síntese de XSLT (este projeto) roda 100% em Linux/WSL** — é .NET 10 + Saxon + Ollama operando sobre **dados exportados** (mapeadores via VPN/SQL; pares input→XML já gerados). **Não precisa do runtime Sysmiddle vivo.**

> **Sinergia elegante:** o objetivo final deste projeto — **substituir o Sysmiddle por XSLT gerado** — é exatamente o que **remove as dependências Windows-only** e viabiliza a migração "tudo em Linux". O projeto de IA **é** o caminho para a VM Linux.

**Recomendação de arquitetura:**
1. Construir a síntese como **projeto .NET 10 standalone Linux-native** (sem acoplar ao runtime Sysmiddle).
2. Futura **VM Linux:** API (.NET 10) + Redis + SQL Server rodam nativos. As peças Sysmiddle ficam num host Windows **apenas enquanto** ainda forem necessárias — e vão sendo aposentadas conforme os XSLTs gerados forem aprovados.

---

## 10. Riscos e mitigações

| Risco | Mitigação |
|-------|-----------|
| XSD da NF-e é enorme e rígido | Provar o loop em **UM** mapeador / **um** tipo (NF-e mod. 55) antes de escalar |
| Gabarito não-determinístico (timestamp/GUID) | **Normalizar** esses nós fora do diff |
| Regras C# com lógica complexa | Baseline determinístico cobre LinkMappings; LLM foca só nas Rules, com diff guiando |
| Overfitting (XSLT "decora" valores) | Múltiplos exemplos/mapeador + exemplo *held-out* |
| Dado fiscal sensível | LLM **local**; anonimizar para qualquer uso em nuvem |

---

## 11. Métricas de sucesso (para o TCC)

- **Cobertura determinística** (% de campos resolvidos só pelo transpilador).
- **Taxa de convergência** (% de mapeadores que chegam a `diff==0`).
- **Iterações médias** do loop de reparo por mapeador.
- **Validação XSD** (100% dos XSLTs aprovados geram NF-e XSD-válida).
- **Custo** (tokens/tempo LLM local por mapeador) vs. baseline.

---

*LayoutParser · Arquitetura de síntese de XSLT · v1 · `@lp-architect`*
