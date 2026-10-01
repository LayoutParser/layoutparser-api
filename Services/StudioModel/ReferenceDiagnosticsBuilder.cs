using LayoutParserApi.Models.Dtos.StudioModel;
using XslSynth.Core;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// Diagnósticos de referência <c>I.</c>/<c>T.</c> das regras (issue #618; análise ConnectUs B1-B3).
    /// Semântica fiel ao motor 4.4.1: tokenização via <see cref="SysmiddleDslTokenizer"/>; <c>I.</c> em
    /// OrdinalIgnoreCase (1º em pré-ordem); <c>T.</c> ordinal exato; FullXPath sem nome do layout e pulando
    /// Sequence/GroupWithoutOrder/CharacterIgnoreGroup. SOMENTE leitura/explicação. Nunca lança.
    /// Ordem de <c>nodes</c> = pré-ordem da árvore (contrato do modelo). Span = offsets no <c>Code</c> da regra.
    /// </summary>
    public static class ReferenceDiagnosticsBuilder
    {
        private static readonly HashSet<string> SkippedTypes = new(StringComparer.Ordinal)
            { "sequence", "groupWithoutOrder", "characterIgnoreGroup" };

        private const string Separators = ",-+*!|&}\r\n\t\a\b\v\f;=)><";

        private sealed record Entry(string Id, string Path, int Pos);

        public static List<StudioDiagnostic> Build(
            IReadOnlyDictionary<string, StudioNode> nodes,
            IReadOnlyDictionary<string, StudioRule> rules)
        {
            var result = new List<StudioDiagnostic>();
            try
            {
                var input = BuildEntries(nodes, "input");
                var target = BuildEntries(nodes, "target");
                var targetPos = target.ToDictionary(e => e.Id, e => e.Pos, StringComparer.Ordinal);

                foreach (var (ruleId, rule) in rules)
                {
                    if (string.IsNullOrEmpty(rule.Code)) continue;
                    var anchorPos = rule.AnchorId != null && targetPos.TryGetValue(rule.AnchorId, out var ap) ? ap : (int?)null;
                    foreach (var r in SysmiddleDslTokenizer.Scan(rule.Code))
                    {
                        if (r.Kind == SysmiddleRefKind.Input) Check(result, ruleId, rule.Code, r, input, ignoreCase: true, anchorPos: null);
                        else if (r.Kind == SysmiddleRefKind.Target) Check(result, ruleId, rule.Code, r, target, ignoreCase: false, anchorPos);
                    }
                }
            }
            catch
            {
                // Diagnóstico nunca derruba a montagem do modelo.
            }
            return result;
        }

        private static void Check(List<StudioDiagnostic> result, string ruleId, string code, SysmiddleRef r,
            List<Entry> entries, bool ignoreCase, int? anchorPos)
        {
            var prefix = ignoreCase ? "I." : "T.";
            var span = new[] { r.Start, r.End };
            var path = r.Path;
            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // [n] não existe no FullXPath: I. => Null; T. => valor perdido.
            if (path.Contains('['))
            {
                result.Add(Diag("REF_INDEX_UNSUPPORTED", "warning",
                    $"{prefix}{path}: índice [n] não é suportado em {prefix}; o ConnectUs não resolve (use GetValueFromElementXPath ou GetListValuesFromFullXPath).",
                    ruleId, path, span, "GetValueFromElementXPath / GetListValuesFromFullXPath"));
                return;
            }

            var matches = entries.Where(e => string.Equals(e.Path, path, cmp)).ToList();
            if (matches.Count >= 2)
                result.Add(Diag("REF_AMBIGUOUS", "warning",
                    $"{prefix}{path}: {matches.Count} elementos homônimos; o ConnectUs escolhe o 1º em pré-ordem ({matches[0].Id})"
                    + (ignoreCase ? "." : " e ele consome todos os valores."),
                    ruleId, path, span, null, ids: matches.Select(m => m.Id).ToList()));

            if (matches.Count > 0)
            {
                if (anchorPos.HasValue && matches[0].Pos < anchorPos.Value)
                    result.Add(Diag("REF_T_NOT_CONSUMED", "warning",
                        $"T.{path}: o destino é visitado antes da âncora da regra; o valor atribuído não é consumido.",
                        ruleId, path, span, null));
                return;
            }

            // Não resolve. T. só difere na caixa => o ConnectUs perde o valor.
            if (!ignoreCase)
            {
                var ci = entries.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (ci.Count > 0)
                {
                    result.Add(Diag("REF_T_CASE_MISMATCH", "error",
                        $"T.{path}: T. é sensível à caixa; o caminho existe como '{ci[0].Path}' e o valor é perdido em silêncio.",
                        ruleId, path, span, ci[0].Path));
                    return;
                }
            }

            // Nome que contém separador: o token é cortado antes do nome completo (ex.: I.CAB/VL-TOT).
            var after = code.Substring(r.Start + 2);
            var breaker = entries.FirstOrDefault(e => e.Path.Length > path.Length
                && e.Path.IndexOfAny(Separators.ToCharArray()) >= 0
                && after.StartsWith(e.Path, cmp));
            if (breaker != null)
            {
                result.Add(Diag("REF_NAME_BREAKS_TOKEN", "error",
                    $"{prefix}{path}: o nome '{breaker.Path}' contém caractere separador; o motor lê só '{path}' e o resto vira código (erro de compilação).",
                    ruleId, path, span, null));
                return;
            }

            var sug = Suggest(path, entries, cmp);
            result.Add(Diag("REF_UNRESOLVED", "warning",
                $"{prefix}{path}: caminho não resolve" + (ignoreCase ? " (resulta em Null)" : " (valor descartado)")
                + " em silêncio no ConnectUs." + (sug != null ? $" Sugestão: '{sug}'." : string.Empty),
                ruleId, path, span, sug));
        }

        /// <summary>Caminho existente com mesmo nº de segmentos e exatamente 1 segmento diferente.</summary>
        private static string? Suggest(string path, List<Entry> entries, StringComparison cmp)
        {
            var seg = path.Split('/');
            foreach (var e in entries)
            {
                var es = e.Path.Split('/');
                if (es.Length != seg.Length) continue;
                var diff = 0;
                for (var i = 0; i < seg.Length && diff < 2; i++)
                    if (!string.Equals(seg[i], es[i], cmp)) diff++;
                if (diff == 1) return e.Path;
            }
            return null;
        }

        private static StudioDiagnostic Diag(string code, string sev, string msg, string ruleId, string path,
            int[] span, string? suggestion, List<string>? ids = null)
            => new(code, sev, msg, Id: ruleId, Path: path, Ids: ids, Span: span, Suggestion: suggestion, RuleId: ruleId);

        /// <summary>FullXPath (sem nome do layout, pulando tipos estruturais) por nó, na pré-ordem do dicionário.</summary>
        private static List<Entry> BuildEntries(IReadOnlyDictionary<string, StudioNode> nodes, string tree)
        {
            var list = new List<Entry>();
            var pos = 0;
            foreach (var (id, n) in nodes)
            {
                if (n.Tree != tree) continue;
                pos++;
                if (SkippedTypes.Contains(n.Type)) continue;
                var segs = new List<string>();
                var cur = n;
                var guard = 0;
                while (cur != null && guard++ < 256)
                {
                    if (!SkippedTypes.Contains(cur.Type)) segs.Add(cur.Name);
                    cur = cur.ParentId != null && nodes.TryGetValue(cur.ParentId, out var p) ? p : null;
                }
                segs.Reverse();
                list.Add(new Entry(id, string.Join('/', segs), pos));
            }
            return list;
        }
    }
}
