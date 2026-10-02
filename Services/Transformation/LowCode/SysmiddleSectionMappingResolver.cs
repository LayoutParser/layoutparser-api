using System.Xml.Linq;

using LayoutParserApi.Models.Transformation;

using XslSynth.Core;
using XslSynth.Model;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Fase 0 do contrato de rastreabilidade TXT↔XML (issue #138/#126) para o pathway <c>sysmiddle</c>.
    ///
    /// <para><b>Fonte de dado — 100% estrutural, nunca por valor.</b> Usa <see cref="RealMapperParser"/>
    /// (já canônico desde a issue #139) para ler o MapeadorVO decifrado (<c>Mapper.DecryptedContent</c>)
    /// e olha só para <see cref="MapperRule"/> — não para <see cref="LinkMappingItem"/>. Motivo: a DSL de
    /// uma <c>Rule</c> (<c>ContentValue</c>) carrega os dois lados de forma estrutural —
    /// <c>T.&lt;path&gt;</c> já é o XPath COMPLETO de destino (extraído pelo próprio
    /// <see cref="RealMapperParser.TargetPathFromDsl"/>) e <c>I.&lt;LinhaOrigem&gt;/...</c> referencia o
    /// NOME da linha de origem, que é a mesma convenção de <c>LineInfo.LineName</c> usada pelo parser
    /// posicional (Models/Entities/LineInfo.cs). Já <c>LinkMappingItem</c> só resolve a FOLHA do destino
    /// por convenção de nome (ver GuidXPathCatalog / A3) — sem o LayoutVO completo do destino carregado
    /// em memória neste endpoint, não dá para montar o XPath completo de forma estrutural, então esses
    /// itens são deliberadamente IGNORADOS aqui (não geram <c>best-effort</c> por aproximação).
    /// </para>
    ///
    /// <para><b>Limitação conhecida (Fase 0, documentada — não escondida):</b> <see cref="SectionMappingSource.LineOccurrence"/>
    /// não resolve a ocorrência FÍSICA real da linha dentro do TXT recebido nesta chamada — este
    /// endpoint (<c>execute-candidates</c>) não roda o parser posicional antes de invocar o runner
    /// low-code (o .exe recebe o TXT bruto). O que É resolvido estruturalmente: quando a MESMA linha
    /// alimenta N regras distintas do MESMO mapper (grupo repetido modelado como N regras em sequência,
    /// não como 1 regra com loop), cada regra recebe uma ocorrência 1..N crescente, na ordem de
    /// <see cref="MapperRule.Sequence"/> — isso É estrutural (contagem de regras declaradas), não é
    /// inferência sobre o conteúdo do documento. O mesmo vale para <see cref="SectionMappingTarget.XmlOccurrence"/>:
    /// contagem de nós que casam com o XPath no XML do PRÓPRIO candidato já gerado.
    /// </para>
    /// </summary>
    public static class SysmiddleSectionMappingResolver
    {

        /// <summary>
        /// Resolve os <see cref="SectionMapping"/> de um candidato sysmiddle já executado.
        /// Nunca lança: qualquer falha de parse do mapper/XML degrada para lista vazia (pathway
        /// suporta, mas não encontrou nada resolvível para ESTE candidato) — nunca derruba
        /// <c>execute-candidates</c>.
        /// </summary>
        public static (List<SectionMapping> Mappings, Dictionary<string, string>? Namespaces) Resolve(
            string? mapperDecryptedContent, string? outputXml, Action<string>? log = null)
        {
            var result = new List<SectionMapping>();

            if (string.IsNullOrWhiteSpace(mapperDecryptedContent) || string.IsNullOrWhiteSpace(outputXml))
                return (result, null);

            MapperVo mapperVo;
            try
            {
                mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapperDecryptedContent));
            }
            catch (Exception ex)
            {
                log?.Invoke($"[section-mappings] falha ao parsear MapperVO: {ex.Message}");
                return (result, null);
            }

            XDocument outputDoc;
            try
            {
                outputDoc = XDocument.Parse(outputXml);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[section-mappings] XML de saída do candidato ilegível: {ex.Message}");
                return (result, null);
            }

            // Namespace único do documento de saída (default xmlns da raiz) — reportado uma vez, no
            // nível do candidato, com prefixo estável "nfe" (única raiz de destino modelada hoje).
            var defaultNs = outputDoc.Root?.GetDefaultNamespace();
            var xmlNamespaces = (defaultNs is not null && defaultNs != XNamespace.None)
                ? new Dictionary<string, string> { ["nfe"] = defaultNs.NamespaceName }
                : null;

            // Ocorrência do lado da LINHA: contador por (lineName, targetPath EXATO) — regra
            // estrutural declarada em duplicidade é o único sinal aceito de "grupo repetido"
            // nesta fase (ver limitação no cabeçalho da classe).
            var lineOccurrenceCounter = new Dictionary<(string LineName, string TargetPath), int>();

            foreach (var rule in mapperVo.Rules.OrderBy(r => r.Sequence))
            {
                if (string.IsNullOrWhiteSpace(rule.ContentValue) || string.IsNullOrWhiteSpace(rule.TargetPath))
                    continue;

                // Estrutural: só aceita TargetPath que veio de fato de "T.<path>" na DSL (não do
                // fallback por sufixo de Name — esse fallback é aproximação por convenção de nome,
                // não estrutura declarada; ver RealMapperParser.Parse §TargetPath).
                var targetPathFromDsl = RealMapperParser.TargetPathFromDsl(rule.ContentValue);
                if (string.IsNullOrWhiteSpace(targetPathFromDsl))
                    continue;

                // I.<LinhaOrigem>/... — tokenização fiel ao motor; nome da linha = 1º segmento do caminho.
                var firstInput = SysmiddleDslTokenizer.InputPaths(rule.ContentValue).FirstOrDefault();
                if (string.IsNullOrEmpty(firstInput))
                    continue;

                var lineName = firstInput.Split('/', 2)[0].Trim();
                if (lineName.Length == 0)
                    continue;

                var xpath = BuildXPath(targetPathFromDsl, xmlNamespaces is not null ? "nfe" : null);
                var xmlOccurrence = CountXPathOccurrences(outputDoc, targetPathFromDsl, xmlNamespaces);

                var key = (lineName, targetPathFromDsl);
                lineOccurrenceCounter.TryGetValue(key, out var prevCount);
                var lineOccurrence = prevCount + 1;
                lineOccurrenceCounter[key] = lineOccurrence;

                result.Add(new SectionMapping
                {
                    Source = new SectionMappingSource
                    {
                        LineGuid = rule.ElementGuid,
                        LineName = lineName,
                        LineOccurrence = lineOccurrence
                    },
                    Targets = new List<SectionMappingTarget>
                    {
                        new SectionMappingTarget
                        {
                            XPath = xpath,
                            NodeKind = "element",
                            XmlOccurrence = Math.Max(1, xmlOccurrence)
                        }
                    },
                    // Sempre "authoritative": só chegamos aqui com XPath vindo de T.<path> DECLARADO
                    // na DSL — nenhum fallback/heurística é usado neste resolver (ver classe doc).
                    Confidence = "authoritative"
                });
            }

            return (result, xmlNamespaces);
        }

        /// <summary>Monta XPath absoluto a partir do path relativo derivado da DSL (ex.: "NFe/infNFe/emit").</summary>
        private static string BuildXPath(string dslPath, string? nsPrefix)
        {
            var segments = dslPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return "/";

            var prefixed = nsPrefix is null
                ? segments
                : segments.Select(s => s.StartsWith('@') ? s : $"{nsPrefix}:{s}");

            return "/" + string.Join("/", prefixed);
        }

        /// <summary>
        /// Conta nós no XML de saída do PRÓPRIO candidato que casam com o path (por nome local, sem
        /// depender do prefixo declarado no documento — o documento pode usar prefixo diferente de
        /// "nfe"). Falha/ambiguidade → 1 (não bloqueia o mapping, só não refina a ocorrência).
        /// </summary>
        private static int CountXPathOccurrences(XDocument doc, string dslPath, Dictionary<string, string>? namespaces)
        {
            try
            {
                var segments = dslPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !s.StartsWith('@'))
                    .ToArray();
                if (segments.Length == 0 || doc.Root is null)
                    return 1;

                IEnumerable<XElement> current = new[] { doc.Root };
                // O primeiro segmento normalmente já é o nome da raiz (ex.: "NFe") — pula se casar.
                var startIndex = string.Equals(segments[0], doc.Root.Name.LocalName, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                for (var i = startIndex; i < segments.Length; i++)
                {
                    var name = segments[i];
                    current = current.SelectMany(e => e.Elements().Where(c => c.Name.LocalName == name));
                }

                var count = current.Count();
                return count > 0 ? count : 1;
            }
            catch
            {
                return 1;
            }
        }
    }
}
