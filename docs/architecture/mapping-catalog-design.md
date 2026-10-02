# Catálogo unificado de mapeadores/transformações (TCL / XSL / XSLT) — Desenho

Issue #626 · Pedido do portal (Hub thread 389c34bc) · Autora: Aria (`@lp-architect`) · 2026-10-02
Status: **proposta para execução** (nenhum código de produção neste commit).

## 1. Problema e requisitos

O Mapping Studio precisa de uma árvore única de mapeadores, entregue "mastigada" pela API
(portal não toca banco). Hoje há três fontes sem identidade comum:

| Fonte | Onde | Chave | Problema |
|-------|------|-------|----------|
| ConnectUs (SQL `172.31.249.51`) | `tbProject > tbFolder > tbPackage > tbMapper` | `(MapperGuid, ProjectId)`; `tbMapper.ProjectId` nullable | `mapperGuid` **não é único global**; banco **somente leitura** |
| Auto-gerados | `tbGeneratedMapperArtifact` (IdentityDatabase) | PK só `MapperGuid` | colide com a chave composta acima; hoje exposto via `GET mapping-releases` (`origin: auto_generated`) |
| Neogrid | `ReferenceExampleCatalogService`, 159 pares TCL/XSL em `ReferenceExamples:BasePath` (`tcl|xsl/{DocType}/{Versao}/{Arquivo}`) | `id` do serviço (derivado de arquivo) | só leitura de disco; sem projeto/pasta formais |

Requisitos do dono: (1) API entrega árvore pronta; (2) 3 níveis: origem > projeto/pasta > mapeador (engine
`tcl|xsl|xslt`); (3) ConnectUs e Map4Connect serão abandonados — remover adaptador sem quebrar contrato;
(4) o item de catálogo é referência única para o portal **e** para o parser de TXT; (5) 172.31.249.51
é read-only, dado próprio em `IdentityDatabase`; (6) aditivo, paginado por pasta, ordenação alfabética estável.

## 2. Decisões

### D1. Persistir (índice em tabela própria) vs montar sob demanda — **Decisão: persistir um índice de metadados em `IdentityDatabase`, sem copiar conteúdo das fontes externas**

| | A) Sob demanda | B) Tabela própria (escolhida) |
|---|---|---|
| Latência | cada request varre SQL remoto + disco; pasta de 159 pares e JOINs 4 níveis | consulta indexada local, paginação e ordenação em SQL |
| Resiliência | fonte fora => árvore vazia/erro | fonte fora => serve último snapshot, marca `stale` |
| Id estável | precisa ser recomputado a cada leitura | persistido, imutável |
| Remoção de adaptador | itens somem imediatamente | itens ficam `retired` (preserva referência do parser) |
| Custo | zero storage | sync + migração DDL |

Justificativa: o requisito (3) e a resiliência (princípio do projeto) decidem. Persistimos **só metadados +
ponteiro** (`SourceRef`), nunca o corpo TCL/XSL (corpo continua buscado na fonte ou no
`tbGeneratedMapperArtifact`) — evita duplicar fonte da verdade e vazar conteúdo. Para artefatos próprios,
o corpo já vive em IdentityDatabase. Redis opcional pode cachear a árvore de nível 1/2 (cache, nunca primário).

### D2. Id estável de catálogo — **`catalogId` = GUID v5 (determinístico) sobre `"{sourceSystem}|{sourceProjectKey}|{sourceItemKey}"`**

- `sourceItemKey`: ConnectUs/Map4Connect = `MapperGuid`; Neogrid = caminho relativo normalizado
  (`{docType}/{versao}/{baseName}`); próprio = `MapperGuid` do artefato.
- `sourceProjectKey`: `tbProject.IdentifierGuid` (ou `"-"` se `ProjectId` nulo); Neogrid = `docType`.
- Determinístico => idempotência do sync sem tabela de-para, mesmo id em re-import/rebuild. GUID (não int
  identity) para não vazar ordem e para o portal tratá-lo como opaco.
