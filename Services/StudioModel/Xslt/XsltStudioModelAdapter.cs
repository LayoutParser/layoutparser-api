using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

namespace LayoutParserApi.Services.StudioModel.Xslt
{
    /// <summary>
    /// XSLT do mapper: <c>Xslt</c> nulo = não foi possível obter (ver <c>Failure</c>); <c>Exists=false</c> → 404.
    /// <c>TargetLayoutXml</c> é o LayoutVO de destino (árvore de destino), quando disponível.
    /// </summary>
    public sealed record XsltSourceResult(bool Exists, string? Xslt, string? TargetLayoutGuid, string? TargetLayoutXml, string? Failure);

    /// <summary>Origem do XSLT de um mapper. Lança <see cref="StudioModelUnavailableException"/> se a fonte (catálogo) estiver fora.</summary>
    public interface IXsltSource
    {
        Task<XsltSourceResult> GetAsync(string mapperGuid, CancellationToken ct);
    }

    /// <summary>
    /// O XSLT persistido do mapper (<c>Mapper.XslContent</c>, extraído do MapperVO descriptografado pelo
    /// <c>MapperDatabaseService</c>) + o layout de DESTINO do mapper. Somente leitura, sem geração nem efeito colateral.
    /// </summary>
    public sealed class MapperXsltSource : IXsltSource
    {
        private readonly ICachedMapperService _mappers;
        private readonly ICachedLayoutService _layouts;
        private readonly ILogger<MapperXsltSource> _logger;

        public MapperXsltSource(ICachedMapperService mappers, ICachedLayoutService layouts, ILogger<MapperXsltSource> logger)
        {
            _mappers = mappers;
            _layouts = layouts;
            _logger = logger;
        }

        public async Task<XsltSourceResult> GetAsync(string mapperGuid, CancellationToken ct)
        {
            List<Models.Entities.Mapper> all;
            try { all = await _mappers.GetAllMappersAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Studio-model/xslt: falha ao consultar catálogo de mappers para {MapperGuid}.", mapperGuid);
                throw new StudioModelUnavailableException("Catálogo de mappers indisponível.", ex);
            }

            var mapper = all.FirstOrDefault(m => string.Equals(m.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));
            if (mapper == null) return new XsltSourceResult(false, null, null, null, null);

            var guid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid;
            if (string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(mapper.DecryptedContent))
            {
                try { guid = MapperVoReader.Read(mapper.DecryptedContent).TargetLayoutGuid; }
                catch (Exception ex) { _logger.LogWarning(ex, "Studio-model/xslt: MapperVO {MapperGuid} ilegível ao buscar layout de destino.", mapperGuid); }
            }

            var xslt = string.IsNullOrWhiteSpace(mapper.XslContent) ? null : mapper.XslContent;
            string? layoutXml = null;
            string? failure = xslt == null ? "Mapper sem XSLT associado." : null;

            if (string.IsNullOrWhiteSpace(guid))
                failure ??= "Mapper sem GUID de layout de destino.";
            else
            {
                try
                {
                    var record = await _layouts.GetLayoutByGuidAsync(guid);
                    if (record == null)
                    {
                        const string prefix = "LAY_";
                        var alt = guid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? guid[prefix.Length..] : prefix + guid;
                        record = await _layouts.GetLayoutByGuidAsync(alt);
                    }
                    layoutXml = string.IsNullOrWhiteSpace(record?.DecryptedContent) ? null : record!.DecryptedContent;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Studio-model/xslt: falha ao buscar layout de destino {LayoutGuid}.", guid);
                }
            }

            return new XsltSourceResult(true, xslt, guid, layoutXml, failure);
        }
    }

    /// <summary>
    /// Adaptador XSLT (Fase 3) — SOMENTE LEITURA: árvore de destino (LayoutVO do mapper) + links/rules extraídos do XSLT
    /// (<see cref="XsltReader"/>); árvore de entrada só com o que o XSLT referencia. <c>eTag</c>/<c>rawHash</c> sobre o XSLT bruto.
    /// Nunca loga o conteúdo do XSLT nem do layout.
    /// </summary>
    public sealed class XsltStudioModelAdapter : IStudioModelAdapter
    {
        public const int SchemaVersion = 1;

        private readonly IXsltSource _source;
        private readonly ILogger<XsltStudioModelAdapter> _logger;
        private readonly IDataTypeCatalog? _dataTypes;

