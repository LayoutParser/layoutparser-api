using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.StudioModel;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// <c>GET .../mappings/{mapperGuid}/studio-model?engine=sysmiddle</c> — modelo único do Mapping Studio
    /// (Fase 1: somente leitura, adaptador Sysmiddle). Ver <c>docs/architecture/studio-model-design.md</c>.
    /// Rota de LEITURA: não aplica <see cref="MappingEngineGuardFilter"/> nem auditoria (mesma justificativa do
    /// <see cref="LayoutTreeController"/>, que permanece intacto).
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mappings/{mapperGuid}")]
    public class StudioModelController : ControllerBase
    {
        private readonly IStudioModelService _service;
        private readonly ILogger<StudioModelController> _logger;

        public StudioModelController(IStudioModelService service, ILogger<StudioModelController> logger)
        {
            _service = service;
            _logger = logger;
        }

        /// <summary>
        /// Devolve árvores de entrada/destino, vínculos, regras e diagnósticos. 404: sem identidade/não-membro
        /// (<see cref="RequireWorkspaceRoleFilter"/>) ou mapper inexistente; 400: <c>engine</c> inválido;
        /// 501: engine sem adaptador (tcl/xslt na Fase 1); 503: catálogo indisponível; 304: <c>If-None-Match</c> casa o eTag.
        /// Headers: <c>ETag</c> e <c>Cache-Control: private, no-cache</c>.
        /// </summary>
        [HttpGet("studio-model")]
        [RequireWorkspaceRole(WorkspaceRole.Owner, WorkspaceRole.FiscalAdmin, WorkspaceRole.Mapper, WorkspaceRole.Reviewer, WorkspaceRole.Operator, WorkspaceRole.Viewer)]
        public async Task<IActionResult> GetStudioModel(Guid workspaceId, string mapperGuid, [FromQuery] string? engine, CancellationToken cancellationToken)
        {
            try
            {
                var model = await _service.GetAsync(workspaceId, mapperGuid, engine, cancellationToken);
                if (model == null) return NotFound();

                var etag = model.Artifact.ETag;
                Response.Headers[HeaderNames.ETag] = etag;
                Response.Headers[HeaderNames.CacheControl] = "private, no-cache";

                if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var inm) && inm.Any(v => v != null && v.Split(',').Any(t => t.Trim() == etag)))
                    return StatusCode(StatusCodes.Status304NotModified);

                return Ok(model);
            }
            catch (StudioModelInvalidEngineException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (StudioModelEngineNotSupportedException ex)
            {
                return StatusCode(StatusCodes.Status501NotImplemented, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                // Inclui StudioModelUnavailableException; nunca expõe conteúdo de XML.
                _logger.LogError(ex, "Falha ao montar studio-model para o mapper {MapperGuid}.", mapperGuid);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o catálogo de mappers no momento." });
            }
        }
    }
}