- `mapperGuid` fica no payload apenas como campo informativo (`sourceItemKey`), **nunca** como chave de rota.
  Colisão resolvida: o namespace inclui `sourceSystem` + projeto.
- O `tbGeneratedMapperArtifact` (PK só `MapperGuid`) ganha mapeamento 1:1 via `OwnArtifact` (origem própria):
  não alterar a PK agora; o catálogo guarda `SourceItemKey=MapperGuid`. Se um mesmo MapperGuid existir em dois
  projetos, o artefato gerado hoje já é ambíguo — registrar como risco R3 e adicionar `ProjectId` ao artefato na Fase 4.

### D3. Adaptadores por origem — **`IMappingCatalogSource` (um por `SourceSystem`), registrado por DI**

```
interface IMappingCatalogSource {
  SourceSystem System { get; }              // ConnectUs | Neogrid | Map4Connect | Own
  Task<SourceSnapshot> ReadAsync(CancellationToken ct);   // projetos/pastas + itens (metadado)
  Task<MappingContent?> GetContentAsync(SourceRef r, CancellationToken ct);
}
```

- `ConnectUsCatalogSource` (SQL read-only, `Database:*`, **somente SELECT**), `Map4ConnectCatalogSource`
  (mesma mecânica; a ser confirmada a fonte), `NeogridFileCatalogSource` (envolve `ReferenceExampleCatalogService`,
  sem reescrevê-lo), `OwnArtifactCatalogSource` (`tbGeneratedMapperArtifact` + futuros).
- Feature flag por origem: `MappingCatalog:Sources:ConnectUs:Enabled`. Remover ConnectUs = desligar flag, depois
  apagar a classe; itens existentes passam a `Retired` (continuam resolvíveis por `catalogId`).
- Contrato nunca expõe nada específico de adaptador (sem nome de tabela). `sourceSystem` é enum extensível;
  o portal trata valor desconhecido com rótulo genérico.

### D4. Modelo de dados (IdentityDatabase; DDL somente em `IdentityDatabase:*`)

```
tbMappingCatalogSource   (SourceSystem PK, Enabled, LastSyncUtc, LastStatus, LastError)
tbMappingCatalogFolder   (FolderId GUID PK, SourceSystem, SourceProjectKey NULL?, SourceProjectId BIGINT NULL,
                          Name, NameSort, ParentKind, Retired BIT, UNIQUE(SourceSystem, SourceProjectKey))
tbMappingCatalogItem     (CatalogId GUID PK, FolderId FK, SourceSystem, SourceItemKey, Engine, Name, NameSort,
                          Version NULL, DocType NULL, ContentHash NULL, SourceRefJson, Retired BIT,
                          LastSeenUtc, UNIQUE(SourceSystem, FolderId, SourceItemKey))
  índices: (FolderId, NameSort, CatalogId) para paginação keyset/offset estável
```

- `projectId` nulo => pasta sintética **"Sem projeto"** por origem (`SourceProjectKey = "-"`, nome fixo).
- Ordenação: `NameSort` (nome normalizado, case/acento-insensível) + `CatalogId` como desempate => ordem total estável.
- Parser de TXT: a coluna `Engine` e `CatalogId` são a referência única; o parser resolve conteúdo por
  `GetContentAsync` pelo mesmo `catalogId` (nada de segundo registro de TCL).

### D5. Endpoints e JSON (aditivos; prefixo `api/mapping-catalog`)

