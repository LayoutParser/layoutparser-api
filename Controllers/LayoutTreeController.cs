using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// <c>GET .../mappings/{mappingId}/layout-tree</c> (issue #425, ADR "adr-layout-tree-endpoint-425").
    /// Devolve, para um mapeador Sysmiddle específico, a árvore completa do layout de origem e do
    /// layout de destino (hierarquia, atributos, cardinalidade) + os vínculos diretos entre nós das
    /// duas árvores — para o React replicar a UI de dupla-árvore do Connect Us. Rota de LEITURA,
    /// mesma justificativa de <see cref="MappingExplanationController"/> quanto a não aplicar
    /// <see cref="MappingEngineGuardFilter"/> (explicar/ler Sysmiddle é sempre permitido).
    ///
    /// <para><b>Fora de escopo (não implementado aqui):</b> descoberta de qual mapper corresponde a
    /// um workspace (issue #417) — este endpoint é parametrizado por <c>mappingId</c> já conhecido
    /// pelo chamador.</para>
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mappings/{mappingId}")]
    public class LayoutTreeController : ControllerBase
    {
        private readonly ILayoutTreeService _layoutTreeService;
        private readonly ILogger<LayoutTreeController> _logger;

        public LayoutTreeController(ILayoutTreeService layoutTreeService, ILogger<LayoutTreeController> logger)
        {
            _layoutTreeService = layoutTreeService;
            _logger = logger;
        }

        /// <summary>
        /// RBAC: qualquer papel de membro (leitura, mesmo espírito de
        /// <c>MappingDraftsController.ListDrafts</c>) — não-membro ou sem identidade → 404
        /// (<see cref="RequireWorkspaceRoleFilter"/>). <c>mappingId</c> não resolvido no catálogo
        /// <c>tbMapper</c> → 404 também (indistinguível, mesmo padrão fail-closed do resto da API).
        /// </summary>
        [HttpGet("layout-tree")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        public async Task<IActionResult> GetLayoutTree(Guid workspaceId, string mappingId, CancellationToken cancellationToken)
        {
            try
            {
                var tree = await _layoutTreeService.GetLayoutTreeAsync(mappingId, cancellationToken);
                return tree == null ? NotFound() : Ok(tree);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao montar árvore de layout para o mapper {MapperGuid}.", mappingId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o catálogo de mappers no momento." });
            }
        }
    }
}
