# Design: endpoint `studio-model` (modelo único do Mapping Studio)

**Autora:** Aria (@lp-architect) · **Data:** 2026-09-30 · **Executor:** @lp-backend-dev (Dex)
**Origem:** `layoutparser-portal/docs/proposals/studio-model-api-request.md` + análise ConnectUs (`sysmiddle-analise/relatorio.md`, §1, §3, §4, §8).
**Status:** desenho. Nenhum código de produção foi escrito. Fase 1 (só GET Sysmiddle) está pronta para execução.

## 0. Decisões já tomadas pelo dono (não reabrir)

1. Contrato ÚNICO no formato UCMapper/ConnectUs: duas árvores; ligações e regras penduradas no nó de destino.
2. Três adaptadores: `SysmiddleAdapter` (MapperVO+LayoutVO, fidelidade total), `TclAdapter` (só árvore de entrada; `offset` = soma dos `length` anteriores; sem links/rules), `XsltAdapter` (destino + links/rules; o não estruturável vira `opaque`, somente leitura).
3. `capabilities.editableOps` por engine.
4. Escrita = PATCH com `If-Match`/eTag, ops `addLink/removeLink/setRule/updateNode/rename`, por patch sobre o artefato original, dentro do fluxo de rascunho/release, com justificativa e auditoria.
5. IDs de TCL/XSLT determinísticos a partir do caminho por nome; `rename` devolve mapa id antigo→novo.
6. `display.text` calculado na API (`Nome    (±, mín, máx)` / `Nome    (±, Tipo)`; 4 espaços).
7. Diagnósticos: `ORPHAN_LINK`, `AMBIGUOUS_NAME_PATH`, `TARGET_HAS_LINK_AND_RULE`, `TARGET_HAS_MULTIPLE_LINKS`.
8. Parser tolerante a ordem/presença de elementos (sem versão de esquema).

## 1. O que já existe e será reaproveitado

