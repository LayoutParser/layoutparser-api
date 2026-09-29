# Slice 6 — Gate transversal contra mutação Sysmiddle (issue #232)

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

## 1. Inventário de endpoints Sysmiddle-adjacentes

### A) Pathway antigo (execução, pré-existente)
`TransformationExecutionController.ExecuteSysmiddleCandidatesAsync` (via `LowCodeAutoTransformationService`)
e `ParseController` (`candidateId=sysmiddle-{guid}`) só **executam** mapeadores `.exe`/DLL já
publicados no catálogo (`tbMapper`) — nunca escrevem/geram artefato Sysmiddle novo. `LayoutParserLib`
(cripto) e `LayoutParserDecrypt.exe` são consumidos como caixa-preta; a API não tem, nunca teve,
capacidade de *autoria* nesse formato (não existe serializer/writer Sysmiddle no código). Não é uma
garantia testada — é ausência estrutural de capacidade. Mesmo diagnóstico via
`LayoutDatabaseController` (rota de debug que descriptografa Base64, não escreve) e
`MapperDatabaseController` (lista/lê `tbMapper`, não grava mapeador). **Risco real: zero, mas não
testado formalmente.**

### B) Endpoints fiscais novos (Slices 1-5) com parâmetro `engine`
| Controller | `[ServiceFilter(MappingEngineGuardFilter)]`? | Nota |
|---|---|---|
| `MappingDraftsController` (Slice 3) | ✅ Sim, nível de controller | Recusa `engine=sysmiddle` em query e body |
| `MappingExplanationController` (Slice 4) | ❌ Não | Deliberado — rota de leitura, `sysmiddle` é `Engine` válido pra **explicar** (spec §4 permite `explain`) |
| `FiscalMappingPackagesController` (Slice 2) | N/A | Sem campo `engine` no contrato — não é vetor |
| `TransformationExecutionController` (Slice 5, `compile`/`test-runs`) | A verificar no código do Slice 5 (não confirmado nesta sessão) | Transpiladores geram XSLT/TCL a partir de `MappingDraftRule` — nunca "sysmiddle" como alvo de compilação; checar se aceita `engine` como parâmetro de saída |

## 2. Decisão: `MappingExplanationController` NÃO recebe o filtro

Mantém-se a exclusão. `MappingEngineGuardFilter` bloqueia por *ação sobre o valor*, não por
presença do literal "sysmiddle" — ele impede que `engine=sysmiddle` autorize escrita. No
`MappingExplanationController` não há escrita: é sempre leitura/explicação, e o próprio contrato
(`_sysmiddleAdapter.ExplainAsync`) é o caso permitido pela spec §4 ("Sysmiddle pode explain, nunca
author"). Aplicar o filtro aqui seria redundante e semanticamente errado (bloquearia o único uso
legítimo do valor "sysmiddle" no sistema). Defesa em profundidade não significa aplicar todo filtro
a todo endpoint — significa aplicar o filtro certo à ação certa. Decisão: **manter como está**,
documentar a razão inline no controller (já existe comentário, reforçar citando este slice).

## 3. Vetores de adulteração a cobrir em teste (além do já coberto no Slice 3)

1. `engine` como array/objeto em vez de string — `MappingEngineGuardFilter.ResolveEnginesAsync` só
   trata `ValueKind == String`; um payload `{"engine":["sysmiddle"]}` ou `{"engine":{"value":"sysmiddle"}}`
   passa sem detecção. **Lacuna real.**
   tratava `ValueKind == String`; um payload `{"engine":["sysmiddle"]}` ou `{"engine":{"value":"sysmiddle"}}`
   passava sem detecção. **Lacuna real — CORRIGIDA em 2026-09-01 (ver seção abaixo).**
2. Homoglyphs/Unicode que normalizam para "sysmiddle" (ex.: caracteres Cyrillic visualmente
   idênticos) — comparação é `OrdinalIgnoreCase` sobre string literal ASCII; não há normalização
   Unicode (NFKC) antes da comparação. Se o backend downstream faz qualquer `ToLowerInvariant`
   culture-aware ou trim de diacríticos antes de rotear pelo valor, um valor disfarçado poderia
   escapar do filtro e ainda ser interpretado como "sysmiddle" mais adiante. **Verificar se algum
   consumidor downstream do campo `engine` faz esse tipo de normalização — se não fizer, o vetor é
   teórico, não explorável (o valor disfarçado nunca vira "sysmiddle" de fato em lugar nenhum).**
3. `engine` ausente — o filtro só bloqueia valor explícito "sysmiddle"; ausência passa. Cada
   controller downstream precisa ter fail-closed próprio (allowlist explícita, como o comentário do
   filtro já adverte). Confirmar que `MappingDraftsController.CreateDraft` de fato rejeita `engine`
   ausente/vazio, não só "sysmiddle" — se hoje aceita ausência como default silencioso para algum
   motor, é lacuna a testar (não necessariamente a corrigir, se o default for `tcl`/`xslt` explícito
   e documentado).
