using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.StudioModel.Sysmiddle
{
    /// <summary>
    /// Adaptador Sysmiddle (MapperVO + LayoutVO, fidelidade total) — Fase 1, SOMENTE LEITURA.
    /// Resolve o mapper por <c>MapperGuid</c> em <see cref="ICachedMapperService"/> e os layouts por
    /// <see cref="ICachedLayoutService.GetLayoutByGuidAsync"/> (mesmo padrão do <c>layout-tree</c>;
    /// <c>*FromXml</c> tem prioridade). Nunca loga conteúdo de XML nem <c>code</c> de regra: só GUIDs e contagens.
    /// </summary>
    public sealed class SysmiddleStudioModelAdapter : IStudioModelAdapter
    {
        public const int SchemaVersion = 1;

        private readonly ICachedMapperService _mappers;
        private readonly ICachedLayoutService _layouts;
        private readonly ILogger<SysmiddleStudioModelAdapter> _logger;
        private readonly IDataTypeCatalog? _dataTypes;

        public SysmiddleStudioModelAdapter(
            ICachedMapperService mappers,
            ICachedLayoutService layouts,
            ILogger<SysmiddleStudioModelAdapter> logger,
            IDataTypeCatalog? dataTypes = null) // opcional: sem fonte registrada, nome do tipo vira "?"
        {
            _mappers = mappers;
            _layouts = layouts;
            _logger = logger;
            _dataTypes = dataTypes;
        }

        public string Engine => "sysmiddle";

        public StudioCapabilities Capabilities { get; } = new(false, new List<string>());

        public async Task<StudioModelDocument?> LoadAsync(StudioModelRequest request, CancellationToken ct)
        {
            List<Mapper> all;
            try
            {
                all = await _mappers.GetAllMappersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Studio-model: falha ao consultar catálogo de mappers para {MapperGuid}.", request.MapperGuid);
                throw new StudioModelUnavailableException("Catálogo de mappers indisponível.", ex);
            }

            var mapper = all.FirstOrDefault(m => string.Equals(m.MapperGuid, request.MapperGuid, StringComparison.OrdinalIgnoreCase));
            if (mapper == null || string.IsNullOrWhiteSpace(mapper.DecryptedContent))
                return null;

            var diagnostics = new List<StudioDiagnostic>();
            var variants = new HashSet<string>(StringComparer.Ordinal);

            // --- mapper (links/rules): ilegível degrada para vazio ---
            MapperReadResult? mapperRead = null;
            try
            {
                mapperRead = MapperVoReader.Read(mapper.DecryptedContent);
                variants.UnionWith(mapperRead.VariantFields);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Studio-model: MapperVO {MapperGuid} ilegível — links/regras degradam para vazio.", request.MapperGuid);
                diagnostics.Add(new StudioDiagnostic("MAPPER_UNREADABLE", "warning", "XML do mapper ilegível; vínculos e regras indisponíveis.", Id: mapper.MapperGuid));
            }

            var inputGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? mapperRead?.InputLayoutGuid;
            var targetGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid ?? mapperRead?.TargetLayoutGuid;

            Func<string, string?>? resolver = _dataTypes == null ? null : _dataTypes.ResolveName;
            var input = await ResolveSideAsync(inputGuid, "input", resolver, diagnostics, variants, ct);
            var target = await ResolveSideAsync(targetGuid, "target", resolver, diagnostics, variants, ct);

            // --- nós (duas árvores num dicionário único, ordem de inserção) ---
            var nodes = new Dictionary<string, StudioNode>(StringComparer.Ordinal);
            foreach (var side in new[] { input, target })
            {
                if (side.Read == null) continue;
                diagnostics.AddRange(side.Read.Diagnostics);
                foreach (var (id, node) in side.Read.Nodes)
                {
                    if (!nodes.TryAdd(id, node))
                        diagnostics.Add(new StudioDiagnostic("DUPLICATE_NODE_ID", "warning", "ElementGuid presente nas duas árvores; nó ignorado.", Id: id));
                }
            }

            // --- links e regras (órfãos permanecem) ---
            var links = new Dictionary<string, StudioLink>(StringComparer.Ordinal);
            var rules = new Dictionary<string, StudioRule>(StringComparer.Ordinal);
            var linkedIds = new HashSet<string>(StringComparer.Ordinal);

            if (mapperRead != null)
            {
                foreach (var l in mapperRead.Links)
                {
                    nodes.TryGetValue(l.SourceId ?? string.Empty, out var src);
                    nodes.TryGetValue(l.TargetId ?? string.Empty, out var tgt);
                    var text = src != null && tgt != null ? $"{src.Name}_{tgt.Name}" : (l.Name ?? l.Id);
                    var iterates = src != null && tgt != null
                        && NodeTypeMap.Containers.Contains(src.Type) && NodeTypeMap.Containers.Contains(tgt.Type);
                    if (src != null) linkedIds.Add(l.SourceId!);
                    if (tgt != null) linkedIds.Add(l.TargetId!);
                    links[l.Id] = new StudioLink(l.SourceId, l.TargetId, l.Order,
                        new StudioLinkDisplay(text, "linkMapping", tgt != null ? l.TargetId : null), l.Opts, iterates);
                }

                foreach (var r in mapperRead.Rules)
                {
                    nodes.TryGetValue(r.AnchorId ?? string.Empty, out var anchor);
                    if (anchor != null) linkedIds.Add(r.AnchorId!);
                    var scan = RuleCodeScanner.Scan(r.Code, r.PrePos, anchor?.Path);
                    rules[r.Id] = new StudioRule(
                        r.AnchorId,
                        new StudioRuleDisplay($"Rule_{anchor?.Name ?? r.Name}", "rule", anchor != null ? r.AnchorId : null),
                        r.Name, r.Code, scan.Reads, scan.Writes, scan.Functions, r.PrePos, scan.Opaque);
                }
            }

            foreach (var id in linkedIds)
            {
                var n = nodes[id];
                nodes[id] = n with { Display = n.Display with { Linked = true } };
            }

            diagnostics.AddRange(DiagnosticsBuilder.Build(nodes, links, rules));

            // --- catálogo de tipos resolvidos (vazio se indisponível) ---
            var datatypes = new Dictionary<string, StudioDataType>(StringComparer.Ordinal);
            foreach (var n in nodes.Values)
                if (n.Props.TryGetValue("dataType", out var dt) && dt is StudioDataTypeRef { Name: not null } r)
                    datatypes[r.Guid] = new StudioDataType(r.Name);

            // --- hashes (design §5): conteúdo exato por fonte ---
            var mapperHash = StudioModelHasher.HashSource(mapper.DecryptedContent);
            var inputHash = StudioModelHasher.HashSource(input.Content);
            var targetHash = StudioModelHasher.HashSource(target.Content);

            var artifact = new StudioArtifact(
                Engine,
                mapper.MapperGuid,
                mapperRead?.Name ?? mapper.Name,
                StudioModelHasher.HashArtifact(mapperHash, inputHash, targetHash),
                StudioModelHasher.ComputeETag(mapperHash, inputHash, targetHash, SchemaVersion),
                VariantFieldNames.All.Where(variants.Contains).ToList(),
                new List<StudioSource>
                {
                    new("mapper", mapper.MapperGuid, mapperHash),
                    new("input-layout", inputGuid, inputHash),
                    new("target-layout", targetGuid, targetHash),
                });

            _logger.LogInformation(
                "Studio-model montado para {MapperGuid}: {NodeCount} nós, {LinkCount} vínculos, {RuleCount} regras, {DiagnosticCount} diagnósticos.",
                mapper.MapperGuid, nodes.Count, links.Count, rules.Count, diagnostics.Count);

            return new StudioModelDocument(
                SchemaVersion, artifact, Capabilities,
                new StudioTrees(input.Tree, target.Tree),
                nodes, links, rules, datatypes, diagnostics);
        }

        private sealed record Side(StudioTree Tree, LayoutReadResult? Read, string? Content);

        /// <summary>Degrada: layout ausente/ilegível → <c>available:false</c> + <c>LAYOUT_UNAVAILABLE</c>; nunca lança.</summary>
        private async Task<Side> ResolveSideAsync(
            string? layoutGuid, string treeName, Func<string, string?>? resolver,
            List<StudioDiagnostic> diagnostics, HashSet<string> variants, CancellationToken ct)
        {
            StudioTree Unavailable(string reason)
            {
                diagnostics.Add(new StudioDiagnostic("LAYOUT_UNAVAILABLE", "warning", reason, Id: layoutGuid, Path: treeName));
                return new StudioTree(layoutGuid, "unknown", new List<string>(), false);
            }

            if (string.IsNullOrWhiteSpace(layoutGuid))
                return new Side(Unavailable("Mapper sem GUID de layout neste lado."), null, null);

            Models.Database.LayoutRecord? record;
            try
            {
                record = await LookupAsync(layoutGuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Studio-model: falha ao buscar layout {LayoutGuid} ({Tree}).", layoutGuid, treeName);
                return new Side(Unavailable("Falha ao consultar o layout."), null, null);
            }

            if (record == null || string.IsNullOrWhiteSpace(record.DecryptedContent))
            {
                _logger.LogWarning("Studio-model: layout {LayoutGuid} ({Tree}) não encontrado.", layoutGuid, treeName);
                return new Side(Unavailable("Layout não encontrado."), null, null);
            }

            try
            {
                var read = LayoutVoReader.Read(record.DecryptedContent, treeName, resolver);
                variants.UnionWith(read.VariantFields);
                return new Side(new StudioTree(read.LayoutGuid ?? layoutGuid, read.Format, read.RootIds, true), read, record.DecryptedContent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Studio-model: layout {LayoutGuid} ({Tree}) ilegível.", layoutGuid, treeName);
                // Conteúdo ainda entra no hash (alteração do layout invalida o eTag mesmo ilegível).
                var unavailable = Unavailable("Layout ilegível.");
                return new Side(unavailable, null, record.DecryptedContent);
            }
        }

        /// <summary>No índice o GUID pode vir com ou sem prefixo <c>LAY_</c>: tenta a forma recebida e a alternativa.</summary>
        private async Task<Models.Database.LayoutRecord?> LookupAsync(string guid)
        {
            var found = await _layouts.GetLayoutByGuidAsync(guid);
            if (found != null) return found;

            const string prefix = "LAY_";
            var alt = guid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? guid[prefix.Length..] : prefix + guid;
            return await _layouts.GetLayoutByGuidAsync(alt);
        }
    }
}