| Peça | Onde | Uso |
|---|---|---|
| `GET .../mappings/{mappingId}/layout-tree` | `Controllers/LayoutTreeController.cs`, `Services/Fiscal/LayoutTreeService.cs` | Mesmo padrão de resolução (mapper por `MapperGuid` em `ICachedMapperService`, layouts por `ICachedLayoutService.GetLayoutByGuidAsync`), mesma rota-base, mesmo `[RequireWorkspaceRole]` de leitura e mesmo 404/503. **Permanece como está** (compatibilidade); `studio-model` é superconjunto. |
| `GuidXPathCatalog` / `RealMapperParser` | `ai/XslSynth.Contracts/Core/` | Fonte do caminho por nome e do parse de LinkMappings/Rules. **Limitação:** `BuildTree` descarta Choice/Sequence (achata) e não lê `props`. O `studio-model` precisa de parser próprio do LayoutVO (ver §3); não alterar o `BuildTree` (usado pelo loop XslSynth). |
| Adapters de explicação | `SysmiddleExplanationAdapter`, `TclExplanationAdapter`, `XsltExplanationAdapter` | Mostram como cada engine é lido hoje; reuso na Fase 2-4. |
| Rascunho/release | `MappingDraftsController`, `IMappingDraftStore`/`SqlMappingDraftStore`, `SqlMappingReleaseStore` (IdentityDatabase) | Destino das escritas nas Fases 2-4. O vocabulário `If-Match` (base64 de ROWVERSION; 428/400/412/422) já existe e deve ser reutilizado. |
| Auditoria (#226) | `AuditActionFilter` via `[ServiceFilter]` | Aplicar só ao PATCH. O GET não audita (leitura). |
| `Scripts/GenerateTclAndXsl.cs` | raiz | **Não reutilizar** (bug conhecido: `<CHILD>` por `ParentElement.Contains`, `Descendants()` duplica campos). |

## 2. Contrato JSON final (v1)

Envelope (campos novos em relação ao pedido marcados com `+`):

```json
{
  "schemaVersion": 1,                                   // + versão do CONTRATO da API (não do artefato)
  "artifact": {
    "engine": "sysmiddle",                              // sysmiddle | tcl | xslt
    "id": "MAP_…", "name": "…",
    "rawHash": "sha256:<hex>",                          // hash do(s) XML(s) autoritário(s), ver §5
    "eTag": "\"<base64>\"",                             // ver §5
    "variantFields": ["FullXPath", "UniqueOccurrence"], // elementos opcionais encontrados no artefato
    "sources": [ { "role": "mapper", "id": "MAP_…", "rawHash": "…" },
                 { "role": "input-layout", "id": "LAY_…", "rawHash": "…" },
                 { "role": "target-layout", "id": "LAY_…", "rawHash": "…" } ]   // +
  },
  "capabilities": { "edit": false, "editableOps": [] },
  "trees": {
    "input":  { "layoutRef": "LAY_…", "format": "text-positional", "rootIds": ["LIN_…"], "available": true },
    "target": { "layoutRef": "LAY_…", "format": "xml", "rootIds": ["GRT_…"], "available": true }
  },
  "nodes":  { "<id>": Node },
  "links":  { "<id>": Link },
  "rules":  { "<id>": Rule },
  "datatypes": { "DAT_…": { "name": "Str_MAX" } },     // + catálogo resolvido (vazio se indisponível)
  "diagnostics": [ Diagnostic ]
}
```

`format` ∈ `text-positional | text-delimited | xml | json`. `available=false` quando o layout referenciado não foi resolvido (degrada, `rootIds: []`).

### 2.1 Node (comum)

```json
{ "tree": "input|target", "type": "<ver tabela>", "name": "…", "path": "A/B/@attr",
  "parentId": "…|null", "order": 3, "required": false,
  "display": { "text": "Nome    (-, Str_MAX)", "icon": "field", "linked": true },
  "props": { ... por type ... } }
```

- `order` = `Sequence` do XML (ordena irmãos de qualquer tipo); empate/ausência: posição no documento.
- `path`: por nome, separado por `/`, a partir do 1º elemento (sem nome do layout), atributo como `@nome`. Choice/Sequence NÃO entram no `path`, mas SÃO nós na árvore (o desktop mostra; o `BuildTree` legado os achata, o studio-model não).
- `display.icon`: `line, field, repeaterGroup, grouper, characterIgnoreGroup, groupTag, tag, attribute, choice, sequence, groupWithoutOrder, jsonObject` (valor JSON usa `field`). `display.linked` = existe link/rule com este nó como origem/destino.
- `display.text`: nó com filhos → `Nome    (±, mín, máx)`; nó com valor → `Nome    (±, NomeDoTipo)`. `±` = `-` se `required=false` (confirmado 456/456), `+` se `true` (NÃO CONFIRMADO, tratar como hipótese; ver §8). `mín/máx` ausentes → `-`? **Decisão:** omitir o segmento (`Nome    (-)`) e abrir pergunta (§8). Tipo de dado sem catálogo → nome do GUID cru não é exibido; usa `?`.
- `type` ↔ `xsi:type` do `<Element>`:

| type | xsi:type (Sysmiddle) | prefixo GUID |
|---|---|---|
| `line` | LineElementVO | LIN |
| `field` | FieldElementVO | FLD |
| `repeaterGroup` | RepeaterGroupElementVO* | — |
| `grouper` | GrouperElementVO* | — |
| `groupTag` | GroupTagElementVO | GRT |
| `tag` | TagElementVO | TAG |
| `attribute` | AttributeElementVO | ATT |
| `choice` | ChoiceElementVO | CHO |
| `sequence` | SequenceElementVO | SEQ |
| `jsonObject` | JsonObjectElementVO* | OBJ/VAL |
| `unknown` | qualquer outro | — |

\* nomes exatos de `xsi:type` NÃO observados nos dados (só os de 01/02 estão nas amostras). O mapeamento deve ser uma tabela em um único lugar, tolerante (`EndsWith("ElementVO")`, case-insensitive); tipo desconhecido vira `type:"unknown"` + `props.xsiType` (nunca lança).

### 2.2 `props` por type

Todos os campos abaixo são opcionais no XML; ausente ⇒ `null` (nunca inventar default). Comuns a todos: `description`.

| type | props |
|---|---|
| `line` | `initialValue`, `minOccurs`, `maxOccurs`, `validateLength` (IsToValidateLengthCharacters), `validateFieldLesserLength`, `positionalGroupRepetition`, `notRealizeParser`, `createOnlyChildren` |
| `field` | `length` (LengthField), `offset` (calculado; só layout posicional), `align` (Left/Right), `trim` (RemoveWhiteSpaceType All/Right/Left/Nothing), `dataType {guid,name}`, `staticValue`/`isStaticValue`, `isSequential`, `startValue`, `incrementValue`, `caseSensitive`, `initialValue`, `minOccurs`, `maxOccurs` |
| `repeaterGroup` | `minOccurs`, `maxOccurs`, `initialValue` |
| `grouper` | `minOccurs`, `maxOccurs` |
| `groupTag` | `minOccurs`, `maxOccurs`, `notCreateGroupTagOnlyChilds`, `acceptEmpty`, `useCData` |
| `tag` | `dataType`, `useCData`, `acceptEmpty`, `containsAttribute`, `minOccurs`, `maxOccurs`, `length`, `trim`, `staticValue`, `isStaticValue` |
| `attribute` | `dataType`, `trim`, `staticValue`, `isStaticValue` |
| `choice` | `minOccurs`, `maxOccurs` |
| `sequence` | `minOccurs`, `maxOccurs` |
| `jsonObject` | `minOccurs`, `maxOccurs`, `dataType` (quando valor) |

`offset`: soma de `length` dos irmãos anteriores em ordem de `order` (só irmãos `field`; linhas filhas não contam) **dentro da mesma linha**. Se algum irmão anterior tem `length` nulo, `offset=null` + diagnóstico informativo `OFFSET_UNRESOLVED` (novo, não bloqueante).
Props que o parser leu mas não mapeou ficam em `props.extra` (`{ "NomeElemento": "valor" }`, só escalares) para não perder fidelidade de leitura e alimentar `variantFields`.

### 2.3 Link

```json
{ "sourceId": "FLD_…", "targetId": "TAG_…", "order": 1,
  "display": { "text": "Origem_Destino", "icon": "linkMapping", "underTargetId": "TAG_…" },
  "opts": { "trim": "All", "truncate": false, "default": null, "allowEmpty": true,
            "notCreateGroupTagOnlyChilds": false, "createEmptyElement": false,
            "uniqueOccurrence": null, "createValuesDirectBySourceElement": null,
            "useDecimalMapper": null, "fullXPath": null },
  "iterates": true }
```

- **Atenção ao nome enganoso do XML:** `LinkMappingItem/InputLayoutGuid` = ElementGuid da ORIGEM; `TargetLayoutGuid` = ElementGuid do DESTINO.
- `iterates` (Sysmiddle): `true` quando origem E destino são contêineres (`line|repeaterGroup|grouper|groupTag`) — 2.998 casos nos dados reais. Não é flag do XML, é derivado.
- `opts.*` de versões novas (`UniqueOccurrence`, `CreateValuesDirectBySourceElement`, `IsToUseDecimalMapper`, `FullXPath`, `ElementWithoutValue`) são lidos se existirem, `null` se não; semântica não confirmada, portanto somente leitura (ver §8).
- `display.text` = `{nomeOrigem}_{nomeDestino}` (não persistido; calculado).

### 2.4 Rule

```json
{ "anchorId": "TAG_…", "display": { "text": "Rule_qtd", "icon": "rule", "underTargetId": "TAG_…" },
  "name": "…", "code": "…", "reads": ["A/B"], "writes": ["x/y"], "functions": ["Concat"],
  "prePos": false, "opaque": [ { "reason": "side-effect:MSqlServerHelper", "span": [120, 180] } ] }
```

- `anchorId` = `Rule/TargetElementGuid`. `reads` = caminhos `I.`, `writes` = `T.` (extração por tokenização, tolerante a `=` e `==`; não executa nada). `span` = offsets de caractere em `code`.
- `reason` ∈ `side-effect:<fn>`, `out-of-catalog:<fn>`, `global-var`, `loop`, `csharp-new`, `write-outside-anchor`, `pre-pos-rule`.
- O campo `ast` do §8 do relatório FICA DE FORA da v1 (YAGNI; o front edita `code` como texto).
- Regra sem origem é `rules`, nunca link com `sourceId` nulo.
- `display.text` = `Rule_{nomeDestino}`. Regras de versões antigas (`Regra_…`) mantêm o nome que vier do XML em `name`; `display.text` é sempre o calculado.

### 2.5 Diagnostic

```json
{ "code": "ORPHAN_LINK", "severity": "warning", "id": "LKM_…", "message": "…" }
```

| code | id/campos | Critério |
|---|---|---|
| `ORPHAN_LINK` | `id` (link ou rule), `missing: ["source"|"target"]` | GUID de origem/destino (ou âncora da regra) não existe nas árvores. Órfão **permanece** em `links`/`rules` com `display.underTargetId=null`; nunca descartado. |
| `AMBIGUOUS_NAME_PATH` | `path`, `ids: [...]` | ≥2 nós com o mesmo `path` na mesma árvore (85 casos reais). Afeta resolução de `I.`/`T.`. |
| `TARGET_HAS_LINK_AND_RULE` | `id` (nó destino) | Destino com link e rule (0 casos reais; desktop bloqueia). |
| `TARGET_HAS_MULTIPLE_LINKS` | `id`, `linkIds` | >1 link para o mesmo destino (3/14.982, provável resíduo). Também vale para >1 rule. |
| `OFFSET_UNRESOLVED` | `id` | ver §2.2 (informativo). |
| `UNKNOWN_NODE_TYPE` | `id`, `xsiType` | Tipo fora da tabela. |

`severity`: `error` = `TARGET_HAS_LINK_AND_RULE`; `warning` = demais.

## 3. Interface do adaptador, DI e estrutura de arquivos

### 3.1 Interface

```csharp
namespace LayoutParserApi.Services.StudioModel;

public interface IStudioModelAdapter
{
    string Engine { get; }                        // "sysmiddle" | "tcl" | "xslt"
    StudioCapabilities Capabilities { get; }      // editableOps estático por engine (Fase 1: edit=false)

    /// <summary>null = artefato inexistente (→ 404). Lança StudioModelUnavailableException
    /// quando a FONTE (SQL/cache) está fora (→ 503). Layout ausente/ilegível NÃO lança: degrada.</summary>
    Task<StudioModelDocument?> LoadAsync(StudioModelRequest request, CancellationToken ct);

    // Fases 2-4 (não implementar na Fase 1; declarar só quando chegar a hora):
    // Task<PatchResult> ApplyAsync(StudioModelDocument current, IReadOnlyList<StudioOp> ops, PatchContext ctx, CancellationToken ct);
}

public sealed record StudioModelRequest(Guid WorkspaceId, string MapperGuid);

public interface IStudioModelService   // fachada escolhida pelo controller
{
    Task<StudioModelDocument?> GetAsync(Guid workspaceId, string mapperGuid, string engine, CancellationToken ct);
}
```

`StudioModelService` recebe `IEnumerable<IStudioModelAdapter>` e escolhe por `Engine` (case-insensitive). Engine desconhecido/ausente ⇒ **400** `{error}`; default quando `engine` omitido: `sysmiddle` (o front sempre manda, mas o default evita 400 acidental). Engine registrado mas ainda não implementado (tcl/xslt na Fase 1) ⇒ **501** `{error:"Engine não suportado nesta versão."}` — não 404, para o front distinguir de "não existe".

### 3.2 Estrutura (todos novos; nada editado além do DI e do novo controller)

```
Controllers/StudioModelController.cs                  // rota idêntica à do LayoutTreeController
Services/Interfaces/IStudioModelService.cs
Services/StudioModel/IStudioModelAdapter.cs
Services/StudioModel/StudioModelService.cs
Services/StudioModel/StudioModelException.cs          // StudioModelUnavailableException
Services/StudioModel/Sysmiddle/SysmiddleStudioModelAdapter.cs
Services/StudioModel/Sysmiddle/LayoutVoReader.cs      // LayoutVO XML → nós + props (tolerante)
Services/StudioModel/Sysmiddle/MapperVoReader.cs      // MapperVO XML → links/rules (tolerante)
Services/StudioModel/Sysmiddle/NodeTypeMap.cs         // xsi:type → type/icon (tabela única, §2.1)
Services/StudioModel/Sysmiddle/RuleCodeScanner.cs     // reads/writes/functions/opaque (tokenizador, sem executar)
Services/StudioModel/DisplayTextBuilder.cs            // "Nome    (±, …)" (compartilhado pelos 3 adapters)
Services/StudioModel/DiagnosticsBuilder.cs            // diagnósticos sobre o modelo já montado (engine-agnóstico)
Services/StudioModel/StudioModelHasher.cs             // rawHash + eTag
Models/Dtos/StudioModel/StudioModelDocument.cs        // records do contrato (§2)
```

Namespaces: `LayoutParserApi.Services.StudioModel[.Sysmiddle]`, `LayoutParserApi.Models.Dtos.StudioModel`.
Nota: os readers usam `System.Xml.Linq` com lookup por nome local (`el.Element("X")`, sem depender de ordem; ignorar desconhecidos). `TagElementVO` serializa filhos como `<Elements><Elements>…` (quirk de 02-layout-saida-xml.xml): o reader aceita filhos sob `Elements` com nome `Element` **ou** `Elements`.

### 3.3 DI (`Program.cs`)

No grupo onde `ILayoutTreeService` já é registrado (linha ~550, bloco Fiscal/Mapping Studio):

```csharp
builder.Services.AddScoped<IStudioModelAdapter, SysmiddleStudioModelAdapter>();   // + TclStudioModelAdapter, XsltStudioModelAdapter nas Fases 3-4
builder.Services.AddScoped<IStudioModelService, StudioModelService>();
```

`Scoped` (padrão; sem estado compartilhado). Sem dependência nova de infra: reutiliza `ICachedMapperService` e `ICachedLayoutService` já registrados. **Checklist para Dex:** há `tests/.../ProgramCompositionRootTests.cs` — rodar; o histórico (auditoria 2026-08-14) mostra DI apagada em merge, então adicionar o registro ao teste de composição.

## 4. Autorização, erros e headers (vale para GET; PATCH herda)

| Situação | HTTP | Corpo |
|---|---|---|
| Sem identidade / não-membro | 404 | vazio (fail-closed, padrão do repo) |
| `mapperGuid` não existe / sem `DecryptedContent` | 404 | vazio |
| `engine` inválido | 400 | `{ "error": "…" }` |
| Engine registrado sem implementação | 501 | `{ "error": "…" }` |
| Catálogo SQL/cache indisponível (exceção ao listar mappers) | 503 | `{ "error": "Não foi possível consultar o catálogo de mappers no momento." }` |
| Mapper ok, layout ausente/ilegível | 200 | lado com `available:false`, `rootIds:[]` + diagnóstico `LAYOUT_UNAVAILABLE` (warning) |
| XML do mapper ilegível (`XDocument.Parse` falha) | 200 degradado: árvores ok, `links/rules` vazios + `LAYOUT_UNAVAILABLE`-análogo `MAPPER_UNREADABLE` | — |

- Autorização: `[RequireWorkspaceRole(Owner, FiscalAdmin, Mapper, Reviewer, Operator, Viewer)]` (mesma lista do `layout-tree`). NÃO aplicar `MappingEngineGuardFilter` (ler Sysmiddle é sempre permitido, mesma justificativa do `LayoutTreeController`).
- Mensagens em PT-BR; nunca logar `DecryptedContent`, nem `code` de regras, nem valores de XML (podem conter dado de cliente). Logar só GUIDs, tamanhos e contagens (`{MapperGuid}`, `{NodeCount}`).
- Headers de resposta do GET: `ETag: "<eTag>"` (mesmo valor do corpo) e `Cache-Control: private, no-cache`. Suportar `If-None-Match` ⇒ 304 (barato e evita reserializar árvore grande); opcional se atrapalhar o prazo da Fase 1 (listar como nice-to-have).
- Fase 1 NÃO precisa de `AuditActionFilter` (leitura).

## 5. eTag e rawHash

- **rawHash** = `sha256:<hex>` do conteúdo UTF-8 normalizado dos XMLs autoritários. Por fonte: hash do `DecryptedContent` exato (bytes como vêm, **sem** reindentar/reordenar). `artifact.rawHash` = SHA-256 da concatenação ordenada `mapper|input-layout|target-layout` de hashes por fonte (`sources[].rawHash` individuais permitem ao front/PATCH saber QUAL lado mudou).
- **eTag** = base64 do SHA-256 dos mesmos hashes **mais** `schemaVersion` do contrato, truncado para 16 bytes e entre aspas na forma HTTP. Motivo de incluir `schemaVersion`: mudança de contrato invalida caches do front sem mudar o artefato.
- Para Fase 1 o eTag é puramente derivado do conteúdo (não há ROWVERSION do `tbMapper`, SOMENTE LEITURA). Para PATCH (Fases 2+): o eTag passa a ser o **ROWVERSION do rascunho** (base64, vocabulário 428/400/412/422 idêntico ao `UpdateRule`), combinado com o rawHash do artefato-base: se `rawHash` atual da fonte ≠ `baseRawHash` gravado no rascunho ⇒ **409** `{error, code:"SOURCE_CHANGED"}` (alguém publicou outra versão do mapper no catálogo).
- Nunca usar `LastUpdateDate` como eTag (precisão/relógio do banco compartilhado).

## 6. IDs e patch por engine (esboço — Fases 2-4)

| Engine | ID de nó | Patch |
|---|---|---|
| sysmiddle | GUIDs do XML (`LIN_/FLD_/…`, `LKM_`, `RUL_`). Estáveis sob `rename`. | Edita o XML original via `XElement` in-place (nunca reserializa o documento inteiro): `addLink` insere `<LinkMappingItem>` com GUID `LKM_`+novo; `removeLink` remove o elemento; `setRule` cria/edita `<Rule>` ancorada; `updateNode` altera só os elementos escalares mapeados em `props` (elemento ausente ⇒ cria, preservando ordem relativa dos existentes); `rename` troca `<Name>` e, se `updateRuleCode`, reescreve `I.`/`T.` em `Rule/Code` por substituição de caminho validada (nunca regex cega). Elementos/atributos desconhecidos NÃO são tocados. |
| tcl | `tcl:<caminho-por-nome>` (ex.: `tcl:LINHA_CAB/LINHA_ITEM/Quantidade`), determinístico; homônimos irmãos recebem sufixo `~2, ~3` por ordem (+ diagnóstico `AMBIGUOUS_NAME_PATH`). | Patch textual sobre o TCL (LINE/FIELD/CHILD); só `updateNode` (length/name), `rename`; sem links/rules (`editableOps`: `updateNode`,`rename`). `rename` devolve `idMap` (todo descendente muda de id). |
| xslt | `xslt:<caminho-por-nome>` no destino; `lnk:`/`rul:` + hash do XPath/expressão. | Edição in-place do XSLT apenas de blocos estruturáveis (`xsl:value-of`/`for-each`); blocos `opaque` são somente leitura e ops sobre eles ⇒ 422 `OPAQUE_READONLY`. `for-each` ⇒ link contêiner `iterates:true`. `editableOps` a definir na Fase 4 (provavelmente `addLink/removeLink/updateNode` destino). |

Regras comuns de PATCH (todas as engines):
- Corpo: `{ "ops":[…], "justification":"…" }`; `If-Match` obrigatório (428 sem; 400 malformado; 412 conflito; 409 violação de invariantes; 422 op inválida com `code`).
- Validação servidor: `addLink`/`setRule` em destino que já tem link ou rule ⇒ 409 `TARGET_ALREADY_BOUND`; `rename` com `updateRuleCode:false` ⇒ **200 sem aplicar nada de rename além do `<Name>`** e devolve `warnings:[{code:"RULES_AFFECTED", ruleIds:[…]}]` (responde ao pedido "devolver quais regras seriam afetadas").
- **Escrita NUNCA vai direto em `tbMapper`** (SOMENTE LEITURA, `security.md`). O PATCH grava uma revisão do artefato em `IdentityDatabase:*` (família `tbMappingDraft*`/`tbMappingRelease`; provável nova entidade `tbStudioArtifactRevision` com `baseRawHash`, `patchedContent`, `ops`, `justification`, `userId`) e a promoção segue o fluxo de release existente. Resposta: `studio-model` recalculado sobre o conteúdo patchado + novo `eTag`.
- Justificativa obrigatória (como `UpdateRule` para `edited`); `[ServiceFilter(typeof(AuditActionFilter))]` no PATCH; RBAC de escrita `Mapper, FiscalAdmin, Owner`.
- Órfãos: preservados no documento de saída; nunca removidos pelo patch (diferente do desktop, que os remove no `Save`).
- Idempotência: `clientOpId` opcional por op (Fase 2+; evita duplicar `addLink` em retry).

## 7. Plano de testes (SOMENTE dados sintéticos)

**Regra dura:** nenhum XML/TXT real de cliente, nenhum GUID real, em teste, fixture ou log. Usar só as amostras sintéticas (`sysmiddle-analise/amostras-sinteticas/01..07`, GUIDs `…0000-0000-…a001`). Copiar as necessárias para `tests/LayoutParserApi.Tests/Fixtures/StudioModel/` (mesmas de 01, 02, 03 + variantes abaixo) após revisar que são sintéticas (o cabeçalho delas já diz `SINTETICO`).

Projeto: `tests/LayoutParserApi.Tests/StudioModel/` (xUnit, como `Fiscal/LayoutTreeServiceTests.cs`).

1. **Unit — `LayoutVoReaderTests`**: amostra 01 → tipos/props/`offset` corretos (LINHA_CAB: TipoRegistro 0, NumeroPedido 3…); `Sequence` ordena campos e linhas misturados; amostra 02 → quirk `<Elements><Elements>`; Choice/Sequence viram nós; `xsi:type` desconhecido → `unknown`, sem exceção.
2. **Unit — tolerância** (variantes sintéticas derivadas de 01/03): elementos em ordem trocada; sem `Description`/`IsRequired`; com `FullXPath`/`ElementType`/`ParentElement`; com `ElementWithoutValue` em vez de `AllowEmpty`; blocos `XPathLinkMappings`/`StaticValueMappings` vazios; elementos desconhecidos no meio. Todos devem produzir o mesmo modelo (ou só `variantFields` diferentes).
3. **Unit — `DisplayTextBuilderTests`**: 4 espaços; `-`/`+`; com filhos vs valor; sem min/max; tipo sem catálogo.
4. **Unit — `DiagnosticsBuilderTests`** (mapper sintético montado à mão): um caso para cada código (`ORPHAN_LINK` com GUID inexistente, `AMBIGUOUS_NAME_PATH` com dois irmãos homônimos, `TARGET_HAS_LINK_AND_RULE`, `TARGET_HAS_MULTIPLE_LINKS`).
5. **Unit — `RuleCodeScannerTests`**: `I.`/`T.`/`#.`/`$.`; comparação `=` e `==`; `begin/end` e `{}`; função de efeito colateral ⇒ `opaque`; `span` dentro do `code`. Código de regra sintético, nunca de cliente.
6. **Unit — `StudioModelHasherTests`**: estabilidade (mesmo XML ⇒ mesmo hash), sensível a 1 byte, independente da ordem de propriedades do JSON.
7. **Service/Controller** (mocks de `ICachedMapperService`/`ICachedLayoutService`, padrão de `LayoutTreeServiceTests`): 404 (mapper inexistente), 503 (exceção do catálogo), 200 degradado (layout null), 400 (`engine` inválido), 501 (tcl na Fase 1), 404 sem identidade/não-membro.
8. **Composição**: `ProgramCompositionRootTests` resolve `IStudioModelService` com todos os adapters.
9. **Contrato**: snapshot JSON da amostra 01+02+03 (golden file sintético) — trava regressão do contrato; serializar com `UnsafeRelaxedJsonEscaping` (já padrão global), validar que `<`/`>` em `code` de regra sobrevivem.
10. **Paridade com `layout-tree`**: para o mesmo mapper sintético, os GUIDs de `layout-tree.Rules` ⊆ `studio-model.links` (garante que o superconjunto não contradiz o endpoint existente).
11. **Performance**: fixture sintética gerada por código com ~11k campos / 15k links (escala real dos dados, sem dados reais); meta p95 < 500 ms de montagem (sem I/O) — medir, não assumir.

## 8. FASE 1 — GET com `SysmiddleAdapter` (passo a passo para o Dex)

Escopo: só leitura, só `engine=sysmiddle`. `capabilities: { edit:false, editableOps:[] }`. TCL/XSLT ⇒ 501. Sem PATCH, sem tabela nova, sem mudança de banco.

1. **Branch/Git:** trabalhar na `develop` (regra do dono); Conventional Commits; sem push (Gage faz).
2. **DTOs** em `Models/Dtos/StudioModel/StudioModelDocument.cs`: records imutáveis do §2. Usar `Dictionary<string, Node>` (não `IReadOnlyDictionary` sem ordenação: manter **ordem de inserção** = ordem da árvore para saída estável). Enums como string. Tudo opcional = nullable; `WhenWritingNull` já é global (não serializar `props` nulos).
3. **Obter o XML autoritário (mapper):** `ICachedMapperService.GetAllMappersAsync()` → `Mapper` por `MapperGuid` (case-insensitive), usar `DecryptedContent` — **exatamente como `LayoutTreeService.GetLayoutTreeAsync`**. Sem `DecryptedContent` ⇒ `null` (404). Exceção ao listar ⇒ `StudioModelUnavailableException` (503). Pendência conhecida (auditoria 2026-09-08): `GetAllMappersAsync` é sem `WHERE ProjectId`; para Fase 1 aceitar (endpoint já filtra por membership), mas registrar a pergunta §9-7.
4. **Obter o XML autoritário (layouts):** GUIDs de `InputLayoutGuidFromXml ?? InputLayoutGuid` (prioridade XML, como no `layout-tree`) e idem target. `ICachedLayoutService.GetLayoutByGuidAsync(guid)` → `DecryptedContent`. Null/exceção ⇒ lado `available:false` (warning no log sem conteúdo). Nota do relatório: no índice Redis o GUID vem **sem prefixo** `LAY_`; se o lookup por GUID do `CachedLayoutService` já normaliza (há `CachedLayoutServiceGuidLookupTests`), não tratar de novo; se não, tentar as duas formas.
5. **Nunca usar** o `Models.Entities.MapperVo`/`LinkMappingItem` legados nem `RealMapperParser` para o modelo: o `RealMapperParser` não expõe todos os `opts` e perde ordem de documento. Ler o XML direto nos `LayoutVoReader`/`MapperVoReader` (§3.2). `RealMapperParser` pode ser usado APENAS como verificação cruzada em teste (contagem de links/rules deve bater).
6. **`LayoutVoReader`:** raiz `LayoutVO`; `LayoutType` → `format` (`TextPositional→text-positional`, `TextDelimited→text-delimited`, `Xml→xml`, `Json→json`; ausente ⇒ derivar do `xsi:type` da raiz como o `DetectKind` atual; senão `unknown`). Percorrer `Elements/Element` **e** `Elements/Elements`; para cada nó: `ElementGuid` (Trim — o XML real traz quebras de linha dentro do texto, ver comentário `§trim` no `GuidXPathCatalog`), `Name` (Trim), `Sequence`, `IsRequired`, `props` por type, `MinimalOccurrence/MaximumOccurrence`. Nó sem `ElementGuid`: gerar id sintético `idx:<caminho>` e diagnóstico, não descartar. Calcular `path`, `offset` (só posicional) e `order`. Conferir tolerância a namespaces (`xsi:type` via `XNamespace`).
7. **`datatypes`:** o catálogo `DataTypeVO` (GUID `DAT_` → nome, ex. `Str_MAX`) NÃO foi localizado no repo. Fase 1: tentar resolver por um `IDataTypeCatalog` **opcional** (`sp.GetService<>`, nullable — padrão de dependência opcional do repo); se não houver fonte, `datatypes` vazio, `dataType.name=null` e `display.text` usa `?`. Bloqueio a confirmar com o dono (§9-1) — não inventar nomes.
8. **`MapperVoReader`:** `MapperGuid`, `Name`, `InputLayoutGuid/TargetLayoutGuid`; `LinkMappings/LinkMappingItem` → Link (ver §2.3, nome enganoso dos GUIDs); `Rules/Rule` → Rule (`TargetElementGuid`, `Name`, `Code`, `IsPrePosRule` se existir). Preencher `variantFields` com a lista de elementos **opcionais presentes** (conjunto fechado: `FullXPath, ElementType, ParentElement, UniqueOccurrence, CreateValuesDirectBySourceElement, IsToUseDecimalMapper, ElementWithoutValue, XPathLinkMappings, StaticValueMappings, IsXPathMapper`).
9. **`RuleCodeScanner`:** tokenizador simples (não interpretar C#). Extrai `I.<path>`, `T.<path>`, nomes de função (identificador seguido de `(`), marca `opaque` conforme §2.4 com `span`. Lista de funções "com efeito colateral" e catálogo embutido em um único `static readonly HashSet<string>` (fonte: relatório §4/§5; ex.: `MSqlServerHelper`, `OracleHelper`, `GetConfigParametersValue`, `DecryptData`, `LogHelperMapper`). Falha do scanner ⇒ regra com `code` intacto e `opaque:[{reason:"scan-failed"}]`; nunca derruba o request.
10. **`DisplayTextBuilder` + `DiagnosticsBuilder`:** puros, sem I/O, engine-agnósticos (reuso Fases 3-4). Calcular `linked`, `underTargetId`, `iterates`, diagnósticos do §2.5.
11. **`StudioModelHasher`** (§5): hash por fonte + `artifact.rawHash` + `eTag`.
12. **Adapter + Service + Controller:**
    - `StudioModelController` com `[ApiController]`, `[Route("api/workspaces/{workspaceId:guid}/mappings/{mapperGuid}")]`, `[HttpGet("studio-model")]`, `[RequireWorkspaceRole(...todos os papéis...)]`, query `engine` (default `sysmiddle`). Try/catch ao redor do service ⇒ 503 `{error}` para `StudioModelUnavailableException` e para qualquer exceção inesperada (`LogError` sem conteúdo). Sem lógica de negócio no controller.
    - Colocar o parâmetro de rota como `mapperGuid` (o `layout-tree` usa `mappingId`; é só nome, mesma rota-base — não há conflito de template pois os sufixos diferem).
13. **DI** (§3.3) + teste de composição.
14. **Swagger/XML docs:** `<summary>` PT-BR no controller e nos DTOs; delegar README/guia a `@lp-doc` (Duda) depois de pronto (contrato novo).
15. **Testes** do §7 (itens 1-10 na Fase 1). Quality gates: `dotnet build` sem erro e `dotnet test` verde antes de concluir. Quinn valida; contrato final conferido com o portal (`layoutparser-portal`) antes de congelar `schemaVersion: 1`.
16. **Entrega ao portal:** as 3 respostas às perguntas abertas do pedido (§9-2..4) devem ser repassadas por @lp-pm/@lp-doc.

**Fora da Fase 1:** PATCH, `autoLink`, TCL/XSLT, `ast` de regra, cache Redis do modelo montado (só considerar se a medição do item 11 do §7 reprovar; Redis seria cache, SQL segue fonte da verdade, com invalidação por `rawHash`).

## 9. Riscos e perguntas em aberto

**Perguntas (dono / produto):**
1. **Catálogo `DataTypeVO`** (GUID `DAT_` → `Str_MAX`): onde vive hoje na nossa API/SQL? Sem ele `display.text` de campo/tag não fica idêntico ao desktop. Se estiver no mesmo banco do mapper (somente leitura), criar `IDataTypeCatalog` de leitura.
2. **Respostas ao portal (pedido §"Perguntas em aberto"):** (a) `display.text` = calculado na API (decidido); (b) auto-mapeamento: critério NÃO CONFIRMADO (provável igualdade de nome) — depende de experimento no desktop (relatório §9-2); `ops:autoLink` adiado; (c) `I.`/`T.` homônimos: API só diagnostica `AMBIGUOUS_NAME_PATH` e, na escrita, recusa `rename`/`setRule` que dependam de caminho ambíguo (422 `AMBIGUOUS_PATH`) — como o desktop resolve é NÃO CONFIRMADO; (d) escrita entra no fluxo de rascunho/release com justificativa+auditoria (decidido).
3. Sentido do `+` (`IsRequired=true`) no `display.text`: NÃO CONFIRMADO (só `-` validado 456/456). Risco baixo: usar `+` e corrigir depois; `schemaVersion` permite ajustar.
4. Formato de `display.text` sem min/max: confirmar com capturas de tela do desktop (relatório §9-1).
5. Semântica de `IsPrePosRule`, `UniqueOccurrence`, `CreateValuesDirectBySourceElement`, `IsToUseDecimalMapper`: só lidos, não editáveis (relatório §9-6).
6. `xsi:type` exatos de `RepeaterGroup/Grouper/JsonObject/CharacterIgnoreGroup/GroupWithoutOrder` nunca vistos em amostra; o mapeamento deve ser confirmado contra os layouts reais **pelo dono, localmente**, e só resultado agregado volta ao repo.
7. `GetAllMappersAsync` sem filtro por `ProjectId`/pacote: um membro de workspace lê qualquer `mapperGuid` do catálogo compartilhado? Hoje igual ao `layout-tree` (mesma exposição); decidir se `studio-model` deve escopar por pacote do workspace antes de abrir a escrita.

**Riscos:**
- **Vazamento de dado de cliente:** `code` de regra e valores estáticos podem conter dado sensível; proibido logar, proibido em fixture; resposta só para membros do workspace.
- **Tamanho do payload:** ~11k campos + ~15k links por resposta (escala real) ⇒ JSON grande; mitigar com `ETag/If-None-Match` e compressão de resposta (verificar se já habilitada no pipeline). Medir antes de otimizar.
- **Deriva de esquema sem versão:** parser tolerante é obrigatório; o maior risco é ordem de `Sequence` e elementos ausentes — coberto pelos testes de tolerância (§7-2).
- **Dois parsers do mesmo LayoutVO** (`GuidXPathCatalog` legado vs `LayoutVoReader`): risco de divergência de `path`. Mitigação: teste de paridade (§7-10) e, depois da Fase 1, avaliar migrar o `layout-tree` para o novo reader (fora de escopo agora).
- **Segredo versionado (achado da análise, §6 do relatório):** chave/IV Rijndael literal em `layoutparser-decrypt` (e na Lib arquivada) — encaminhar a `@lp-devops`. Não impacta o desenho, mas o `studio-model` depende da decifragem já feita no catálogo (nenhuma nova decifragem é introduzida aqui).
- **Escrita (Fases 2+):** patch in-place de XML sem esquema pode corromper artefato de produção; exigir teste de round-trip (patch nulo ⇒ byte-idêntico) e dry-run por padrão. `172.31.249.51` permanece SOMENTE LEITURA: qualquer tabela nova vai em `IdentityDatabase:*`.
- **Sequenciamento:** a Fase 1 não depende de #90/#173; a Fase 2 depende do desenho da revisão de artefato + release (alinhar com `MappingDraftsController`/`SqlMappingReleaseStore` antes de codificar).

## 10. Resumo executável (para colar no handoff)

```yaml
handoff:
  from_agent: lp-architect
  to_agent: lp-backend-dev
  tarefa: "Fase 1 do studio-model: GET .../mappings/{mapperGuid}/studio-model?engine=sysmiddle"
  branch: develop
  decisoes:
    - "Contrato único v1 (schemaVersion=1) em docs/architecture/studio-model-design.md §2"
    - "Parser próprio (LayoutVoReader/MapperVoReader), tolerante a ordem/presença; não reutilizar BuildTree nem GenerateTclAndXsl.cs"
    - "eTag/rawHash derivados do conteúdo (SHA-256); SQL 172.31.249.51 somente leitura"
    - "404 sem identidade/sem membership/inexistente; 503 {error} catálogo fora; 501 para tcl/xslt; layout ausente degrada (200)"
    - "Escrita só Fases 2+, via rascunho/release em IdentityDatabase, nunca direto em tbMapper"
  bloqueios: ["Catálogo DataTypeVO (DAT_→nome) sem fonte localizada — §9-1"]
  proximo_passo: "Executar §8 (passos 1-15); depois @lp-qa e @lp-doc"
```
