using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Catálogo unificado de mapeadores (TCL/XSL/XSLT) em árvore de 3 níveis: origem &gt; pasta &gt; item
    /// (issue #631, <c>docs/architecture/mapping-catalog-design.md</c> D5). A leitura vem do índice local
    /// (IdentityDatabase) — funciona com a origem fora do ar, sinalizando <c>sourceStatus</c>
    /// (<c>ok|stale|unavailable</c>). Se o índice cair, só este catálogo responde 503. Lógica nos serviços.
    /// </summary>
    /// <remarks>
    /// <para><b>Identidade:</b> o item é endereçado SEMPRE por <c>catalogId</c> (GUID v5 opaco e determinístico). O
    /// <c>mapperGuid</c> NÃO é chave (repete entre projetos) e está <b>deprecated</b> como forma de endereçar: aparece
    /// apenas como <c>sourceItemKey</c> informativo. É a mesma referência usada pelo parser de TXT
    /// (<c>catalogId</c> opcional em <c>POST api/TransformationExecution/execute</c> e <c>execute-candidates</c>).</para>
    /// <para><b>Origens</b> (<c>sourceSystem</c>, snake_case): <c>connect_us</c>, <c>neogrid</c>, <c>map4connect</c>, <c>own</c>.
    /// <b>Motores</b> (<c>engine</c>): <c>tcl</c>, <c>xsl</c>, <c>xslt</c>. O sync de cada origem é DESLIGADO por padrão
    /// (<c>MappingCatalog:Sources:{Origem}:Enabled</c>); leitura de árvore e <c>content</c> usam o que já estiver no índice.</para>
    /// <para><b>Resiliência:</b> a árvore vem do índice local (funciona com a origem fora); só <c>content</c> toca a origem
    /// e falha como 503 sem derrubar o resto. Conteúdo cifrado nunca é devolvido.</para>
    /// </remarks>
    [ApiController]
    [Route("api/mapping-catalog")]
    public class MappingCatalogController : ControllerBase
    {
        private readonly IMappingCatalogService _catalog;
        private readonly IMappingCatalogSyncTrigger _syncTrigger;

        public MappingCatalogController(IMappingCatalogService catalog, IMappingCatalogSyncTrigger syncTrigger)
        {
            _catalog = catalog;
            _syncTrigger = syncTrigger;
        }

        /// <summary>Nível 1: origens com contagens, último sync e status.</summary>
        /// <remarks>Itens: <c>{ sourceSystem, enabled, lastSyncUtc?, status (ok|stale|unavailable), lastError?, folderCount, itemCount }</c>.</remarks>
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
        /// <remarks>
        /// Campos: <c>catalogId</c>, <c>sourceSystem</c>, <c>engine</c>, <c>name</c>, <c>folderId</c>, <c>projectId?</c>, <c>projectName?</c>,
        /// <c>sourceItemKey</c> (informativo; NÃO use como chave), <c>version?</c>, <c>docType?</c>, <c>contentHash?</c> (versiona o corpo),
        /// <c>retired</c>, <c>detailUrl</c>, <c>pairedCatalogId?</c> (par TCL&lt;-&gt;XSL da Neogrid), <c>sourceStatus</c>. Retirados continuam resolvíveis.
        /// </remarks>
        /// <response code="404">Item inexistente.</response>
        [HttpGet("items/{catalogId:guid}")]
        [ProducesResponseType(typeof(MappingCatalogItemView), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetItem(Guid catalogId, CancellationToken cancellationToken = default)
            => Map(await _catalog.GetItemAsync(catalogId, cancellationToken));

        /// <summary>Corpo TCL/XSL/XSLT do item, buscado na origem (única chamada que a toca).</summary>
        /// <remarks>Resposta: <c>{ catalogId, engine, content }</c>. O corpo nunca é logado; conteúdo cifrado (ConnectUs sem descriptografia) vira 404.</remarks>
        /// <response code="404">Item ou conteúdo inexistente.</response>
        /// <response code="503">Origem ou índice indisponível.</response>
        [HttpGet("items/{catalogId:guid}/content")]
        [ServiceFilter(typeof(AuditActionFilter))]
        [ProducesResponseType(typeof(MappingCatalogContentResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetContent(Guid catalogId, CancellationToken cancellationToken = default)
            => Map(await _catalog.GetContentAsync(catalogId, cancellationToken));

        /// <summary>
        /// Dispara o sync do índice em background (gatilho manual) e responde na hora. Restrito a sudo
        /// (não-sudo recebe 404). Só sincroniza origens com o flag <c>MappingCatalog:Sources:{Origem}:Enabled</c> ligado.
        /// </summary>
        /// <param name="sourceSystem">Opcional: restringe a uma origem (connect_us | neogrid | map4connect | own).</param>
        /// <response code="202">Sync enfileirado; acompanhe por <c>GET sources</c> (<c>lastSyncUtc</c>/<c>status</c>).</response>
        /// <response code="400"><c>sourceSystem</c> desconhecido.</response>
        /// <response code="409">Origem (ou todas) desligada na configuração.</response>
        /// <response code="429">Fila de sync cheia.</response>
        [HttpPost("sync")]
        [RequireSudo]
        [ServiceFilter(typeof(AuditActionFilter))]
        public IActionResult Sync([FromQuery] string? sourceSystem = null)
        {
            SourceSystem? only = null;
            if (!string.IsNullOrWhiteSpace(sourceSystem))
            {
                if (!SourceSystemExtensions.TryParseWireName(sourceSystem, out var parsed))
                    return BadRequest(new { error = "sourceSystem inválido. Use: connect_us, neogrid, map4connect ou own." });
                only = parsed;
            }
            return _syncTrigger.Trigger(only) switch
            {
                CatalogSyncTriggerResult.Accepted => Accepted(new { status = "queued", sourceSystem = only?.ToWireName() }),
                CatalogSyncTriggerResult.Disabled => Conflict(new { error = "Sync desligado na configuração para a(s) origem(ns) pedida(s) (MappingCatalog:Sources:{Origem}:Enabled)." }),
                _ => StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Fila de sync cheia; tente novamente em instantes." }),
            };
        }

        private IActionResult Map<T>(CatalogResult<T> result) => result.Outcome switch
        {
            CatalogOutcome.Ok => Ok(result.Value),
            CatalogOutcome.NotFound => NotFound(new { error = result.Message }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.Message }),
        };
    }
}