| Método | Rota | Uso |
|--------|------|-----|
| GET | `/api/mapping-catalog/sources` | nível 1 com contagens, `lastSyncUtc`, `status` (`ok|stale|unavailable`) |
| GET | `/api/mapping-catalog/sources/{sourceSystem}/folders` | nível 2 (pastas), alfabético, paginado |
| GET | `/api/mapping-catalog/folders/{folderId}/items?page=&pageSize=&engine=&q=` | nível 3 paginado **por pasta** |
| GET | `/api/mapping-catalog/items/{catalogId}` | detalhe (+ `detailUrl`) |
| GET | `/api/mapping-catalog/items/{catalogId}/content` | corpo TCL/XSL/XSLT (proxy da fonte; 200/404/503) |
| GET | `/api/mapping-catalog/tree?depth=2` | atalho opcional: níveis 1+2 sem itens (itens sempre por pasta) |
| POST | `/api/mapping-catalog/sync` (admin/interno) | dispara sync (ver D7) |

`pageSize` default 50, máx 200 (padrão dos demais endpoints). Item:

```json
{
  "catalogId": "5b0c…",
  "sourceSystem": "neogrid",
  "engine": "tcl",
  "name": "NFe_Entrada",
  "folderId": "a1…",
  "projectId": null,
  "projectName": "Sem projeto",
  "sourceItemKey": "nfe/4.00/NFe_Entrada",
  "version": "4.00",
  "retired": false,
  "detailUrl": "/api/mapping-catalog/items/5b0c…",
  "pairedCatalogId": "9f2…"
}
```

`sourceSystem`: `connect_us | neogrid | map4connect | own` (snake_case em JSON, enum no C# com
`JsonStringEnumConverter`). `pairedCatalogId` liga o par TCL<->XSL Neogrid (hoje implícito no `baseName`).
Resposta de lista: `{ items[], page, pageSize, totalCount, sourceStatus }`.

### D6. Desambiguar `detailUrl` e migrar `GET mapping-releases`

- `detailUrl` **sempre** por `catalogId` (nunca por `mapperGuid`) — único e opaco.
- `mapping-releases` permanece **inalterado** (campos atuais). Migração aditiva: itens `auto_generated`
  ganham os campos novos `catalogId` e `catalogDetailUrl` (omitidos se o índice ainda não tiver o item).
  `mapperGuid` segue presente por compat; o portal migra para `catalogId` e depois se marca `mapperGuid`
  como `deprecated` no Swagger. Remoção só em versão futura, com aviso. Rotas `reference-examples` ficam como estão
  (delegam ao mesmo serviço) e marcadas como superadas pelo catálogo.
- Chamada antiga `.../auto-generated/{mapperGuid}` (GeneratedMapperArtifactController): manter; se o GUID
  casar com >1 item no catálogo, devolver 409 com `candidates[catalogId]` em vez de escolher um (nunca adivinhar).

### D7. Sincronização e idempotência

- Sync por origem em `BackgroundService` (intervalo configurável, default 6h; Neogrid por mudança de
  `ContentHash`/mtime) + gatilho manual. Trava por origem (`sp_getapplock`) para execução única.
- **Upsert por `CatalogId`** (MERGE): insere novo, atualiza metadados se mudou, atualiza `LastSeenUtc`. Item não
  visto num sync **completo e bem-sucedido** => `Retired=1` (nunca DELETE). Sync parcial/falho **não retira nada**.
- Leitura ConnectUs: apenas `SELECT ... WITH (NOLOCK)` com `CommandTimeout` curto; **nenhuma escrita**
  no 172.31.249.51 (security.md). Escrita só em `IdentityDatabase`.
- Bootstrap: DDL via `EnsureSchema` (padrão do store existente) apontando `IdentityDatabase:*`.

### D8. Resiliência

- Cada adaptador isolado em try/catch + timeout + circuit-breaker simples; falha => `sourceStatus: "stale"|"unavailable"`
  na resposta e `LastError` em `tbMappingCatalogSource`; os demais continuam respondendo.
- Leitura de árvore usa só o índice local => funciona com ConnectUs offline. `/content` é a única chamada que toca a
  fonte; falha vira 503 com mensagem clara, sem derrubar o request.
- IdentityDatabase fora: 503 do catálogo apenas (outros endpoints intactos). Logging estruturado, sem conteúdo de documento.

