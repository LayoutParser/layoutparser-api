# Viabilidade — decompilar DLLs do Sysmiddle como insumo para RAG

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

> **PT-BR** · Análise de viabilidade de uma ideia trazida pelo dono do projeto: entregar as DLLs do
> Sysmiddle (motor low-code) para o Ollama/loop RAG, para que a IA "entenda a regra de negócio de
> verdade" ao montar/melhorar mapeamentos TCL/XSL/XSLT.
>
> **EN** · Feasibility analysis of decompiling Sysmiddle's engine DLLs to feed the RAG loop, as an
> alternative/complement to learning from the (Layout, Mapper, Functions) → XSLT triple already used.

> Documento de arquitetura (autoria: `@lp-architect`). Decisão de direção pertence ao dono do
> projeto (ver §2 — ressalva de licença). Execução, se aprovada: `@lp-parser-llm`.

---

## 0. Recomendação resumida

**Não decompilar a DLL. Indexar os artefatos estruturados que a API já lê hoje** (Layout XML,
Mapper — `LinkMappingItem`/`MapperRule.ContentValue`/`XslContent` — e as Functions customizadas do
Sysmiddle, se existirem como asset separado e legível). Esse caminho já é, em essência, o que
`ia-xslt-synthesis.md` descreve (§3-§5) — a diferença real trazida pela pergunta do dono é:
**as `Functions` customizadas ainda não estão mapeadas como fonte no design existente**, e isso
merece fechar antes de escalar. Decompilar a DLL do motor (cripto/runtime) não agrega regra de
negócio nova — a regra de negócio **não mora na DLL do motor**, mora nos dados que o motor
interpreta (Mapper/Layout/Functions), que já são nossos e já são legíveis sem engenharia reversa.

---

## 1. O que a pergunta confunde: "motor" vs. "regra de negócio"

O Sysmiddle tem duas coisas distintas, e a proposta do dono as trata como uma só:

| Camada | O que é | Onde mora a regra de negócio? |
|--------|---------|-------------------------------|
| **Motor / runtime** (`LayoutParserLowCodeRunner.exe`, cripto em `LayoutParserLib`) | Interpretador genérico: lê Layout+Mapper+Functions e **executa** a transformação | **Não.** É o interpretador — igual à JVM não conter a lógica de um `.jar`. Decompilar isso revela *como o Sysmiddle interpreta*, não *o que a NF-e do cliente X deve virar*. |
| **Dados que o motor interpreta** (Layout XML, `Mapper.LinkMappingItem`/`MapperRule.ContentValue`/`XslContent`, Functions customizadas) | A regra de negócio de fato: campo→campo, condicionais, formatação fiscal | **Sim.** É aqui que "como Layout+Mapeador+Functions se combinam" está codificado — e é exatamente o que `ia-xslt-synthesis.md` §3 já cataloga (LinkMappings determinístico + Rules DSL via LLM). |

Isso já está confirmado pelo próprio design existente: o `MapperVo` (extraído do XML
descriptografado do Mapper, **não** da DLL) é a fonte usada pelo `MapperExtractor`/
`DeterministicXslTranspiler`/`LlmXslSynthesizer`. O trio (LinkMappings, Rules, XslContent) já é o
"como Layout+Mapeador se combinam" — a única peça que a arquitetura atual **não nomeia
explicitamente** é "Functions customizadas" como um artefato à parte (ver §3).

**Consequência:** decompilar a DLL do motor não fecha um gap de conhecimento de negócio — ela não
tem esse conhecimento. Teria, no máximo, valor para entender *detalhes de como o Sysmiddle resolve
ambiguidades de execução* (ordem de aplicação de regras, precedência, tratamento de erro) — um
ganho estreito, de engenharia, não de "regra de negócio", e caro/arriscado de obter (§2).

---

## 2. Viabilidade técnica de decompilar

- **Stack:** `.NET Framework 4.8.1` (confirmado em `ia-xslt-synthesis.md` §9 e no ecossistema —
  `LayoutParserLib`/`LayoutParserDecrypt.exe`/`LayoutParserLowCodeRunner.exe` são todos .NET
  Framework Windows-only). .NET Framework **compila para IL**, que ferramentas como **ILSpy/dnSpy**
  decompilam de volta para C# com fidelidade alta — **se não houver ofuscação**. Não há registro no
  projeto de tentativa prévia de decompilar essas DLLs; não posso confirmar se há ofuscador
  (`ConfuserEx`, `.NET Reactor`, etc.) aplicado — é a primeira pergunta técnica a responder antes de
  investir tempo nisso, caso a direção avance apesar da ressalva do §2.1.
