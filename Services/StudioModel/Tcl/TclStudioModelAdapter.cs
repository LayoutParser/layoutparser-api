using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

namespace LayoutParserApi.Services.StudioModel.Tcl
{
    /// <summary>TCL do mapper: <c>Tcl</c> nulo = não foi possível obter (ver <c>Failure</c>); <c>Exists=false</c> → 404.</summary>
    public sealed record TclSourceResult(bool Exists, string? Tcl, string? LayoutGuid, string? Failure);

    /// <summary>Origem do TCL de um mapper. Lança <see cref="StudioModelUnavailableException"/> se a fonte (catálogo) estiver fora.</summary>
    public interface ITclSource
    {
        Task<TclSourceResult> GetAsync(string mapperGuid, CancellationToken ct);
    }

    /// <summary>
    /// Não há TCL persistido por mapper (o Sysmiddle não usa TCL): o TCL do mapper é o que o nosso motor GERA
    /// a partir do layout de ENTRADA dele (<see cref="TclGeneratorService"/>). Somente leitura, sem efeito colateral.
    /// </summary>
    public sealed class LayoutDerivedTclSource : ITclSource
    {
        private readonly ICachedMapperService _mappers;
        private readonly ICachedLayoutService _layouts;
        private readonly TclGeneratorService _generator;
        private readonly ILogger<LayoutDerivedTclSource> _logger;

        public LayoutDerivedTclSource(ICachedMapperService mappers, ICachedLayoutService layouts, TclGeneratorService generator, ILogger<LayoutDerivedTclSource> logger)
        {
            _mappers = mappers;
            _layouts = layouts;
            _generator = generator;
            _logger = logger;
        }

        public async Task<TclSourceResult> GetAsync(string mapperGuid, CancellationToken ct)
        {
            List<Models.Entities.Mapper> all;
            try { all = await _mappers.GetAllMappersAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Studio-model/tcl: falha ao consultar catálogo de mappers para {MapperGuid}.", mapperGuid);
                throw new StudioModelUnavailableException("Catálogo de mappers indisponível.", ex);
            }

            var mapper = all.FirstOrDefault(m => string.Equals(m.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));
            if (mapper == null || string.IsNullOrWhiteSpace(mapper.DecryptedContent))
                return new TclSourceResult(false, null, null, null);

            var guid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid;
            if (string.IsNullOrWhiteSpace(guid))
            {
                try { guid = Sysmiddle.MapperVoReader.Read(mapper.DecryptedContent).InputLayoutGuid; }
                catch (Exception ex) { _logger.LogWarning(ex, "Studio-model/tcl: MapperVO {MapperGuid} ilegível ao buscar layout de entrada.", mapperGuid); }
            }
            if (string.IsNullOrWhiteSpace(guid))
                return new TclSourceResult(true, null, null, "Mapper sem GUID de layout de entrada.");

            Models.Database.LayoutRecord? record;
            try
            {
                record = await _layouts.GetLayoutByGuidAsync(guid);
                if (record == null)
                {
                    const string prefix = "LAY_";
                    var alt = guid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? guid[prefix.Length..] : prefix + guid;
                    record = await _layouts.GetLayoutByGuidAsync(alt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Studio-model/tcl: falha ao buscar layout {LayoutGuid}.", guid);
                return new TclSourceResult(true, null, guid, "Falha ao consultar o layout de entrada.");
            }

            if (record == null || string.IsNullOrWhiteSpace(record.DecryptedContent))
                return new TclSourceResult(true, null, guid, "Layout de entrada não encontrado.");

            try
            {
                return new TclSourceResult(true, _generator.GenerateTclFromLayoutXml(record.DecryptedContent), guid, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Studio-model/tcl: falha ao gerar TCL do layout {LayoutGuid}.", guid);
                return new TclSourceResult(true, null, guid, "Layout de entrada ilegível; TCL não gerado.");
            }
        }
    }

    /// <summary>
    /// Adaptador TCL (Fase 2) — SOMENTE LEITURA: só árvore de entrada (line/field; <c>CHILD</c> vira linha aninhada),
    /// sem links/rules. <c>eTag</c>/<c>rawHash</c> = SHA-256 do TCL. Nunca loga o conteúdo do TCL.
    /// </summary>
    public sealed class TclStudioModelAdapter : IStudioModelAdapter
    {
        public const int SchemaVersion = 1;

        private readonly ITclSource _source;
        private readonly ILogger<TclStudioModelAdapter> _logger;

        public TclStudioModelAdapter(ITclSource source, ILogger<TclStudioModelAdapter> logger)
        {
            _source = source;
            _logger = logger;
        }

        public string Engine => "tcl";

        public StudioCapabilities Capabilities { get; } = new(false, new List<string>());

        public async Task<StudioModelDocument?> LoadAsync(StudioModelRequest request, CancellationToken ct)
        {
            var src = await _source.GetAsync(request.MapperGuid, ct);
            if (!src.Exists) return null;

            TclReadResult read;
            try { read = TclReader.Read(src.Tcl); }
            catch (Exception ex)
            {
                // Defesa em profundidade: o leitor já não lança.
                _logger.LogWarning(ex, "Studio-model/tcl: leitor falhou para {MapperGuid}.", request.MapperGuid);
                read = new TclReadResult(new(), new(), new() { new StudioDiagnostic("TCL_UNPARSEABLE", "warning", "TCL ilegível; árvore indisponível.") });
            }

            var diagnostics = new List<StudioDiagnostic>();
            if (src.Tcl == null)
                diagnostics.Add(new StudioDiagnostic("LAYOUT_UNAVAILABLE", "warning", src.Failure ?? "TCL indisponível.", Id: src.LayoutGuid, Path: "input"));
            diagnostics.AddRange(read.Diagnostics.Where(d => src.Tcl != null || d.Code != "TCL_UNPARSEABLE"));

            var nodes = new Dictionary<string, StudioNode>(StringComparer.Ordinal);
            foreach (var (id, node) in read.Nodes) nodes[id] = node;

            var hash = StudioModelHasher.HashSource(src.Tcl ?? string.Empty);
            var artifact = new StudioArtifact(
                Engine, request.MapperGuid, null, hash!,
                StudioModelHasher.ComputeETag(hash, null, null, SchemaVersion),
                new List<string>(),
                new List<StudioSource> { new("tcl", request.MapperGuid, hash) });

            _logger.LogInformation("Studio-model/tcl montado para {MapperGuid}: {NodeCount} nós, {DiagnosticCount} diagnósticos.",
                request.MapperGuid, nodes.Count, diagnostics.Count);

            return new StudioModelDocument(
                SchemaVersion, artifact, Capabilities,
                new StudioTrees(
                    new StudioTree(src.LayoutGuid, "text-positional", read.RootIds, src.Tcl != null && read.RootIds.Count > 0),
                    new StudioTree(null, "unknown", new List<string>(), false)),
                nodes, new Dictionary<string, StudioLink>(), new Dictionary<string, StudioRule>(),
                new Dictionary<string, StudioDataType>(), diagnostics);
        }
    }
}