### D9. Segurança

Somente leitura no banco compartilhado; `content` nunca loga corpo; autorização segue o modelo atual (identidade do BFF, sem
`[Authorize]` ainda — o catálogo herda a decisão de produto em aberto). Sync POST restrito (loopback/admin).

## 3. Fases de entrega

| Fase | Entrega | Critério de aceite |
|------|---------|--------------------|
| 1 | Contratos (enum, DTOs), `IMappingCatalogSource`, DDL + store, geração de `catalogId` (v5) com testes de determinismo | build + testes; sem endpoint |
| 2 | Adaptadores Neogrid e Own + sync + endpoints `sources/folders/items/content` | portal navega 3 níveis só com Neogrid/próprio |
| 3 | Adaptador ConnectUs (read-only) + pasta "Sem projeto" + colisão de `mapperGuid` coberta por teste | dois projetos com mesmo MapperGuid => 2 `catalogId` |
| 4 | Campos aditivos em `mapping-releases`; `ProjectId` em `tbGeneratedMapperArtifact` (ou chave composta); parser consome `catalogId` | compat comprovada por teste de contrato |
| 5 | Map4Connect (se a fonte for confirmada); runbook de desligar adaptador (`Retired`) | desligar flag mantém `catalogId` resolvível |

## 4. Riscos

- **R1** Fonte do Map4Connect indefinida: Fase 5 bloqueada até o dono informar a origem.
- **R2** Sync lento do ConnectUs (JOIN 4 níveis em banco compartilhado): paginar leitura, janela fora do horário, `NOLOCK`, limites.
- **R3** `tbGeneratedMapperArtifact` com PK só `MapperGuid`: ambiguidade se mapperGuid repetir entre projetos (hoje latente).
- **R4** Neogrid: `id` derivado de arquivo muda se renomearem pastas => `catalogId` muda; mitigado usando chave por conteúdo/par
  (`docType/versao/baseName`) e alias em `Retired`, mas renomeação segue gerando item novo.
- **R5** Parser e portal dependendo do mesmo item: mudança de `Engine`/conteúdo afeta parse; versionar por `ContentHash`.
- **R6** Escrita acidental no 172.31.249.51 por config errada: adaptador usa conexão `ReadOnly` + `ApplicationIntent=ReadOnly` e teste que garante só SELECT.
- **R7** Segurança de acesso ao catálogo sem `[Authorize]` (decisão de produto em aberto).

## 5. Stories sugeridas (para `@lp-pm`)

1. Contratos do catálogo: enum `SourceSystem`, DTOs, `catalogId` v5 + testes de determinismo (Dex).
2. DDL `tbMappingCatalog*` em `IdentityDatabase` + store com upsert/retire (Dex).
3. `IMappingCatalogSource` + adaptador Neogrid sobre `ReferenceExampleCatalogService` (par TCL/XSL) (Dex/Lia).
4. Adaptador `Own` (`tbGeneratedMapperArtifact`) (Dex).
5. Endpoints `sources/folders/items/content` com paginação por pasta e ordenação estável (Dex).
6. Sync em background + lock + gatilho manual + estados `stale/unavailable` (Dex).
7. Adaptador ConnectUs read-only + pasta "Sem projeto" + teste de colisão de `mapperGuid` (Dex/Quinn).
8. Campos aditivos `catalogId/catalogDetailUrl` em `mapping-releases`; 409 em ambiguidade (Dex).
9. Adicionar `ProjectId` a `tbGeneratedMapperArtifact` (migração) (Dex).
10. Parser de TXT resolve TCL por `catalogId` (Lia).
11. Adaptador Map4Connect — depende de definição da fonte (dono).
12. Runbook/teste "desligar adaptador sem quebrar contrato" (Quinn/Duda).
13. Documentação Swagger/README do catálogo e deprecação de `mapperGuid` (Duda).
