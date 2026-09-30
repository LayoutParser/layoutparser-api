using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.StudioModel;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Xunit.Abstractions;

using static LayoutParserApi.Tests.StudioModel.StudioModelTestSupport;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Adaptador Sysmiddle ponta a ponta com fakes (dados SINTÉTICOS 01-03).</summary>
    public sealed class StudioModelAdapterTests
    {
        private readonly ITestOutputHelper _out;
        public StudioModelAdapterTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task Amostras_01_02_03_montam_o_modelo_completo_sem_diagnosticos()
        {
            var (adapter, _, _) = Build();
            var doc = await Load(adapter);

            Assert.NotNull(doc);
            Assert.Equal(1, doc!.SchemaVersion);
            Assert.False(doc.Capabilities.Edit);
            Assert.Empty(doc.Capabilities.EditableOps);
            Assert.Equal("sysmiddle", doc.Artifact.Engine);
            Assert.Equal(12, doc.Nodes.Count);               // 7 (entrada) + 5 (destino: pedido, numero, item, produto, qtd)
            Assert.True(doc.Trees.Input.Available);
            Assert.Equal("text-positional", doc.Trees.Input.Format);
            Assert.Equal("xml", doc.Trees.Target.Format);
            Assert.Equal(3, doc.Links.Count);
            Assert.Empty(doc.Diagnostics);
            Assert.Empty(doc.Datatypes);                      // sem catálogo

            // ligação de grupo itera; ligação folha→folha não
            Assert.True(doc.Links["LKM_00000000-0000-0000-0000-000000003002"].Iterates);
            Assert.False(doc.Links["LKM_00000000-0000-0000-0000-000000003001"].Iterates);
            Assert.Equal("NumeroPedido_numero", doc.Links["LKM_00000000-0000-0000-0000-000000003001"].Display.Text);
            Assert.Equal("TAG_00000000-0000-0000-0000-00000000f001", doc.Links["LKM_00000000-0000-0000-0000-000000003001"].Display.UnderTargetId);

            var rule = Assert.Single(doc.Rules).Value;
            Assert.Equal("Rule_qtd", rule.Display.Text);
            Assert.Equal(new[] { "LINHA_CAB/LINHA_ITEM/Quantidade" }, rule.Reads);
            Assert.Equal(new[] { "pedido/item/qtd" }, rule.Writes);

            // linked / display
            Assert.True(doc.Nodes["FLD_00000000-0000-0000-0000-00000000c002"].Display.Linked);
            Assert.False(doc.Nodes["FLD_00000000-0000-0000-0000-00000000c001"].Display.Linked);
            Assert.True(doc.Nodes["TAG_00000000-0000-0000-0000-00000000f003"].Display.Linked); // âncora da regra
            Assert.Equal("LINHA_CAB    (+, 1, 1)", doc.Nodes["LIN_00000000-0000-0000-0000-00000000b001"].Display.Text);
            Assert.Equal("TipoRegistro    (+, ?)", doc.Nodes["FLD_00000000-0000-0000-0000-00000000c001"].Display.Text);

            Assert.Equal(3, doc.Artifact.Sources.Count);
            Assert.All(doc.Artifact.Sources, s => Assert.StartsWith("sha256:", s.RawHash));
        }

        [Fact]
        public async Task Com_catalogo_de_tipos_display_e_datatypes_sao_resolvidos()
        {
            var (adapter, _, _) = Build(catalog: new FakeCatalog());
            var doc = (await Load(adapter))!;
            Assert.Equal("TipoRegistro    (+, Str_MAX)", doc.Nodes["FLD_00000000-0000-0000-0000-00000000c001"].Display.Text);
            Assert.Equal("Str_MAX", doc.Datatypes["DAT_00000000-0000-0000-0000-00000000d001"].Name);
        }

        [Fact]
        public async Task Mapper_inexistente_retorna_null()
        {
            var (adapter, mappers, _) = Build();
            mappers.Mappers.Clear();
            Assert.Null(await Load(adapter));
        }

        [Fact]
        public async Task Mapper_sem_conteudo_retorna_null()
        {
            var (adapter, mappers, _) = Build();
            mappers.Mappers[0].DecryptedContent = "";
            Assert.Null(await Load(adapter));
        }

        [Fact]
        public async Task Catalogo_fora_lanca_StudioModelUnavailableException()
        {
            var (adapter, mappers, _) = Build();
            mappers.Throw = new InvalidOperationException("SQL fora");
            await Assert.ThrowsAsync<StudioModelUnavailableException>(() => Load(adapter));
        }

        [Fact]
        public async Task Layout_ausente_degrada_200_com_available_false()
        {
            var (adapter, _, layouts) = Build();
            layouts.ByGuid.Remove(TargetLayoutGuid);
            var doc = (await Load(adapter))!;

            Assert.False(doc.Trees.Target.Available);
            Assert.Empty(doc.Trees.Target.RootIds);
            Assert.True(doc.Trees.Input.Available);
            Assert.Contains(doc.Diagnostics, d => d.Code == "LAYOUT_UNAVAILABLE" && d.Id == TargetLayoutGuid);
            // links permanecem e viram órfãos (não descartados)
            Assert.Equal(3, doc.Links.Count);
            Assert.Contains(doc.Diagnostics, d => d.Code == "ORPHAN_LINK");
            Assert.Null(doc.Links["LKM_00000000-0000-0000-0000-000000003001"].Display.UnderTargetId);
        }

        [Fact]
        public async Task Layout_ilegivel_degrada_e_mapper_ilegivel_zera_links()
        {
            var (adapter, _, _) = Build(inputXml: "<LayoutVO>", mapperXml: "nao-e-xml");
            var doc = (await Load(adapter))!;
            Assert.False(doc.Trees.Input.Available);
            Assert.Empty(doc.Links);
            Assert.Empty(doc.Rules);
            Assert.Contains(doc.Diagnostics, d => d.Code == "MAPPER_UNREADABLE");
            Assert.Contains(doc.Diagnostics, d => d.Code == "LAYOUT_UNAVAILABLE");
        }

        [Fact]
        public async Task Lookup_de_layout_tenta_a_forma_sem_prefixo_LAY()
        {
            var (adapter, _, layouts) = Build();
            layouts.ByGuid.Remove(InputLayoutGuid);
            layouts.ByGuid[InputLayoutGuid["LAY_".Length..]] = new() { DecryptedContent = InputXml };
            Assert.True((await Load(adapter))!.Trees.Input.Available);
        }

        [Fact]
        public async Task ETag_e_estavel_e_muda_quando_um_layout_muda()
        {
            var a = (await Load(Build().Adapter))!.Artifact;
            var b = (await Load(Build().Adapter))!.Artifact;
            var c = (await Load(Build(targetXml: TargetXml + " ").Adapter))!.Artifact;
            Assert.Equal(a.ETag, b.ETag);
            Assert.Equal(a.RawHash, b.RawHash);
            Assert.NotEqual(a.ETag, c.ETag);
            Assert.Equal(a.Sources[0].RawHash, c.Sources[0].RawHash);   // mapper igual
            Assert.NotEqual(a.Sources[2].RawHash, c.Sources[2].RawHash); // só o destino mudou
        }

        [Fact]
        public async Task Diagnosticos_do_desenho_aparecem_no_mapper_adulterado()
        {
            var x = System.Xml.Linq.XDocument.Parse(MapperXml);
            var links = x.Root!.Element("LinkMappings")!;
            var dup = new System.Xml.Linq.XElement(links.Elements().First());
            dup.Element("ElementGuid")!.Value = "LKM_DUP";
            links.Add(dup);                                   // 2º link para TAG f001
            var orphan = new System.Xml.Linq.XElement(links.Elements().First());
            orphan.Element("ElementGuid")!.Value = "LKM_ORF";
            orphan.Element("InputLayoutGuid")!.Value = "FLD_INEXISTENTE";
            links.Add(orphan);
            var both = new System.Xml.Linq.XElement(x.Root.Element("Rules")!.Elements().First());
            both.Element("ElementGuid")!.Value = "RUL_2";
            both.Element("TargetElementGuid")!.Value = "TAG_00000000-0000-0000-0000-00000000f001"; // f001 já tem link
            x.Root.Element("Rules")!.Add(both);

            var doc = (await Load(Build(mapperXml: x.ToString()).Adapter))!;
            Assert.Contains(doc.Diagnostics, d => d.Code == "ORPHAN_LINK" && d.Id == "LKM_ORF");
            Assert.Contains(doc.Diagnostics, d => d.Code == "TARGET_HAS_MULTIPLE_LINKS");
            Assert.Contains(doc.Diagnostics, d => d.Code == "TARGET_HAS_LINK_AND_RULE" && d.Severity == "error");
        }

        // ---------- contrato (design §7-9) ----------

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        [Fact]
        public async Task Snapshot_do_contrato_das_amostras_01_02_03()
        {
            var doc = (await Load(Build().Adapter))!;
            var actual = JsonSerializer.Serialize(doc, Json).Replace("\r\n", "\n");

            var updateDir = Environment.GetEnvironmentVariable("STUDIO_MODEL_GOLDEN_DIR");
            if (!string.IsNullOrEmpty(updateDir))
                File.WriteAllText(Path.Combine(updateDir, "studio-model.golden.json"), actual, new UTF8Encoding(false));

            var expected = Fixture("studio-model.golden.json").Replace("\r\n", "\n");
            Assert.Equal(expected.TrimEnd(), actual.TrimEnd());
        }

        [Fact]
        public async Task Menor_e_maior_no_code_da_regra_sobrevivem_sem_escape()
        {
            var xml = MapperXml.Replace("#.qtd == \"0000\"", "I.LINHA_CAB/NumeroPedido &lt; 5 &amp;&amp; I.LINHA_CAB/NumeroPedido &gt; 1");
            var doc = (await Load(Build(mapperXml: xml).Adapter))!;
            var json = JsonSerializer.Serialize(doc, Json);
            Assert.Contains("NumeroPedido < 5 && I.LINHA_CAB/NumeroPedido > 1", json);
            Assert.DoesNotContain("\\u003C", json);
        }

        // ---------- paridade com layout-tree (design §7-10) ----------

        [Fact]
        public async Task Guids_de_layout_tree_rules_estao_contidos_em_studio_model_links()
        {
            var (adapter, mappers, layouts) = Build();
            var studio = (await Load(adapter))!;
            var tree = await new LayoutTreeService(mappers, layouts, NullLogger<LayoutTreeService>.Instance)
                .GetLayoutTreeAsync(MapperGuid, CancellationToken.None);

            Assert.NotNull(tree);
            Assert.NotEmpty(tree!.Rules);
            foreach (var rule in tree.Rules)
            {
                Assert.True(studio.Links.TryGetValue(rule.RuleId, out var link), $"link {rule.RuleId} ausente");
                Assert.Equal(rule.SourceElementGuid, link!.SourceId);
                Assert.Equal(rule.TargetElementGuid, link.TargetId);
            }
        }

        // ---------- performance (design §7-11): dados gerados por código ----------

        [Fact]
        public async Task Escala_real_11k_campos_15k_links_monta_rapido()
        {
            const int lines = 200, perLine = 55;                        // 11.000 campos
            var inp = new StringBuilder("<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"TextLayoutVO\"><LayoutType>TextPositional</LayoutType><Elements>");
            var tgt = new StringBuilder("<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"XmlLayoutVO\"><LayoutType>Xml</LayoutType><Elements>");
            var map = new StringBuilder($"<MapperVO><MapperGuid>{MapperGuid}</MapperGuid><LinkMappings>");
            var links = 0;
            for (var l = 0; l < lines; l++)
            {
                inp.Append($"<Element xsi:type=\"LineElementVO\"><ElementGuid>LIN_{l}</ElementGuid><Sequence>{l + 1}</Sequence><Name>L{l}</Name><Elements>");
                tgt.Append($"<Element xsi:type=\"GroupTagElementVO\"><ElementGuid>GRT_{l}</ElementGuid><Sequence>{l + 1}</Sequence><Name>G{l}</Name><Elements>");
                for (var f = 0; f < perLine; f++)
                {
                    inp.Append($"<Element xsi:type=\"FieldElementVO\"><ElementGuid>FLD_{l}_{f}</ElementGuid><Sequence>{f + 1}</Sequence><Name>F{f}</Name><LengthField>5</LengthField></Element>");
                    tgt.Append($"<Element xsi:type=\"TagElementVO\"><ElementGuid>TAG_{l}_{f}</ElementGuid><Sequence>{f + 1}</Sequence><Name>T{f}</Name></Element>");
                    if (links < 15000)
                    {
                        map.Append($"<LinkMappingItem><ElementGuid>LKM_{links}</ElementGuid><InputLayoutGuid>FLD_{l}_{f}</InputLayoutGuid><TargetLayoutGuid>TAG_{l}_{f}</TargetLayoutGuid></LinkMappingItem>");
                        links++;
                    }
                }
                inp.Append("</Elements></Element>");
                tgt.Append("</Elements></Element>");
            }
            inp.Append("</Elements></LayoutVO>"); tgt.Append("</Elements></LayoutVO>"); map.Append("</LinkMappings></MapperVO>");

            var (adapter, _, _) = Build(map.ToString(), inp.ToString(), tgt.ToString());
            await Load(adapter); // aquecimento (JIT)
            // Mediana de 5 execuções: o runner de CI (Debug, compartilhado) oscila muito e uma única
            // medição já estourou o limite. A meta real (500 ms) é medida em Release pelo QA.
            var tempos = new List<long>();
            StudioModelDocument doc = null!;
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                doc = (await Load(adapter))!;
                sw.Stop();
                tempos.Add(sw.ElapsedMilliseconds);
            }
            tempos.Sort();
            var mediana = tempos[tempos.Count / 2];

            _out.WriteLine($"Montagem (mediana de 5): {mediana} ms [{string.Join(", ", tempos)}], nós={doc.Nodes.Count}, links={doc.Links.Count}");
            Assert.Equal(lines + lines * perLine * 2 + lines, doc.Nodes.Count);
            Assert.Equal(11000, doc.Links.Count);             // só 11k pares de nós existem; 15k seria o teto
            // Guarda contra regressão estrutural (ex.: O(n²)), não contra a meta de desempenho.
            Assert.True(mediana < 5000, $"montagem lenta: mediana {mediana} ms");
        }
    }
}
