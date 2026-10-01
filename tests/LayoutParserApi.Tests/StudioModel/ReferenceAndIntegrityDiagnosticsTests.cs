using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel;
using Xunit;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Testes sintéticos (sem dado real) das issues #618 e #619.</summary>
    public sealed class ReferenceAndIntegrityDiagnosticsTests
    {
        private static (string, StudioNode) N(string id, string tree, string type, string name, string? parent = null)
            => (id, new StudioNode(tree, type, name, name, parent, 1, false, new StudioNodeDisplay(name, type, false), new()));

        private static Dictionary<string, StudioNode> Nodes(params (string id, StudioNode n)[] xs)
        {
            var d = new Dictionary<string, StudioNode>();
            foreach (var (id, n) in xs) d[id] = n;
            return d;
        }

        private static StudioRule Rule(string? anchor, string code)
            => new(anchor, new StudioRuleDisplay("r", "rule", anchor), "r", code, new(), new(), new(), false, new());

        private static StudioLink Link(string? s, string? t, int order = 1)
            => new(s, t, order, new StudioLinkDisplay("x", "linkMapping", t),
                new StudioLinkOpts(null, null, null, null, null, null, null, null, null, null), false);

        private static List<StudioDiagnostic> Refs(Dictionary<string, StudioNode> nodes, string? anchor, string code)
            => ReferenceDiagnosticsBuilder.Build(nodes, new Dictionary<string, StudioRule> { ["R"] = Rule(anchor, code) });

        private static Dictionary<string, StudioNode> Sample() => Nodes(
            N("I0", "input", "line", "REG"),
            N("I1", "input", "field", "FILLER", "I0"),
            N("I2", "input", "field", "VALOR", "I0"),
            N("I3", "input", "field", "FILLER", "I0"),
            N("I4", "input", "line", "ITE"),
            N("I5", "input", "field", "COD", "I4"),
            N("I6", "input", "line", "CAB"),
            N("I7", "input", "field", "VL-TOT", "I6"),
            N("T2", "target", "tag", "antes"),
            N("T0", "target", "groupTag", "item"),
            N("T1", "target", "tag", "codigo", "T0"),
            N("TS", "target", "sequence", "seq", "T0"),
            N("T3", "target", "tag", "dentro", "TS"));

        [Fact]
        public void B_ex1_homonimos_origem_sao_ambiguos_e_1o_vence()
        {
            var d = Refs(Sample(), "T1", "#.f = I.REG/FILLER;");
            var a = Assert.Single(d, x => x.Code == "REF_AMBIGUOUS");
            Assert.Equal(new[] { "I1", "I3" }, a.Ids);
            Assert.Contains("I1", a.Message);
            Assert.Equal("R", a.RuleId);
            Assert.NotNull(a.Span);
        }

        [Fact]
        public void I_ignora_caixa_e_resolve_sem_diagnostico()
        {
            Assert.Empty(Refs(Sample(), "T1", "T.item/codigo = I.ite/cod;"));
        }

        [Fact]
        public void B_ex3_indice_e_caixa()
        {
            var d = Refs(Sample(), "T1", "#.c = I.ITE[2]/COD;\nT.Item/codigo = #.c;");
            Assert.Single(d, x => x.Code == "REF_INDEX_UNSUPPORTED");
            var cm = Assert.Single(d, x => x.Code == "REF_T_CASE_MISMATCH");
            Assert.Equal("item/codigo", cm.Suggestion);
            Assert.DoesNotContain(d, x => x.Code == "REF_UNRESOLVED");
        }

        [Fact]
        public void B_ex4_nome_com_hifen_quebra_o_token()
        {
            var d = Refs(Sample(), "T1", "#.v = I.CAB/VL-TOT;");
            Assert.Single(d, x => x.Code == "REF_NAME_BREAKS_TOKEN");
            Assert.DoesNotContain(d, x => x.Code == "REF_UNRESOLVED");
        }

        [Fact]
        public void Nao_resolvido_sugere_caminho_de_1_segmento_diferente()
        {
            var d = Refs(Sample(), "T1", "#.c = I.ITE/CODIGO; T.item/cod = 'x';");
            var s = d.Where(x => x.Code == "REF_UNRESOLVED").ToList();
            Assert.Equal(2, s.Count);
            Assert.Equal("ITE/COD", s[0].Suggestion);
            Assert.Equal("item/codigo", s[1].Suggestion);
            Assert.Equal("warning", s[0].Severity);
        }

        [Fact]
        public void Pula_sequence_no_fullxpath_e_ignora_literal()
        {
            Assert.Empty(Refs(Sample(), "T1", "T.item/dentro = '1'; // I.NADA/X"));
        }

        [Fact]
        public void T_para_no_visitado_antes_da_ancora_nao_e_consumido()
        {
            var d = Refs(Sample(), "T1", "T.antes = 'x';");
            Assert.Single(d, x => x.Code == "REF_T_NOT_CONSUMED");
            Assert.Empty(Refs(Sample(), "T2", "T.antes = 'x';")); // a própria âncora não é "antes"
        }

        [Fact]
        public void Codigo_vazio_ou_sem_referencia_nao_gera_nada()
        {
            Assert.Empty(Refs(Sample(), "T1", ""));
        }

        [Fact]
        public void Cobre_os_cinco_codigos_de_integridade()
        {
            var nodes = Nodes(N("S1", "input", "field", "a"), N("T1", "target", "tag", "t"), N("T2", "target", "tag", "u"));
            var links = new Dictionary<string, StudioLink>
            {
                ["L1"] = Link("S1", "T2", 2),
                ["L2"] = Link("S1", "T2", 1),        // N:1 — vence L2 (Sequence menor)
                ["L3"] = Link("FANTASMA", "T1"),     // origem órfã
                ["L4"] = Link("S1", "SUMIU"),        // destino órfão
            };
            var rules = new Dictionary<string, StudioRule>
            {
                ["R1"] = Rule("T2", ""),             // T2 com ligação e regra
                ["R2"] = Rule("NAO_EXISTE", ""),
            };

            var d = IntegrityDiagnosticsBuilder.Build(nodes, links, rules);

            Assert.Equal("L3", Assert.Single(d, x => x.Code == "LINK_ORPHAN_SOURCE").Id);
            Assert.Equal("L4", Assert.Single(d, x => x.Code == "LINK_ORPHAN_TARGET").Id);
            Assert.Equal("R2", Assert.Single(d, x => x.Code == "RULE_ORPHAN_TARGET").Id);
            var both = Assert.Single(d, x => x.Code == "TARGET_LINK_AND_RULE");
            Assert.Equal("T2", both.Id);
            Assert.Equal("R1", both.RuleId);
            var n1 = Assert.Single(d, x => x.Code == "N1_ORDER_SENSITIVE");
            Assert.Equal("L2", n1.WinnerId);
            Assert.Equal(new[] { "L2", "L1" }, n1.LinkIds);
            Assert.Equal(4, links.Count + 0); // nada removido
            Assert.Equal(2, rules.Count);
        }
    }
}