4. Query vs. body divergente — já coberto no Slice 3; replicar como caso de regressão na suíte
   nova, não reimplementar.
5. Content-Type não-JSON (`multipart/form-data`, `application/x-www-form-urlencoded`) carregando
   `engine=sysmiddle` — o filtro só lê `request.HasJsonContentType()`; um upload multipart com campo
   de formulário `engine=sysmiddle` não é inspecionado pelo filtro. Verificar se algum endpoint dos
   Slices 1-5 aceita `engine` fora de JSON puro (ex.: upload de arquivo + campos de form). Se
   nenhum aceitar, vetor é teórico; se algum aceitar, é lacuna real.

## 4. RBAC/role — não é vetor adicional hoje

Confirmado nesta sessão: nenhum controller tem `[Authorize]` ou enforcement de papel — `ICurrentUser`/
`WorkspaceRole` (Slice 1) populam identidade e papel, mas nada no pipeline HTTP hoje decide "esse
papel pode isso". Não existe rota administrativa privilegiada que bypasse `MappingEngineGuardFilter`
porque não existe *nenhuma* rota com controle de acesso por papel ainda — o gate único e universal
(o filtro) é a única barreira que existe, para todo mundo. Isso não é uma lacuna do Slice 6: é fora
de escopo (autorização por papel é decisão de produto em aberto, `rollout-p2-autenticacao.md`).
Quando RBAC por papel for implementado, revisitar se algum papel deveria ter rota de bypass
intencional (não deveria, pela spec §4) — registrar como item futuro, não bloqueante deste slice.

## 5. Plano de teste (suíte nova, `tests/LayoutParserApi.Tests/Security/SysmiddleGateTests.cs`)

Majoritariamente teste, não código de produção novo — exceto o item 5.1 abaixo, que é lacuna real.

- **5.1 [CÓDIGO NOVO NECESSÁRIO]** Estender `MappingEngineGuardFilter.ResolveEnginesAsync` para
  tratar `engine` como array (`ValueKind == Array`, checar cada elemento string) — hoje silenciosamente
  ignorado. Sem isso, `{"engine":["sysmiddle"]}` passa despercebido. Pequeno, mesmo arquivo.
- 5.2 Suíte de integração cobrindo TODOS os endpoints da tabela §1-B com: `engine=sysmiddle` em
  query, em body, em ambos com valores diferentes, `engine` ausente, `engine` como array (após 5.1),
  content-type não-JSON quando aplicável.
- 5.3 Teste de regressão explícito no pathway antigo (§1-A): assert de que
  `ExecuteSysmiddleCandidatesAsync` e `LowCodeAutoTransformationService` não expõem nenhum
  método de escrita/publicação — teste estrutural (reflection sobre a superfície pública) mais do
  que teste de comportamento, já que a ausência de capacidade é a garantia.
- 5.4 Teste negativo específico do `MappingExplanationController`: `engine=sysmiddle` em `explain`
  retorna 200 (não bloqueado) — prova que a exclusão é intencional e continua válida após mudanças
  futuras (guarda contra alguém "corrigir" isso sem saber que é deliberado).
- 5.5 Se o Slice 5 (`compile`/`test-runs`) aceitar `engine`, replicar a matriz de §1-B para lá —
  confirmar isso lendo `TransformationExecutionController` antes de escrever a suíte final.

## Veredito

A garantia central (Sysmiddle não gera/edita nada) é sólida por **ausência estrutural de
capacidade** no pathway antigo, e o pathway novo (Slice 3) já tem defesa ativa testada. As lacunas
reais são pontuais e pequenas: (1) filtro não trata `engine` como array/objeto, (2) cobertura do
filtro não confirmada no Slice 5, (3) nenhuma suíte hoje prova a ausência de capacidade de escrita
Sysmiddle de forma automatizada — é confiança implícita, não gate testado.

## Correção do filtro — 2026-09-01

`MappingEngineGuardFilter` só reconhecia `engine` como string simples — `{"engine":["sysmiddle"]}`
(ou qualquer array com `sysmiddle` no meio) passava despercebido, porque `ResolveEnginesAsync`
checava `engineProp.ValueKind == JsonValueKind.String` e ignorava silenciosamente qualquer outro
`ValueKind`.

**Fix:** `ResolveEnginesAsync` agora delega a avaliação do corpo pra `IsEngineBlocked(JsonElement)`,
que trata três casos: string simples (comportamento antigo, preservado); array (qualquer elemento
string batendo `sysmiddle` recusa — cobre `["xslt","sysmiddle"]`); e, por padrão fail-closed,
qualquer outro `ValueKind` (objeto, número, bool, null) é tratado como recusa — não reconhecer o
formato não deveria significar "aceitar por omissão".

