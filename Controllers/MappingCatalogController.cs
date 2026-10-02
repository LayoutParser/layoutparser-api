using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Filters;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Catálogo unificado de mapeadores (TCL/XSL/XSLT) em árvore de 3 níveis: origem &gt; pasta &gt; item
    /// (issue #631, <c>docs/architecture/mapping-catalog-design.md</c> D5). A leitura vem do índice local
    /// (IdentityDatabase) — funciona com a origem fora do ar, sinalizando <c>sourceStatus</c>
    /// (<c>ok|stale|unavailable</c>). Se o índice cair, só este catálogo responde 503. Lógica nos serviços.
    /// </summary>
    [ApiController]
    [Route("api/mapping-catalog")]
    public class MappingCatalogController : ControllerBase
    {
        private readonly IMappingCatalogService _catalog;

        public MappingCatalogController(IMappingCatalogService catalog)
        {
            _catalog = catalog;
        }

        /// <summary>Nível 1: origens com contagens, último sync e status.</summary>
        /// <response code="200">Lista de origens (<c>sourceSystem</c> em snake_case: connect_us, neogrid, map4connect, own).</response>
        /// <response code="503">Índice do catálogo indisponível.</response>
        [HttpGet("sources")]
        [ProducesResponseType(typeof(IReadOnlyList<MappingCatalogSourceSummary>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Sources(CancellationToken cancellationToken)
            => Map(await _catalog.ListSourcesAsync(cancellationToken));

        /// <summary>Nível 2: pastas (projetos) de uma origem, ordem alfabética estável, paginado.</summary>
        /// <param name="sourceSystem">connect_us | neogrid | map4connect | own.</param>
        /// <param name="page">Página (1..). Default 1.</param>
        /// <param name="pageSize">Itens por página (default 50, máx. 200).</param>
        /// <response code="400"><c>sourceSystem</c> desconhecido.</response>
        [HttpGet("sources/{sourceSystem}/folders")]
        [ProducesResponseType(typeof(MappingCatalogListResponse<MappingCatalogFolderDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Folders(string sourceSystem, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
        {
            if (!SourceSystemExtensions.TryParseWireName(sourceSystem, out var system))
                return BadRequest(new { error = "sourceSystem inválido. Use: connect_us, neogrid, map4connect ou own." });
            return Map(await _catalog.ListFoldersAsync(system, page, pageSize, cancellationToken));
        }

        /// <summary>Nível 3: itens de uma pasta, paginado, ordem <c>nome + catalogId</c> (total e estável).</summary>
        /// <param name="folderId">Id da pasta.</param>
        /// <param name="page">Página (1..). Default 1.</param>
        /// <param name="pageSize">Itens por página (default 50, máx. 200).</param>
        /// <param name="engine">Filtro opcional: tcl | xsl | xslt.</param>
        /// <param name="q">Busca opcional por trecho do nome (sem diferenciar maiúsculas/acentos).</param>
        /// <param name="includeRetired">Inclui itens retirados (default false).</param>
        /// <response code="400"><c>engine</c> inválido.</response>
        /// <response code="404">Pasta inexistente.</response>
        [HttpGet("folders/{folderId:guid}/items")]
        [ProducesResponseType(typeof(MappingCatalogListResponse<MappingCatalogItemView>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Items(Guid folderId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
            [FromQuery] string? engine = null, [FromQuery] string? q = null, [FromQuery] bool includeRetired = false,
            CancellationToken cancellationToken = default)
        {
            string? normalizedEngine = null;
            if (!string.IsNullOrWhiteSpace(engine))
            {
                normalizedEngine = engine.Trim().ToLowerInvariant();
                if (normalizedEngine is not (MappingCatalogEngine.Tcl or MappingCatalogEngine.Xsl or MappingCatalogEngine.Xslt))
                    return BadRequest(new { error = "engine inválido. Use: tcl, xsl ou xslt." });
            }
            return Map(await _catalog.ListItemsAsync(folderId, normalizedEngine, q, includeRetired, page, pageSize, cancellationToken));
        }

        /// <summary>Detalhe de um item (referência única, por <c>catalogId</c> opaco).</summary>
        /// <response code="404">Item inexistente.</response>
        [HttpGet("items/{catalogId:guid}")]
        [ProducesResponseType(typeof(MappingCatalogItemView), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetItem(Guid catalogId, CancellationToken cancellationToken = default)
            => Map(await _catalog.GetItemAsync(catalogId, cancellationToken));

        /// <summary>Corpo TCL/XSL/XSLT do item, buscado na origem (única chamada que a toca).</summary>
        /// <response code="404">Item ou conteúdo inexistente.</response>
        /// <response code="503">Origem ou índice indisponível.</response>
        [HttpGet("items/{catalogId:guid}/content")]
        [ServiceFilter(typeof(AuditActionFilter))]
        [ProducesResponseType(typeof(MappingCatalogContentResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetContent(Guid catalogId, CancellationToken cancellationToken = default)
            => Map(await _catalog.GetContentAsync(catalogId, cancellationToken));

        private IActionResult Map<T>(CatalogResult<T> result) => result.Outcome switch
        {
            CatalogOutcome.Ok => Ok(result.Value),
            CatalogOutcome.NotFound => NotFound(new { error = result.Message }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.Message }),
        };
    }
}
