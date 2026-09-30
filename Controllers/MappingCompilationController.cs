using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;

using XslSynth.Core;

namespace LayoutParserApi.Controllers
{
    public sealed class CreateTestRunRequest
    {
        public Guid ReleaseId { get; set; }
        public string? InputXml { get; set; }
        public string? ExpectedXml { get; set; }
        public string? XsdVersion { get; set; }
    }

    /// <summary>Corpo do <c>PATCH .../mapping-drafts/{draftId}/artifacts/{engine}</c> (issue #381).</summary>
    public sealed class UpdateArtifactRequest
    {
        public string? Content { get; set; }
        public string? Justification { get; set; }
    }

    /// <summary>
    /// Compilação determinística (<c>MappingDraftRule[] → XSLT/TCL</c>) e Fiscal Test Lab (Slice 5 —
    /// issue #231). Isolamento por workspace fail-closed (mesmo padrão dos Slices 1-4). Todas as rotas
    /// recusam <c>engine=sysmiddle</c> via <see cref="MappingEngineGuardFilter"/> — defesa em
    /// profundidade, já que o motor real vem do <c>MappingDraft</c> (validado na criação, Slice 3).
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public class MappingCompilationController : ControllerBase
    {
        private readonly IMappingDraftStore _draftStore;
        private readonly IMappingReleaseStore _releaseStore;
        private readonly IMappingCompileService _compileService;
        private readonly IMappingTestRunService _testRunService;
        private readonly IFiscalProfileResolver _fiscalProfileResolver;
        private readonly XsdValidationService _xsdValidationService;
        private readonly IRequiredCoverageCalculator _requiredCoverageCalculator;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingCompilationController> _logger;

        public MappingCompilationController(
            IMappingDraftStore draftStore,
            IMappingReleaseStore releaseStore,
            IMappingCompileService compileService,
            IMappingTestRunService testRunService,
            IFiscalProfileResolver fiscalProfileResolver,
            XsdValidationService xsdValidationService,
            IRequiredCoverageCalculator requiredCoverageCalculator,
            ICurrentUser currentUser,
            ILogger<MappingCompilationController> logger)
        {
            _draftStore = draftStore;
            _releaseStore = releaseStore;
            _compileService = compileService;
            _testRunService = testRunService;
            _fiscalProfileResolver = fiscalProfileResolver;
            _xsdValidationService = xsdValidationService;
            _requiredCoverageCalculator = requiredCoverageCalculator;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Dispara o job assíncrono de compilação — nunca bloqueia esperando a transpilação.</summary>
        [HttpPost("mapping-drafts/{draftId:guid}/compile")]
        public async Task<IActionResult> Compile(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var correlationId = HttpContext.TraceIdentifier;
            Guid jobId;
            try
            {
                jobId = await _compileService.EnqueueAsync(workspaceId, draftId, userId, correlationId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Compilação recusada para draft {DraftId}.", draftId);
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao iniciar compilação do draft {DraftId}.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível iniciar a compilação no momento." });
            }

            return AcceptedAtAction(nameof(GetCompileJob), new { workspaceId, draftId, jobId }, new { jobId, status = CompileJobStatus.Queued });
        }

        /// <summary>Status observável do job de compilação — não é fire-and-forget cego.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/compile/{jobId:guid}")]
        public async Task<IActionResult> GetCompileJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var state = await _compileService.GetStatusAsync(jobId, cancellationToken);
            if (state == null)
                return NotFound();

            return Ok(new { jobId = state.JobId, status = state.Status, releaseId = state.ReleaseId, error = state.Error, durationMs = state.DurationMs });
        }

        /// <summary>Consulta a release compilada — artefatos, diagnósticos de compilação e resultado do Fiscal Test Lab, se já executado.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/releases/{releaseId:guid}")]
        public async Task<IActionResult> GetRelease(Guid workspaceId, Guid draftId, Guid releaseId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var release = await _releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId || release.DraftId != draftId)
                return NotFound();

            return Ok(await ToReleaseResponseAsync(release, userId, cancellationToken));
        }

        /// <summary>
        /// Diff canônico entre os XMLs REAIS produzidos por duas releases do MESMO draft (issue #380,
        /// cross-check #198.2b) — reusa <see cref="CanonicalDiffer"/> sobre o <c>ActualXml</c>
        /// persistido no test-run de cada release (comparação release×release, não contra o
        /// gabarito). Agregado por elemento do schema alvo, espelhando o padrão de
        /// <see cref="MappingTestRunSummaryExtensions.GroupDivergencesByRule"/> (issue #367), mas
        /// agrupando por nome de elemento em vez de por regra.
        /// </summary>
        [HttpGet("mapping-drafts/{draftId:guid}/releases/diff")]
        public async Task<IActionResult> DiffReleases(
            Guid workspaceId, Guid draftId, [FromQuery] Guid fromReleaseId, [FromQuery] Guid toReleaseId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (fromReleaseId == Guid.Empty || toReleaseId == Guid.Empty)
                return UnprocessableEntity(new { error = "Parâmetros \"fromReleaseId\" e \"toReleaseId\" são obrigatórios." });

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var fromRelease = await _releaseStore.GetReleaseIfMemberAsync(fromReleaseId, userId, cancellationToken);
            if (fromRelease == null || fromRelease.WorkspaceId != workspaceId || fromRelease.DraftId != draftId)
                return NotFound();

            var toRelease = await _releaseStore.GetReleaseIfMemberAsync(toReleaseId, userId, cancellationToken);
            if (toRelease == null || toRelease.WorkspaceId != workspaceId || toRelease.DraftId != draftId)
                return NotFound();

            // ActualXml só existe quando o test-run rodou até aplicar o XSLT com sucesso (issue #380
            // — ver comentário em MappingTestRunSummary). Sem ele, não há o que comparar entre as
            // duas releases — 422 com orientação explícita, nunca adivinha.
            var fromActualXml = fromRelease.TestRunSummary?.ActualXml;
            var toActualXml = toRelease.TestRunSummary?.ActualXml;
            if (string.IsNullOrEmpty(fromActualXml) || string.IsNullOrEmpty(toActualXml))
            {
                return UnprocessableEntity(new
                {
                    error = "As duas releases precisam ter um test-run executado (com XSLT aplicado com sucesso) antes de comparar — rode POST .../test-runs para a(s) release(s) faltante(s).",
                });
            }

            IReadOnlyList<NodeDiff> diffs;
            try
            {
                diffs = new CanonicalDiffer().Diff(fromActualXml, toActualXml);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao calcular diff release×release ({FromReleaseId} × {ToReleaseId}).", fromReleaseId, toReleaseId);
                return UnprocessableEntity(new { error = "Não foi possível comparar o XML das duas releases — verifique se o test-run de ambas produziu XML válido." });
            }

            var diffsByElement = diffs
                .GroupBy(ExtractSchemaElement)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { element = g.Key, diffs = g.ToList() })
                .ToList();

            return Ok(new
            {
                fromReleaseId,
                toReleaseId,
                diffs,
                diffsByElement,
            });
        }

