# Design — Slice 7: Governança/Publicação + Piloto FIAT (issue #94, seções 12/14)

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

> Autora: `@lp-architect` (Aria) · 2026-09-01 · Só design, sem código. Último slice da
> fundação. Depende de Slice 5 (`MappingRelease` em `draft_compiled`/`test_passed`/`test_failed`)
> e Slice 6 (gate Sysmiddle já confirmado não-vetor).

## 1. Máquina de estados de `MappingRelease` (estende Slice 5)

```
draft_compiled → test_passed ─┬→ InReview → Approved → Published → Deprecated → Archived
                 test_failed ─┘                              ↑
                                                          rollback (nova transição)
```

- `test_failed` bloqueia entrada em `InReview` (gate obrigatório, campo `RequiredGatesPassed`
  já contratado no Slice 5).
- `Approved`/`Published` são estados **novos** deste slice, adicionados ao enum existente —
  não reabrir o enum do Slice 5, só estender.
- Imutabilidade: `Published` congela `Artifacts[]`; qualquer edição manual de TCL/XSL/XSLT
  cria um novo `MappingRelease` (nova revisão), nunca muta o publicado — reaproveita o
  `DraftId`/hash de snapshot do Slice 5 para detectar "é revisão nova ou é o mesmo compile".
- Nova revisão exige regressão: `Approve`/`Publish` de uma revisão N+1 exige um `test-runs`
  novo com `RequiredGatesPassed=true` — não herda o resultado da N.

Novos campos em `MappingRelease`:
```
Environment: development | validation | production   // onde está ativo, não onde foi testado
ApprovedByUserId / ApprovedAt / ApprovalJustification
PublishedByUserId / PublishedAt
PreviousPublishedReleaseId   // snapshot no momento do publish, ver §3
```
`MappingTransition` (nova tabela, não reaproveitar log genérico): `ReleaseId, FromStatus,
ToStatus, ActorUserId, OccurredAt, Justification, ChecksSnapshot` — obrigatório por transição,
cobre "ator/instante/checks/justificativa" da spec.

## 2. RBAC mínimo (escopo deste slice, não RBAC genérico)

Hoje: zero `[Authorize]` em qualquer controller; `ICurrentUser` já resolve identidade via BFF.
Decisão: introduzir `[Authorize]` **só** nos 3 endpoints novos abaixo, papel checado contra
`WorkspaceMembership.Role` do workspace do release (não claim global). `Reviewer`/`FiscalAdmin`
aprovam; só `FiscalAdmin`/`Owner` publicam e revertem (rollback tem blast radius de produção).
Isso é o mínimo pra spec §12 ("aprovação e promoção exigem RBAC") sem abrir a issue #94 inteira
(CRUD de mapeador por `admin`) — a policy nasce escopada a `MappingRelease`, reaproveitável
depois quando #94 for implementada, não o contrário.

## 3. Rollback

Promove a versão anterior **publicada** (não a imediatamente anterior por número) —
`PreviousPublishedReleaseId`, gravado no momento do `publish` como snapshot de "o que estava
`Published` antes desta transição", evita depender de reconstruir isso a partir do histórico de
`MappingTransition` em runtime (mais simples, mais barato, auditável do mesmo jeito porque a
transição de rollback também grava `MappingTransition`). Rollback: `Published` atual →
`Deprecated`; `PreviousPublishedReleaseId` → `Published`. Endpoint idempotente (rollback duas
vezes seguidas é no-op, não encadeia).

## 4. Endpoints novos

```
POST /api/workspaces/{workspaceId}/mapping-releases/{releaseId}/approve   [Reviewer|FiscalAdmin]
  Body: { justification }. Exige status test_passed. → InReview→Approved.

POST /api/workspaces/{workspaceId}/mapping-releases/{releaseId}/publish   [FiscalAdmin|Owner]
  Exige Approved + Environment target. → Published; grava PreviousPublishedReleaseId.

POST /api/workspaces/{workspaceId}/mapping-releases/{releaseId}/rollback  [FiscalAdmin|Owner]
  Sem body. Idempotente conforme §3.
```
Padrão ETag/idempotência do Slice 3, ` MappingEngineGuardFilter` não se aplica (sem campo
`engine` mutável nestes contratos).

## 5. Teste ponta a ponta FIAT

Não automatizar o pipeline inteiro num único teste caro. Dividir:
- **Integração automatizada (CI):** um teste por transição do gate FIAT (§14) usando fixture
  sintética/sanitizada (não os artefatos reais do React `.codex/temp/teste` — nunca sobem ao
  Git). Cobre inventário→draft→compile→test-run→approve→publish com asserts de estado, não de
  conteúdo fiscal real.
