using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    public sealed class CreateTestSuiteRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public sealed class AddTestSuiteFixtureRequest
    {
        public string? Name { get; set; }
        public string? InputXml { get; set; }
        public string? ExpectedXml { get; set; }
        public string? XsdVersion { get; set; }
    }

    public sealed class RunTestSuiteRequest
    {
        public Guid ReleaseId { get; set; }
    }

    /// <summary>
    /// Suíte de teste versionada do Fiscal Test Lab (issue #423) — agrupa múltiplas fixtures
    /// (pares XML input/gabarito) sob um mesmo <see cref="Models.Entities.Fiscal.MappingDraft"/> e
    /// permite rodá-las em bloco contra uma release específica, com histórico de execução persistido
    /// (comparação de regressão entre versões do mapping ao longo do tempo).
    /// </summary>
    /// <remarks>
    /// Escopo desta rodada (documentado na issue #423): CRUD de suíte/fixture + execução + histórico
    /// — NÃO inclui endpoint de comparação visual entre duas execuções (front-end,
    /// LayoutParserReact#204) nem edição/remoção de fixture (só create/list). O front consome o
    /// histórico paginado (<c>GET .../runs</c>) e monta a comparação no cliente.
    /// </remarks>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mapping-drafts/{draftId:guid}/test-suites")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public sealed class TestSuiteController : ControllerBase
    {
        private readonly ITestSuiteStore _suiteStore;
        private readonly IMappingDraftStore _draftStore;
        private readonly ITestSuiteRunService _runService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<TestSuiteController> _logger;

        public TestSuiteController(
            ITestSuiteStore suiteStore,
            IMappingDraftStore draftStore,
            ITestSuiteRunService runService,
            ICurrentUser currentUser,
            ILogger<TestSuiteController> logger)
        {
            _suiteStore = suiteStore;
            _draftStore = draftStore;
            _runService = runService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Cria uma suíte vazia — fixtures são adicionadas depois via <c>POST .../fixtures</c>.</summary>
        /// <remarks>RBAC: <c>mapper</c>/<c>fiscal_admin</c>/<c>owner</c>. Passa por <see cref="MappingEngineGuardFilter"/> (Sysmiddle nunca é mutado).</remarks>
        /// <response code="201">Suíte criada (<c>suiteId</c>, <c>draftId</c>, <c>name</c>, <c>description</c>, <c>createdByUserId</c>, <c>createdAt</c>, <c>eTag</c>); <c>Location</c> aponta para <c>GET {suiteId}</c>.</response>
        /// <response code="404">Sem identidade, não-membro, ou draft inexistente/de outro workspace.</response>
        /// <response code="422">Campo <c>name</c> ausente ou vazio.</response>
        [HttpPost]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        public async Task<IActionResult> CreateSuite(Guid workspaceId, Guid draftId, [FromBody] CreateTestSuiteRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (string.IsNullOrWhiteSpace(request.Name))
                return UnprocessableEntity(new { error = "Campo \"name\" obrigatório." });

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var suite = await _suiteStore.CreateSuiteAsync(workspaceId, draftId, request.Name.Trim(), request.Description, userId, cancellationToken);
            return CreatedAtAction(nameof(GetSuite), new { workspaceId, draftId, suiteId = suite.SuiteId }, ToSuiteResponse(suite));
        }

        /// <summary>Lista as suítes do draft.</summary>
        /// <response code="200">Array de suítes do draft.</response>
        /// <response code="404">Sem identidade, não-membro, ou draft inexistente/de outro workspace.</response>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet]
        public async Task<IActionResult> ListSuites(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var suites = await _suiteStore.ListByDraftAsync(draftId, cancellationToken);
            return Ok(suites.Select(ToSuiteResponse));
        }

        /// <summary>Consulta uma suíte.</summary>
        /// <response code="200">Suíte (mesmo formato do <c>POST</c>).</response>
        /// <response code="404">Sem identidade, não-membro, ou suíte inexistente/de outro draft/workspace.</response>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("{suiteId:guid}")]
        public async Task<IActionResult> GetSuite(Guid workspaceId, Guid draftId, Guid suiteId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            return Ok(ToSuiteResponse(suite));
        }

        /// <summary>Adiciona uma fixture (par XML input/gabarito) à suíte — mesmo par que <c>POST .../test-runs</c> aceita avulso, agora nomeado e agrupado.</summary>
        /// <remarks>RBAC: <c>mapper</c>/<c>fiscal_admin</c>/<c>owner</c>. Só cria e lista: não há edição nem remoção de fixture.</remarks>
        /// <response code="201">Fixture criada (<c>fixtureId</c>, <c>suiteId</c>, <c>name</c>, <c>inputXml</c>, <c>expectedXml</c>, <c>xsdVersion</c>, <c>sortOrder</c>, <c>createdAt</c>).</response>
        /// <response code="404">Sem identidade, não-membro, ou suíte inexistente/de outro draft/workspace.</response>
        /// <response code="422">Faltam <c>name</c>, <c>inputXml</c> ou <c>expectedXml</c>.</response>
        [HttpPost("{suiteId:guid}/fixtures")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        public async Task<IActionResult> AddFixture(Guid workspaceId, Guid draftId, Guid suiteId, [FromBody] AddTestSuiteFixtureRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (string.IsNullOrWhiteSpace(request.Name))
                return UnprocessableEntity(new { error = "Campo \"name\" obrigatório." });

            if (string.IsNullOrWhiteSpace(request.InputXml) || string.IsNullOrWhiteSpace(request.ExpectedXml))
                return UnprocessableEntity(new { error = "Campos \"inputXml\" e \"expectedXml\" obrigatórios." });

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            var fixture = await _suiteStore.AddFixtureAsync(suiteId, request.Name.Trim(), request.InputXml, request.ExpectedXml, request.XsdVersion, cancellationToken);
            return CreatedAtAction(nameof(ListFixtures), new { workspaceId, draftId, suiteId }, ToFixtureResponse(fixture));
        }

        /// <summary>Lista as fixtures da suíte, na ordem de execução.</summary>
        /// <response code="200">Array de fixtures ordenado por <c>sortOrder</c>.</response>
        /// <response code="404">Sem identidade, não-membro, ou suíte inexistente/de outro draft/workspace.</response>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("{suiteId:guid}/fixtures")]
        public async Task<IActionResult> ListFixtures(Guid workspaceId, Guid draftId, Guid suiteId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            var fixtures = await _suiteStore.ListFixturesAsync(suiteId, cancellationToken);
            return Ok(fixtures.Select(ToFixtureResponse));
        }

        /// <summary>
        /// Roda TODAS as fixtures da suíte contra a release informada e persiste o resultado agregado
        /// como uma nova entrada de histórico. Síncrono (ver <see cref="TestSuiteRunService"/>) — o 200
        /// já devolve o resultado completo, sem job pollável.
        /// </summary>
        /// <remarks>
        /// RBAC: <c>mapper</c>/<c>fiscal_admin</c>/<c>owner</c>. Corpo: <c>{ "releaseId": "&lt;guid&gt;" }</c>.
        /// Cada fixture roda com o mesmo motor do <c>POST .../test-runs</c> avulso (<c>engine</c> da
        /// release: <c>xslt</c> ou <c>tcl</c>) e gera um <c>MappingTestRunSummary</c>; o agregado
        /// <c>requiredGatesPassed</c> é verdadeiro só se TODAS as fixtures passarem.
        /// </remarks>
        /// <response code="200">Execução registrada: <c>runId</c>, <c>suiteId</c>, <c>releaseId</c>, <c>executedByUserId</c>, <c>executedAt</c>, <c>totalFixtures</c>, <c>passed</c>, <c>failed</c>, <c>requiredGatesPassed</c>, <c>durationMs</c>, <c>fixtureResults[]</c>.</response>
        /// <response code="404">Sem identidade ou não-membro do workspace.</response>
        /// <response code="422"><c>releaseId</c> ausente; suíte/draft/release inexistente ou fora do workspace/draft; ou suíte sem fixtures.</response>
        /// <response code="503">Falha inesperada ao executar (detalhe só no log).</response>
        [HttpPost("{suiteId:guid}/run")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        public async Task<IActionResult> RunSuite(Guid workspaceId, Guid draftId, Guid suiteId, [FromBody] RunTestSuiteRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (request.ReleaseId == Guid.Empty)
                return UnprocessableEntity(new { error = "Campo \"releaseId\" obrigatório — referencia a release compilada a testar." });

            var correlationId = HttpContext.TraceIdentifier;
            try
            {
                var run = await _runService.RunSuiteAsync(workspaceId, draftId, suiteId, request.ReleaseId, userId, correlationId, cancellationToken);
                return Ok(ToRunResponse(run));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Execução de suíte recusada (suite={SuiteId}, release={ReleaseId}).", suiteId, request.ReleaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao executar a suíte {SuiteId} contra a release {ReleaseId}.", suiteId, request.ReleaseId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível executar a suíte no momento." });
            }
        }

        /// <summary>Histórico de execuções da suíte, mais recente primeiro — a base para comparar regressão entre versões do mapping.</summary>
        /// <param name="workspaceId">Workspace da rota.</param>
        /// <param name="draftId">Draft dono da suíte.</param>
        /// <param name="suiteId">Suíte cujo histórico será listado.</param>
        /// <param name="page">Página, base 1. Ausente ou &lt;= 0 vira 1.</param>
        /// <param name="pageSize">Itens por página. Ausente ou &lt;= 0 vira 20.</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200"><c>{ items[], totalCount }</c> — cada item no formato do <c>POST .../run</c>.</response>
        /// <response code="404">Sem identidade, não-membro, ou suíte inexistente/de outro draft/workspace.</response>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("{suiteId:guid}/runs")]
        public async Task<IActionResult> ListRuns(Guid workspaceId, Guid draftId, Guid suiteId, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            var (items, totalCount) = await _suiteStore.ListRunsAsync(suiteId, page <= 0 ? 1 : page, pageSize <= 0 ? 20 : pageSize, cancellationToken);
            return Ok(new { items = items.Select(ToRunResponse), totalCount });
        }

        private static object ToSuiteResponse(TestSuiteDetail suite) => new
        {
            suiteId = suite.SuiteId,
            workspaceId = suite.WorkspaceId,
            draftId = suite.DraftId,
            name = suite.Name,
            description = suite.Description,
            createdByUserId = suite.CreatedByUserId,
            createdAt = suite.CreatedAt,
            eTag = suite.ETag,
        };

        private static object ToFixtureResponse(TestSuiteFixtureDetail fixture) => new
        {
            fixtureId = fixture.FixtureId,
            suiteId = fixture.SuiteId,
            name = fixture.Name,
            inputXml = fixture.InputXml,
            expectedXml = fixture.ExpectedXml,
            xsdVersion = fixture.XsdVersion,
            sortOrder = fixture.SortOrder,
            createdAt = fixture.CreatedAt,
        };

        private static object ToRunResponse(TestSuiteRunDetail run) => new
        {
            runId = run.RunId,
            suiteId = run.SuiteId,
            releaseId = run.ReleaseId,
            executedByUserId = run.ExecutedByUserId,
            executedAt = run.ExecutedAt,
            totalFixtures = run.TotalFixtures,
            passed = run.Passed,
            failed = run.Failed,
            requiredGatesPassed = run.RequiredGatesPassed,
            durationMs = run.DurationMs,
            fixtureResults = run.FixtureResults,
        };
    }
}
