using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>Referência <c>I.LINHAxxx/Campo</c> de um mapper que não confere com o layout de entrada.</summary>
    /// <param name="Reference">Ex.: <c>I.LINHA045/AliquotaDaCBS</c>.</param>
    /// <param name="Problem">"linha-inexistente", "campo-inexistente" ou "caixa-diferente".</param>
    /// <param name="Suggestion">Grafia exata do layout ou outra LINHA que contém o campo, quando há.</param>
    public sealed record TclReferenceIssue(string Reference, string Problem, string? Suggestion);

    /// <summary>
    /// Conferência determinística das referências <c>I.LINHAxxx/Campo</c> de um mapper contra o layout
    /// de entrada. O nome do campo é SENSÍVEL A CAIXA e vem do layout (o layout é a fonte da verdade,
    /// não o "português correto": <c>PercentualDoDiferimentoUF</c> ≠ <c>PercentualdoDiferimentoUF</c>).
    /// Uma referência quebrada não dá erro de parse: o campo simplesmente sai vazio e a tag some do XML
    /// (caso IVG: campos do <c>gTribCompraGov</c> apontavam para a LINHA045 quando existem na LINHA046).
    /// </summary>
    public static class TclLayoutReferenceChecker
    {
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly Regex RefRegex = new(@"\bI\.(LINHA\d+)/([A-Za-z0-9_]+)", RegexOptions.Compiled);

        public static IReadOnlyList<TclReferenceIssue> Check(string layoutXml, string mapperText)
        {
            var layout = ReadLayout(layoutXml);
            var issues = new List<TclReferenceIssue>();

            var refs = RefRegex.Matches(mapperText).Select(m => (Line: m.Groups[1].Value, Field: m.Groups[2].Value)).Distinct();
            foreach (var reference in refs)
            {
                var text = $"I.{reference.Line}/{reference.Field}";
                if (!layout.TryGetValue(reference.Line, out var fields))
                {
                    issues.Add(new TclReferenceIssue(text, "linha-inexistente", null));
                    continue;
                }
                if (fields.Contains(reference.Field))
                    continue;

                var caseMatch = fields.FirstOrDefault(f => string.Equals(f, reference.Field, StringComparison.OrdinalIgnoreCase));
                if (caseMatch != null)
                {
                    issues.Add(new TclReferenceIssue(text, "caixa-diferente", caseMatch));
                    continue;
                }

                var elsewhere = layout.FirstOrDefault(kv => kv.Value.Contains(reference.Field)).Key;
                issues.Add(new TclReferenceIssue(text, "campo-inexistente", elsewhere == null ? null : $"{elsewhere}/{reference.Field}"));
            }
            return issues;
        }

        private static Dictionary<string, HashSet<string>> ReadLayout(string layoutXml)
        {
            var doc = XDocument.Parse(layoutXml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""));
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var line in doc.Descendants().Where(e => (string?)e.Attribute(Xsi + "type") == "LineElementVO"))
            {
                var name = line.Elements().FirstOrDefault(x => x.Name.LocalName == "Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                var fields = line.Elements().FirstOrDefault(x => x.Name.LocalName == "Elements")?.Elements()
                    .Where(e => (string?)e.Attribute(Xsi + "type") == "FieldElementVO")
                    .Select(e => e.Elements().FirstOrDefault(x => x.Name.LocalName == "Name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!) ?? Enumerable.Empty<string>();

                result[name] = new HashSet<string>(fields, StringComparer.Ordinal);
            }
            return result;
        }
    }
}