- **Runbook manual documentado** (não código): execução única com os artefatos FIAT reais,
  local, evidenciando XML válido contra XSD real e diff contra gabarito quando este chegar
  (spec §14 lista Excel/XSD/gabarito como "ainda faltam" — bloqueio externo, não deste slice).
  Resultado vira anexo de auditoria, não teste repetível em CI.

## Primeiro passo executável

`@lp-backend-dev`: estender enum de `MappingRelease.Status` + criar `MappingTransition` +
os 3 endpoints com `[Authorize]` por `WorkspaceMembership.Role`. `@lp-qa` desenha as fixtures
sintéticas do gate FIAT em paralelo.

## Implementação — governança (2026-09-01)

`@lp-backend-dev` (Dex). Escopo: governança + RBAC mínimo (não a #94 inteira). Teste FIAT ponta a
ponta fica para `@lp-qa` (próxima etapa).

- **`MappingReleaseStatus`** (`Models/Entities/Fiscal/MappingRelease.cs`) estendido com
  `InReview`/`Approved`/`Published`/`Deprecated`/`Archived`, sem reabrir os 3 valores do Slice 5.
  `MappingRelease` ganhou `Environment`, `ApprovedByUserId`/`ApprovedAt`/`ApprovalJustification`,
  `PublishedByUserId`/`PublishedAt`, `PreviousPublishedReleaseId`.
- **`MappingTransition`** (mesmo arquivo): entidade nova, tabela própria `dbo.tbMappingTransition`
  — não reaproveita nenhum log genérico. `Justification` é obrigatório em `approve` (validado no
  controller); `publish`/`rollback` gravam justificativa própria gerada pela store.
- **`IMappingReleaseStore`/`SqlMappingReleaseStore`**: `ApproveAsync`/`PublishAsync`/`RollbackAsync`.
  `Approve` faz `test_passed → in_review → approved` como UMA transação SQL (duas linhas em
  `tbMappingTransition`), lançando `InvalidOperationException` se o status atual não for
  `test_passed` — cobre `test_failed` e qualquer outro estado. `Publish` exige `approved`, busca
  (na mesma transação, com `UPDLOCK`/`ROWLOCK`) a release hoje `published` do mesmo `DraftId` no
  workspace, rebaixa-a a `deprecated` e grava `PreviousPublishedReleaseId` na nova. `Rollback` é
  no-op (sem gravar transição nova) se a release apontada já não estiver `published` — cobre o
  "duas vezes seguidas" do design §3. Schema: colunas novas adicionadas via `ALTER TABLE ... IF
  COL_LENGTH(...) IS NULL` (idempotente, mesmo padrão de `IF OBJECT_ID(...) IS NULL` já usado nas
  outras stores; tabela `tbMappingRelease` já existia de bases do Slice 5).
- **RBAC mínimo**: `Services/Filters/RequireWorkspaceRoleFilter.cs` — `[RequireWorkspaceRole(...)]`
  (`TypeFilterAttribute`) resolve `ICurrentUser.UserId` + `IIdentityWorkspaceStore.
  GetWorkspaceIfMemberAsync` (reaproveitado do Slice 1, já devolve o `Role`). Sem `UserId`/sem
  membership → 404 (mesmo padrão fail-closed dos slices anteriores). Membro mas papel fora da
  allowlist → 403. Não usa `[Authorize]` do ASP.NET Core porque a identidade não vem de um
  `ClaimsPrincipal` autenticado pela API (vem do BFF via `ICurrentUser`, ver `security.md`) — o
  filtro escopado é o mecanismo real, reutilizável em qualquer controller com `{workspaceId:guid}`
  na rota.
- **`Controllers/MappingGovernanceController.cs`**: `POST .../approve` (`Reviewer`|`FiscalAdmin`),
  `.../publish` (`FiscalAdmin`|`Owner`), `.../rollback` (`FiscalAdmin`|`Owner`), rota
  `api/workspaces/{workspaceId:guid}/mapping-releases/{releaseId:guid}`. Isolamento cross-workspace
  via `GetReleaseIfMemberAsync` + checagem `release.WorkspaceId != workspaceId` (mesmo padrão dos
  Slices 1-5) — 404, não 403, quando o release não é do workspace da rota.
- **Testes** (`tests/.../Controllers/MappingGovernanceControllerTests.cs`, 11 novos): bloqueio
  `test_failed`→`in_review`, aprovação bem-sucedida com as 2 transições registradas
  (ator/instante/justificativa), publish recusado sem `approved`, nova revisão do mesmo `DraftId`
  não herda gate da anterior, rollback idempotente (2ª chamada não gera transição nova nem
  quebra), isolamento cross-workspace (404), e RBAC via o filtro real (403 papel insuficiente, 200
  papel suficiente, 404 sem membership) — não bypassado, o teste invoca
  `RequireWorkspaceRoleFilter.OnActionExecutionAsync` diretamente.
- **Build**: `dotnet build` verde. **Testes**: 529/529 (11 novos + 518 pré-existentes, todos
  verdes).
- **Fora do escopo, de propósito**: teste FIAT ponta a ponta com fixtures reais (próxima etapa,
  `@lp-qa`); RBAC genérico pra outros endpoints da API.

## Gate FIAT sintético — 2026-09-01

`@lp-qa` (Quinn). `tests/LayoutParserApi.Tests/Integration/FiatPilotGateTests.cs`, 1 teste
(`Gate_FIAT_ponta_a_ponta_conecta_os_6_slices_com_fixture_sintetica`) cobrindo os 13 itens do
checklist §14 em sequência, sobre fixture 100% sintética (nunca documento fiscal real — proibido
publicar isso no repositório).

**Veredito: o fluxo ponta a ponta CONECTA os 6 slices sem gap estrutural.** Lógica de negócio real
de cada slice roda de verdade dentro do teste — não é só stub encadeado:

- Slice 2: `FiscalPackageService` real grava artefatos, calcula sha256 determinístico (`FakeFiscalPackageStore`
  substitui só o SQL; `NoOpAntivirusScanner` reflete o comportamento real de indisponibilidade, não
  finge "sempre limpo").
- Slice 3: proposta de IA simulada (Ollama nunca chamado — fixture sintética entra direto via
  `InsertProposedRulesAsync`, como o job real deixaria); uma regra clara + uma `needs_input` com
  pergunta aberta; `MappingDraftsController.UpdateRule` real aplica ETag/If-Match e aceita só a
  regra clara — a ambígua permanece pendente.
- Slice 5: `MappingCompileService` + `MappingDraftRuleTranspiler` reais geram XSLT determinístico;
  só a regra `accepted` aparece no artefato (RuleId da `needs_input` não vaza). `MappingTestRunService`
  real aplica o XSLT via `XsltApplier`, compara com `CanonicalDiffer` contra dois gabaritos (bate/diverge)
  — a divergência carrega `RuleId`/`SourceRefs` de volta até a regra de origem (provenance).
- Slice 4: `XsltExplanationAdapter.ExplainXsltDocument` (parser real, sem LLM) explica o artefato
  compilado no passo anterior — não um artefato fabricado à parte.
- Slice 7: `MappingGovernanceController` real com o MESMO dublê de `IMappingReleaseStore` usado em
  `MappingGovernanceControllerTests` (reproduz a regra de negócio do `SqlMappingReleaseStore`, não
  um fake que sempre aceita) — confirma que `test_failed` bloqueia `approve`, que `publish` exige
  `approved`, e que a cadeia `release.CorrelationId` → `MappingTransition` permanece rastreável do
  compile até o publish.
- Slice 6: `MappingEngineGuardFilter` real recusa `engine=sysmiddle` antes de qualquer controller
  rodar — verificado invocando o filtro diretamente na pipeline simulada (`next()` nunca é chamado).

**Achado à parte (não é gap do gate, é economia deliberada de teste):** o teste usa dois releases
"paralelos" (mesmo `ReleaseId`, estado trocado manualmente entre a chamada com gabarito batendo e
com gabarito divergente) para exercitar os dois caminhos do Fiscal Test Lab sem duplicar toda a
fixture — comportamento correto do serviço real (`ApplyTestRunResultAsync` sobrescreve o estado),
não uma simplificação que esconderia um bug.

**Pendência explícita, fora do alcance de qualquer agente:** o runbook manual com dado real (Excel/
XSD/gabarito FIAT reais, ver spec §14 "ainda faltam") continua bloqueado — só o dono tem esses
artefatos. Este teste sintético prova que o ENCANAMENTO entre os 6 slices funciona; não prova nada
sobre a qualidade da IA real (Ollama) nem sobre o caso FIAT específico (NF-e 4.00 completo). Quando
os artefatos reais chegarem, o runbook do design §5 (execução manual local, evidência de auditoria,
não repetível em CI) é o próximo passo — não este teste.

**Build/test**: `dotnet build` verde (só o warning pré-existente de `LayoutParserLib` não resolvido,
não relacionado). `dotnet test`: 530/530 verdes (529 pré-existentes + 1 novo), sem regressão.
