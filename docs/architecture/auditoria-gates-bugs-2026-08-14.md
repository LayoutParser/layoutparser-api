# Auditoria de gates, bugs e débito técnico — 2026-08-14

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

Missão `review-arch` (`@lp-architect`). Escopo: repositório inteiro (código, CI/CD, docs de
arquitetura, memórias de agente). Objetivo: achar coisas **novas** — o que já virou issue
(#30–#67, todas fechadas em `gh issue list`) não é repetido aqui, exceto quando a issue foi
fechada citando uma correção que **não cobre o que a issue descrevia** (achados #1 e #2 abaixo).

Metodologia: leitura dos índices de memória de todos os agentes, `gh issue list --state all`,
leitura de `Program.cs`, dos 4 workflows (`ci-dev.yml`, `deploy.yml`, `merge-gate.yml`,
`codeql.yml`), do `security-code-scan-baseline.json`, e busca dirigida por padrões de risco
(`.Result`/`.Wait()`, `Task.Run` sem try/catch, DI não registrado) nos controllers e serviços.
Cada achado tem evidência em arquivo:linha ou comando reproduzível.

## Resumo executivo

| Severidade | Qtd | Novos (não rastreados) |
|---|---|---|
| Crítico | 1 | 1 |
| Alto | 2 | 2 |
| Médio | 2 | 2 |
| Baixo | 1 | 1 |

Achado mais importante: uma correção já fechada como concluída (issue #33) **regrediu
silenciosamente** e o endpoint voltou a quebrar em runtime — o teste de regressão que deveria
proteger contra isso não exercita o `Program.cs` real, só uma cópia manual dele.

---

## 1. [CRÍTICO] `DataGenerationController` quebra em runtime — regressão da issue #33 (fechada)

**O que aconteceu:** a issue #33 (fechada 2026-08-13, commit `6082834`) registrou
`ISyntheticDataGeneratorService`, `IExcelDataProcessor`, `ILayoutAnalysisService` e as
dependências do `TxtFileGeneratorFactory` no DI de `Program.cs`. Um commit posterior na mesma
branch (`9e52791` "refactor: remove Pathway 1 legado", consolidado no merge `612a5a3
"resolvendo conflito"`) **apagou o bloco inteiro** desses registros como dano colateral de uma
resolução de conflito — a intenção era remover só `IMapperTransformationService` (Pathway 1),
mas o bloco "Generation Services" que ficava logo abaixo dele foi junto.

**Estado atual confirmado nesta sessão** (`HEAD` = `1dc58f2`, branch `develop`):
```
$ git diff 6082834 HEAD -- Program.cs   # mostra a remoção do bloco inteiro
$ grep -n "ISyntheticDataGeneratorService\|IExcelDataProcessor\|ILayoutAnalysisService\|TxtFileGeneratorFactory" Program.cs
(nenhum resultado)
$ grep -n "ISyntheticDataGeneratorService" Controllers/DataGenerationController.cs
23:        private readonly ISyntheticDataGeneratorService _dataGenerator;
31:            ISyntheticDataGeneratorService dataGenerator,
```
O controller ainda injeta o serviço; o serviço não está mais registrado. Qualquer chamada a
`DataGenerationController` volta a derrubar com `InvalidOperationException` de resolução de DI —
exatamente o sintoma original da issue #33, agora sem issue aberta cobrindo.

**Por que o quality gate não pegou isso:** `tests/LayoutParserApi.Tests/Controllers/
DataGenerationControllerDiTests.cs` foi escrito para "travar a regressão sem precisar subir a
aplicação inteira" — mas faz isso construindo um `ServiceCollection` **próprio**, com os
registros **copiados manualmente** (linhas 33-40 do teste), em vez de carregar o `Program.cs`
real. O teste continua verde porque testa a cópia, não o original — a suíte de testes (`dotnet
test`, gate obrigatório em `ci-dev.yml`) não detecta a regressão porque nunca olhou para o
`Program.cs` de verdade.

**Impacto:** endpoint de geração de dados sintéticos (usado para fixtures/RAG fiscal — ver
`docs/architecture/ia-fiscal-diagnosis-vision.md`) está morto em `develop` agora mesmo.

**Recomendação (para `@lp-backend-dev`):**
- Reintroduzir o bloco de registro em `Program.cs` (grupo "Generation Services").
- Trocar (ou complementar) `DataGenerationControllerDiTests` por um teste que resolve o
  controller a partir do `WebApplicationFactory`/`builder.Services` real (ou, no mínimo, um
  teste de smoke que enumera todos os `[ApiController]` do assembly e garante que cada um
  resolve via DI a partir do container de `Program.cs` — fecha esta classe inteira de bug de
  uma vez, não só para este controller).

**Rastreamento:** NOVO — issue #33 está fechada e não reflete o estado atual; precisa de issue
nova (ou reabertura com nota da regressão).

---

## 2. [ALTO] `AiCandidateStore` — leak de memória/disco ainda presente; issue #51 fechada citando correção de outro subsistema

**O que a issue #51 pedia:** TTL/limpeza para `Services/Transformation/Ai/AiCandidateStore.cs`
— um `ConcurrentDictionary` em memória e arquivos em `MLData/AiTransformationCandidates/*.json`
que crescem sem limite a cada ticket do pathway de IA em `execute-candidates` (issue #40).

**O que foi fechado como correção:** o comentário de fechamento (2026-08-13) cita o commit
`294ca22` — que adiciona `CleanupOldRuns`/`DefaultRetentionDays = 30` em
`ai/XslSynth/Metrics/RunManifest.cs`. Esse é um componente **diferente**: `ai/XslSynth` é o
projeto standalone de síntese/métricas do Job 1 (ver memória `xslsynth-trilha-a-overlap.md`),
não o `AiCandidateStore` do pathway `execute-candidates` da API. Nomes parecidos (ambos lidam
com "retenção"/"limpeza" de artefatos de IA), subsistemas distintos.

**Confirmado nesta sessão:**
```
$ grep -n "TTL\|Retention\|Cleanup\|ConcurrentDictionary" Services/Transformation/Ai/AiCandidateStore.cs
21:        private readonly ConcurrentDictionary<string, AiCandidateStatus> _memory = new();
```
Nenhuma lógica de expiração, nem em memória nem no `File.WriteAllText` (linha 61) que grava em
disco. O leak descrito na issue #51 está integralmente presente no código hoje.

**Impacto:** uso continuado do pathway `execute-candidates` (issue #40, já em produção) acumula
tickets indefinidamente — memória do processo da API e disco em `MLData/
AiTransformationCandidates/` crescem sem parar. Não é urgente (não derruba nada agora), mas é
exatamente o tipo de achado que costuma ser descoberto tarde, com o processo já degradado.

**Recomendação:** reabrir #51 ou abrir issue nova apontando explicitamente para
`Services/Transformation/Ai/AiCandidateStore.cs`, com critério de aceite igual ao original.

**Rastreamento:** issue existe (#51) mas está **fechada incorretamente** — tratar como NOVO para
fins de decisão do dono.

---

## 3. [ALTO] Deploy de produção não roda a suíte de testes — e pode ser disparado sem PR

**Gate que existe:** `ci-dev.yml` (dispara em push a `develop`/`feat/**`) roda `dotnet test`
como gate obrigatório (linha "Testes (xUnit)") — teste vermelho aborta o deploy de dev.

**Gate que NÃO existe:** `deploy.yml` (dispara em `push` a `main`/`master`, e via
`workflow_dispatch`) builda e publica a API **sem nenhum step de teste**. Conferido lendo o
arquivo inteiro: da checagem de `LayoutParserLib`/`LayoutParserDecrypt`/`LayoutParserApi` até o
`Deploy to server (local)`, não há `dotnet test` em lugar nenhum.

**Como isso compõe com a branch protection perdida:** `agent-authority.md` já documenta que a
proteção nativa de branch foi perdida em 2026-08-12 (repos privados, plano free) e que o
enforcement "master só recebe PR de develop" hoje é só o `merge-gate.yml`, que roda **apenas em
evento `pull_request`** (`on: pull_request: branches: [master, main]`). Um `git push` direto a
`master` (por qualquer conta com permissão de escrita, humana ou agente) **não dispara
`merge-gate.yml`** — só dispara `deploy.yml`, que não testa nada. Ou seja: hoje é possível ir de
um `git push origin master` a produção rodando, sem que nenhum teste automatizado tenha
executado sobre aquele código.

Isso não é o mesmo achado da issue #34 (TLS desligado, fechada) nem do registro de
`agent-authority.md` (perda de enforcement — já documentado); é a composição dos dois com a
ausência específica de teste em `deploy.yml`, que ainda não tinha sido apontada.

**Recomendação:** adicionar um step `dotnet test` em `deploy.yml`, mesmo que redundante com
`ci-dev.yml` na maioria dos fluxos (feature → develop → master) — ele é a única rede de segurança
que sobra no caminho push-direto-a-master. Se o custo de repetir a suíte em todo deploy de
produção for uma preocupação, ao menos gatear em `workflow_dispatch`/push fora do fluxo normal.

**Rastreamento:** NOVO.

---

## 4. [MÉDIO] 27 achados de severidade alta do SecurityCodeScan aceitos em baseline sem issue de rastreamento

**Evidência:** `security-code-scan-baseline.json` (raiz do repo), gerado 2026-08-13, lista 26
achados `SCS0018` (path traversal) e 1 `SCS0016` (CSRF) espalhados por `DocumentController.cs`,
`MetricsController.cs`, `ParseController.cs`, `AutomatedTransformationTestService.cs`,
`LowCodeAutoTransformationService.cs`, `LowCodeTransformationStore.cs`,
`TransformationLearningService.cs`, `TransformationValidatorService.cs`,
`DocumentMLValidationService.cs`, `TransformationPipelineService.cs`, `XsdValidationService.cs`.
O gate de CI (`ci-dev.yml`, step "Security Code Scan - gate por severidade") só bloqueia achados
**fora** desse baseline — os 27 listados continuam aparecendo como warning, nunca bloqueiam.

O próprio `_readme` do arquivo instrui: *"devem virar issue via @lp-pm para correção futura"*.
Isso não foi feito — `gh issue list --state all` não tem nenhuma issue com "path traversal",
"SCS0018" ou "SecurityCodeScan" no título. É debito técnico formalmente documentado no código,
mas não no backlog rastreável.

**Nota de risco:** a maioria dos SCS0018 é em caminhos de arquivo compostos a partir de
`layoutName`/`mapperName`/GUIDs vindos de configuração ou banco (baixo risco de exploração
direta por usuário final), mas dois merecem checagem prioritária por estarem em
`Controllers/DocumentController.cs` e `Controllers/ParseController.cs` — mais próximos de input
de requisição HTTP do que os demais (Services internos).

**Recomendação:** `@lp-pm` formalizar como uma única issue "tech-debt" agregando os 27 achados
(ou uma por arquivo, se preferir granularidade), citando o baseline como fonte. Isso também
resolve o problema de fragilidade do baseline por número de linha (documentado no próprio
`_readme`) — uma issue rastreável sobrevive a refactors que uma chave `arquivo:linha` não
sobrevive.

**Rastreamento:** NOVO.

---

## 5. [MÉDIO] `merge-gate.yml` e `deploy.yml` (produção) sem controle de concorrência entre si

Achado menor de composição: `deploy.yml` já documenta (`concurrency: group: deploy-prod,
cancel-in-progress: false`) que deploys concorrentes enfileiram para não interromper uma troca de
binário no meio. Isso é correto para deploys entre si, mas não existe nenhum gate que impeça um
segundo push a `master` (ex.: hotfix urgente) de entrar na fila **atrás** de um deploy que já
está falhando/travado (o timeout do job é 60 min) — não há alerta de fila crescendo, só o log do
Actions. Baixo risco na cadência atual (deploys pouco frequentes), mas vale registrar caso a
cadência aumente.

**Rastreamento:** NOVO, severidade baixa — incluído aqui só como nota, não recomendo abrir issue
agora (esperar sinal real de fila).

---

## 6. [BAIXO] `RefreshLayoutCacheWithRetryAsync` trata layoutCount=0 como sucesso

`Services/Database/CachePermanentWarmupBackgroundService.cs:110-115` — depois do retry com
backoff (implementado corretamente para a issue #67, já fechada e verificada nesta sessão como
de fato corrigida), se a query ao SQL responder com sucesso mas **zero linhas** (ex.: banco
vazio, ou filtro errado silenciosamente), o código trata como sucesso (`_catalogState.
SetResult(0)`) e o `/health/ready` fica `200 Healthy` com catálogo vazio — o cenário que o
smoke test do `ci-dev.yml` tentava evitar ao trocar de "bate numa rota de negócio" para "bate em
/health/ready" (comentário no próprio workflow, linha ~676). Não é o mesmo bug da #67 (aquele era
conexão falhando; este é conexão OK com resultado vazio) — é uma lacuna adjacente.

**Recomendação:** considerar `CatalogHealthCheck` como `Degraded` (não `Healthy`) quando
`LayoutCount == 0` após warm-up bem-sucedido, para o smoke test de deploy pegar esse caso.

**Rastreamento:** NOVO, baixa prioridade — vale nota, não bloqueia nada hoje.

---

## O que foi checado e está OK (não incluído como achado)

- `Task.Run` fire-and-forget em `ParseController.cs`, `LowCodeAutoTransformationService.cs`,
  `AiTransformationCandidateService.cs`: todos envolvidos em try/catch com log estruturado —
  conforme padrão de `dotnet-standards.md`.
- Usos de `.Result`/`.Wait()` em `DecryptionService.cs` e `LowCodeTransformationService.cs`: são
  sobre `Task`s já concluídas (`await allTask` antes), não bloqueiam a thread — falso positivo do
  grep bruto, confirmado por leitura de contexto.
- `CachePermanentWarmupBackgroundService`: retry com backoff progressivo implementado
  corretamente para a issue #67 (exceto a lacuna do achado #6 acima).
- `RAGController`/`RAGService`: DI registrado corretamente (fix de 2026-07-21, confirmado ainda
  presente em `Program.cs:437`).
- Subsistemas de nuvem (Gemini/OpenAI): nenhuma referência viva a `GeminiAIService`/
  `SemanticAIGenerator` em `Program.cs` ou `DataGenerationController.cs` — decommission
  permanece efetivo no código (a pendência real é a revogação manual da chave, já rastreada em
  `security.md`).

## Próximo passo sugerido

Achados #1 e #2 (crítico e alto) são acionáveis imediatamente por `@lp-backend-dev` — são fixes
pequenos e localizados. Achado #3 é uma decisão de arquitetura de CI que cabe a `@lp-devops`.
Achado #4 é trabalho de backlog puro para `@lp-pm`. Recomendo ao dono do projeto priorizar #1 e
#2 primeiro (regressões silenciosas de fixes já pagos), depois decidir entre #3/#4 pelo esforço
disponível.
