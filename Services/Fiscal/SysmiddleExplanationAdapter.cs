using System.Text.RegularExpressions;
using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.StudioModel.Sysmiddle;
using LayoutParserApi.Services.Sysmiddle;

using XslSynth.Core;

using MapperRule = XslSynth.Model.MapperRule;
using MapperVo = XslSynth.Model.MapperVo;
using LinkMappingItem = XslSynth.Model.LinkMappingItem;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Adapter de explicação para mappers Sysmiddle REAIS já publicados (catálogo <c>tbMapper</c>,
    /// Slice 4 — issue #226/#227, design §2.1). Read-only por natureza: nunca gera código, só
    /// TRADUZ o que já está em produção para o contrato canônico <see cref="MappingExplanation"/>.
    ///
    /// <para><b>Fonte:</b> reaproveita <see cref="RealMapperParser"/> (MapperVO real, gramática
    /// decifrada em <c>decisao-dsl-mapper-sysmiddle-2026-08-21.md</c>) + <see cref="DslStructuredParser"/>
    /// (Camada 0 do desenho RAG — já é literalmente um "explicador" da DSL: produz árvore de
    /// decisão com origem/destino/condição/funções por ramo). Não escrevemos um parser de
    /// explicação novo: <see cref="DslStructuredParser"/> já cobre os 3 condicionais + operador
    /// <c>=</c>/<c>!=</c> descritos na decisão de 2026-08-21.</para>
    ///
    /// <para><b>Capabilities.Author é SEMPRE false, hard-coded</b> — garantia central do produto
    /// (spec §4: "Sysmiddle só executa/explica, nunca autoria"). Não vem de config, não é
    /// parametrizável por payload.</para>
    /// </summary>
    public sealed class SysmiddleExplanationAdapter : IMappingExplanationAdapter
    {
        public string Engine => "sysmiddle";

        /// <summary>
        /// Catálogo FECHADO de funções conhecidas do dispatcher <c>RuleInterpretor.ExecuteRuleFunction</c>
        /// (decisão 2026-08-21 §1 — 4 funções confirmadas). Qualquer função fora deste conjunto
        /// existe (é reconhecida como chamada), mas não tem semântica traduzível aqui → <c>opaque</c>.
        /// </summary>
        private static readonly HashSet<string> KnownFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "GetLength",
            "GetValueFromContext",
            "GetDictionaryValuesFromElement",
            "GetSumElementValuesFunction",
        };

        private static readonly EngineCapabilities FixedCapabilities =
            new(Execute: true, Explain: true, Author: false, Compile: false, Publish: false, DeterministicTest: false);

        private readonly ICachedMapperService _cachedMapperService;
        private readonly ILogger<SysmiddleExplanationAdapter> _logger;
        private readonly ISysmiddleFunctionCatalog _functions;

        // Catálogo opcional: sem ele (testes/legado) só os builtins são conhecidos.
        private static readonly Lazy<ISysmiddleFunctionCatalog> DefaultCatalog = new(() => SysmiddleFunctionCatalog.BuiltinOnly());

        public SysmiddleExplanationAdapter(ICachedMapperService cachedMapperService, ILogger<SysmiddleExplanationAdapter> logger,
            ISysmiddleFunctionCatalog? functionCatalog = null)
        {
            _cachedMapperService = cachedMapperService;
            _logger = logger;
            _functions = functionCatalog ?? DefaultCatalog.Value;
        }

        /// <summary>Função traduzível: dispatcher conhecido ou builtin do catálogo.</summary>
        private bool IsTranslatable(string f) =>
            KnownFunctions.Contains(f) || _functions.Lookup(f)?.Origin == SysmiddleFunctionCatalog.OriginBuiltin;

        private bool IsNdd(string f) => _functions.Lookup(f)?.Origin == SysmiddleFunctionCatalog.OriginNddCustom;

        /// <summary>
        /// Dependência real: catálogo de mappers Sysmiddle (<see cref="ICachedMapperService"/>, por
        /// trás dele SQL + decryptor). Timeout curto (issue #90) — é sonda, não caminho de dados.
        /// </summary>
        public async Task<CapabilityHealth> CheckAvailabilityAsync(CancellationToken cancellationToken)
        {
            // GetAllMappersAsync não aceita CancellationToken — timeout aplicado por fora via
            // Task.WhenAny, mesmo racional de "sonda com timeout curto" do resto do health check.
            try
            {
                var mappersTask = _cachedMapperService.GetAllMappersAsync();
                var completed = await Task.WhenAny(mappersTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
                if (completed != mappersTask)
                    return new CapabilityHealth(CapabilityStatus.Unavailable, "Catálogo de mappers Sysmiddle não respondeu dentro do timeout (3s).");

                var mappers = await mappersTask;
                return mappers.Count > 0
                    ? new CapabilityHealth(CapabilityStatus.Healthy, $"Catálogo de mappers Sysmiddle com {mappers.Count} entrada(s).")
                    : new CapabilityHealth(CapabilityStatus.Degraded, "Catálogo de mappers Sysmiddle vazio — explicação sem conteúdo pra retornar.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate de capacidade (#90): catálogo de mappers Sysmiddle indisponível.");
                return new CapabilityHealth(CapabilityStatus.Unavailable, $"Catálogo de mappers Sysmiddle falhou: {ex.Message}");
            }
        }

        public async Task<MappingExplanation?> ExplainAsync(MappingExplanationRequest request, CancellationToken cancellationToken)
        {
            // Sysmiddle não tem versionamento explícito — só "current" é aceito (design §0).
            if (!string.Equals(request.Version, "current", StringComparison.OrdinalIgnoreCase))
                return null;

            List<Mapper> mappers;
            try
            {
                mappers = await _cachedMapperService.GetAllMappersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar catálogo de mappers Sysmiddle para explicação de {MapperGuid}.", request.MappingId);
                throw;
            }

            var mapper = mappers.FirstOrDefault(m => string.Equals(m.MapperGuid, request.MappingId, StringComparison.OrdinalIgnoreCase));
            if (mapper == null || string.IsNullOrWhiteSpace(mapper.DecryptedContent))
                return null;

            MapperVo mapperVo;
            try
            {
                mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapper.DecryptedContent));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MapperVO {MapperGuid} não pôde ser parseado — explicação degrada para 0 regras.", request.MappingId);
                return BuildExplanation(request, mapper, rules: Array.Empty<ExplainedRule>(),
                    limitations: new[] { "MapperVO não pôde ser parseado — verifique o conteúdo descriptografado." });
            }

            var rules = new List<ExplainedRule>();
            rules.AddRange(mapperVo.LinkMappings.Select(ToExplainedRule));
            rules.AddRange(mapperVo.Rules.SelectMany(ToExplainedRules));

            return BuildExplanation(request, mapper, rules, limitations: Array.Empty<string>());
        }

        private static MappingExplanation BuildExplanation(
            MappingExplanationRequest request, Mapper mapper, IReadOnlyList<ExplainedRule> rules, IReadOnlyList<string> limitations)
        {
            var opaqueCount = rules.Count(r => r.SupportLevel == MappingExplanationSupportLevel.Opaque);

            return new MappingExplanation(
                MappingId: request.MappingId,
                Version: "current",
                Engine: "sysmiddle",
                Capabilities: FixedCapabilities,
                SourceSchema: new LayoutParserApi.Models.Dtos.Fiscal.SchemaRef(mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid, mapper.Name),
                TargetSchema: new LayoutParserApi.Models.Dtos.Fiscal.SchemaRef(mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid, mapper.Description),
                Rules: rules,
                Description: mapper.Description,
                Limitations: limitations,
                OpaqueRuleCount: opaqueCount);
        }

        /// <summary>
        /// Mapeamento direto campo→campo (sem DSL) — sempre <c>authoritative</c>, é dado de vinculação puro.
        ///
        /// <para><b>Issue #430 (alinhamento de identidade de nó):</b> <c>SourceRefs</c>/<c>TargetRefs</c>
        /// usam sempre <c>InputGuid</c>/<c>TargetGuid</c> — a MESMA fonte que <see cref="LayoutTreeService"/>
        /// usa em <c>LayoutTreeRule.SourceElementGuid</c>/<c>TargetElementGuid</c>. Antes, <c>TargetLeafName</c>
        /// (nome legível) tinha prioridade sobre o GUID aqui, mas o layout-tree sempre usa o GUID —
        /// isso quebrava o cruzamento regra↔nó no front. O nome legível continua disponível, só que
        /// em <c>HumanDescription</c> (texto pra humano, não em identificador estrutural).</para>
        /// </summary>
        private static ExplainedRule ToExplainedRule(LinkMappingItem link)
        {
            var ruleId = link.ElementGuid ?? $"link:{link.Name}";
            var target = link.TargetGuid ?? "?";
            var source = link.InputGuid ?? "?";
            var targetLabel = link.TargetLeafName ?? target;
            var sourceLabel = link.Name ?? source;

            return new ExplainedRule(
                RuleId: ruleId,
                SourceRefs: new[] { source },
                TargetRefs: new[] { target },
                Condition: null,
                Operations: new[] { "copy" },
                Cardinality: "1:1",
                Evidence: new[] { new EvidenceRef("sysmiddle-link-mapping", link.Name ?? ruleId) },
                HumanDescription: $"Copia o valor de \"{sourceLabel}\" diretamente para \"{targetLabel}\".",
                TechnicalDetail: null,
                SupportLevel: MappingExplanationSupportLevel.Authoritative);
        }

        /// <summary>
        /// Uma <see cref="MapperRule"/> (DSL) pode gerar múltiplos <see cref="ExplainedRule"/> — um
        /// por ramo da árvore de decisão (<see cref="StructuredBranch"/>), já que cada ramo tem
        /// condição/origem/destino próprios.
        /// </summary>
        private IEnumerable<ExplainedRule> ToExplainedRules(MapperRule rule)
        {
            XslSynth.Prompting.StructuredRule? structured;
            try
            {
                structured = new DslStructuredParser().Parse(rule);
            }
            catch (Exception)
            {
                structured = null;
            }

            if (structured == null)
            {
                // DSL fora do subconjunto reconhecido — nunca inventa, marca como opaque inteira.
                yield return ExplainUnstructuredRule(rule);
                yield break;
            }

            if (structured.Branches.Count == 0)
            {
                yield return ExplainUnstructuredRule(rule);
                yield break;
            }

            for (var i = 0; i < structured.Branches.Count; i++)
            {
                var branch = structured.Branches[i];
                var ruleId = (rule.ElementGuid ?? rule.Name ?? "rule") + $":{i}";
                var unknownFunctions = branch.Functions.Where(f => !IsTranslatable(f)).ToList();
                var supportLevel = unknownFunctions.Count > 0
                    ? MappingExplanationSupportLevel.Opaque
                    : MappingExplanationSupportLevel.Authoritative;

                var condition = branch.Condition == "true" ? null : branch.Condition;
                var operations = branch.Functions.Count > 0 ? branch.Functions : new List<string> { "assign" };

                yield return new ExplainedRule(
                    RuleId: ruleId,
                    SourceRefs: branch.Sources.Select(s => $"I.{s}").ToList(),
                    TargetRefs: new[] { $"T.{branch.Target}" },
                    Condition: condition,
                    Operations: operations,
                    Cardinality: structured.LoopType is null ? "1:1" : "1:N",
                    Evidence: new[] { new EvidenceRef("sysmiddle-rule", rule.Name ?? ruleId) },
                    HumanDescription: DescribeBranch(branch, condition, unknownFunctions),
                    TechnicalDetail: Truncate(rule.ContentValue),
                    SupportLevel: supportLevel,
                    Functions: branch.Functions.Count > 0 ? branch.Functions.ToList() : null);
            }
        }

        private static readonly Regex TempAssign = new(
            @"(?<![\w.])[#$]\.(?<t>\w+)\s*=\s*I\.(?<p>[@\w]+(?:/[@\w\-]+)*)\s*;", RegexOptions.Compiled);
        private static readonly Regex HasIf = new(@"\bif\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// Regra sem ramo <c>T.</c> reconhecido (só variáveis temporárias, chamadas soltas, laços...).
        /// Em vez do texto genérico: varre atribuições <c>#.campo = I.caminho</c>, blocos if/else e
        /// funções usadas. Com função NDD/não catalogada → <c>opaque</c> + <c>functions[]</c> + "usa função NDD X";
        /// só atribuições/condicionais com builtins → <c>best_effort</c>; senão mantém o texto genérico.
        /// </summary>
        private ExplainedRule ExplainUnstructuredRule(MapperRule rule)
        {
            var ruleId = rule.ElementGuid ?? rule.Name ?? Guid.NewGuid().ToString();
            var code = (rule.ContentValue ?? "")
                .Replace("%beginRuleContent;", "").Replace("%endRuleContent;", "")
                .Replace("&amp;", "&").Replace("&gt;", ">").Replace("&lt;", "<");
            var scan = RuleCodeScanner.Scan(code);
            var scanFailed = scan.Opaque.Any(o => o.Reason == "scan-failed");
            var functions = scan.Functions;
            var targets = scan.Writes.Select(w => $"T.{w}").ToList();
            if (targets.Count == 0 && rule.TargetPath is not null) targets.Add($"T.{rule.TargetPath}");
            var temps = TempAssign.Matches(code).Select(m => (Temp: m.Groups["t"].Value, Src: m.Groups["p"].Value)).ToList();
            var hasIf = HasIf.IsMatch(code);
            var nonTranslatable = functions.Where(f => !IsTranslatable(f)).ToList();
            var hasLoop = scan.Opaque.Any(o => o.Reason is "loop" or "csharp-new");

            string description, level;
            if (!scanFailed && nonTranslatable.Count > 0)
            {
                level = MappingExplanationSupportLevel.Opaque;
                var ndd = nonTranslatable.Where(IsNdd).ToList();
                var others = nonTranslatable.Except(ndd).ToList();
                var parts = new List<string>();
                if (ndd.Count > 0) parts.Add($"usa função NDD {string.Join(", ", ndd)}");
                if (others.Count > 0) parts.Add($"usa função não catalogada {string.Join(", ", others)}");
                description = "Regra que " + string.Join("; ", parts) + ".";
            }
            else if (!scanFailed && !hasLoop && (temps.Count > 0 || hasIf) && scan.Writes.Count == 0)
            {
                level = MappingExplanationSupportLevel.BestEffort;
                var tempText = temps.Count > 0
                    ? "Calcula variável(is) interna(s): " + string.Join("; ", temps.Select(t => $"#.{t.Temp} = I.{t.Src}")) + "."
                    : "Avalia condições (if/else) sobre valores do documento, sem escrever no destino.";
                description = tempText + (hasIf && temps.Count > 0 ? " Contém blocos if/else." : "");
            }
            else
            {
                level = MappingExplanationSupportLevel.Opaque;
                description = "Regra reconhecida no mapper, mas fora da gramática DSL suportada por este explicador.";
            }

            return new ExplainedRule(
                RuleId: ruleId,
                SourceRefs: scan.Reads.Select(r => $"I.{r}").ToList(),
                TargetRefs: targets,
                Condition: null,
                Operations: functions.Count > 0 ? functions.ToList() : Array.Empty<string>(),
                Cardinality: "1:1",
                Evidence: new[] { new EvidenceRef("sysmiddle-rule", rule.Name ?? ruleId) },
                HumanDescription: description,
                TechnicalDetail: Truncate(rule.ContentValue),
                SupportLevel: level,
                Functions: functions.Count > 0 ? functions.ToList() : null);
        }

        private string DescribeBranch(XslSynth.Prompting.StructuredBranch branch, string? condition, IReadOnlyList<string> nonTranslatable)
        {
            var sourcesText = branch.Sources.Count == 0 ? "um valor calculado" : string.Join(", ", branch.Sources);
            var basis = condition is null
                ? $"Preenche \"{branch.Target}\" a partir de {sourcesText}."
                : $"Quando {condition}, preenche \"{branch.Target}\" a partir de {sourcesText}.";
            if (branch.Functions.Count == 0) return basis;
            var text = basis + $" Usa a(s) função(ões): {string.Join(", ", branch.Functions)}.";
            var ndd = nonTranslatable.Where(IsNdd).ToList();
            return ndd.Count == 0 ? text : text + $" Usa função NDD {string.Join(", ", ndd)}.";
        }

        /// <summary>Trecho técnico truncado — nunca payload fiscal real, só a DSL/configuração da regra.</summary>
        private static string? Truncate(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            const int max = 400;
            return content.Length <= max ? content : content[..max] + "…";
        }
    }
}
