# RBAC de workspace — levantamento do estado atual (2026-09-30)

Levantamento SOMENTE LEITURA, em resposta ao pedido do portal (thread hub `abce4a62…`, "RBAC de
workspace: o dono definiu 4 papeis"). Nenhum código alterado; nenhuma consulta ao SQL `172.31.249.51`.
Autor: Aria (`@lp-architect`). Paths relativos à raiz do repo `layoutparser-api`.

## 1. Como o RBAC funciona hoje

### 1.1 Os 6 papéis
Definidos como strings em `Models/Entities/Identity/WorkspaceMembership.cs:9-17`:
`owner`, `fiscal_admin`, `mapper`, `reviewer`, `operator`, `viewer`. Coluna `Role NVARCHAR(32) NOT NULL`
em `tbLpWorkspaceMembership` e `tbLpWorkspaceInvite` (`Services/Database/SqlIdentityWorkspaceStore.cs:298,316`);
**não há CHECK constraint** no DDL lazy (grep negativo) — a validação é só na aplicação.

### 1.2 Hierarquia / equivalências
**Não existe hierarquia nem equivalência.** O filtro faz pertinência literal em lista
(`Services/Filters/RequireWorkspaceRoleFilter.cs:72`: `_allowedRoles.Contains(workspace.Role, OrdinalIgnoreCase)`).
Cada atributo lista os papéis explicitamente. Na prática:
- "leitura" = lista de 6 papéis (`Owner, FiscalAdmin, Mapper, Reviewer, Operator, Viewer`);
- "escrita de trabalho" = `Mapper, FiscalAdmin, Owner` (operator e viewer NÃO escrevem; reviewer NÃO escreve aqui);
- exceção: `MappingRuleAnswersController` PUT = `Owner, FiscalAdmin, Mapper, Reviewer`;
- aprovar = `Reviewer, FiscalAdmin` (owner NÃO aprova!); publicar/rollback/deprecar/arquivar = `FiscalAdmin, Owner`.
Hoje **operator == viewer** (só leitura), confirmado.

### 1.3 Onde é avaliado
Um único mecanismo: `[RequireWorkspaceRole(params string[])]` (TypeFilter) → `RequireWorkspaceRoleFilter`
(`RequireWorkspaceRoleFilter.cs:22-86`). Sem `ICurrentUser.UserId` ou sem membership → **404**; membro com papel
fora da lista → **403** `{ error }`. Lê o papel via `IIdentityWorkspaceStore.GetWorkspaceIfMemberAsync`
(1 query SQL por request). Não há `[Authorize(Policy)]` de workspace. Identidade vem do BFF
(`TrustedIdentityMiddleware`, headers sob guarda de loopback).
Outros papéis ortogonais (não de workspace): `[Authorize(Roles="admin"|"operador")]` (roles do IdP/BFF, ex.
`DataGenerationController`, `LogsController`, `MapperDatabaseController`) e `[RequireSudo]` (super-admin por e-mail,
`AdminController`). Não confundir.

### 1.4 Atribuição e validação de membros
`Controllers/WorkspaceMembersController.cs` (`/api/workspaces/{id}/members`, atributo de classe `Owner, FiscalAdmin`, linha 26):
- `AssignableRoles` (linhas 30-34) = `fiscal_admin, mapper, reviewer, operator, viewer`; `owner` **nunca** atribuível pela API
  (POST 79-81 e PATCH 100-102 respondem 400; mensagem de erro lista os 5 ids).
- POST cria membership (se o e-mail já tem usuário) ou convite pendente (`tbLpWorkspaceInvite`) — `SqlWorkspaceMemberStore.cs:59,131,161`.
- PATCH: troca papel de membro ou convite; owner imutável (`:216`); não rebaixa o último admin (`IsAdminRole` = owner|fiscal_admin, `:294-296`, `:222`).
- DELETE: idem, protege owner e último admin (`:265,271`).
- Só workspace `kind=team` aceita membros (`Guarded`, `:149`). Owner é criado pelo sistema no workspace pessoal
  (`SqlIdentityWorkspaceStore.cs:147`) — e promoção de workspace a team é `AdminController` (sudo).
- Nota: hoje o **fiscal_admin pode promover outro a fiscal_admin e mexer em todos exceto owner**; no modelo-alvo isso já coincide com "Administrador gerencia membros".

### 1.5 Migração / dados existentes (descrição, sem consulta)
Membros já gravados têm `Role` ∈ {owner, fiscal_admin, mapper, reviewer, operator, viewer}; convites pendentes idem.
Como o papel é string livre, **nenhuma migração de schema é necessária** para manter os ids. Se "mapper/reviewer → operator"
for feito por equivalência em código (recomendado), também **não há UPDATE de dados**. O único efeito sobre dados existentes
é semântico: quem hoje é `operator` passa a escrever (ver §5). Quantidade de linhas por papel: **desconhecida** (não consultada);
dono/DBA pode contar via a store de membros (leitura) antes de ativar.

## 2. Matriz endpoint x papel exigido hoje

Legenda: **R6** = `Owner, FiscalAdmin, Mapper, Reviewer, Operator, Viewer` (qualquer membro com papel conhecido).
**W3** = `Mapper, FiscalAdmin, Owner`. "Membro" = sem atributo, só membership (404 se não-membro; nenhum 403 por papel).
"Alvo" = papel mínimo proposto no modelo de 4 papéis (L = Leitor+, O = Operador+, A = Administrador+, P = Proprietário).

### 2.1 Endpoints com `{workspaceId}` na rota

| Verbo + rota | Controller (file:line) | Exigido hoje | Alvo proposto |
|---|---|---|---|
| GET `/api/workspaces/me` | WorkspacesController.cs:38 | autenticado (sem workspace) | L (inalterado) |
| GET `/api/workspaces/{id}` | WorkspacesController.cs:77 | Membro (manual) | L |
| GET `/api/workspaces/{id}/members` | WorkspaceMembersController.cs:62 | Owner, FiscalAdmin | A |
| POST `/api/workspaces/{id}/members` | WorkspaceMembersController.cs:72 | Owner, FiscalAdmin | A |
| PATCH `/api/workspaces/{id}/members/{userId}` | WorkspaceMembersController.cs:97 | Owner, FiscalAdmin | A |
| DELETE `/api/workspaces/{id}/members/{userId}` | WorkspaceMembersController.cs:108 | Owner, FiscalAdmin | A |
| GET `/api/workspaces/{id}/analyses` | FiscalAnalysesController.cs:45 (attr classe :20) | R6 | L |
| GET `/api/workspaces/{id}/analyses/{analysisId}` | FiscalAnalysesController.cs:85 | R6 | L |
| GET `.../analyses/{analysisId}/files/{fileId}` | FiscalAnalysesController.cs:137 | R6 | L |
| DELETE `.../analyses/{analysisId}` | FiscalAnalysesController.cs:168 | R6 (**escrita/destrutiva com leitura**) | O (ou A; decisão do dono) |
| GET `.../mappings/{mappingId}/layout-tree` | LayoutTreeController.cs:40 | R6 | L |
| GET `.../mappings/{mapperGuid}/studio-model` | StudioModelController.cs:46 | R6 | L |
| GET `.../mappings/{mappingId}/generated-transformation` | GeneratedMapperArtifactController.cs:68 | R6 | L |
| GET `.../mappings/{mappingId}/versions/{version}/explanation` | MappingExplanationController.cs:46 | Membro (manual) | L |
| POST `.../projects/{projectId}/mapping-packages` | FiscalMappingPackagesController.cs:49 | Membro (manual :65) — escrita sem papel | O |
| GET `.../projects` | FiscalMappingPackagesController.cs:135 | Membro | L |
| POST `.../mapping-packages/{packageId}/revisions` | FiscalMappingPackagesController.cs:183 | Membro — escrita sem papel | O |
| GET `.../mapping-packages/{packageId}/artifacts/{artifactId}/excel-inventory` | FiscalMappingPackagesController.cs:250 | Membro | L |
| GET `.../mapping-packages/{packageId}` | FiscalMappingPackagesController.cs:287 | Membro | L |
| POST `.../mapping-packages/{packageId}/drafts` | MappingDraftsController.cs:121 | Membro (manual :137) — escrita sem papel | O |
| GET `.../mapping-drafts` | MappingDraftsController.cs:89 | R6 | L |
| GET `.../mapping-drafts/{draftId}` | MappingDraftsController.cs:179 | Membro | L |
| PUT `.../mapping-drafts/{draftId}/fiscal-profile` | MappingDraftsController.cs:207 | W3 | O |
| POST `.../mapping-drafts/{draftId}/suggestions` | MappingDraftsController.cs:248 | Membro — escrita (enfileira job IA) | O |
| GET `.../suggestions/{jobId}` | MappingDraftsController.cs:274 | Membro | L |
| DELETE `.../suggestions/{jobId}` | MappingDraftsController.cs:293 | Membro — escrita (cancela job) | O |
| PATCH `.../mapping-drafts/{draftId}/rules/{ruleId}` | MappingDraftsController.cs:324 | Membro — escrita sem papel | O |
| PUT `.../rules/{ruleId}/questions/{idx}/answer` | MappingRuleAnswersController.cs:71 | Owner, FiscalAdmin, Mapper, **Reviewer** | O |
| GET `.../mapping-drafts/{draftId}/question-answers` | MappingRuleAnswersController.cs:124 | R6 | L |
| GET `.../rules/{ruleId}/question-answers` | MappingRuleAnswersController.cs:138 | R6 | L |
| POST `.../mapping-drafts/{draftId}/compile` | MappingCompilationController.cs:73 | Membro — escrita (cria release) | O |
| GET `.../compile/{jobId}` | MappingCompilationController.cs:104 | Membro | L |
| GET `.../releases/{releaseId}` | MappingCompilationController.cs:122 | Membro | L |
| GET `.../releases/diff` | MappingCompilationController.cs:143 | Membro | L |
| PATCH `.../mapping-drafts/{draftId}/artifacts/{engine}` | MappingCompilationController.cs:234 | W3 | O |
| POST `.../mapping-drafts/{draftId}/test-runs` | MappingCompilationController.cs:315 | Membro — **muta a release** (ver §3.2) | O para o modo atual; Leitor só via modo novo "efêmero" |
| GET `.../test-runs/{jobId}` | MappingCompilationController.cs:357 | Membro | L |
| POST `.../test-suites` | TestSuiteController.cs:71 | W3 | O |
| GET `.../test-suites`, `/{suiteId}`, `/{suiteId}/fixtures`, `/{suiteId}/runs` | TestSuiteController.cs:92,109,151,217 | Membro | L |
| POST `.../test-suites/{suiteId}/fixtures` | TestSuiteController.cs:127 | W3 | O |
| POST `.../test-suites/{suiteId}/run` | TestSuiteController.cs:180 | W3 | O (persiste run; ver §3.2) |
| GET `/api/workspaces/{id}/mapping-releases` | MappingGovernanceController.cs:131 | R6 | L |
| POST `.../mapping-releases/{rid}/approve` | MappingGovernanceController.cs:217 | Reviewer, FiscalAdmin | A (ver §3.1) |
| POST `.../publish` | MappingGovernanceController.cs:245 | FiscalAdmin, Owner | A |
| POST `.../rollback` | MappingGovernanceController.cs:272 | FiscalAdmin, Owner | A |
| POST `.../deprecate` | MappingGovernanceController.cs:304 | FiscalAdmin, Owner | A |
| POST `.../archive` | MappingGovernanceController.cs:338 | FiscalAdmin, Owner | A |
| GET/PATCH/DELETE `/api/admin/workspaces...` | AdminController.cs:36,52,82 | `[RequireSudo]` (não é papel de workspace) | fora do escopo |

Total: 49 ações com `{workspaceId}`; **21 têm `[RequireWorkspaceRole]`**, 27 são "só membro" (todas listadas em
`ExcecoesAprovadas`, `tests/.../Security/WorkspaceIsolationReflectionTests.cs:33-67`) e 1 é `AdminController` (sudo).
Escritas "só membro" (candidatas a Operador+): CreatePackage, CreateRevision, CreateDraft, CreateSuggestionJob,
CancelSuggestionJob, UpdateRule, Compile, CreateTestRun, e o DELETE de analysis (R6).

### 2.2 Endpoints SEM workspace na rota (nenhuma verificação de workspace nem de papel de workspace)
Exigem no máximo `[Authorize]` (identidade) ou `[Authorize(Roles="admin|operador")]` do IdP; muitos são anônimos.
Não participam do RBAC de workspace hoje.

| Grupo | Endpoints (verbo) | Observação |
|---|---|---|
| `TransformationExecutionController` (`api/TransformationExecution`) | POST execute (:125), POST execute-legacy (:229), POST validate (:1528), POST learn-from-examples (:1557), POST run-test (:1600) — **sem atributo**; POST execute-candidates (:272), POST field-correction (:487), PUT/GET ai-prompt-adicional, PUT/GET ai-preferences, GET ai-session/history, POST ai-session/history/{t}/resume, POST execute-lowcode (:1661), POST field-mappings (:1712), GET execute-candidates/{t}/ia-status (:1171) — `[Authorize]`; GET field-correction/pending, POST field-correction/{id}/review — `[Authorize(Roles="admin")]` | núcleo do "testar transformação com doc" (§3.2) |
| `ParseController` (`api/Parse`) | POST upload (:110), POST detect (:622), POST auto (:782), GET transformations/{ticket} (:425), GET .../candidates/{mapperGuid} (:469) | aceita `workspaceId` opcional em form só p/ histórico (opt-in, :111,797) — não é autorização |
| `LayoutDatabaseController`, `MapperDatabaseController`, `DocumentController`, `LayoutsController`, `RAGController`, `LearningController`, `AutoTransformationController`, `TestingController`, `TestController`, `MonitoringController`, `MetricsController`, `AiMetricsController`, `ValidationDiagnosticController`, `XmlAnalysisController`, `SysmiddlePreflightController`, `ReferenceExamplesController`, `DataGenerationController`, `LogsController` | catálogo/parse/IA/ferramentas | fora do modelo de workspace; revisão separada (alguns são escrita global: RAG add-example/reload, learn-from-examples, refresh-cache, run-all) |

## 3. Respostas às 3 dúvidas

### 3.1 Quem aprova release hoje? Opções, segregação
Hoje `POST .../approve` = **`Reviewer` ou `FiscalAdmin`** (`MappingGovernanceController.cs:218`); **`owner` não aprova** (inconsistência
com "Proprietário = tudo do Administrador"). `Reviewer` hoje é atribuível via API e não escreve nada, só aprova e lê.
Campos de autoria existem e permitem segregação:
- `MappingDraft.CreatedByUserId` (`Models/Entities/Fiscal/MappingDraft.cs:55`), `FiscalMappingPackage.CreatedByUserId` (`:35`), `TestSuite.CreatedByUserId` (`:22`);
- `MappingRelease.ApprovedByUserId` (`MappingRelease.cs:168`) e `PublishedByUserId` (`:174`) — já gravados (o handler passa `userId` ao `ApproveAsync`).
- **Lacuna:** não há "quem editou por último" para artefato/regras (PATCH de artefato e de regra gravam justificativa/hash, mas o campo
  de autor da última edição não foi verificado como coluna dedicada em todas as tabelas; a release é gerada por compile e tem o usuário do compile
  apenas se existir campo equivalente — não confirmado). Segregação exata "não aprova o que editou" exigiria registrar `LastEditedByUserId`
  por release/draft (ou uma tabela de histórico de edições). Viável, custo médio, **não necessário para o MVP dos 4 papéis**.

Opções:
- **A (recomendada, simples):** aprovar/publicar/reverter/descontinuar/arquivar = Administrador+ (Owner incluído). Reviewer vira legado ≡ operator, perde
  aprovação. Coerente com o pedido ("Operador NÃO publica/reverte/descontinua"; aprovar não foi listado — **precisa de confirmação do dono**).
- **B:** Operador aprova, Administrador publica. Mantém o fluxo em dois estágios sem admin no caminho, mas sem segregação é "aprovação de si mesmo".
- **C (futuro):** A + regra "aprovador != `CreatedByUserId` do draft / autor da última edição", com exceção para Proprietário unipessoal; exige campo novo.
Implicação de segurança: em workspace de time pequeno, C pode travar (um único admin que também edita) — precisaria de override auditado.

### 3.2 "Testar transformação com documento editado" hoje: o que existe, o que persiste
| Endpoint | O que faz | Persiste / efeito colateral | Limites |
|---|---|---|---|
| POST `/api/TransformationExecution/execute` (:125) | TXT/XML→XML pelo pathway tcl-xsl, por `LayoutName`; `Validate` opcional | Sem escrita de artefato no handler; não há `Task.Run` nele. Usa layout do catálogo (não o draft do workspace) | nenhum limite explícito de corpo nem de taxa; **sem atributo nenhum** |
| POST `.../run-test` (:1600) | transforma + valida vs `ExpectedOutputXml` | sem persistência vista; mesmo caveat acima | idem |
| POST `.../execute-candidates` (:272, `[Authorize]`) | multi-candidato (sysmiddle + IA) | **SIM, em background:** `TryEnqueueAiCandidate` (:371, `Task.Run` :965 → `AiCandidateStore`, particionado por usuário via `CurrentUserId`) e `TryPersistFieldCorrectionContext` (:400/424/439 → `tbFieldCorrectionContext`, guarda `inputContent` do documento). Risco: dado de documento editado de Leitor vai para store/treino | idem |
| POST `.../execute-lowcode` (:1661), `field-mappings` (:1712) | runner low-code / composição | não auditado em detalhe | idem |
| POST `.../mapping-drafts/{d}/test-runs` (Compilation:315) | roda a release **compilada** contra `inputXml`+`expectedXml` | **SIM, muta a release:** `ApplyTestRunResultAsync` faz `UPDATE tbMappingRelease SET TestRunSummaryJson, Status = test_passed/test_failed` (`MappingTestRunService.cs:79`, store `:201-210`). Isso é gate de approve (approve exige `test_passed`). Job em memória (`Jobs[jobId]`) | `RequestSizeLimit` só nos uploads de pacote (`FiscalMappingPackagesController:50,184`, 50 MB/artefato); **não há limite no test-run**; **não há rate limiter** (grep `AddRateLimiter` negativo) |
| POST `.../test-suites/{s}/run` (TestSuite:180) | roda suite de fixtures | persiste run (`tbTestSuiteRun`) | sem limite |

Conclusão: **hoje não existe** endpoint "efêmero" por workspace que aceite documento editado sem persistir nada. O mais próximo é
`/execute` / `/run-test` (sem persistência mas sem ligação a workspace/mapper e sem RBAC) — viável como rota para Leitor apenas se ganharem
`[RequireWorkspaceRole]` (exige `{workspaceId}` na rota ou checagem de membership manual).
O que precisaria mudar para aceitar `viewer`:
1. **Novo endpoint efêmero** (ex. `POST /api/workspaces/{id}/mapping-drafts/{d}/transform-preview`) L+, contrato: `releaseId` + `inputXml` (sem `expectedXml`
   obrigatório), executa o XSLT/TCL da release e devolve `transformedXml`; **zero escrita** (não chama `ApplyTestRunResultAsync`, não grava fixture/run).
   Reusa `MappingTestRunService.EvaluateFixtureAsync` (`:~110`), que é pura, extraindo a parte sem o `UPDATE`.
2. `CreateTestRun` atual continua O+ (muda status da release → não pode ser Leitor).
3. Para reuso de `execute-candidates` por Leitor: ou bloquear o background (flag `persist=false` / ignorar para Leitor nos pontos `TryEnqueueAiCandidate` e `TryPersistFieldCorrectionContext`) ou restringir o endpoint a O+. Recomendo **não** expor execute-candidates ao Leitor.
4. Limites: aplicar `[RequestSizeLimit]` (sugestão 5 MB) e rate limiting por usuário (`AddRateLimiter`) no novo endpoint; hoje não há.
5. Auditoria: registrar a execução efêmera (`AuditActionFilter`) sem o conteúdo do documento (dado fiscal sensível — regra de segurança do projeto).

### 3.3 Endpoints sem atributo: Operador+ vs Leitor+
- **Operador+ (escrita sem atributo hoje):** POST CreatePackage; POST CreateRevision; POST CreateDraft; POST suggestions; DELETE suggestions/{job}; PATCH rules/{ruleId};
  POST compile; POST test-runs; DELETE analyses/{id} (hoje R6).
- **Leitor+ (leitura só-membro):** GET workspace, explanation, projects, packages/{id}, excel-inventory, mapping-drafts/{id}, suggestions/{job}, compile/{job}, releases/{id}, releases/diff,
  test-runs/{job}, test-suites (+{id}, fixtures, runs).
- Com atributo a ajustar: os 3 de escrita W3 + PUT answer → trocar listas pelo conjunto de "Operador+" (§4 do plano); approve/publish/rollback/deprecate/archive → Administrador+ (incl. Owner no approve).
- O teste de reflexão `WorkspaceIsolationReflectionTests` (`:129`) já cobra atributo-ou-exceção; migrar as 27 exceções para atributos deixa de precisar da lista.

## 4. Papel em `GET /api/workspaces/me`
**Já existe.** `WorkspacesController.cs:53-60`: `{ activeWorkspaceId, isSudo, workspaces: [{ workspaceId, name, kind, role, createdAt }] }`,
`role` = string crua do banco (`SqlIdentityWorkspaceStore.cs:180,221`), idem em `GET /api/workspaces/{id}` (`:99-106`). Hoje pode retornar os 6 ids, inclusive `mapper`/`reviewer`.
Se mapper/reviewer forem legados, recomendo: **normalizar na saída** (`mapper|reviewer → operator`, campo `role` já normalizado) para o portal só lidar com 4 valores,
e opcionalmente expor `roleRaw` para depuração. Sem mudar o formato. `GET .../members` também devolve `role` cru (`WorkspaceMembersController.cs:166`) — aplicar a mesma normalização.

## 5. Riscos / impactos
| Risco | Detalhe | Mitigação |
|---|---|---|
| Elevação silenciosa de `operator` | Quem é operator hoje só lê; ao redefinir, passa a editar/compilar/testar. Sem migração de dado, vale na hora do deploy | Contar membros `operator` por workspace antes (leitura, pelo dono); feature flag `Rbac:OperatorWrite`; comunicar aos admins; ou ativar só após revisão da lista |
| Legados mapper/reviewer | Continuam no banco e funcionando por equivalência; Reviewer **perde** aprovação se escolhida Opção A | Decidir explicitamente (§3.1); helper central `RoleGroups` |
| Duplicação de listas literais | 21 atributos com listas de papéis escritas à mão (Mapper/Reviewer espalhados) | Introduzir conjuntos nomeados (`WorkspaceRoleGroups.Reader/Operator/Admin`) e normalizador `Normalize(role)`; refatorar atributos e o `AssignableRoles` |
| Owner não aprova hoje | Bug de modelo vs. alvo | Corrigir no mesmo passo |
| Lacunas "só membro" em escritas | 8 escritas sem papel: Leitor hoje já consegue criar draft/pacote/compilar/mudar status de release via test-run | Fechar é ganho de segurança independente do RBAC novo; maior risco do passo |
| Test-run muta status da release | Leitor que rodar poderia virar `test_passed` e destravar approve | Endpoint efêmero separado (§3.2) |
| `execute-candidates` persiste documento (`tbFieldCorrectionContext`, AiCandidateStore) | Documento editado de Leitor entra em store/treino | Não expor ao Leitor |
| Auditoria | `AuditActionFilter` só em endpoints marcados; log de 403 existe (`RequireWorkspaceRoleFilter:74`), mas sem trilha de mudança de papel em PATCH de membro | Auditar PATCH/POST/DELETE de members e approve/publish |
| Testes existentes | `MappingGovernanceControllerTests`, `MappingArtifactEditControllerTests`, `MappingRuleAnswersControllerTests`, `LayoutTreeControllerTests`, `WorkspaceMembersControllerTests`, `WorkspaceIsolationReflectionTests` assumem as listas atuais e a mensagem de erro dos 5 papéis | Atualizar esperados; novo teste de matriz (papel x endpoint) |
| Mensagem 400 de papéis | Texto fixo lista `mapper, reviewer` | Trocar para `fiscal_admin, operator, viewer` (+ rejeitar legados com 400 nos POST/PATCH, como o portal sugeriu) |
| Dados existentes `mapper/reviewer` ao PATCH | Um admin reeditando o papel de um legado precisa escolher um dos 3 novos | OK; GET devolve normalizado |
| Regra do último admin | `IsAdminRole` usa owner/fiscal_admin — segue válida | Nenhuma |
| Sem rate limit / sem limite de tamanho no preview | Abuso de CPU por Leitor | Limites no novo endpoint |

## 6. Plano de implementação em passos pequenos

| # | Passo | Quem | Observação |
|---|---|---|---|
| 0 | Dono confirma: (a) quem aprova (Opção A/B/C); (b) Owner aprova; (c) DELETE de analysis = O ou A; (d) mapper/reviewer = operator equivalentes; (e) contagem de operators atuais | dono/Aria | bloqueia 2 e 6 |
| 1 | `WorkspaceRoleGroups` (Reader/Operator/Admin) + `Normalize` (mapper/reviewer→operator) em `Models/Entities/Identity`; testes unitários | backend-dev | sem mudar comportamento |
| 2 | Migrar os 21 atributos para os grupos; approve → Admin+ (Owner incluído); operator passa a escrever (feature flag opcional) | backend-dev | passo que eleva operator |
| 3 | Adicionar `[RequireWorkspaceRole]` Operador+ nas 8 escritas "só membro" e Leitor+ nas leituras; reduzir `ExcecoesAprovadas` | backend-dev | atualiza teste de reflexão |
| 4 | Normalizar `role` em `GET /workspaces/me`, `/{id}`, `/members`; POST/PATCH aceitam só `fiscal_admin, operator, viewer`, rejeitam legados (400), mensagem nova | backend-dev | |
| 5 | Endpoint efêmero de preview (L+), sem persistência, limite de tamanho + rate limit + auditoria sem conteúdo | backend-dev + parser-llm (extração da avaliação pura) | |
| 6 | (Opcional) segregação: `LastEditedByUserId` + regra "não aprova o que editou" | backend-dev | só se Opção C |
| 7 | Testes: matriz papel x endpoint (6 ids x rotas), 403/404, elevação do operator, preview sem efeito colateral (release inalterada), legado | qa | |
| 8 | Docs: README/Swagger, tabela de papéis, nota de breaking para portal; atualizar memória/contrato cross-repo | doc | |
| 9 | Rollout: auditar contagem de operators, comunicar, ativar flag | devops/dono | push/PR só `@lp-devops` |

## 7. Segurança / dados
Nenhum segredo encontrado ou citado. Nenhuma operação tocou `172.31.249.51`. Não foi feito commit; arquivo untracked.
