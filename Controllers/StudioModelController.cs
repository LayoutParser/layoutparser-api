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
    /// (somente leitura: adaptadores Sysmiddle, TCL e XSLT). Ver <c>docs/architecture/studio-model-design.md</c>.
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
        /// Devolve árvores de entrada/destino, vínculos, regras e diagnósticos (referências <c>REF_*</c> de <c>I.</c>/<c>T.</c> e integridade de ligações/regras; ver <see cref="LayoutParserApi.Models.Dtos.StudioModel.StudioDiagnostic"/>; somente visualização). 404: sem identidade/não-membro
        /// (<see cref="RequireWorkspaceRoleFilter"/>) ou mapper inexistente; 400: <c>engine</c> inválido;
        /// 501: engine conhecido sem adaptador registrado; 503: catálogo indisponível; 304: <c>If-None-Match</c> casa o eTag.
        /// Headers: <c>ETag</c> e <c>Cache-Control: private, no-cache</c>.
        /// </summary>
        /// <param name="workspaceId">GUID do workspace (rota).</param>
        /// <param name="mapperGuid">Identificador do mapper no catálogo (rota).</param>
        /// <param name="engine">Engine do artefato: <c>sysmiddle</c>, <c>tcl</c> (só árvore de entrada) ou <c>xslt</c> (destino + links/rules do XSLT, somente leitura).</param>
        /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
        /// <response code="200">Modelo montado (<see cref="LayoutParserApi.Models.Dtos.StudioModel.StudioModelDocument"/>).</response>
        /// <response code="304"><c>If-None-Match</c> casa o ETag atual.</response>
        /// <response code="400"><c>engine</c> inválido.</response>
        /// <response code="404">Sem identidade/não-membro ou mapper inexistente.</response>
        /// <response code="501">Engine válido sem adaptador registrado.</response>
        /// <response code="503">Catálogo de mappers indisponível.</response>
        [HttpGet("studio-model")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
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