**Cobertura no Slice 5 confirmada:** `MappingCompilationController` (endpoints `compile`/
`test-runs`) já tinha `[ServiceFilter(typeof(MappingEngineGuardFilter))]` no nível da classe desde
a implementação original do Slice 5 — o design anterior não tinha esse ponto confirmado
explicitamente, mas o código já estava correto. Adicionado teste de reflection
(`MappingCompilationController_aplica_o_filtro_no_nivel_da_classe`) pra travar isso.

Testes novos em `MappingEngineGuardFilterTests`: array com `sysmiddle` no meio → recusado; array
sem `sysmiddle` → passa; objeto JSON em `engine` → recusado (fail-closed). `dotnet build` (0 erros)
e `dotnet test` (522 casos, todos verdes, sem regressão).

## Suíte de gate consolidada — 2026-09-01 (Quinn/QA)

Criada `tests/LayoutParserApi.Tests/Security/SysmiddleGateTests.cs` (25 testes) cobrindo, num
único arquivo, todos os vetores do §3 e o plano de teste do §5: atributo do filtro nos dois
controllers fiscais (`MappingDraftsController`/`MappingCompilationController`) + ausência
deliberada no `MappingExplanationController`; `engine=sysmiddle` recusado em string/casing/
array/objeto, em query e em body, isolado e combinado (query≠body); ausência de `engine` não
bloqueada pelo filtro mas recusada pelo controller (as duas metades da garantia, juntas);
content-type não-JSON como vetor teórico confirmado (documentado, não explorável hoje);
`MappingExplanationController` permitindo `engine=sysmiddle` via fallback de `MapperGuid` real
(200) e reafirmando `Capabilities.Author=false`/`Publish=false` mesmo nesse único caso legítimo;
teste de caracterização por reflection sobre o assembly inteiro da API procurando por qualquer
tipo com "Sysmiddle" no nome combinado com "Writer"/"Serializer"/"Encoder"/"Author"/"Publisher"/
"Generator" — nenhum encontrado; `ExecuteSysmiddleCandidatesAsync` confirmado privado (não é
superfície pública); nota estrutural de que nenhum controller fiscal tem `[Authorize]` hoje (RBAC
não é vetor, é escopo futuro).

`dotnet build`: 0 erros (só warnings pré-existentes, `SCS0005`/`SCS0018`, não relacionados).
`dotnet test`: 540 testes, **539 passando, 1 falha esperada/documentada** (ver achado abaixo) —
sem regressão nos 522 pré-existentes.

### ACHADO CRÍTICO — bypass do filtro via espaço ao redor de "sysmiddle" — CORRIGIDO (2026-09-01)

`MappingEngineGuardFilter.IsSysmiddle(string? engine)` fazia
`!string.IsNullOrWhiteSpace(engine) && string.Equals(engine, "sysmiddle", StringComparison.OrdinalIgnoreCase)`
— **sem `Trim()`**. Um payload com `engine=" sysmiddle "` (espaço antes/depois, tanto em query
quanto em body, já que os dois caminhos usam o mesmo `IsSysmiddle`) não batia a comparação exata
de string e passava pelo filtro sem ser bloqueado.

**Fix aplicado por `@lp-backend-dev`:** `IsSysmiddle` agora aplica `engine.Trim()` antes da
comparação (`Services/Filters/MappingEngineGuardFilter.cs`) — cobre query e body, já que os dois
passam pelo mesmo método (inclusive via `IsEngineBlocked` para array/objeto). O teste que antes
caracterizava o bypass (`ACHADO_CRITICO_engine_sysmiddle_com_espacos_ao_redor_NAO_e_recusado`) foi
renomeado para `Engine_sysmiddle_com_espacos_ao_redor_e_recusado` e agora assert a recusa correta
(`UnprocessableEntityObjectResult`), não mais o bypass. `dotnet build`: 0 erros. `dotnet test`:
540/540 passando, sem regressão.

## Veredito final — 2026-09-01

A garantia central (Sysmiddle não gera/edita nada) permanece **sólida por ausência estrutural de
capacidade** — nenhum writer/serializer Sysmiddle existe no assembly, confirmado formalmente pela
suíte nova (antes era confiança implícita, agora é gate testado). Os dois controllers fiscais
recusam `engine=sysmiddle` em todas as formas testadas (string/casing/array/objeto/espaços), e o
único endpoint que permite o valor (`MappingExplanationController`) é comprovadamente read-only
(`Capabilities.Author=false` sempre).

Com o fix do `.Trim()` aplicado e testado, o único achado pendente do gate foi fechado. Veredito:
**PASS**.
