using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Sysmiddle;

using Microsoft.Extensions.Logging.Abstractions;

using XslSynth.Core;

using MapperVo = XslSynth.Model.MapperVo;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Implementação de <see cref="ILayoutTreeService"/> (issue #425). Generaliza
    /// <see cref="GuidXPathCatalog"/> (ver ADR "adr-layout-tree-endpoint-425" — reaproveita ~90% de
    /// lógica já madura em vez de reescrever um parser de árvore do zero): troca a fonte de
    /// "arquivo local" pelo lookup por GUID já existente no banco (<see cref="ICachedLayoutService"/>)
    /// e usa <see cref="GuidXPathCatalog.BuildTree"/> (novo, adicionado nesta issue) para preservar a
    /// hierarquia completa — em vez do dicionário achatado que já existia para o loop de síntese XSLT.
    ///
    /// <para><b>Resolução do mapper:</b> mesmo padrão de <see cref="SysmiddleExplanationAdapter"/> —
    /// <c>mappingId</c> é o <c>MapperGuid</c> real do catálogo <c>tbMapper</c> (SOMENTE LEITURA,
    /// ver <c>.claude/rules/security.md</c>), <c>InputLayoutGuidFromXml</c>/
    /// <c>TargetLayoutGuidFromXml</c> têm prioridade sobre as colunas de banco (mais confiáveis).</para>
    /// </summary>
    public sealed class LayoutTreeService : ILayoutTreeService
    {
        private readonly ICachedMapperService _cachedMapperService;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly ILogger<LayoutTreeService> _logger;
        private readonly SysmiddleExplanationAdapter _explainer;

        public LayoutTreeService(
            ICachedMapperService cachedMapperService,
            ICachedLayoutService cachedLayoutService,
            ILogger<LayoutTreeService> logger,
            ISysmiddleFunctionCatalog? functionCatalog = null)
        {
            _cachedMapperService = cachedMapperService;
            _cachedLayoutService = cachedLayoutService;
            _logger = logger;
            // Mesma tradução de GET .../explanation (ToExplainedRules) — evita duplicar a gramática DSL.
            _explainer = new SysmiddleExplanationAdapter(cachedMapperService, NullLogger<SysmiddleExplanationAdapter>.Instance, functionCatalog);
        }

        public async Task<LayoutTreeResponse?> GetLayoutTreeAsync(string mappingId, CancellationToken cancellationToken, LayoutTreeDslOptions? dslOptions = null)
        {
            List<Mapper> mappers;
            try
            {
                mappers = await _cachedMapperService.GetAllMappersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar catálogo de mappers Sysmiddle para árvore de layout de {MapperGuid}.", mappingId);
                throw;
            }

            var mapper = mappers.FirstOrDefault(m => string.Equals(m.MapperGuid, mappingId, StringComparison.OrdinalIgnoreCase));
            if (mapper == null || string.IsNullOrWhiteSpace(mapper.DecryptedContent))
                return null;

            var inputLayoutGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid;
            var targetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid;

            var source = await ResolveSideAsync(inputLayoutGuid, cancellationToken);
            var target = await ResolveSideAsync(targetLayoutGuid, cancellationToken);
            var (rules, limitations, dslRules) = ResolveRules(mapper, source, target, dslOptions ?? new LayoutTreeDslOptions());

            return new LayoutTreeResponse(mapper.MapperGuid, source, target, rules, limitations, dslRules);
        }

        /// <summary>
        /// Resolve um lado (origem OU destino) da árvore. Degrade gracioso (ADR §"Consequências —
        /// resiliência"): layout ausente/GUID vazio/XML ilegível → lado com <c>Roots</c> vazio, NUNCA
        /// deixa o request inteiro cair — o outro lado (e as regras) ainda são úteis ao consumidor.
        /// </summary>
        private async Task<LayoutTreeSide> ResolveSideAsync(string? layoutGuid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid))
            {
                _logger.LogWarning("Árvore de layout: LayoutGuid ausente no mapper — lado degrada para árvore vazia.");
                return new LayoutTreeSide(null, LayoutTreeKinds.Unknown, Array.Empty<LayoutTreeNodeDto>(), LayoutTreeUnavailableReasons.LayoutNotFound);
            }

            Models.Database.LayoutRecord? layoutRecord;
            try
            {
                layoutRecord = await _cachedLayoutService.GetLayoutByGuidAsync(layoutGuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Árvore de layout: falha ao buscar layout {LayoutGuid} — lado degrada para árvore vazia.", layoutGuid);
                return new LayoutTreeSide(layoutGuid, LayoutTreeKinds.Unknown, Array.Empty<LayoutTreeNodeDto>(), LayoutTreeUnavailableReasons.LayoutNotFound);
            }

            if (layoutRecord == null)
            {
                _logger.LogWarning("Árvore de layout: layout {LayoutGuid} não encontrado — lado degrada para árvore vazia.", layoutGuid);
                return new LayoutTreeSide(layoutGuid, LayoutTreeKinds.Unknown, Array.Empty<LayoutTreeNodeDto>(), LayoutTreeUnavailableReasons.LayoutNotFound);
            }

            var (resolvedLayoutGuid, roots) = GuidXPathCatalog.BuildTree(
                layoutRecord.DecryptedContent, sourceLabel: layoutRecord.Name, log: msg => _logger.LogInformation("{Msg}", msg));

            var (kind, legivel) = DetectKind(layoutRecord.DecryptedContent);
            var dtoRoots = ToDto(roots, kind);

            // Motivo explícito quando a árvore não pôde ser materializada (campo opcional, 200 mantido).
            string? motivo = null;
            if (!legivel) motivo = LayoutTreeUnavailableReasons.LayoutUnreadable;
            else if (kind == LayoutTreeKinds.Unknown) motivo = LayoutTreeUnavailableReasons.UnsupportedKind;
            else if (dtoRoots.Count == 0)
                motivo = kind == LayoutTreeKinds.Xml ? LayoutTreeUnavailableReasons.XsdUnresolved : LayoutTreeUnavailableReasons.LayoutUnreadable;

            return new LayoutTreeSide(resolvedLayoutGuid ?? layoutGuid, kind, dtoRoots, motivo);
        }

        /// <summary>Lê o <c>xsi:type</c> da raiz do LayoutVO ("TextLayoutVO"/"XmlLayoutVO"); <c>Legivel=false</c> se vazio/XML inválido.</summary>
        private static (string Kind, bool Legivel) DetectKind(string? xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
                return (LayoutTreeKinds.Unknown, false);

            try
            {
                var root = XDocument.Parse(xmlContent).Root;
                if (root == null) return (LayoutTreeKinds.Unknown, false);
                var tipo = (string?)root.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type") ?? "";
                if (tipo.Contains("Text", StringComparison.OrdinalIgnoreCase)) return (LayoutTreeKinds.Text, true);
                if (tipo.Contains("Xml", StringComparison.OrdinalIgnoreCase)) return (LayoutTreeKinds.Xml, true);
                return (LayoutTreeKinds.Unknown, true);
            }
            catch
            {
                return (LayoutTreeKinds.Unknown, false);
            }
        }

        private static IReadOnlyList<LayoutTreeNodeDto> ToDto(IReadOnlyList<LayoutTreeNode> nodes, string layoutKind)
            => nodes.Select(n => ToDto(n, layoutKind)).ToList();

        private static LayoutTreeNodeDto ToDto(LayoutTreeNode node, string layoutKind)
        {
            var prefix = LayoutTreeNodeTypes.PrefixOf(node.ElementGuid);
            return new(
                node.ElementGuid,
                node.Name,
                node.Kind,
                node.MinOccurs is null && node.MaxOccurs is null ? null : new LayoutTreeCardinality(node.MinOccurs, node.MaxOccurs),
                ToDto(node.Children, layoutKind),
                LayoutTreeNodeTypes.Classify(layoutKind, node.Kind, prefix),
                prefix,
                node.XsiType);
        }

        /// <summary>
        /// Regras = <c>LinkMappingItemVO</c> reais (mapeamento direto campo→campo) — o único ponto
        /// do MapperVO onde origem E destino são GUIDs de nó (ADR §"Contrato de resposta proposto").
        ///
        /// <para><b>Issue #430:</b> Regras DSL (<c>MapperRule</c>/branches condicionais) não entram na
        /// lista — <c>TargetGuid</c> existe no <c>MapperRule</c>, mas a origem é texto da DSL
        /// (ex. <c>I.xMun</c>), não um GUID de nó do catálogo, e inventar um aqui violaria "nunca
        /// inventa" (mesma regra que rege <see cref="SysmiddleExplanationAdapter"/>). Quando o mapper
        /// tem regras DSL, sinaliza via <c>Limitations</c> — ver <see cref="LayoutTreeResponse"/>.</para>
        /// </summary>
        private (IReadOnlyList<LayoutTreeRule> Rules, IReadOnlyList<string> Limitations, LayoutTreeDslRules? Dsl) ResolveRules(
            Mapper mapper, LayoutTreeSide source, LayoutTreeSide target, LayoutTreeDslOptions options)
        {
            MapperVo mapperVo;
            try
            {
                mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapper.DecryptedContent));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Árvore de layout: MapperVO {MapperGuid} não pôde ser parseado — regras degradam para lista vazia.", mapper.MapperGuid);
                return (Array.Empty<LayoutTreeRule>(), Array.Empty<string>(), null);
            }

            var rules = mapperVo.LinkMappings
                .Select(link => new LayoutTreeRule(
                    link.ElementGuid ?? $"link:{link.Name}",
                    link.InputGuid,
                    link.TargetGuid))
                .ToList();

            if (mapperVo.Rules.Count == 0)
                return (rules, Array.Empty<string>(), null);

            var dsl = BuildDslRules(mapperVo, source, target, options);
            var limitations = new[]
            {
                dsl.Unresolved > 0
                    ? $"{dsl.Unresolved} de {mapperVo.Rules.Count} regra(s) condicional(is)/DSL não puderam ser vinculadas a nós da árvore " +
                      "(texto I./T. ausente, ambíguo ou fora do catálogo) — constam em dslRules.items com resolved=false, só como texto."
                    : $"Mapper tem {mapperVo.Rules.Count} regra(s) condicional(is)/DSL, todas vinculadas a nós — ver dslRules (não aparecem em Rules[])."
            };

            return (rules, limitations, dsl);
        }

        /// <summary>
        /// Monta <c>dslRules</c>: reaproveita <see cref="SysmiddleExplanationAdapter.ToExplainedRules"/> e resolve
        /// o texto I./T. para GUIDs SÓ quando casa de forma unívoca (nunca inventa — mesma regra do #430).
        /// </summary>
        private LayoutTreeDslRules BuildDslRules(MapperVo mapperVo, LayoutTreeSide source, LayoutTreeSide target, LayoutTreeDslOptions options)
        {
            var sourceIndex = new NodeIndex(source.Roots);
            var targetIndex = new NodeIndex(target.Roots);

            var all = new List<LayoutTreeDslRule>();
            foreach (var mapperRule in mapperVo.Rules)
            {
                var ruleName = mapperRule.Name ?? mapperRule.ElementGuid ?? "regra";
                IEnumerable<ExplainedRule> explained;
                try
                {
                    explained = _explainer.ToExplainedRules(mapperRule).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Árvore de layout: regra DSL {Rule} não pôde ser explicada — ignorada em dslRules.", ruleName);
                    continue;
                }

                foreach (var r in explained)
                {
                    var (srcGuids, srcAll) = sourceIndex.Resolve(r.SourceRefs);
                    var (tgtGuids, tgtAll) = targetIndex.Resolve(r.TargetRefs);
                    var opaque = r.SupportLevel == MappingExplanationSupportLevel.Opaque;
                    all.Add(new LayoutTreeDslRule(
                        r.RuleId, ruleName, opaque ? "opaque" : "dsl",
                        r.SupportLevel == MappingExplanationSupportLevel.Authoritative, r.SupportLevel,
                        r.SourceRefs, r.TargetRefs, srcGuids, tgtGuids,
                        Resolved: srcAll && tgtAll && r.TargetRefs.Count > 0,
                        r.Condition, r.HumanDescription, r.TechnicalDetail));
                }
            }

            var unresolved = all.Count(r => !r.Resolved);
            IEnumerable<LayoutTreeDslRule> filtered = all;
            if (options.TargetNodeGuid is not null)
                filtered = all.Where(r => r.TargetNodeGuids.Contains(options.TargetNodeGuid, StringComparer.OrdinalIgnoreCase));
            var list = filtered.ToList();
            var items = list.Skip(options.Offset).Take(options.Limit).ToList();
            return new LayoutTreeDslRules(list.Count, unresolved, options.Offset, options.Limit, items);
        }

        /// <summary>
        /// Índice nome/caminho → GUID de uma árvore. Texto I./T. (sem prefixo, '@' ignorado, caminho com '/')
        /// casa primeiro pelo caminho completo (sufixo) e depois pelo nome da folha; só vale se houver
        /// exatamente UM nó com GUID — caso contrário fica sem resolução.
        /// </summary>
        private sealed class NodeIndex
        {
            private readonly List<(string[] Path, string? Guid)> _nodes = new();

            public NodeIndex(IReadOnlyList<LayoutTreeNodeDto> roots)
            {
                void Walk(LayoutTreeNodeDto n, List<string> prefix)
                {
                    prefix.Add(Norm(n.Name));
                    _nodes.Add((prefix.ToArray(), n.ElementGuid));
                    foreach (var c in n.Children) Walk(c, prefix);
                    prefix.RemoveAt(prefix.Count - 1);
                }
                foreach (var r in roots) Walk(r, new List<string>());
            }

            private static string Norm(string s) => s.Trim().TrimStart('@').ToLowerInvariant();

            public (IReadOnlyList<string> Guids, bool AllResolved) Resolve(IReadOnlyList<string> refs)
            {
                var guids = new List<string>();
                var all = refs.Count > 0;
                foreach (var raw in refs)
                {
                    var text = raw.Length > 2 && raw[1] == '.' && (raw[0] == 'I' || raw[0] == 'T') ? raw[2..] : raw;
                    var segs = text.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Norm).ToArray();
                    if (segs.Length == 0) { all = false; continue; }

                    var matches = _nodes.Where(n => n.Path.Length >= segs.Length && n.Path.TakeLast(segs.Length).SequenceEqual(segs)).ToList();
                    if (matches.Count == 1 && !string.IsNullOrEmpty(matches[0].Guid))
                        guids.Add(matches[0].Guid!);
                    else
                        all = false;
                }
                return (guids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), all);
            }
        }
    }
}