        /// <summary>Elemento do schema a que um <see cref="NodeDiff.XPath"/> se refere — último segmento antes de "@"/índice posicional.</summary>
        private static string ExtractSchemaElement(NodeDiff diff)
        {
            var withoutAttr = diff.XPath.Split('@')[0].TrimEnd('/');
            var segments = withoutAttr.Split('/');
            var last = segments.LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? withoutAttr;
            var bracketIndex = last.IndexOf('[');
            return bracketIndex >= 0 ? last[..bracketIndex] : last;
        }

        // engine="sysmiddle" nunca é aceito aqui: MappingEngineGuardFilter só enxerga query/body, não
        // este segmento de rota — a checagem explícita abaixo cobre o que o filtro de classe não vê.
        private static readonly IReadOnlyCollection<string> AllowedArtifactEngines = new[] { "tcl", "xslt" };

        /// <summary>
        /// Editor manual de artefato TCL/XSLT (issue #381 sub-fases 5b/5c, ADR
        /// <c>adr-edicao-manual-artefato-versionamento-2026-09-10.md</c>). Nunca sobrescreve a
        /// release-base — cria uma <see cref="MappingRelease"/> derivada com
        /// <c>ArtifactSource=manual_edit</c>. Concorrência otimista via <c>If-Match</c> (mesmo
        /// vocabulário 428/400/412 do <c>PATCH .../rules/{ruleId}</c>), mas o <c>200</c> aqui MUTA um
        /// recurso novo em vez de editar in-place (ADR §2.3).
        /// </summary>
        /// <remarks>
        /// RBAC (ADR §2.4): exige papel <c>mapper</c>/<c>fiscal_admin</c>/<c>owner</c> no workspace da
        /// rota — maior impacto que a edição de regra (produz release candidata). Sem membership → 404;
        /// papel insuficiente → 403. <c>engine=sysmiddle</c> ou fora de <c>{tcl,xslt}</c> → 422;
        /// <c>content</c>/<c>justification</c> ausentes → 422; sintaxe inválida (5c) → 422; sem release
        /// compilada do draft/engine para servir de base → 422; <c>If-Match</c> ausente → 428; não-base64
        /// → 400; divergente do hash atual → 412 com <c>current</c>. <b>Sem 401</b> (identidade vem do BFF).
        /// </remarks>
        [HttpPatch("mapping-drafts/{draftId:guid}/artifacts/{engine}")]
        [RequireWorkspaceRole(WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> UpdateArtifact(Guid workspaceId, Guid draftId, string engine, [FromBody] UpdateArtifactRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var normalizedEngine = engine?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedEngine) || !AllowedArtifactEngines.Contains(normalizedEngine))
                return UnprocessableEntity(new { error = $"\"engine\" deve ser um de: {string.Join(", ", AllowedArtifactEngines)}. \"sysmiddle\" é somente leitura/explicação." });

            if (string.IsNullOrEmpty(request.Content))
                return UnprocessableEntity(new { error = "Campo \"content\" obrigatório — texto completo do artefato editado." });

            if (string.IsNullOrWhiteSpace(request.Justification))
                return UnprocessableEntity(new { error = "Campo \"justification\" obrigatório para editar um artefato manualmente." });

            if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || string.IsNullOrWhiteSpace(ifMatchValues.ToString()))
                return StatusCode(StatusCodes.Status428PreconditionRequired, new { error = "Header If-Match é obrigatório para editar um artefato." });