        public XsltStudioModelAdapter(IXsltSource source, ILogger<XsltStudioModelAdapter> logger, IDataTypeCatalog? dataTypes = null)
        {
            _source = source;
            _logger = logger;
            _dataTypes = dataTypes;
        }

        public string Engine => "xslt";

        public StudioCapabilities Capabilities { get; } = new(false, new List<string>());

        public async Task<StudioModelDocument?> LoadAsync(StudioModelRequest request, CancellationToken ct)
        {
            var src = await _source.GetAsync(request.MapperGuid, ct);
            if (!src.Exists) return null;

            var diagnostics = new List<StudioDiagnostic>();
            var nodes = new Dictionary<string, StudioNode>(StringComparer.Ordinal);

            // --- árvore de destino: degrada, nunca lança ---
            StudioTree target;
            if (src.TargetLayoutXml == null)
            {
                diagnostics.Add(new StudioDiagnostic("LAYOUT_UNAVAILABLE", "warning", "Layout de destino indisponível.", Id: src.TargetLayoutGuid, Path: "target"));
                target = new StudioTree(src.TargetLayoutGuid, "unknown", new List<string>(), false);
            }
            else
            {
                try
                {
                    var read = LayoutVoReader.Read(src.TargetLayoutXml, "target", _dataTypes == null ? null : _dataTypes.ResolveName);
                    diagnostics.AddRange(read.Diagnostics);
                    foreach (var (id, node) in read.Nodes) nodes.TryAdd(id, node);
                    target = new StudioTree(read.LayoutGuid ?? src.TargetLayoutGuid, read.Format, read.RootIds, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Studio-model/xslt: layout de destino {LayoutGuid} ilegível.", src.TargetLayoutGuid);
                    diagnostics.Add(new StudioDiagnostic("LAYOUT_UNAVAILABLE", "warning", "Layout de destino ilegível.", Id: src.TargetLayoutGuid, Path: "target"));
                    target = new StudioTree(src.TargetLayoutGuid, "unknown", new List<string>(), false);
                }
            }

            // --- XSLT: leitor já não lança; defesa em profundidade ---
            XsltReadResult read2;
            var targetOnly = nodes.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
            try { read2 = XsltReader.Read(src.Xslt, targetOnly); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Studio-model/xslt: leitor falhou para {MapperGuid}.", request.MapperGuid);
                read2 = new XsltReadResult(new(), new(), new(), new(), new(), new() { new StudioDiagnostic("XSLT_UNPARSEABLE", "warning", "XSLT ilegível; vínculos e regras indisponíveis.") });
            }

            if (src.Xslt == null)
                diagnostics.Add(new StudioDiagnostic("XSLT_UNAVAILABLE", "warning", src.Failure ?? "XSLT indisponível.", Id: request.MapperGuid));
            diagnostics.AddRange(read2.Diagnostics.Where(d => src.Xslt != null || d.Code != "XSLT_UNPARSEABLE"));

            foreach (var (id, node) in read2.InputNodes) nodes.TryAdd(id, node);
            foreach (var id in read2.LinkedTargetIds)
                if (nodes.TryGetValue(id, out var n)) nodes[id] = n with { Display = n.Display with { Linked = true } };

            diagnostics.AddRange(DiagnosticsBuilder.Build(nodes, read2.Links, read2.Rules));

            var xsltHash = StudioModelHasher.HashSource(src.Xslt ?? string.Empty);
            var targetHash = StudioModelHasher.HashSource(src.TargetLayoutXml);
            var artifact = new StudioArtifact(
                Engine, request.MapperGuid, null, xsltHash!,
                StudioModelHasher.ComputeETag(xsltHash, null, targetHash, SchemaVersion),
                new List<string>(),
                new List<StudioSource> { new("xslt", request.MapperGuid, xsltHash), new("target-layout", src.TargetLayoutGuid, targetHash) });

            _logger.LogInformation("Studio-model/xslt montado para {MapperGuid}: {NodeCount} nós, {LinkCount} vínculos, {RuleCount} regras, {DiagnosticCount} diagnósticos.",
                request.MapperGuid, nodes.Count, read2.Links.Count, read2.Rules.Count, diagnostics.Count);

            return new StudioModelDocument(
                SchemaVersion, artifact, Capabilities,
                new StudioTrees(new StudioTree(null, "xml", read2.InputRootIds, read2.InputNodes.Count > 0), target),
                nodes, read2.Links, read2.Rules, new Dictionary<string, StudioDataType>(), diagnostics);
        }
    }
}
