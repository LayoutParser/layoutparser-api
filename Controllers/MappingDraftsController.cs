using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    public sealed class CreateDraftRequest
    {
        public Guid RevisionId { get; set; }
        public string? Engine { get; set; }
    }

    /// <summary>Corpo do <c>PUT .../fiscal-profile</c> (issue #379, ADR §2.6).</summary>
    public sealed class SetFiscalProfileRequest
    {
        public string? DocumentType { get; set; }
        public string? SchemaVersion { get; set; }
        public string? Operation { get; set; }
        public string? Jurisdiction { get; set; }
    }

    public sealed class UpdateRuleRequest
    {
        public string? Status { get; set; }
        public string? Justification { get; set; }
        public List<string>? SourceRefs { get; set; }
        public List<string>? TargetRefs { get; set; }
        public string? Operation { get; set; }
        public string? Answer { get; set; }
    }

    /// <summary>
    /// <c>MappingDraft</c> human-in-the-loop (Slice 3 — issue #230). IA propõe regras estruturadas,
    /// nunca código executável; humano aceita/edita/rejeita/responde. Isolamento por workspace
    /// fail-closed (mesmo padrão do Slice 1/2). Todas as rotas recusam <c>engine=sysmiddle</c> via
    /// <see cref="MappingEngineGuardFilter"/>.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public class MappingDraftsController : ControllerBase
    {
        private static readonly IReadOnlyCollection<string> AllowedEngines = new[] { "tcl", "xslt" };

        private readonly IMappingDraftStore _store;
        private readonly IMappingSuggestionService _suggestionService;
        private readonly IIdentityWorkspaceService _identityWorkspaceService;
        private readonly IFiscalProfileResolver _fiscalProfileResolver;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingDraftsController> _logger;

        public MappingDraftsController(
            IMappingDraftStore store,
            IMappingSuggestionService suggestionService,
            IIdentityWorkspaceService identityWorkspaceService,
            IFiscalProfileResolver fiscalProfileResolver,
            ICurrentUser currentUser,
            ILogger<MappingDraftsController> logger)
        {
            _store = store;
            _suggestionService = suggestionService;
            _identityWorkspaceService = identityWorkspaceService;
            _fiscalProfileResolver = fiscalProfileResolver;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>
        /// Lista drafts do workspace, paginado (issue #416 — front não tinha forma de descobrir
        /// drafts sem já saber o GUID; único endpoint de leitura era <c>GET .../mapping-drafts/{draftId}</c>,
        /// que exige o GUID de antemão). Mesmo padrão de descoberta do <c>MappingGovernanceController.List</c>
        /// (issue #198/#377).
        /// </summary>
        /// <remarks>
        /// RBAC: qualquer papel de membro (<c>owner</c>/<c>fiscal_admin</c>/<c>mapper</c>/
        /// <c>reviewer</c>/<c>operator</c>/<c>viewer</c>). Não-membro ou sem identidade → 404.
        /// Filtro opcional <c>engine</c> (<c>tcl</c>/<c>xslt</c> — valor fora disso → 400). Draft não
        /// tem "status" próprio (só as regras têm), então não há filtro de status aqui.
        /// </remarks>
        /// <param name="workspaceId">Workspace dono dos drafts.</param>
        /// <param name="page">Página (1-based). Default 1.</param>
        /// <param name="pageSize">Tamanho da página (1..100). Default 20.</param>
        /// <param name="engine">Opcional. Filtra por motor do draft (<c>tcl</c>/<c>xslt</c>).</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        [HttpGet("mapping-drafts")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        public async Task<IActionResult> ListDrafts(
            Guid workspaceId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? engine = null,
            CancellationToken cancellationToken = default)
        {
            if (page < 1)
                return BadRequest(new { error = "\"page\" deve ser >= 1." });

            if (pageSize < 1 || pageSize > 100)
                return BadRequest(new { error = "\"pageSize\" deve estar entre 1 e 100." });

            if (!string.IsNullOrWhiteSpace(engine) && !AllowedEngines.Contains(engine, StringComparer.OrdinalIgnoreCase))
                return BadRequest(new { error = $"\"engine\" inválido. Valores aceitos: {string.Join(", ", AllowedEngines)}." });

            var engineFilter = string.IsNullOrWhiteSpace(engine) ? null : engine.ToLowerInvariant();

            var (items, totalCount) = await _store.ListByWorkspaceAsync(workspaceId, page, pageSize, engineFilter, cancellationToken);

            return Ok(new
            {
                items = items.Select(ToDraftSummaryResponse),
                page,
                pageSize,
                totalCount,
            });
        }

        /// <summary>Cria um Draft a partir de uma revisão EXATA de um pacote (Slice 2) — nunca "a mais recente" implícita.</summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpPost("mapping-packages/{packageId:guid}/drafts")]
        public async Task<IActionResult> CreateDraft(Guid workspaceId, Guid packageId, [FromBody] CreateDraftRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            // Motor default quando ausente: rejeitar ambiguidade (design §4) — nunca assumir tcl/xslt silenciosamente.
            if (string.IsNullOrWhiteSpace(request.Engine) || !AllowedEngines.Contains(request.Engine, StringComparer.OrdinalIgnoreCase))
                return UnprocessableEntity(new { error = $"Campo \"engine\" obrigatório, um de: {string.Join(", ", AllowedEngines)}." });

            if (request.RevisionId == Guid.Empty)
                return UnprocessableEntity(new { error = "Campo \"revisionId\" obrigatório." });

            WorkspaceSummary? membership;
            try
            {
                membership = await _identityWorkspaceService.GetWorkspaceForMemberAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao verificar membership do workspace {WorkspaceId} para criação de draft.", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível verificar o workspace no momento." });
            }

            if (membership == null)
                return NotFound();

            // Pacote/revisão de OUTRO workspace recebem a mesma resposta (404) de "revisão não pertence
            // ao pacote" — não revela a existência do pacote fora do workspace da rota (#196).
            bool revisionBelongs;
            try
            {
                revisionBelongs = await _store.RevisionBelongsToWorkspacePackageAsync(workspaceId, packageId, request.RevisionId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao validar revisão {RevisionId} do pacote {PackageId}.", request.RevisionId, packageId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível validar a revisão no momento." });
            }

            if (!revisionBelongs)
                return NotFound();

            MappingDraftDetail draft;
            try
            {
                draft = await _store.CreateDraftAsync(workspaceId, packageId, request.RevisionId, userId, request.Engine.ToLowerInvariant(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar draft (workspace={WorkspaceId}, package={PackageId}).", workspaceId, packageId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível criar o draft no momento." });
            }

            return CreatedAtAction(nameof(GetDraft), new { workspaceId, draftId = draft.DraftId }, ToDraftResponse(draft));
        }

        /// <summary>Consulta o draft + regras atuais — só se o usuário for membro do workspace dono.</summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("mapping-drafts/{draftId:guid}")]
        public async Task<IActionResult> GetDraft(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            MappingDraftDetail? draft;
            try
            {
                draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar draft {DraftId}.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o draft no momento." });
            }

            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            return Ok(ToDraftResponse(draft));
        }

        /// <summary>
        /// Grava/substitui o perfil fiscal de trabalho do draft (issue #379, ADR §2.6) — idempotente
        /// (200, cria ou substitui). Editar depois de já existir release derivada NÃO retroage
        /// (ADR §2.2) — a release compilada guarda seu próprio snapshot.
        /// </summary>
        [HttpPut("mapping-drafts/{draftId:guid}/fiscal-profile")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        public async Task<IActionResult> SetFiscalProfile(Guid workspaceId, Guid draftId, [FromBody] SetFiscalProfileRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (string.IsNullOrWhiteSpace(request.DocumentType) || string.IsNullOrWhiteSpace(request.SchemaVersion)
                || string.IsNullOrWhiteSpace(request.Operation) || string.IsNullOrWhiteSpace(request.Jurisdiction))
            {
                return UnprocessableEntity(new { error = "Campos \"documentType\", \"schemaVersion\", \"operation\" e \"jurisdiction\" são obrigatórios." });
            }

            var profile = new FiscalProfile(request.DocumentType, request.SchemaVersion, request.Operation, request.Jurisdiction);
            var validation = _fiscalProfileResolver.Validate(profile);
            if (!validation.IsValid)
                return UnprocessableEntity(new { error = validation.Error });

            MappingDraftDetail? draft;
            try
            {
                // Confirma isolamento por workspace ANTES de gravar (mesmo padrão dos demais endpoints).
                var existing = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
                if (existing == null || existing.WorkspaceId != workspaceId)
                    return NotFound();

                draft = await _store.SetFiscalProfileAsync(draftId, userId, profile, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao gravar perfil fiscal do draft {DraftId}.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível gravar o perfil fiscal no momento." });
            }

            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            return Ok(ToDraftResponse(draft));
        }

        /// <summary>Dispara o job assíncrono de sugestão de regras via IA — nunca bloqueia esperando a IA.</summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpPost("mapping-drafts/{draftId:guid}/suggestions")]
        public async Task<IActionResult> CreateSuggestionJob(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            MappingDraftDetail? draft;
            try
            {
                draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar draft {DraftId} para disparo de sugestão.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível iniciar a sugestão no momento." });
            }

            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var jobId = await _suggestionService.EnqueueAsync(draftId, workspaceId, draft.RevisionId, draft.Engine, cancellationToken);

            return AcceptedAtAction(nameof(GetSuggestionJob), new { workspaceId, draftId, jobId }, new { jobId, status = SuggestionJobStatus.Queued });
        }

        /// <summary>Status observável do job — não é fire-and-forget cego (spec §8: "observáveis").</summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("mapping-drafts/{draftId:guid}/suggestions/{jobId:guid}")]
        public async Task<IActionResult> GetSuggestionJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            // Confirma isolamento por workspace via o draft antes de expor o status do job.
            var draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var state = await _suggestionService.GetStatusAsync(jobId, cancellationToken);
            if (state == null)
                return NotFound();

            return Ok(new { jobId = state.JobId, status = state.Status, rulesCreated = state.RulesCreated, error = state.Error });
        }

        /// <summary>Cancelamento cooperativo do job de sugestão.</summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpDelete("mapping-drafts/{draftId:guid}/suggestions/{jobId:guid}")]
        public async Task<IActionResult> CancelSuggestionJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var canceled = await _suggestionService.CancelAsync(jobId, cancellationToken);
            if (!canceled)
                return NotFound();

            return Accepted();
        }

        /// <summary>
        /// Aceita/edita/rejeita/responde uma regra. Exige <c>If-Match</c> (428 sem header, 412 se
        /// divergente do <c>ROWVERSION</c> atual) — concorrência otimista greenfield (design §3).
        /// </summary>
        /// <remarks>
        /// Vocabulário de erro (reusado pelo futuro editor de artefato, issue #226): <c>200</c> +
        /// campo <c>eTag</c> no corpo (base64 do novo <c>ROWVERSION</c>); <c>412</c> com
        /// <c>{ current: &lt;regra atual&gt; }</c>; <c>428</c> se falta <c>If-Match</c>; <c>400</c>
        /// se <c>If-Match</c> não é base64; <c>422</c> para semântica inválida (status ausente/
        /// desconhecido, justificativa faltando em rejected/edited); <c>404</c> para sem identidade/
        /// sem membership/draft de outro workspace/regra inexistente. <b>Não há <c>401</c></b>
        /// (identidade vem do BFF — "não autenticado" ⇒ 404 fail-closed) nem <c>403</c> neste
        /// endpoint (autorização só por membership, sem <c>[RequireWorkspaceRole]</c>).
        /// </remarks>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpPatch("mapping-drafts/{draftId:guid}/rules/{ruleId:guid}")]
        public async Task<IActionResult> UpdateRule(Guid workspaceId, Guid draftId, Guid ruleId, [FromBody] UpdateRuleRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || string.IsNullOrWhiteSpace(ifMatchValues.ToString()))
                return StatusCode(StatusCodes.Status428PreconditionRequired, new { error = "Header If-Match é obrigatório para editar uma regra." });

            byte[] expectedRowVersion;
            try
            {
                expectedRowVersion = Convert.FromBase64String(ifMatchValues.ToString().Trim('"'));
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Header If-Match inválido (esperado base64 do ETag)." });
            }

            var newStatus = ResolveNewStatus(request);
            if (newStatus == null || !MappingDraftRuleStatus.IsValid(newStatus))
                return UnprocessableEntity(new { error = "Campo \"status\" obrigatório: accepted, edited ou rejected (ou envie \"answer\" para responder needs_input)." });

            // Justificativa obrigatória para rejected/edited (design §2), opcional para accepted.
            if ((newStatus == MappingDraftRuleStatus.Rejected || newStatus == MappingDraftRuleStatus.Edited) && string.IsNullOrWhiteSpace(request.Justification))
                return UnprocessableEntity(new { error = $"Justificativa obrigatória para status \"{newStatus}\"." });

            // Confirma isolamento por workspace ANTES de tentar o UPDATE otimista.
            var draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            UpdateRuleOutcome outcome;
            try
            {
                outcome = await _store.UpdateRuleStatusAsync(
                    draftId, ruleId, userId, expectedRowVersion, newStatus, request.Justification,
                    request.SourceRefs, request.TargetRefs, request.Operation, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao atualizar regra {RuleId} do draft {DraftId}.", ruleId, draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível atualizar a regra no momento." });
            }

            return outcome.Result switch
            {
                UpdateRuleResult.NotFound => NotFound(),
                UpdateRuleResult.Conflict => StatusCode(StatusCodes.Status412PreconditionFailed, new
                {
                    error = "A regra foi alterada por outra operação — recarregue e tente novamente.",
                    current = await LoadCurrentRuleAsync(draftId, ruleId, userId, cancellationToken),
                }),
                _ => Ok(ToRuleResponse(outcome.Rule!)),
            };
        }

        private async Task<object?> LoadCurrentRuleAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
        {
            var current = await _store.GetRuleIfMemberAsync(draftId, ruleId, userId, cancellationToken);
            return current == null ? null : ToRuleResponse(current);
        }

        private static string? ResolveNewStatus(UpdateRuleRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.Status))
                return request.Status!.ToLowerInvariant();

            // "answer" responde uma regra needs_input — vira "proposed" novamente para nova avaliação humana.
            if (!string.IsNullOrWhiteSpace(request.Answer))
                return MappingDraftRuleStatus.Proposed;

            return null;
        }

        private object ToDraftSummaryResponse(MappingDraftSummary draft) => new
        {
            draftId = draft.DraftId,
            workspaceId = draft.WorkspaceId,
            packageId = draft.PackageId,
            revisionId = draft.RevisionId,
            engine = draft.Engine,
            createdAt = draft.CreatedAt,
            rulesCount = draft.RulesCount,
            fiscalProfile = draft.FiscalProfile == null ? null : ToFiscalProfileResponse(draft.FiscalProfile),
        };

        private object ToDraftResponse(MappingDraftDetail draft) => new
        {
            draftId = draft.DraftId,
            workspaceId = draft.WorkspaceId,
            packageId = draft.PackageId,
            revisionId = draft.RevisionId,
            engine = draft.Engine,
            createdAt = draft.CreatedAt,
            rules = draft.Rules.Select(ToRuleResponse),
            // Issue #379 (ADR §2.6): perfil de trabalho do draft + resolvedXsd derivado (eco de
            // XsdValidation:DocumentTypes, não persistido).
            fiscalProfile = draft.FiscalProfile == null ? null : ToFiscalProfileResponse(draft.FiscalProfile),
        };

        private object ToFiscalProfileResponse(FiscalProfile profile) => new
        {
            documentType = profile.DocumentType,
            schemaVersion = profile.SchemaVersion,
            operation = profile.Operation,
            jurisdiction = profile.Jurisdiction,
            resolvedXsd = _fiscalProfileResolver.Resolve(profile.DocumentType, profile.SchemaVersion),
        };

        private static object ToRuleResponse(MappingDraftRuleDetail rule) => new
        {
            ruleId = rule.RuleId,
            draftId = rule.DraftId,
            sourceRefs = rule.SourceRefs,
            targetRefs = rule.TargetRefs,
            operation = rule.Operation,
            conditions = rule.ConditionsJson,
            transformations = rule.TransformationsJson,
            cardinality = rule.Cardinality,
            evidence = rule.Evidence,
            confidence = rule.Confidence,
            status = rule.Status,
            questions = rule.OpenQuestions,
            createdAt = rule.CreatedAt,
            eTag = rule.ETag,
        };
    }
}
