namespace LayoutParserApi.Services.StudioModel.Sysmiddle
{
    /// <summary>
    /// Tabela ÚNICA <c>xsi:type</c> (Sysmiddle) → <c>type</c>/<c>icon</c> do modelo (design §2.1).
    /// Tolerante: remove namespace/prefixo e o sufixo <c>ElementVO</c>, case-insensitive. Os nomes de
    /// RepeaterGroup/Grouper/JsonObject/CharacterIgnoreGroup/GroupWithoutOrder NÃO foram observados em
    /// amostra (design §9-6) — o mapeamento é por hipótese e tipo desconhecido vira <c>unknown</c>, nunca lança.
    /// </summary>
    public static class NodeTypeMap
    {
        public const string Unknown = "unknown";

        private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            ["line"] = "line",
            ["field"] = "field",
            ["repeatergroup"] = "repeaterGroup",
            ["grouper"] = "grouper",
            ["grouptag"] = "groupTag",
            ["tag"] = "tag",
            ["attribute"] = "attribute",
            ["choice"] = "choice",
            ["sequence"] = "sequence",
            ["jsonobject"] = "jsonObject",
            ["characterignoregroup"] = "characterIgnoreGroup",
            ["groupwithoutorder"] = "groupWithoutOrder",
        };

        /// <summary>Tipos que ligam por contexto de iteração (link <c>iterates</c>).</summary>
        public static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        {
            "line", "repeaterGroup", "grouper", "groupTag",
        };

        /// <summary>Nós estruturais que NÃO entram no <c>path</c> por nome (Choice/Sequence).</summary>
        public static bool IsStructuralOnly(string type) => type is "choice" or "sequence";

        public static string Resolve(string? xsiType)
        {
            if (string.IsNullOrWhiteSpace(xsiType)) return Unknown;
            var t = xsiType.Trim();
            var colon = t.LastIndexOf(':');
            if (colon >= 0) t = t[(colon + 1)..];
            if (t.EndsWith("ElementVO", StringComparison.OrdinalIgnoreCase)) t = t[..^"ElementVO".Length];
            else if (t.EndsWith("VO", StringComparison.OrdinalIgnoreCase)) t = t[..^2];
            return Types.TryGetValue(t, out var type) ? type : Unknown;
        }

        public static string IconFor(string type) => type; // ícone = próprio type (unknown permanece "unknown")
    }
}
