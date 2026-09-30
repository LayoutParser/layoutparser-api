using System.Text.RegularExpressions;

using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.StudioModel.Sysmiddle
{
    /// <summary>Resultado da varredura textual do código de uma regra.</summary>
    public sealed record RuleScanResult(
        List<string> Reads, List<string> Writes, List<string> Functions, List<StudioOpaqueSpan> Opaque);

    /// <summary>
    /// Tokenizador simples do <c>code</c> de uma regra (design §8-9): extrai <c>I.</c>/<c>T.</c>, funções
    /// e marca trechos opacos com <c>span</c> (offsets de caractere). NÃO interpreta C# e NÃO executa nada.
    /// Falha ⇒ regra com <c>opaque:[scan-failed]</c>; nunca lança.
    /// </summary>
    public static class RuleCodeScanner
    {
        // Funções com efeito colateral (banco/config/cripto/log) — fonte única (relatório §4/§5).
        private static readonly HashSet<string> SideEffectFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "MSqlServerHelper", "OracleHelper", "GetConfigParametersValue", "DecryptData", "LogHelperMapper",
        };

        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "if", "else", "while", "for", "foreach", "switch", "catch", "return", "begin", "end",
        };

        private static readonly Regex PathRef = new(
            @"(?<![\w.])(?<k>[IT])\.(?<p>[@\w]+(?:/[@\w\-]+)*)", RegexOptions.Compiled);
        private static readonly Regex GlobalVar = new(@"(?<![\w.])\$\.\w+", RegexOptions.Compiled);
        private static readonly Regex Call = new(@"(?<![\w.])(?<f>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex Loop = new(@"\b(while|for|foreach)\b", RegexOptions.Compiled);
        private static readonly Regex CsNew = new(@"\bnew\s+[A-Za-z_][\w.<>]*", RegexOptions.Compiled);

        /// <param name="anchorPath">Caminho por nome do nó-âncora (para <c>write-outside-anchor</c>); <c>null</c> = não avalia.</param>
        public static RuleScanResult Scan(string? code, bool prePos = false, string? anchorPath = null)
        {
            try
            {
                return ScanCore(code ?? string.Empty, prePos, anchorPath);
            }
            catch
            {
                return new RuleScanResult(new(), new(), new(),
                    new() { new StudioOpaqueSpan("scan-failed", null) });
            }
        }

        private static RuleScanResult ScanCore(string code, bool prePos, string? anchorPath)
        {
            var reads = new List<string>();
            var writes = new List<string>();
            var functions = new List<string>();
            var opaque = new List<StudioOpaqueSpan>();

            if (prePos)
                opaque.Add(new StudioOpaqueSpan("pre-pos-rule", new[] { 0, code.Length }));

            foreach (Match m in PathRef.Matches(code))
            {
                var path = m.Groups["p"].Value;
                if (m.Groups["k"].Value == "I")
                {
                    if (!reads.Contains(path)) reads.Add(path);
                }
                else
                {
                    if (!writes.Contains(path)) writes.Add(path);
                    if (anchorPath != null && path != anchorPath && !path.StartsWith(anchorPath + "/", StringComparison.Ordinal))
                        opaque.Add(new StudioOpaqueSpan("write-outside-anchor", Span(m)));
                }
            }

            foreach (Match m in Call.Matches(code))
            {
                var f = m.Groups["f"].Value;
                if (Keywords.Contains(f)) continue;
                if (!functions.Contains(f)) functions.Add(f);
                if (SideEffectFunctions.Contains(f))
                    opaque.Add(new StudioOpaqueSpan($"side-effect:{f}", Span(m)));
            }

            foreach (Match m in GlobalVar.Matches(code)) opaque.Add(new StudioOpaqueSpan("global-var", Span(m)));
            foreach (Match m in Loop.Matches(code)) opaque.Add(new StudioOpaqueSpan("loop", Span(m)));
            foreach (Match m in CsNew.Matches(code)) opaque.Add(new StudioOpaqueSpan("csharp-new", Span(m)));

            return new RuleScanResult(reads, writes, functions, opaque);
        }

        private static int[] Span(Match m) => new[] { m.Index, m.Index + m.Length };
    }
}