- **Mesmo sem ofuscação**, o produto de uma decompilação é **código de infraestrutura do
  interpretador** — parsing de XML de configuração, dispatch de regras, chamadas ao motor de cripto.
  Não é pseudo-código de "regra fiscal" indexável por um RAG; é engenharia reversa de um produto de
  terceiro, com baixo retorno para o problema que o projeto já resolve de outro jeito (§1).
- **Custo real:** tempo de engenharia reversa (dias a semanas, dependendo de ofuscação) para extrair
  conhecimento que **já está acessível em texto limpo** via `MapperVo` descriptografado pela própria
  API (`DecryptionService`) — caminho que já existe e é ordens de magnitude mais barato.

### 2.1 Ressalva de licença — decisão do dono, não da arquitetura

Sysmiddle é ferramenta de terceiro (Connect US, conforme `ia-xslt-synthesis.md` §1). Decompilar a
DLL do motor para extrair lógica **provavelmente esbarra em termos de licença/EULA** — a maioria
de produtos comerciais .NET proíbe engenharia reversa exceto para interoperabilidade (e mesmo essa
exceção varia por jurisdição/contrato). **Eu não tenho o contrato de licenciamento do Sysmiddle em
mãos e não posso avaliar isso sozinha — esta é uma pergunta que precisa ir para o dono do projeto
decidir antes de qualquer decompilação**, independentemente do mérito técnico. Não decida isso
silenciosamente durante a implementação; se a direção for "decompilar mesmo assim", pare e escale.

---

## 3. Caminho recomendado: indexar os artefatos estruturados (dado nosso, sem risco de terceiro)

Este caminho já é, no fundamento, a arquitetura descrita em `ia-xslt-synthesis.md`. O que muda aqui
é reafirmar que ele **substitui integralmente** a ideia de decompilar a DLL, e fechar o gap das
Functions customizadas:

| Bloco pedido pelo dono | Já mapeado como fonte? | Onde |
|---|---|---|
| **Layout** | Sim | Layout XML, consumido pela API hoje via parsing |
| **Mapeador (regras)** | Sim | `MapperVo` → `LinkMappingItem` (declarativo) + `MapperRule.ContentValue` (DSL) + `Mapper.XslContent` (semente few-shot) — `ia-xslt-synthesis.md` §3 |
| **Functions (regras customizadas)** | **Não nomeado explicitamente ainda** | A verificar: se são funções referenciadas de dentro do `MapperRule.ContentValue` (DSL do Sysmiddle chamando uma function customizada por nome) ou se são um artefato separado (ex.: outro XML/tabela no SQL com corpo de função). **Gap real a fechar antes de escalar** — ver §4 abaixo. |

**Por que isso é estritamente melhor que a DLL:**
1. **É dado nosso** (documentos gerados pelo Sysmiddle a partir do desenho do analista, armazenados
   no nosso SQL), não código de terceiro — sem questão de licença.
2. **Já é legível** — não exige engenharia reversa. `DecryptionService` já resolve a única barreira
   real (cripto Sysmiddle sobre o `ValueContent`).
3. **Alinhado à visão já aprovada do projeto** (RAG + loop verificador, `ia-xslt-synthesis.md` §2):
   aprender do par (Layout, Mapeador, Functions) → XSLT gerado é exatamente "transpilação
   verificada", não "aprendizado do zero" — o mesmo princípio, agora explicitamente cobrindo
   Functions.
4. **Reaproveita o esqueleto existente** — `MapperExtractor`, `CorpusBuilder`, `RAGService`/
   `ExampleStore` já estão desenhados para consumir exatamente este tipo de artefato (§5 do doc de
   síntese).

---

## 4. Gap real a investigar (não decidido aqui): onde vivem as "Functions customizadas"

O design atual (`ia-xslt-synthesis.md`) nomeia `LinkMappingItem`, `MapperRule.ContentValue` e
`XslContent` como as três naturezas da transformação — mas não nomeia "Functions" como um quarto
bloco separado. Duas hipóteses, a confirmar por `@lp-parser-llm` ao inspecionar um `MapperVo` real:

- **(a) Functions = chamadas dentro do DSL** (ex.: `GetLength()`, citado como exemplo em §3 do doc
  de síntese) — nesse caso já estão cobertas pelo mesmo mecanismo de LLM DSL→XSLT, só precisam de
  um catálogo de "funções conhecidas do Sysmiddle e seu equivalente XSLT/XPath" para virar few-shot
  mais forte (ex.: `GetLength()` → `string-length()`).
