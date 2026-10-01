using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// Diagnósticos sobre o modelo já montado (design §2.5). Puro, sem I/O, engine-agnóstico.
    /// Hoje emite SOMENTE <c>AMBIGUOUS_NAME_PATH</c>.
    /// MUDANÇA (remoção autorizada pelo dono): os códigos legados <c>ORPHAN_LINK</c>,
    /// <c>TARGET_HAS_LINK_AND_RULE</c> e <c>TARGET_HAS_MULTIPLE_LINKS</c> deixaram de ser emitidos; foram
    /// substituídos por <c>LINK_ORPHAN_SOURCE/TARGET</c>, <c>RULE_ORPHAN_TARGET</c>, <c>TARGET_LINK_AND_RULE</c>,
    /// <c>N1_ORDER_SENSITIVE</c> e <c>TARGET_MULTIPLE_RULES</c> (<see cref="IntegrityDiagnosticsBuilder"/>).
    /// </summary>
    public static class DiagnosticsBuilder
    {
        public static List<StudioDiagnostic> Build(
            IReadOnlyDictionary<string, StudioNode> nodes,
            IReadOnlyDictionary<string, StudioLink> links,
            IReadOnlyDictionary<string, StudioRule> rules)
        {
            var result = new List<StudioDiagnostic>();

            // AMBIGUOUS_NAME_PATH — ≥2 nós com o mesmo path na mesma árvore (Choice/Sequence não entram no path).
            var byPath = nodes
                .Where(n => !NodeTypeMap.IsStructuralOnly(n.Value.Type))
                .GroupBy(n => (n.Value.Tree, n.Value.Path))
                .Where(g => g.Count() >= 2);
            foreach (var g in byPath)
                result.Add(new StudioDiagnostic("AMBIGUOUS_NAME_PATH", "warning",
                    "Mais de um nó com o mesmo caminho por nome na árvore; resolução de I./T. é ambígua.",
                    Path: g.Key.Path, Ids: g.Select(x => x.Key).ToList()));

            return result;
        }
    }
}
