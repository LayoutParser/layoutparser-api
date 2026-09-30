using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

using Xunit;

namespace LayoutParserApi.Tests.StudioModel
{
    public sealed class MapperVoReaderTests
    {
        [Fact]
        public void Amostra03_le_links_e_regras_com_guids_de_origem_e_destino_corretos()
        {
            var r = MapperVoReader.Read(StudioModelTestSupport.MapperXml);
            Assert.Equal(3, r.Links.Count);
            Assert.Equal(StudioModelTestSupport.InputLayoutGuid, r.InputLayoutGuid);
            var l = r.Links[0];
            Assert.Equal("FLD_00000000-0000-0000-0000-00000000c002", l.SourceId);
            Assert.Equal("TAG_00000000-0000-0000-0000-00000000f001", l.TargetId);
            Assert.Equal("All", l.Opts.Trim);
            Assert.Equal(false, l.Opts.AllowEmpty);
            Assert.Null(l.Opts.Default);
            Assert.Null(l.Opts.UniqueOccurrence);          // ausente => null, nunca default inventado
            Assert.Equal("SEM-COD", r.Links[2].Opts.Default);

            var rule = Assert.Single(r.Rules);
            Assert.Equal("TAG_00000000-0000-0000-0000-00000000f003", rule.AnchorId);
            Assert.Contains("RemoveZerosLeft", rule.Code);
        }

        [Fact]
        public void Blocos_novos_vazios_e_opts_novos_sao_tolerados()
        {
            var x = XDocument.Parse(StudioModelTestSupport.MapperXml);
            x.Root!.Add(new XElement("XPathLinkMappings"), new XElement("StaticValueMappings"), new XElement("IsXPathMapper", "false"));
            var item = x.Root.Element("LinkMappings")!.Elements().First();
            item.Element("AllowEmpty")!.Remove();
            item.Add(new XElement("ElementWithoutValue", "true"), new XElement("FullXPath", "true"), new XElement("UniqueOccurrence", "true"));
            var r = MapperVoReader.Read(x.ToString());
            Assert.Equal(3, r.Links.Count);
            Assert.True(r.Links[0].Opts.UniqueOccurrence);
            Assert.Null(r.Links[0].Opts.AllowEmpty);
            Assert.Superset(new HashSet<string> { "XPathLinkMappings", "StaticValueMappings", "IsXPathMapper", "ElementWithoutValue", "FullXPath", "UniqueOccurrence" }, r.VariantFields);
        }

        [Fact]
        public void Ordem_trocada_dos_elementos_do_link_nao_altera_a_leitura()
        {
            var x = XDocument.Parse(StudioModelTestSupport.MapperXml);
            foreach (var item in x.Descendants("LinkMappingItem").ToList())
            {
                var kids = item.Elements().Reverse().ToList();
                item.RemoveNodes();
                item.Add(kids);
            }
            var a = MapperVoReader.Read(StudioModelTestSupport.MapperXml);
            var b = MapperVoReader.Read(x.ToString());
            Assert.Equal(a.Links, b.Links);
        }

        [Fact]
        public void Xml_invalido_lanca()
            => Assert.ThrowsAny<System.Xml.XmlException>(() => MapperVoReader.Read("nao-e-xml"));
    }

    public sealed class RuleCodeScannerTests
    {
        [Fact]
        public void Extrai_reads_writes_funcoes_ignorando_locais_e_palavras_chave()
        {
            var code = "#.qtd = I.LINHA_CAB/LINHA_ITEM/Quantidade;\nif (#.qtd == \"0\") begin\n T.pedido/item/qtd = \"1\";\nend\nelse begin\n T.pedido/item/qtd = RemoveZerosLeft(#.qtd);\nend";
            var s = RuleCodeScanner.Scan(code, anchorPath: "pedido/item/qtd");
            Assert.Equal(new[] { "LINHA_CAB/LINHA_ITEM/Quantidade" }, s.Reads);
            Assert.Equal(new[] { "pedido/item/qtd" }, s.Writes);
            Assert.Equal(new[] { "RemoveZerosLeft" }, s.Functions);
            Assert.Empty(s.Opaque);
        }

        [Fact]
        public void Funcao_com_efeito_colateral_vira_opaque_com_span_dentro_do_code()
        {
            var code = "T.a = 1; #.x = MSqlServerHelper(\"q\");";
            var s = RuleCodeScanner.Scan(code);
            var o = Assert.Single(s.Opaque, x => x.Reason == "side-effect:MSqlServerHelper");
            Assert.NotNull(o.Span);
            Assert.StartsWith("MSqlServerHelper", code[o.Span![0]..o.Span[1]]);
        }

        [Fact]
        public void Global_loop_new_e_escrita_fora_da_ancora_sao_marcados()
        {
            var code = "$.g = 1; while (x) { } var l = new List<string>(); T.outro/no = 2;";
            var reasons = RuleCodeScanner.Scan(code, anchorPath: "a/b").Opaque.Select(o => o.Reason).ToList();
            Assert.Contains("global-var", reasons);
            Assert.Contains("loop", reasons);
            Assert.Contains("csharp-new", reasons);
            Assert.Contains("write-outside-anchor", reasons);
        }

        [Fact]
        public void Regra_pre_pos_marca_o_codigo_inteiro()
        {
            var s = RuleCodeScanner.Scan("T.a = 1;", prePos: true);
            var o = Assert.Single(s.Opaque, x => x.Reason == "pre-pos-rule");
            Assert.Equal(new[] { 0, 8 }, o.Span);
        }

        [Fact]
        public void Codigo_nulo_nao_quebra()
            => Assert.Empty(RuleCodeScanner.Scan(null).Reads);
    }