- **(b) Functions = artefato separado** (biblioteca de funções customizadas por cliente, definida
  em outro XML/tabela, referenciada pelo Mapper por nome) — nesse caso é um **novo tipo de fonte**
  a extrair e indexar, análogo ao `Mapper.XslContent`, e o `MapperExtractor` precisaria de um método
  novo para lê-lo.

Recomendo que `@lp-parser-llm` confirme qual hipótese é a real inspecionando um `MapperVo`
descriptografado com Rules que referenciam funções customizadas (o mapeador de referência já usado
em `ia-xslt-synthesis.md`, `MAP_MQSERIES_SEND_ENV_TXT_XML_NFE`, é um bom candidato) antes de
desenhar a extração — não assumir (a) ou (b) sem checar o dado real.

---

## 5. Fronteira real de cobertura do XSLT

XSLT (mesmo 2.0/3.0, já recomendado em `ia-xslt-synthesis.md` §8 via Saxon) cobre bem transformação
estrutural, condicionais, formatação de string/número/data e agregação — a maior parte do que
`LinkMappingItem` e a maioria das `MapperRule` fazem. Mas há categorias de lógica que **XSLT
genuinamente não expressa bem ou não expressa de forma nativa**, e que só aparecerão se as
Functions customizadas do Sysmiddle fizerem uso delas:

| Categoria | XSLT cobre? | Observação |
|---|---|---|
| Condicional, formatação, cálculo simples, concatenação, truncamento | Sim, nativamente | Grosso do `LinkMappingItem`/regras simples |
| Lookup em tabela estática (ex.: tabela CFOP) | Sim | `xsl:key`/documento externo via `document()` |
| Chamada a serviço externo (HTTP, consulta SQL em tempo de transformação) | **Não** | XSLT é uma linguagem de transformação de árvore, sem I/O de rede por padrão; extension functions (`Saxon:call-out`/`.NET`) existem mas reintroduzem uma dependência de runtime — contraria o objetivo de "eliminar dependência do runtime proprietário" (`ia-xslt-synthesis.md` §1). Se alguma Function fizer isso, é sinal de que a lógica pertence à camada de orquestração da API (pré/pós-processamento), não ao XSLT gerado. |
| Estado mutável complexo entre elementos não-relacionados na árvore (ex.: acumulador global cruzando múltiplos nós fora de escopo XPath) | **Fraco** | XSLT é funcional/sem-efeito-colateral por design; XSLT 3.0 tem `xsl:accumulator` para casos de estado acumulado, mas é limitado a padrões específicos — lógica imperativa arbitrária não tem bom equivalente. |
| Geração de valor não-determinístico (GUID, timestamp "de verdade") | Parcial | XSLT tem `generate-id()`/extension functions, mas não é o forte da linguagem; já é tratado como não-determinismo a normalizar no diff (`ia-xslt-synthesis.md` §10). |

**Não fingir que XSLT resolve tudo.** Se, ao investigar o gap do §4, `@lp-parser-llm` encontrar
Functions customizadas que fazem I/O externo ou estado complexo, a resposta arquitetural correta
não é forçar isso em XSLT nem "decompilar a DLL para replicar o comportamento" — é reconhecer que
essa fração da lógica **permanece fora do XSLT gerado**, como pré/pós-processamento em código C# na
própria API (que já é o padrão do projeto para lógica de orquestração), documentando o porquê caso
a caso à medida que aparecer.

---

## 6. Recomendação final e próximos passos

1. **Não decompilar a DLL do motor Sysmiddle.** Não agrega conhecimento de negócio (§1) e carrega
   risco de licença que não pode ser decidido pela arquitetura (§2.1) — se o dono ainda quiser essa
   via depois de ler este documento, ele precisa antes checar o contrato/EULA do Sysmiddle, não a
   equipe técnica.
2. **Seguir o caminho já desenhado em `ia-xslt-synthesis.md`** — indexar Layout + Mapper
   (LinkMappings/Rules/XslContent) já descriptografados via `MapperExtractor`/`CorpusBuilder`.
3. **Fechar o gap das "Functions customizadas"** (§4) como próximo passo concreto — investigação
   pontual por `@lp-parser-llm` num `MapperVo` real, antes de qualquer trabalho de extração nova.
4. **Registrar a fronteira de cobertura (§5)** como critério de design: qualquer Function que
   dependa de I/O externo ou estado complexo não-XPath vira lógica de orquestração em C#, não XSLT
   gerado — não é uma falha da síntese, é o limite correto da ferramenta.

---

*LayoutParser · Viabilidade DLLs Sysmiddle para RAG · v1 · `@lp-architect`*
