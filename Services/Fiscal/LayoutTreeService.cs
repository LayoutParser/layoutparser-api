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
            var (rules, limitations, dslRules, diagnostics) = ResolveRules(mapper, source, target, dslOptions ?? new LayoutTreeDslOptions());

            return new LayoutTreeResponse(mapper.MapperGuid, source, target, rules, limitations, dslRules, diagnostics);
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
                // O ConnectUs compara o GUID do layout de forma ordinal e o catálogo ora guarda com "LAY_", ora sem:
                // tenta a forma alternativa antes de degradar a árvore para vazia.
                if (layoutRecord == null)
                {
                    var alternativo = layoutGuid.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase) ? layoutGuid[4..] : "LAY_" + layoutGuid;
                    layoutRecord = await _cachedLayoutService.GetLayoutByGuidAsync(alternativo);
                }
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

        private static IReadOnlyList<LayoutTreeNodeDto> ToDto(IReadOnlyList<LayoutTreeNode> nodes, string layoutKind, string parentPath = "")
            => nodes.Select(n => ToDto(n, layoutKind, parentPath)).ToList();

        /// <summary>
        /// <c>XPath</c> segue o <c>FullXPath</c> do ConnectUs: nomes separados por '/', absoluto, sem o nome do layout,
        /// sem '@' em atributo; nós com <c>ShowInPath=false</c> não entram (e ficam com <c>XPath</c> nulo).
        /// </summary>
        private static LayoutTreeNodeDto ToDto(LayoutTreeNode node, string layoutKind, string parentPath)
        {
            var prefix = LayoutTreeNodeTypes.PrefixOf(node.ElementGuid);
            var nodeType = LayoutTreeNodeTypes.Classify(layoutKind, node.Kind, prefix, node.XsiType);
            var (labelPt, iconKey, showInPath) = LayoutTreeNodeTypes.Presentation(nodeType);
            var path = showInPath ? (parentPath.Length == 0 ? node.Name : parentPath + "/" + node.Name) : parentPath;
            return new(
                node.ElementGuid,
                node.Name,
                node.Kind,
                node.MinOccurs is null && node.MaxOccurs is null ? null : new LayoutTreeCardinality(node.MinOccurs, node.MaxOccurs),
                ToDto(node.Children, layoutKind, path),
                nodeType,
                prefix,
                node.XsiType,
                labelPt,
                iconKey,
                showInPath,
                showInPath ? path : null);
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
        private (IReadOnlyList<LayoutTreeRule> Rules, IReadOnlyList<string> Limitations, LayoutTreeDslRules? Dsl, IReadOnlyList<LayoutTreeDiagnostic> Diagnostics) ResolveRules(
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
                return (Array.Empty<LayoutTreeRule>(), Array.Empty<string>(), null, Array.Empty<LayoutTreeDiagnostic>());
            }

            var rules = mapperVo.LinkMappings
                .Select(link => new LayoutTreeRule(
                    link.ElementGuid ?? $"link:{link.Name}",
                    link.InputGuid,
                    link.TargetGuid))
                .ToList();

            var diagnostics = BuildStructuralDiagnostics(mapperVo);

            if (mapperVo.Rules.Count == 0)
                return (rules, Array.Empty<string>(), null, diagnostics);

            var dsl = BuildDslRules(mapperVo, source, target, options);
            var limitations = new[]
            {
                dsl.Unresolved > 0
                    ? $"{dsl.Unresolved} de {mapperVo.Rules.Count} regra(s) condicional(is)/DSL não puderam ser vinculadas a nós da árvore " +
                      "(texto I./T. ausente, ambíguo ou fora do catálogo) — constam em dslRules.items com resolved=false, só como texto."
                    : $"Mapper tem {mapperVo.Rules.Count} regra(s) condicional(is)/DSL, todas vinculadas a nós — ver dslRules (não aparecem em Rules[])."
            };

            return (rules, limitations, dsl, diagnostics);
        }

        /// <summary>
        /// Destinos com vínculo + regra (a regra é ignorada no runtime) e destinos com N:1 (ordem importa).
        /// Só considera GUID de destino explícito — nunca infere por nome.
        /// </summary>
        private static IReadOnlyList<LayoutTreeDiagnostic> BuildStructuralDiagnostics(MapperVo mapperVo)
        {
            var result = new List<LayoutTreeDiagnostic>();
            var links = mapperVo.LinkMappings
                .Where(l => !string.IsNullOrWhiteSpace(l.TargetGuid))
                .GroupBy(l => l.TargetGuid!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(l => l.ElementGuid ?? $"link:{l.Name}").ToList(), StringComparer.Ordinal);
            var rulesByTarget = mapperVo.Rules
                .Where(r => !string.IsNullOrWhiteSpace(r.TargetElementGuid))
                .GroupBy(r => r.TargetElementGuid!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(r => r.ElementGuid ?? r.Name ?? "rule").ToList(), StringComparer.Ordinal);

            foreach (var (target, ids) in links)
            {
                if (ids.Count > 1)
                    result.Add(new LayoutTreeDiagnostic("N1_ORDER_SENSITIVE", target, ids,
                        $"Destino com {ids.Count} vínculos: o ConnectUs usa o 1º (na ordem do arquivo) cuja origem tem dados."));
                if (rulesByTarget.TryGetValue(target, out var ruleIds))
                    result.Add(new LayoutTreeDiagnostic("TARGET_LINK_AND_RULE", target, ids.Concat(ruleIds).ToList(),
                        "Destino com vínculo e regra: no ConnectUs o vínculo tem precedência e a regra nunca é executada."));
            }
            return result;
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
                    var diagnostics = new List<string>();
                    var (srcGuids, srcAll) = sourceIndex.Resolve(r.SourceRefs, caseSensitive: false, diagnostics);
                    var (tgtGuids, tgtAll) = targetIndex.Resolve(r.TargetRefs, caseSensitive: true, diagnostics);
                    var opaque = r.SupportLevel == MappingExplanationSupportLevel.Opaque;
                    all.Add(new LayoutTreeDslRule(
                        r.RuleId, ruleName, opaque ? "opaque" : "dsl",
                        r.SupportLevel == MappingExplanationSupportLevel.Authoritative, r.SupportLevel,
                        r.SourceRefs, r.TargetRefs, srcGuids, tgtGuids,
                        Resolved: srcAll && tgtAll && r.TargetRefs.Count > 0,
                        r.Condition, r.HumanDescription, r.TechnicalDetail,
                        Diagnostics: diagnostics.Distinct().ToList()));
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
        /// Índice caminho absoluto → GUID, na semântica do ConnectUs (<c>FullXPath</c>): <c>I.</c> compara sem
        /// diferenciar caixa e <c>T.</c> com caixa exata. Só vale se houver exatamente UM nó com GUID; mais de um
        /// (irmãos homônimos — o ConnectUs usaria o 1º em silêncio) ou nenhum → sem GUID, com diagnóstico
        /// (<c>REF_AMBIGUOUS</c>, <c>REF_UNRESOLVED</c>, <c>REF_T_CASE_MISMATCH</c>).
        /// </summary>
        private sealed class NodeIndex
        {
            private readonly List<(string XPath, string? Guid)> _nodes = new();

            public NodeIndex(IReadOnlyList<LayoutTreeNodeDto> roots)
            {
                void Walk(LayoutTreeNodeDto n)
                {
                    if (n.XPath is not null) _nodes.Add((n.XPath, n.ElementGuid));
                    foreach (var c in n.Children) Walk(c);
                }
                foreach (var r in roots) Walk(r);
            }

            public (IReadOnlyList<string> Guids, bool AllResolved) Resolve(IReadOnlyList<string> refs, bool caseSensitive, List<string> diagnostics)
            {
                var guids = new List<string>();
                var all = refs.Count > 0;
                foreach (var raw in refs)
                {
                    var text = raw.Length > 2 && raw[1] == '.' && (raw[0] == 'I' || raw[0] == 'T') ? raw[2..] : raw;
                    text = text.Trim().Replace("/@", "/").TrimStart('@');
                    if (text.Length == 0) { all = false; diagnostics.Add("REF_UNRESOLVED"); continue; }

                    var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    var matches = _nodes.Where(n => string.Equals(n.XPath, text, cmp)).ToList();
                    if (matches.Count == 1 && !string.IsNullOrEmpty(matches[0].Guid))
                    {
                        guids.Add(matches[0].Guid!);
                        continue;
                    }

                    all = false;
                    if (matches.Count > 1)
                        diagnostics.Add("REF_AMBIGUOUS");
                    else if (caseSensitive && _nodes.Any(n => string.Equals(n.XPath, text, StringComparison.OrdinalIgnoreCase)))
                        diagnostics.Add("REF_T_CASE_MISMATCH");
                    else
                        diagnostics.Add("REF_UNRESOLVED");
                }
                return (guids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), all);
            }
        }
    }
}