    public sealed class DisplayTextBuilderTests
    {
        [Fact]
        public void Container_com_ocorrencias_usa_quatro_espacos_e_sinal()
        {
            Assert.Equal("LINHA    (-, 1, 999)", DisplayTextBuilder.ForContainer("LINHA", false, 1, 999));
            Assert.Equal("LINHA    (+, 0, 1)", DisplayTextBuilder.ForContainer("LINHA", true, 0, 1));
        }

        [Fact]
        public void Sem_min_ou_max_omite_o_segmento()
        {
            Assert.Equal("X    (-)", DisplayTextBuilder.ForContainer("X", false, null, null));
            Assert.Equal("X    (+)", DisplayTextBuilder.ForContainer("X", true, 1, null));
        }

        [Fact]
        public void Valor_usa_nome_do_tipo_e_interrogacao_sem_catalogo()
        {
            Assert.Equal("Nome    (-, Str_MAX)", DisplayTextBuilder.ForValue("Nome", false, "Str_MAX"));
            Assert.Equal("Nome    (-, ?)", DisplayTextBuilder.ForValue("Nome", false, null));
            Assert.Equal("Nome    (+, ?)", DisplayTextBuilder.ForValue("Nome", true, ""));
        }
    }

    public sealed class StudioModelHasherTests
    {
        [Fact]
        public void Mesmo_conteudo_mesmo_hash_e_um_byte_muda_tudo()
        {
            Assert.Equal(StudioModelHasher.HashSource("<a/>"), StudioModelHasher.HashSource("<a/>"));
            Assert.NotEqual(StudioModelHasher.HashSource("<a/>"), StudioModelHasher.HashSource("<a />"));
            Assert.StartsWith("sha256:", StudioModelHasher.HashSource("x"));
            Assert.Null(StudioModelHasher.HashSource(null));
        }

        [Fact]
        public void ETag_inclui_schemaVersion_e_tem_forma_http()
        {
            var a = StudioModelHasher.ComputeETag("m", "i", "t", 1);
            Assert.Equal(a, StudioModelHasher.ComputeETag("m", "i", "t", 1));
            Assert.NotEqual(a, StudioModelHasher.ComputeETag("m", "i", "t", 2));
            Assert.NotEqual(a, StudioModelHasher.ComputeETag("m", "i2", "t", 1));
            Assert.StartsWith("\"", a);
            Assert.EndsWith("\"", a);
        }

        [Fact]
        public void Hash_do_artefato_distingue_qual_lado_mudou()
        {
            Assert.NotEqual(StudioModelHasher.HashArtifact("m", "i", "t"), StudioModelHasher.HashArtifact("m", "t", "i"));
        }
    }

    public sealed class DiagnosticsBuilderTests
    {
        private static StudioNode Node(string tree, string type, string name, string path)
            => new(tree, type, name, path, null, 1, false, new StudioNodeDisplay(name, type, false), new());

        private static StudioLink Link(string? s, string? t)
            => new(s, t, 1, new StudioLinkDisplay("x", "linkMapping", t), new StudioLinkOpts(null, null, null, null, null, null, null, null, null, null), false);

        private static StudioRule Rule(string? anchor)
            => new(anchor, new StudioRuleDisplay("r", "rule", anchor), "r", "", new(), new(), new(), false, new());

        private static Dictionary<string, StudioNode> Nodes(params (string id, StudioNode n)[] xs) => xs.ToDictionary(x => x.id, x => x.n);

        [Fact]
        public void Cobre_os_quatro_codigos()
        {
            var nodes = Nodes(
                ("S1", Node("input", "field", "a", "a")),
                ("T1", Node("target", "tag", "t", "t")),
                ("T2", Node("target", "tag", "t", "t")),      // homônimo => AMBIGUOUS
                ("T3", Node("target", "tag", "u", "u")));
            var links = new Dictionary<string, StudioLink>
            {
                ["L1"] = Link("S1", "T3"),
                ["L2"] = Link("S1", "T3"),                    // múltiplos links em T3
                ["L3"] = Link("FANTASMA", "T1"),              // órfão (origem)
                ["L4"] = Link("S1", "T1"),                    // T1 com link E regra
            };
            var rules = new Dictionary<string, StudioRule> { ["R1"] = Rule("T1"), ["R2"] = Rule("NAO_EXISTE") };

            var d = DiagnosticsBuilder.Build(nodes, links, rules);

            var orphan = d.Where(x => x.Code == "ORPHAN_LINK").ToList();
            Assert.Contains(orphan, x => x.Id == "L3" && x.Missing!.SequenceEqual(new[] { "source" }));
            Assert.Contains(orphan, x => x.Id == "R2");
            Assert.Contains(d, x => x.Code == "AMBIGUOUS_NAME_PATH" && x.Path == "t" && x.Ids!.SequenceEqual(new[] { "T1", "T2" }));
            var both = Assert.Single(d, x => x.Code == "TARGET_HAS_LINK_AND_RULE");
            Assert.Equal("T1", both.Id);
            Assert.Equal("error", both.Severity);
            var multi = Assert.Single(d, x => x.Code == "TARGET_HAS_MULTIPLE_LINKS" && x.Id == "T3");
            Assert.Equal(new[] { "L1", "L2" }, multi.LinkIds);
        }

        [Fact]
        public void Choice_e_Sequence_nao_geram_ambiguidade()
        {
            var nodes = Nodes(("C", Node("target", "choice", "c", "p")), ("S", Node("target", "sequence", "s", "p")));
            Assert.Empty(DiagnosticsBuilder.Build(nodes, new Dictionary<string, StudioLink>(), new Dictionary<string, StudioRule>()));
        }
    }
}
