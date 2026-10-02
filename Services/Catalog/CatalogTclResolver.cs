using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Catalog
{
    /// <summary>TCL obtido do catálogo: conteúdo + metadados para rastreio (versionado por <see cref="ContentHash"/>, design D4/R5).</summary>
    public sealed record CatalogTclResult(string Content, Guid CatalogId, SourceSystem SourceSystem, string? ContentHash, bool Retired);

    /// <summary>
    /// Resolve o TCL de um item do catálogo unificado pelo <c>catalogId</c> (issue #636, design D4): é a
    /// MESMA referência que o portal usa — não existe segundo registro de TCL. Fallback do pipeline de
    /// TXT: o arquivo em disco continua sendo a primeira fonte.
    /// </summary>
    public interface ICatalogTclResolver
    {
        /// <summary>
        /// Devolve o TCL (engine <c>tcl</c>) do item, ou <c>null</c> (item inexistente/de outro motor, origem
        /// desligada ou indisponível, conteúdo vazio ou que não parece XML — p.ex. cifra). NUNCA lança.
        /// </summary>
        Task<CatalogTclResult?> ResolveTclAsync(Guid catalogId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Regras (todas degradam para <c>null</c>, o pipeline segue em <c>map_not_found</c> exato):
    /// <list type="bullet">
    /// <item>só <c>engine=tcl</c> (um XSL/XSLT nunca é aceito como mapa de entrada);</item>
    /// <item>respeita a flag <c>MappingCatalog:Sources:{Origem}:Enabled</c> — origem desligada não é lida
    /// (rollout seguro em produção: nada muda enquanto o dono não ligar);</item>
    /// <item>item <c>Retired</c> continua resolvível (design D3), sinalizado no resultado;</item>
    /// <item>nunca devolve cifra: o corpo precisa começar com <c>&lt;</c> (TCL é XML), senão é descartado
    /// e só o catalogId/origem vão ao log — jamais o conteúdo.</item>
    /// </list>
    /// </summary>
    public sealed class CatalogTclResolver : ICatalogTclResolver
    {
        private readonly IMappingCatalogStore _store;
        private readonly IEnumerable<IMappingCatalogSource> _sources;
        private readonly IOptionsMonitor<MappingCatalogOptions> _options;
        private readonly ILogger<CatalogTclResolver> _logger;

        public CatalogTclResolver(IMappingCatalogStore store, IEnumerable<IMappingCatalogSource> sources,
            IOptionsMonitor<MappingCatalogOptions> options, ILogger<CatalogTclResolver> logger)
        {
            _store = store;
            _sources = sources;
            _options = options;
            _logger = logger;
        }

        public async Task<CatalogTclResult?> ResolveTclAsync(Guid catalogId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (catalogId == Guid.Empty)
                    return null;

                var found = await _store.FindItemAsync(catalogId, cancellationToken);
                if (!found.Available || found.Value is null)
                {
                    _logger.LogInformation("Catálogo: item {CatalogId} não encontrado/indisponível para resolver o TCL.", catalogId);
                    return null;
                }

                var item = found.Value;
                if (!string.Equals(item.Engine, MappingCatalogEngine.Tcl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Catálogo: item {CatalogId} tem engine {Engine}, não tcl — recusado como mapa de entrada.", catalogId, item.Engine);
                    return null;
                }

                if (!_options.CurrentValue.IsEnabled(item.SourceSystem))
                {
                    _logger.LogInformation("Catálogo: origem {SourceSystem} desligada (MappingCatalog:Sources); TCL do item {CatalogId} não lido.",
                        item.SourceSystem.ToWireName(), catalogId);
                    return null;
                }

                var adapter = _sources.FirstOrDefault(s => s.System == item.SourceSystem);
                if (adapter is null)
                {
                    _logger.LogWarning("Catálogo: origem {SourceSystem} sem adaptador registrado (item {CatalogId}).", item.SourceSystem.ToWireName(), catalogId);
                    return null;
                }

                var content = await adapter.GetContentAsync(new MappingCatalogSourceRef(item.SourceItemKey, item.SourceRefJson), cancellationToken);
                if (content is null || string.IsNullOrWhiteSpace(content.Content))
                    return null;

                if (!content.Content.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('<'))
                {
                    // Cifra ou lixo: nunca vai para o parser nem para o log.
                    _logger.LogWarning("Catálogo: conteúdo do item {CatalogId} ({SourceSystem}) não é XML; descartado.", catalogId, item.SourceSystem.ToWireName());
                    return null;
                }

                return new CatalogTclResult(content.Content, catalogId, item.SourceSystem, item.ContentHash, item.Retired);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catálogo: falha ao resolver o TCL do item {CatalogId}; seguindo sem fallback.", catalogId);
                return null;
            }
        }
    }
}
