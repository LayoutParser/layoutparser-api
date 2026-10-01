using LayoutParserApi.Services.Filters;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// <c>MappingExplanation</c> (Slice 4 — issue #226/#227). Contrato canônico, independente do
    /// motor. Rota de LEITURA — explicar Sysmiddle é exatamente o caso permitido (spec §4:
    /// Sysmiddle pode <c>explain</c>, nunca <c>author</c>), então
    /// <see cref="Services.Filters.MappingEngineGuardFilter"/> NÃO é aplicado aqui (design §3).
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mappings/{mappingId}/versions/{version}")]
    public class MappingExplanationController : ControllerBase
    {
        private readonly IMappingDraftStore _draftStore;
        private readonly IMappingExplanationAdapter _sysmiddleAdapter;
        private readonly IMappingExplanationAdapter _tclAdapter;
        private readonly IMappingExplanationAdapter _xsltAdapter;
        private readonly IIdentityWorkspaceService _identityWorkspaceService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingExplanationController> _logger;

        public MappingExplanationController(
            IMappingDraftStore draftStore,
            IEnumerable<IMappingExplanationAdapter> adapters,
            IIdentityWorkspaceService identityWorkspaceService,
            ICurrentUser currentUser,
            ILogger<MappingExplanationController> logger)
        {
            _draftStore = draftStore;
            _sysmiddleAdapter = adapters.Single(a => a.Engine == "sysmiddle");
            _tclAdapter = adapters.Single(a => a.Engine == "tcl");
            _xsltAdapter = adapters.Single(a => a.Engine == "xslt");
            _identityWorkspaceService = identityWorkspaceService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>
        /// Resolve <c>engine</c> a partir de <c>mappingId</c> (design §3): primeiro tenta como
        /// <c>draftId</c> (Slice 3), depois como <c>MapperGuid</c> Sysmiddle. Sempre 200 com o
        /// contrato canônico, exceto 404 fail-closed (sem membership OU nada resolve).
        /// </summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("explanation")]
        public async Task<IActionResult> GetExplanation(Guid workspaceId, string mappingId, string version, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            WorkspaceSummary? membership;
            try
            {
                membership = await _identityWorkspaceService.GetWorkspaceForMemberAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao verificar membership do workspace {WorkspaceId} para explicação de mapping.", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível verificar o workspace no momento." });
            }

            if (membership == null)
                return NotFound();

            var request = new MappingExplanationRequest(workspaceId, userId, mappingId, version);

            // 1) tenta como draftId (Slice 3) — o motor real do draft decide tcl vs. xslt.
            if (Guid.TryParse(mappingId, out var draftId))
            {
                MappingDraftDetail? draft;
                try
                {
                    draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao consultar draft {DraftId} para explicação.", draftId);
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o draft no momento." });
                }

                if (draft != null && draft.WorkspaceId == workspaceId)
                {
                    var draftAdapter = draft.Engine.ToLowerInvariant() switch
                    {
                        "tcl" => _tclAdapter,
                        "xslt" => _xsltAdapter,
                        _ => null,
                    };

                    if (draftAdapter == null)
                        return NotFound();

                    var draftExplanation = await draftAdapter.ExplainAsync(request, cancellationToken);
                    return draftExplanation == null ? NotFound() : Ok(draftExplanation);
                }
            }

            // 2) fallback: MapperGuid Sysmiddle real, catálogo tbMapper (não é escopado por workspace,
            // é read-only/global — só a existência do mapper importa).
            var sysmiddleExplanation = await _sysmiddleAdapter.ExplainAsync(request, cancellationToken);
            return sysmiddleExplanation == null ? NotFound() : Ok(sysmiddleExplanation);
        }
    }
}
