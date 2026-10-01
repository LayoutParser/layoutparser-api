using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;

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

        public LayoutTreeService(
            ICachedMapperService cachedMapperService,
            ICachedLayoutService cachedLayoutService,
            ILogger<LayoutTreeService> logger)
        {
            _cachedMapperService = cachedMapperService;
            _cachedLayoutService = cachedLayoutService;
            _logger = logger;
        }

        public async Task<LayoutTreeResponse?> GetLayoutTreeAsync(string mappingId, CancellationToken cancellationToken)
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
            var (rules, limitations) = ResolveRules(mapper);

            return new LayoutTreeResponse(mapper.MapperGuid, source, target, rules, limitations);
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
            var dtoRoots = ToDto(roots);

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

        private static IReadOnlyList<LayoutTreeNodeDto> ToDto(IReadOnlyList<LayoutTreeNode> nodes)
            => nodes.Select(ToDto).ToList();

        private static LayoutTreeNodeDto ToDto(LayoutTreeNode node)
            => new(
                node.ElementGuid,
                node.Name,
                node.Kind,
                node.MinOccurs is null && node.MaxOccurs is null ? null : new LayoutTreeCardinality(node.MinOccurs, node.MaxOccurs),
                ToDto(node.Children));

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
        private (IReadOnlyList<LayoutTreeRule> Rules, IReadOnlyList<string> Limitations) ResolveRules(Mapper mapper)
        {
            MapperVo mapperVo;
            try
            {
                mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapper.DecryptedContent));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Árvore de layout: MapperVO {MapperGuid} não pôde ser parseado — regras degradam para lista vazia.", mapper.MapperGuid);
                return (Array.Empty<LayoutTreeRule>(), Array.Empty<string>());
            }

            var rules = mapperVo.LinkMappings
                .Select(link => new LayoutTreeRule(
                    link.ElementGuid ?? $"link:{link.Name}",
                    link.InputGuid,
                    link.TargetGuid))
                .ToList();

            var limitations = mapperVo.Rules.Count > 0
                ? new[]
                {
                    $"Mapper tem {mapperVo.Rules.Count} regra(s) condicional(is)/DSL que NÃO aparecem em Rules[] — " +
                    "origem/destino dessas regras não são GUIDs de nó resolvíveis (ver GET .../explanation " +
                    "para a lista completa, incluindo as DSL, com sourceRefs/targetRefs prefixados I./T.)."
                }
                : Array.Empty<string>();

            return (rules, limitations);
        }
    }
}
