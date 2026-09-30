using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// Diagnósticos sobre o modelo já montado (design §2.5). Puro, sem I/O, engine-agnóstico.
    /// Órfãos PERMANECEM em links/rules — aqui só se reporta.
    /// </summary>
    public static class DiagnosticsBuilder
    {
        public static List<StudioDiagnostic> Build(
            IReadOnlyDictionary<string, StudioNode> nodes,
            IReadOnlyDictionary<string, StudioLink> links,
            IReadOnlyDictionary<string, StudioRule> rules)
        {
            var result = new List<StudioDiagnostic>();

            // ORPHAN_LINK — origem/destino (ou âncora da regra) inexistente nas árvores.
            foreach (var (id, link) in links)
            {
                var missing = new List<string>();
                if (link.SourceId == null || !nodes.ContainsKey(link.SourceId)) missing.Add("source");
                if (link.TargetId == null || !nodes.ContainsKey(link.TargetId)) missing.Add("target");
                if (missing.Count > 0)
                    result.Add(new StudioDiagnostic("ORPHAN_LINK", "warning",
                        "Vínculo aponta para nó inexistente nas árvores.", Id: id, Missing: missing));
            }
            foreach (var (id, rule) in rules)
            {
                if (rule.AnchorId == null || !nodes.ContainsKey(rule.AnchorId))
                    result.Add(new StudioDiagnostic("ORPHAN_LINK", "warning",
                        "Regra ancorada em nó inexistente nas árvores.", Id: id, Missing: new List<string> { "target" }));
            }

            // AMBIGUOUS_NAME_PATH — ≥2 nós com o mesmo path na mesma árvore (Choice/Sequence não entram no path).
            var byPath = nodes
                .Where(n => !NodeTypeMap.IsStructuralOnly(n.Value.Type))
                .GroupBy(n => (n.Value.Tree, n.Value.Path))
                .Where(g => g.Count() >= 2);
            foreach (var g in byPath)
                result.Add(new StudioDiagnostic("AMBIGUOUS_NAME_PATH", "warning",
                    "Mais de um nó com o mesmo caminho por nome na árvore; resolução de I./T. é ambígua.",
                    Path: g.Key.Path, Ids: g.Select(x => x.Key).ToList()));

            // TARGET_HAS_LINK_AND_RULE / TARGET_HAS_MULTIPLE_LINKS
            var linksByTarget = links.Where(l => l.Value.TargetId != null).GroupBy(l => l.Value.TargetId!).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());
            var rulesByAnchor = rules.Where(r => r.Value.AnchorId != null).GroupBy(r => r.Value.AnchorId!).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());

            foreach (var (target, linkIds) in linksByTarget)
            {
                if (rulesByAnchor.ContainsKey(target))
                    result.Add(new StudioDiagnostic("TARGET_HAS_LINK_AND_RULE", "error",
                        "Nó de destino possui vínculo e regra ao mesmo tempo.", Id: target));
                if (linkIds.Count > 1)
                    result.Add(new StudioDiagnostic("TARGET_HAS_MULTIPLE_LINKS", "warning",
                        "Nó de destino recebe mais de um vínculo.", Id: target, LinkIds: linkIds));
            }
            foreach (var (anchor, ruleIds) in rulesByAnchor)
            {
                if (ruleIds.Count > 1)
                    result.Add(new StudioDiagnostic("TARGET_HAS_MULTIPLE_LINKS", "warning",
                        "Nó de destino recebe mais de uma regra.", Id: anchor, LinkIds: ruleIds));
            }

            return result;
        }
    }
}