            string expectedArtifactHash;
            try
            {
                expectedArtifactHash = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ifMatchValues.ToString().Trim('"')));
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Header If-Match inválido (esperado base64 do hash do artefato)." });
            }

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            // O draft só tem UM motor (Slice 3) — não existe artefato "tcl" gerado a partir de um draft
            // "xslt" (e vice-versa). Ainda não é o filtro de sysmiddle (já recusado acima); é a
            // consistência draft↔engine.
            if (!string.Equals(draft.Engine, normalizedEngine, StringComparison.OrdinalIgnoreCase))
                return UnprocessableEntity(new { error = $"O draft usa o motor \"{draft.Engine}\"; não há artefato \"{normalizedEngine}\" para editar." });

            if (!ArtifactSyntaxValidator.TryValidate(normalizedEngine, request.Content, out var syntaxError))
                return UnprocessableEntity(new { error = syntaxError });

            var correlationId = HttpContext.TraceIdentifier;
            CreateManualEditOutcome outcome;
            try
            {
                outcome = await _releaseStore.CreateManualEditArtifactReleaseAsync(
                    workspaceId, draftId, normalizedEngine, request.Content, request.Justification,
                    expectedArtifactHash, userId, correlationId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao editar artefato \"{Engine}\" do draft {DraftId}.", normalizedEngine, draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível salvar a edição do artefato no momento." });
            }

            if (outcome.Result == CreateManualEditResult.NoBaseRelease)
                return UnprocessableEntity(new { error = "Não há release compilada para este draft/engine — compile o draft antes de editar o artefato manualmente." });

            if (outcome.Result == CreateManualEditResult.Conflict)
                return StatusCode(StatusCodes.Status412PreconditionFailed, new
                {
                    error = "O artefato foi alterado por outra operação — recarregue e tente novamente.",
                    current = outcome.CurrentArtifact,
                });

            // ADR §4: aqui "eTag" é o hash do artefato NOVO (não o RowVersion genérico da release,
            // que é o que ToReleaseResponseAsync usa por padrão) — é contra ele que o próximo PATCH
            // deste engine casa o If-Match, não contra o RowVersion.
            return Ok(await ToReleaseResponseAsync(outcome.Release!, userId, cancellationToken,
                eTagOverride: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    outcome.Release!.Artifacts.First(a => a.Kind == normalizedEngine).Hash))));
        }

        /// <summary>
        /// Dispara o job assíncrono do Fiscal Test Lab contra a release compilada — nunca bloqueia
        /// esperando a execução do XSLT/diff. <c>engine=tcl</c> não tem runner determinístico neste
        /// slice: o job conclui com <c>RequiredGatesPassed=false</c> e diagnóstico explícito (nunca
        /// finge sucesso).
        /// </summary>
        [HttpPost("mapping-drafts/{draftId:guid}/test-runs")]
        public async Task<IActionResult> CreateTestRun(Guid workspaceId, Guid draftId, [FromBody] CreateTestRunRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (request.ReleaseId == Guid.Empty)
                return UnprocessableEntity(new { error = "Campo \"releaseId\" obrigatório — referencia a release compilada a testar." });

            if (string.IsNullOrWhiteSpace(request.InputXml) || string.IsNullOrWhiteSpace(request.ExpectedXml))
                return UnprocessableEntity(new { error = "Campos \"inputXml\" e \"expectedXml\" obrigatórios — fixture do Fiscal Test Lab." });

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var release = await _releaseStore.GetReleaseIfMemberAsync(request.ReleaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId || release.DraftId != draftId)
                return UnprocessableEntity(new { error = "\"releaseId\" não corresponde a uma release compilada deste draft." });

            var correlationId = HttpContext.TraceIdentifier;
            Guid jobId;
            try
            {
                jobId = await _testRunService.EnqueueAsync(
                    workspaceId, draftId, request.ReleaseId, userId, request.InputXml, request.ExpectedXml, request.XsdVersion, correlationId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Test-run recusado para release {ReleaseId}.", request.ReleaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao iniciar test-run da release {ReleaseId}.", request.ReleaseId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível iniciar o test-run no momento." });
            }

            return AcceptedAtAction(nameof(GetTestRunJob), new { workspaceId, draftId, jobId }, new { jobId, status = TestRunJobStatus.Queued });
        }

        /// <summary>Status observável do job de test-run.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/test-runs/{jobId:guid}")]
        public async Task<IActionResult> GetTestRunJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var state = await _testRunService.GetStatusAsync(jobId, cancellationToken);
            if (state == null)
                return NotFound();

            return Ok(new
            {
                jobId = state.JobId,
                status = state.Status,
                releaseId = state.ReleaseId,
                requiredGatesPassed = state.RequiredGatesPassed,
                error = state.Error,
                durationMs = state.DurationMs,
            });
        }

        private async Task<MappingReleaseResponse> ToReleaseResponseAsync(MappingReleaseDetail release, Guid userId, CancellationToken cancellationToken, string? eTagOverride = null)
        {
            var requiredCoverage = await ComputeRequiredCoverageAsync(release, userId, cancellationToken);
            return new MappingReleaseResponse
            {
                ReleaseId = release.ReleaseId,
                WorkspaceId = release.WorkspaceId,
                DraftId = release.DraftId,
                Engine = release.Engine,
                Artifacts = release.Artifacts,
                SourceRuleIds = release.SourceRuleIds,
                CompileDiagnostics = release.CompileDiagnostics,
                RulesSnapshotHash = release.RulesSnapshotHash,
                TestRunSummary = release.TestRunSummary,
                // Diff granular por regra (issue #367 / LayoutParserReact #228): mesma divergência de
                // testRunSummary.divergences, agrupada por ruleId — evita o front ter que fazer
                // divergences.filter(d => d.ruleId === x) no cliente. Não quebra o agregado existente.
                DivergencesByRuleId = release.TestRunSummary == null
                    ? null
                    : MappingTestRunSummaryExtensions.GroupDivergencesByRule(release.TestRunSummary),
                Status = release.Status,
                // Issue #381 (ADR §2.2/§4): "compiled" (default) ou "manual_edit". rulesDesynced é
                // derivado (não persistido) — o front desabilita o diff-por-regra e mostra o selo
                // "editado manualmente" quando true.
                ArtifactSource = release.ArtifactSource,
                DerivedFromReleaseId = release.DerivedFromReleaseId,
                ManualEditReason = release.ManualEditReason,
                ManuallyEditedArtifactKinds = release.ManuallyEditedArtifactKinds,
                RulesDesynced = release.RulesDesynced,
                CorrelationId = release.CorrelationId,
                CreatedAt = release.CreatedAt,
                ETag = eTagOverride ?? release.ETag,
                // Issue #379 (ADR §2.6): snapshot congelado do perfil fiscal da release + resolvedXsd
                // recalculado a partir do snapshot (estável — documentType+schemaVersion congelados).
                FiscalProfile = release.FiscalProfile == null ? null : ToFiscalProfileResponse(release.FiscalProfile, _fiscalProfileResolver),
                // Issue #380 (#198.5): cobertura estática de destinos obrigatórios do XSD alvo — null
                // quando a release não tem FiscalProfile (sem XSD resolvido), mesma semântica de
                // "perfil ausente" do #379.
                RequiredCoverage = requiredCoverage,
            };
        }

        /// <summary>
        /// Cobertura ESTÁTICA de destinos obrigatórios (issue #380, #198.5) — cruza os elementos/
        /// atributos <c>minOccurs&gt;=1</c>/<c>use=required</c> do XSD alvo (resolvido via
        /// <see cref="IFiscalProfileResolver"/>, mesmo <c>resolvedXsd</c> do #379) com os
        /// <c>TargetRefs</c> das regras accepted/edited que compuseram a release
        /// (<see cref="MappingRelease.SourceRuleIds"/>). Degrada para <c>null</c> — nunca lança —
        /// quando falta perfil fiscal, XSD não resolve, schema não carrega do disco, ou o elemento
        /// raiz não bate com o XSD configurado.
        /// </summary>
        private async Task<RequiredCoverageResponse?> ComputeRequiredCoverageAsync(MappingReleaseDetail release, Guid userId, CancellationToken cancellationToken)
        {
            if (release.FiscalProfile == null)
                return null;

            var resolvedXsd = _fiscalProfileResolver.Resolve(release.FiscalProfile.DocumentType, release.FiscalProfile.SchemaVersion);
            if (resolvedXsd == null)
                return null;

            var schemaSet = _xsdValidationService.TryLoadSchemaSet(resolvedXsd.XsdVersion);
            if (schemaSet == null)
                return null;

            var draft = await _draftStore.GetDraftIfMemberAsync(release.DraftId, userId, cancellationToken);
            if (draft == null)
                return null;

            var acceptedRuleIds = new HashSet<Guid>(release.SourceRuleIds);
            var targetRefs = draft.Rules
                .Where(r => acceptedRuleIds.Contains(r.RuleId))
                .SelectMany(r => r.TargetRefs)
                .ToList();

            var result = _requiredCoverageCalculator.Calculate(schemaSet, resolvedXsd.RootElement, resolvedXsd.Namespace, targetRefs);
            if (result == null)
                return null;

            return new RequiredCoverageResponse { Percent = result.Percent, Uncovered = result.Uncovered };
        }

        private static FiscalProfileResponse ToFiscalProfileResponse(FiscalProfile profile, IFiscalProfileResolver fiscalProfileResolver) => new()
        {
            DocumentType = profile.DocumentType,
            SchemaVersion = profile.SchemaVersion,
            Operation = profile.Operation,
            Jurisdiction = profile.Jurisdiction,
            ResolvedXsd = fiscalProfileResolver.Resolve(profile.DocumentType, profile.SchemaVersion),
        };
    }
}
