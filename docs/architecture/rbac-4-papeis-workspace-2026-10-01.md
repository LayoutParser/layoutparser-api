# RBAC de workspace: alinhamento aos 4 papéis do dono (2026-10-01)

Origem: pedido do portal via hub MCP (thread abce4a62, msg 9c501438). Apenas análise; nada implementado.
Autora: Aria (@lp-architect).

## 0. Papéis finais e mapeamento

| Papel final | Id na API | Hoje | Observação |
|---|---|---|---|
| Leitor | `viewer` | viewer | lê tudo; testa transformação sem persistir |
| Operador | `operator` | operator (hoje == viewer, só leitura) | passa a ter ESCRITA de trabalho |
| Administrador | `fiscal_admin` | fiscal_admin | Operador + publicar/reverter/descontinuar/arquivar + membros |
| Proprietário | `owner` | owner | único, só sistema, não convidável (`AssignableRoles` já exclui owner) |
| (legado) | `mapper`, `reviewer` | mapper escreve; reviewer aprova | tratados como `operator` (aceitos, não atribuíveis) |

Conceito proposto: hierarquia por NÍVEL, não por lista de ids em cada atributo.
`viewer(1) < operator(2) < fiscal_admin(3) < owner(4)`; `mapper`, `reviewer` => nível 2.
Novo atributo `[RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]` (mínimo) substitui as listas de strings
repetidas em ~14 pontos. Isso elimina a classe de bug "esqueci um papel na lista".

## 1. Matriz endpoint x papel (estado atual -> alvo)

Legenda: L=Leitor, O=Operador, A=Admin, P=Proprietário. "Hoje" = atributo atual.
Todos os endpoints de workspace já retornam 404 para não-membro (filtro); isso não muda.

### 1.1 Com `[RequireWorkspaceRole]` hoje

| Endpoint | Hoje | Alvo (mínimo) |
|---|---|---|
| GET workspaces/{w}/mapping-releases (Governance.List) | todos os 6 | L |
| POST .../mapping-releases/{r}/approve | reviewer, fiscal_admin | ver decisão D1 (A, ou O+) |
| POST .../publish | fiscal_admin, owner | A |
| POST .../rollback | fiscal_admin, owner | A |
| POST .../deprecate | fiscal_admin, owner | A |
| POST .../archive | fiscal_admin, owner | A |
| PUT mapping-drafts/{d}/rules/{r}/questions/{i}/answer (resposta de regra) | owner, fiscal_admin, mapper, reviewer | O |
| GET mapping-drafts/{d}/question-answers (+ por regra) | todos | L |
| GET mappings/{id}/layout-tree | todos | L |
| GET mappings/{g}/studio-model | todos | L |
| GET mappings/{id}/generated-transformation | todos | L |
| PATCH mapping-drafts/{d}/artifacts/{engine} (editar TCL/XSL) | mapper, fiscal_admin, owner | O |
| PUT mapping-drafts/{d}/fiscal-profile | mapper, fiscal_admin, owner | O |
| GET mapping-drafts (lista) | todos | L |
| POST mapping-drafts/{d}/test-suites | mapper, fiscal_admin, owner | O |
| POST test-suites/{s}/fixtures | idem | O |
| POST test-suites/{s}/run | idem | O |
| GET /analyses, /analyses/{id}, /files/{f} (FiscalAnalyses, atributo no controller) | todos | L |
| DELETE /analyses/{id} | todos (BUG: viewer pode apagar) | O |
| GET/POST/PATCH/DELETE workspaces/{w}/members | owner, fiscal_admin | A (GET membros: ver D4) |

### 1.2 Com `{workspaceId}` na rota SEM atributo (só membership; lista de exceções do `WorkspaceIsolationReflectionTests`)

Qualquer membro (inclusive viewer/operator hoje) executa. Estes são os buracos reais para o RBAC novo.

