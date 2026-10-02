using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// Diagnósticos de integridade de ligações/regras (issue #619; análise ConnectUs B4/A4).
    /// SOMENTE leitura: órfãos são sinalizados, NUNCA removidos. Puro, sem I/O, nunca lança.
    /// Substitui os códigos legados do <see cref="DiagnosticsBuilder"/> (removidos): <c>ORPHAN_LINK</c> ->
    /// <c>LINK_ORPHAN_SOURCE/LINK_ORPHAN_TARGET/RULE_ORPHAN_TARGET</c>; <c>TARGET_HAS_LINK_AND_RULE</c> ->
    /// <c>TARGET_LINK_AND_RULE</c>; <c>TARGET_HAS_MULTIPLE_LINKS</c> (links) -> <c>N1_ORDER_SENSITIVE</c>;
    /// <c>TARGET_HAS_MULTIPLE_LINKS</c> (regras) -> <c>TARGET_MULTIPLE_RULES</c>.
    /// </summary>
    public static class IntegrityDiagnosticsBuilder
    {
        public static List<StudioDiagnostic> Build(
            IReadOnlyDictionary<string, StudioNode> nodes,
            IReadOnlyDictionary<string, StudioLink> links,
            IReadOnlyDictionary<string, StudioRule> rules)
        {
            var result = new List<StudioDiagnostic>();
            try
            {
                foreach (var (id, link) in links)
                {
                    if (link.SourceId == null || !nodes.ContainsKey(link.SourceId))
                        result.Add(new StudioDiagnostic("LINK_ORPHAN_SOURCE", "warning",
                            "Ligação aponta para elemento de origem inexistente; o ConnectUs a ignora em silêncio (não removida).",
                            Id: id, Ids: link.SourceId == null ? null : new List<string> { link.SourceId }));
                    if (link.TargetId == null || !nodes.ContainsKey(link.TargetId))
                        result.Add(new StudioDiagnostic("LINK_ORPHAN_TARGET", "warning",
                            "Ligação aponta para elemento de destino inexistente; nunca é visitada pelo ConnectUs (não removida).",
                            Id: id, Ids: link.TargetId == null ? null : new List<string> { link.TargetId }));
                }

                foreach (var (id, rule) in rules)
                {
                    if (rule.AnchorId == null || !nodes.ContainsKey(rule.AnchorId))
                        result.Add(new StudioDiagnostic("RULE_ORPHAN_TARGET", "warning",
                            "Regra ancorada em elemento de destino inexistente; nunca é executada (não removida).",
                            Id: id, RuleId: id));
                }

                // Agrupa por destino, ordem de arquivo = Sequence (Order) e, no empate, ordem de leitura.
                var byTarget = links
                    .Where(l => l.Value.TargetId != null)
                    .Select((l, i) => (l.Key, l.Value, i))
                    .GroupBy(x => x.Value.TargetId!);

                // Mais de uma regra ancorada no mesmo destino (cobria o legado TARGET_HAS_MULTIPLE_LINKS p/ regras).
                foreach (var g in rules.Where(r => r.Value.AnchorId != null).GroupBy(r => r.Value.AnchorId!).Where(g => g.Count() > 1))
                    result.Add(new StudioDiagnostic("TARGET_MULTIPLE_RULES", "warning",
                        "Nó de destino recebe mais de uma regra.", Id: g.Key, LinkIds: g.Select(x => x.Key).ToList()));

                var targetsWithRule = rules.Values.Where(r => r.AnchorId != null).Select(r => r.AnchorId!).ToHashSet(StringComparer.Ordinal);

                foreach (var g in byTarget)
                {
                    var ordered = g.OrderBy(x => x.Value.Order).ThenBy(x => x.i).ToList();
                    var ids = ordered.Select(x => x.Key).ToList();

                    if (targetsWithRule.Contains(g.Key))
                        result.Add(new StudioDiagnostic("TARGET_LINK_AND_RULE", "error",
                            "Destino possui ligação e regra: o ConnectUs só executa a ligação, a regra nunca roda.",
                            Id: g.Key, LinkIds: ids,
                            RuleId: rules.FirstOrDefault(r => r.Value.AnchorId == g.Key).Key));

                    if (ids.Count > 1)
                    {
                        // Vence a 1ª ligação COM DADOS; sem a carga real, aproxima-se pela 1ª (por Sequence) com origem existente.
                        var winner = ordered.FirstOrDefault(x => x.Value.SourceId != null && nodes.ContainsKey(x.Value.SourceId)).Key ?? ids[0];
                        result.Add(new StudioDiagnostic("N1_ORDER_SENSITIVE", "warning",
                            $"Destino com {ids.Count} ligações: no ConnectUs vence a 1ª com dados pela ordem do arquivo. A vencedora indicada ({winner}) é uma aproximação estática (1ª ligação, por Sequence, cuja origem existe no modelo), sem dado de execução.",
                            Id: g.Key, LinkIds: ids, WinnerId: winner));
                    }
                }
            }
            catch
            {
                // Diagnóstico nunca derruba a montagem do modelo.
            }
            return result;
        }
    }
}