| Endpoint | Efeito | Alvo |
|---|---|---|
| POST mapping-packages/{p}/drafts (CreateDraft) | cria draft | O |
| POST projects/{p}/mapping-packages (CreatePackage) | cria pacote | O |
| POST mapping-packages/{p}/revisions (CreateRevision) | cria revisão | O |
| GET projects, GET mapping-packages/{p}, GET .../excel-inventory | leitura | L |
| GET mapping-drafts/{d} (GetDraft) | leitura | L |
| POST mapping-drafts/{d}/suggestions (job IA/Ollama) | cria job, custo CPU | O |
| GET suggestions/{job} | leitura | L |
| DELETE suggestions/{job} (cancelar) | escrita | O |
| PATCH mapping-drafts/{d}/rules/{ruleId} (UpdateRule) | edita regra | O |
| POST mapping-drafts/{d}/compile | gera release candidata | O |
| GET compile/{job}, releases/{r}, releases/diff | leitura | L |
| POST mapping-drafts/{d}/test-runs | grava test-run | O (ver 2.2) |
| GET test-runs/{job}; GET test-suites, /{s}, fixtures, runs | leitura | L |
| GET mappings/{id}/versions/{v}/explanation | leitura | L |
| GET workspaces/{w} (Workspaces.GetWorkspace) | leitura | L |
| /api/admin/** | `[RequireSudo]` | fora do RBAC de workspace; manter |

### 1.3 Endpoints globais (sem workspaceId; não são escopo de papel de workspace)

Hoje: sem atributo de papel, ou `[Authorize]`/`[Authorize(Roles=...)]` (roles do BFF: admin/operador, NÃO são papéis de workspace; `[Authorize]` com identidade do `TrustedIdentityMiddleware`).
Não confundir "operador" do `[Authorize(Roles="operador")]` (BFF/Entra, MapperDatabase refresh-cache/export) com o Operador de workspace. Recomendo NÃO tocar nisso neste PBI e registrar a ambiguidade de nomenclatura para o dono.

| Grupo | Endpoints | Classificação para o RBAC novo |
|---|---|---|
| Transformação/teste do Leitor | TransformationExecution `execute`, `execute-legacy`, `execute-candidates`, `execute-candidates/{t}/ia-status`; Parse `upload`, `detect`, `auto`, `transformations/{t}`; Test `parse-with-sample`; XmlAnalysis `analyze`, `validate-*`, `transform-nfe`, `reverse-reconstruct` | L (sem workspace, sem persistência de trabalho; ver 2.2) |
| Preferências/sessão IA do usuário | `ai-prompt-adicional`, `ai-preferences`, `ai-session/history` (+resume), `field-correction` (POST) | L (dado do próprio usuário, não é artefato de workspace); `field-correction` ver 2.2 |
| Curadoria | `field-correction/pending`, `.../review` | `[Authorize(Roles=admin)]` do BFF; manter |
| Leitura catálogo | Document/*, Layouts, LayoutDatabase (search, GET), MapperDatabase (by-*, all), ReferenceExamples, Monitoring/Metrics/AiMetrics, RAG stats | L |
| Escrita global do catálogo/IA | RAG reload/add-example, Learning learn-from-examples, AutoTransformation generate-*, Testing run-*, DataGeneration (já admin), LayoutDatabase refresh/clear-cache/test-decryption*, MapperDatabase refresh-cache (já operador) | fora de workspace; hoje abertos a qualquer chamador autenticado pelo BFF. Recomendo exigir papel global (sudo/admin do BFF), NÃO papel de workspace. Dívida independente deste PBI; registrar. |

## 2. Respostas às perguntas do portal

### 2.1 Quem aprova release (D1)

Situação: `approve` aceita `reviewer` e `fiscal_admin`; publish exige `fiscal_admin`/`owner`. `reviewer` é um papel
separado exatamente para segregar "quem aprova" de "quem escreve" (4-eyes). Com `reviewer` virando legado=operator,
a segregação some, a menos que seja explicitamente decidida.

| Opção | Efeito | Trade-off |
|---|---|---|
| A: só Admin+ aprova (e publica) | simples, alinha à frase do dono (Operador "NÃO publica") | um Admin pode aprovar a própria release; sem 4-eyes |
| B: Operador também aprova | Operador faz o ciclo até approved; Admin só publica | Operador aprova o que ele mesmo compilou: sem segregação alguma, risco fiscal |
| C (recomendada): Admin+ aprova, MAIS regra "aprovador != autor do compile/última edição" (bloquear self-approval, exceto owner em workspace de 1 membro) | mantém 4-eyes sem papel extra | exige guardar `CompiledBy`/`LastEditedBy` na release (já há `userId` no job de compile; verificar coluna) |

Recomendo C. Mínimo aceitável: A. Evitar B.
Nota: o enunciado do dono lista "publica/reverte/descontinua/arquiva" mas não cita "aprova". A omissão é lacuna de
definição, é decisão D1 do dono.

### 2.2 Como o Leitor testa transformação com documento editado hoje

Todos os caminhos abaixo são GLOBAIS (sem workspaceId) e portanto hoje já acessíveis sem papel de workspace:

| Endpoint | Persiste? |
|---|---|
| POST `/api/TransformationExecution/execute` (e `execute-legacy`) | Não persiste artefato de workspace. Pode alimentar efeitos laterais de IA (cache/AiCandidateStore particionado por usuário, captura de dataset de treino) conforme caminho; é estado do usuário, não de workspace. |
| POST `execute-candidates` (`[Authorize]`) | Mesmo: AiCandidateStore em memória/ticket por usuário (#92). Não grava mapping/release. |
| POST `/api/Parse/upload|detect|auto` | Parse e cache de resultado (ticket). Sem escrita de trabalho. |
| POST `/api/Test/parse-with-sample` | Sem persistência de trabalho. |
| POST `/api/TransformationExecution/field-correction` | PERSISTE correção humana (fila de curadoria). Isso é "persistir"; Leitor NÃO deveria usar. Hoje é `[Authorize]` simples. Recomendo exigir Operador+ em algum workspace OU manter aberto mas ciente de que Leitor grava na fila (D3). |
| POST `workspaces/{w}/mapping-drafts/{d}/test-runs` | PERSISTE job de test-run (tabela de jobs) contra uma release. Escrita de trabalho => O. Para o Leitor testar com documento editado, usar `execute`/`execute-candidates` (efêmero). |
| POST `test-suites/{s}/run`, `/fixtures` | PERSISTEM runs/fixtures => O (já protegidos). |

Conclusão: o fluxo "Leitor edita o documento e testa" já existe de forma efêmera via `execute`/`execute-candidates`
(sem papel de workspace). NÃO precisa de endpoint novo. Risco residual: efeitos laterais de IA/captura de treino
(verificar que `execute` não grava `TrainingDataCapture` quando chamado por Leitor; Dex confirmar, ver handoff passo 7).
Se o dono quiser garantia "nada persiste", o portal deve usar só `execute`/`execute-candidates` e nunca `test-runs`/`field-correction` no modo Leitor.

### 2.3 Endpoints sem atributo: Operador+ ou Leitor+?

Regra proposta: GET/leitura => Leitor+; POST/PUT/PATCH/DELETE que muda estado de workspace, ou dispara job
persistido/custoso (suggestions, compile, test-runs) => Operador+. Aplicação detalhada em §1.2. Itens que merecem
sua atenção: `DELETE /analyses/{id}` (hoje viewer apaga: bug), `POST suggestions` (consome CPU Ollama: Operador+),
`GetDraft` etc. (Leitor+).

## 3. Plano de implementação e migração

Fases (cada uma = 1 PR develop -> master, por @lp-backend-dev):

1. **Modelo de papéis**: `WorkspaceRoles` com níveis e helper `AtLeast(role, min)`; legados `mapper`/`reviewer`
   => nível Operador. Novo atributo `[RequireWorkspaceRole(Level)]` (manter overload com strings p/ compat até a Fase 3).
   `RequireWorkspaceRoleFilter` passa a comparar nível. Papel desconhecido => nível 0 (nega), nunca eleva.
2. **Anotar** todas as ações da §1.2 e corrigir `FiscalAnalyses.Delete`. Atualizar `WorkspaceIsolationReflectionTests`
   (exceções somem; teste passa a exigir atributo em TODA ação com {workspaceId}, sem lista de exceções, exceto Admin sudo e GetWorkspace).
3. **Aprovação (D1)** conforme decisão; incluir regra anti-self-approval se C.
4. **Membros**: `AssignableRoles = {viewer, operator, fiscal_admin}`; POST/PATCH com `mapper`/`reviewer` => 400 com mensagem
   "papel legado, use operator". `LastAdmin` continua contando owner/fiscal_admin. PATCH que muda um membro legado PARA operator é permitido;
   PATCH que o mantém legado não. Owner continua imutável.
5. **Migração de dados** (script idempotente, aplicado por @lp-devops na VM identity, NÃO em 172.31.249.51):
   `UPDATE WorkspaceMembership SET Role='operator' WHERE Role IN ('mapper','reviewer')` — mapper é equivalente exato
   (já escreve), reviewer ganha escrita (efeito colateral aceito se D1 decidido). Executar DEPOIS do deploy do código que trata legado como operator
   (o código tolera os dois estados, migração é segura em qualquer ordem). Rollback: guardar backup `SELECT` dos ids afetados.
   Antes: rodar contagem (abaixo).
6. **`GET /api/workspaces/me`**: já expõe `role` por workspace (`WorkspacesController.GetMe`, campo `role`). Mudança: devolver o id
   CANÔNICO (legado mapeado para `operator`) e acrescentar `permissions` derivadas (ex.: `canEdit`, `canPublish`, `canManageMembers`),
   para o portal não replicar a tabela. Não quebra o contrato atual (campo `role` mantido).
7. **Observabilidade**: log estruturado de 403 por (papel, endpoint) na primeira semana para detectar regressão de papel.

### Contagem de membros operator (risco levantado pelo portal)

NÃO consegui medir. O ambiente desta sessão não tem credencial de `IdentityDatabase:*` (appsettings com `UserId/Password` vazios,
sem user-secrets, sem env var) e não vou usar `172.31.249.51`. Consulta somente-leitura para rodar na VM identity
(`elson@172.25.32.5`, container `layoutparser-identity-sql`, banco `LayoutParserIdentity`), por @lp-devops ou pelo dono:

```sql
SELECT Role, COUNT(*) AS Total FROM WorkspaceMembership GROUP BY Role ORDER BY Total DESC;
-- confirmar o nome real da tabela (pode ser tbWorkspaceMembership); só SELECT.
```
Leitura do risco sem o número: `operator` nunca teve capacidade distinta e a UI de membros só atribui papéis via POST/PATCH
de fiscal_admin/owner; se houver operators, eles ganham escrita ao ligar o novo RBAC. Mitigação: antes do deploy, converter
operators existentes em `viewer` se o dono não quiser que ganhem escrita automaticamente (D2).

## 4. Decisões pendentes do dono

- **D1** Quem aprova release: A, B ou C (recomendo C).
- **D2** Operators/reviewers existentes: promover automaticamente a escrita (padrão da sugestão do portal) ou rebaixar operators a viewer? Depende da contagem.
- **D3** `field-correction` (grava fila de curadoria): permitir ao Leitor ou exigir Operador+?
- **D4** Operador pode LISTAR membros (GET members)? Hoje só owner/fiscal_admin. Recomendo Leitor+ só nome/papel, ou manter Admin+.
- **D5** Ambiguidade de nome: `[Authorize(Roles="operador")]` do BFF (global) vs Operador de workspace. Renomear um dos dois no portal/BFF?
- **D6** Endpoints globais de escrita (RAG/Learning/AutoTransformation/cache) sem papel: dívida separada, abrir PBI?

## 5. Handoff para @lp-backend-dev

```yaml
handoff:
  from_agent: "@lp-architect"
  to_agent: "@lp-backend-dev"
  contexto:
    tarefa: "RBAC 4 papéis (viewer/operator/fiscal_admin/owner); mapper/reviewer legados = operator"
    branch: "develop"
    arquivos_tocados:
      - "Models/Entities/Identity/WorkspaceMembership.cs (WorkspaceRole + níveis)"
      - "Services/Filters/RequireWorkspaceRoleFilter.cs"
      - "Controllers/*: ver §1 do doc"
      - "Controllers/WorkspaceMembersController.cs (AssignableRoles)"
      - "Controllers/WorkspacesController.cs (role canônico + permissions)"
      - "tests/.../Security/WorkspaceIsolationReflectionTests.cs"
  decisoes:
    - "Hierarquia por nível; mapper/reviewer => nível operator; desconhecido => 0 (nega)"
    - "Anotar TODAS as ações com {workspaceId} (§1.2); zerar lista de exceções do teste de reflexão"
    - "FiscalAnalyses.Delete: exigir Operator+ (viewer apaga hoje)"
    - "Aprovação: seguir D1 do dono; default provisório = Admin+ (opção A) até decidir"
    - "AssignableRoles = viewer, operator, fiscal_admin; legados dão 400 com mensagem; owner imutável"
  bloqueios:
    - "D1-D4 pendentes do dono"
    - "contagem de operators exige acesso à VM identity (@lp-devops)"
  proximo_passo: >
    Fases 1-2 do §3 em PR único pequeno (modelo + anotação + testes 403/200 por papel nível a nível);
    Fase 4 junto; Fase 6 (/me com permissions) em seguida. Confirmar que /execute não grava
    TrainingDataCapture para Leitor. Migração SQL (Fase 5) vai para @lp-devops. Build+test verdes antes de concluir.
```

## 6. Decisões do dono e implementação (2026-10-01)

- **D1** (decidida): aprova release = Administrador (`fiscal_admin`) ou Proprietário (`owner`), apenas (opção A; sem regra anti-self-approval).
- **D2** (decidida): atribuíveis via POST/PATCH = `viewer`, `operator`, `fiscal_admin`. `mapper`/`reviewer` = legados nível Operador (aceitos na leitura, 400 "Papel legado" ao atribuir). Leitor: só leitura + teste efêmero (`execute`/`execute-candidates`), sem criar/atualizar mapeadores de planilha/XSD SEFAZ. Operador: edição de TCL/XSL/XSLT/TXT e tudo de trabalho, inclusive esses mapeadores.
- **D3**: Leitor NÃO usa `field-correction` (implementado: 403 se o usuário só é Leitor em todos os workspaces) — confirmado pelo dono (2026-10-01).
- **D4**: só Admin+ lista membros — confirmado pelo dono (2026-10-01).
- **D5**: sem renomear — confirmado pelo dono (2026-10-01).
- Implementado: `WorkspaceRoleLevel` + `WorkspaceRole.AtLeast/Canonical`, `[RequireWorkspaceRole(WorkspaceRoleLevel.X)]` em todas as ações com `{workspaceId}` (teste de reflexão sem exceções, salvo 2 rotas `/api/admin` com sudo), `DELETE /analyses/{id}` = Operator+, `GET /api/workspaces/me` com `role` canônico + `permissions {canEdit, canPublish, canManageMembers}`.
- Migração SQL: `docs/architecture/rbac-migracao-mapper-reviewer-para-operator.sql` (idempotente; só VM identity, via @lp-devops; NÃO executada).
- `TrainingDataCapture`: `execute` não grava; só grava (a) convergência do RepairOrchestrator (síntese IA, dataset global, não por papel) e (b) correção humana aceita na curadoria (admin). Risco residual: Leitor via `execute-candidates` pode disparar síntese IA que, ao convergir, captura exemplo.
