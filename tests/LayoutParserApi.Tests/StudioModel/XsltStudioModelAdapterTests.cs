using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel;
using LayoutParserApi.Services.StudioModel.Xslt;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using static LayoutParserApi.Tests.StudioModel.StudioModelTestSupport;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Adaptador XSLT (Fase 3). SOMENTE amostras sintéticas (07 + variantes) contra o layout de saída 02.</summary>
    public sealed class XsltStudioModelAdapterTests
    {
        private sealed class FakeSource : IXsltSource
        {
            public XsltSourceResult Result { get; set; } = new(true, null, TargetLayoutGuid, null, null);
            public Exception? Throw { get; set; }
            public Task<XsltSourceResult> GetAsync(string mapperGuid, CancellationToken ct) => Throw != null ? throw Throw : Task.FromResult(Result);
        }

        private static async Task<StudioModelDocument?> Load(string? xslt, string? target = null, bool exists = true, string? failure = null)
        {
            var a = new XsltStudioModelAdapter(new FakeSource { Result = new(exists, xslt, TargetLayoutGuid, target ?? TargetXml, failure) }, NullLogger<XsltStudioModelAdapter>.Instance);
            return await a.LoadAsync(new StudioModelRequest(Guid.NewGuid(), MapperGuid), CancellationToken.None);
        }

        private static string Wrap(string body) =>
            "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"><xsl:template match=\"/\">" + body + "</xsl:template></xsl:stylesheet>";

        private const string NumeroId = "TAG_00000000-0000-0000-0000-00000000f001";
        private const string ItemId = "GRT_00000000-0000-0000-0000-00000000e002";
        private const string ProdutoId = "TAG_00000000-0000-0000-0000-00000000f002";
        private const string QtdId = "TAG_00000000-0000-0000-0000-00000000f003";

        [Fact]
        public async Task Amostra_07_monta_links_iterates_e_rule_opaca_do_choose()
        {
            var doc = (await Load(Fixture("07-mapeador.xslt")))!;
            Assert.Equal("xslt", doc.Artifact.Engine);
            Assert.False(doc.Capabilities.Edit);
            Assert.Empty(doc.Capabilities.EditableOps);

            // value-of → link simples; for-each → link iterates contêiner → contêiner
            var numero = doc.Links["xslt:link:pedido/numero"];
            Assert.Equal("xslt:in:ORDEM/CAB/NumeroPedido", numero.SourceId);
            Assert.Equal(NumeroId, numero.TargetId);
            Assert.False(numero.Iterates);
            Assert.Equal("NumeroPedido_numero", numero.Display.Text);

            var item = doc.Links["xslt:link:pedido/item"];
            Assert.True(item.Iterates);
            Assert.Equal("xslt:in:ORDEM/ITENS", item.SourceId);
            Assert.Equal(ItemId, item.TargetId);

            // value-of relativo ao contexto do for-each
            Assert.Equal("xslt:in:ORDEM/ITENS/Codigo", doc.Links["xslt:link:pedido/item/produto"].SourceId);
            Assert.Equal(ProdutoId, doc.Links["xslt:link:pedido/item/produto"].TargetId);

            // choose → rule opaca ancorada em qtd, com reads; sem link para o mesmo destino
            var rule = doc.Rules["xslt:rule:pedido/item/qtd"];
            Assert.Equal(QtdId, rule.AnchorId);
            var op = Assert.Single(rule.Opaque);
            Assert.Equal("xsl:choose", op.Reason);
            Assert.Equal(new[] { 0, rule.Code!.Length }, op.Span);
            Assert.Contains("ORDEM/ITENS/Quantidade", rule.Reads);
            Assert.DoesNotContain("xslt:link:pedido/item/qtd", doc.Links.Keys);

            // árvore de destino do layout + linked; entrada sintetizada
            Assert.True(doc.Trees.Target.Available);
            Assert.True(doc.Nodes[NumeroId].Display.Linked);
            Assert.Equal("numero    (-, ?)".Replace("-", "+"), doc.Nodes[NumeroId].Display.Text);
            Assert.DoesNotContain("±", doc.Nodes[NumeroId].Display.Text);
            Assert.Equal("xml", doc.Trees.Input.Format);
            Assert.Equal(new[] { "xslt:in:ORDEM" }, doc.Trees.Input.RootIds);
            Assert.Equal("line", doc.Nodes["xslt:in:ORDEM/ITENS"].Type);
            Assert.Equal("field", doc.Nodes["xslt:in:ORDEM/CAB/NumeroPedido"].Type);
            Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "error" || d.Code.StartsWith("XSLT_UNRESOLVED") || d.Code == "ORPHAN_LINK");
        }

        [Fact]
        public async Task Hash_e_etag_sao_sha256_do_xslt_e_estaveis()
        {
            var x = Fixture("07-mapeador.xslt");
            var a = (await Load(x))!; var b = (await Load(x))!; var c = (await Load(x + " "))!;
            Assert.Equal(StudioModelHasher.HashSource(x), a.Artifact.RawHash);
            Assert.Equal(a.Artifact.ETag, b.Artifact.ETag);
            Assert.NotEqual(a.Artifact.ETag, c.Artifact.ETag);
        }

        [Fact]
        public async Task Ids_sao_deterministicos_entre_execucoes()
        {
            var x = Fixture("07-mapeador.xslt");
            var a = (await Load(x))!; var b = (await Load(x))!;
            Assert.Equal(a.Links.Keys, b.Links.Keys);
            Assert.Equal(a.Rules.Keys, b.Rules.Keys);
            Assert.Equal(a.Nodes.Keys, b.Nodes.Keys);
            Assert.All(a.Links.Keys.Concat(a.Rules.Keys), k => Assert.StartsWith("xslt:", k));
        }

        [Fact]
        public async Task Value_of_simples_e_atributo_avt_viram_links()
        {
            var doc = (await Load(Wrap("<pedido><numero><xsl:value-of select=\"A/B\"/></numero></pedido>")))!;
            var l = Assert.Single(doc.Links).Value;
            Assert.Equal(NumeroId, l.TargetId);
            Assert.Empty(doc.Rules);
        }

        [Fact]
        public async Task Value_of_com_funcao_vira_rule_opaca_com_functions()
        {
            var doc = (await Load(Wrap("<pedido><numero><xsl:value-of select=\"concat(A/B, '-', C)\"/></numero></pedido>")))!;
            var r = Assert.Single(doc.Rules).Value;
            Assert.Equal("xpath-expression", r.Opaque[0].Reason);
            Assert.Contains("concat", r.Functions);
            Assert.Contains("A/B", r.Reads);
            Assert.Empty(doc.Links);
        }

        [Fact]
        public async Task Call_template_e_variable_viram_rules_opacas_com_reason_claro()
        {
            var x = "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">"
                + "<xsl:variable name=\"g\" select=\"1\"/>"
                + "<xsl:template match=\"/\"><pedido><numero><xsl:call-template name=\"Fmt\"/></numero></pedido></xsl:template></xsl:stylesheet>";
            var doc = (await Load(x))!;
            Assert.Equal("call-template:Fmt", doc.Rules["xslt:rule:pedido/numero"].Opaque[0].Reason);
            var global = doc.Rules["xslt:rule:@global#1"];
            Assert.Equal("variable:g", global.Opaque[0].Reason);
            Assert.Null(global.AnchorId);
            Assert.Contains(doc.Diagnostics, d => d.Code == "ORPHAN_LINK" && d.Id == "xslt:rule:@global#1"); // órfão reportado, não descartado
        }

        [Fact]
        public async Task Um_vinculo_por_destino_o_primeiro_vence()
        {
            var doc = (await Load(Wrap("<pedido><numero><xsl:value-of select=\"A\"/><xsl:value-of select=\"B\"/></numero></pedido>")))!;
            Assert.Single(doc.Links);
            Assert.Equal("xslt:in:A", doc.Links["xslt:link:pedido/numero"].SourceId);
            Assert.Contains(doc.Diagnostics, d => d.Code == "XSLT_TARGET_ALREADY_BOUND");
            Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "TARGET_HAS_LINK_AND_RULE");
        }

        [Fact]
        public async Task Alvo_inexistente_gera_diagnostico_e_link_orfao_sem_lancar()
        {
            var doc = (await Load(Wrap("<pedido><naoexiste><xsl:value-of select=\"A\"/></naoexiste></pedido>")))!;
            var l = Assert.Single(doc.Links).Value;
            Assert.Null(l.TargetId);
            Assert.Contains(doc.Diagnostics, d => d.Code == "XSLT_UNRESOLVED_TARGET" && d.Path == "pedido/naoexiste");
            Assert.Contains(doc.Diagnostics, d => d.Code == "ORPHAN_LINK");
        }

        [Theory]
        [InlineData("<xsl:stylesheet")]
        [InlineData("isto nao e xml")]
        [InlineData("<root/>")]
        [InlineData("<!DOCTYPE x [<!ENTITY e 'a'>]><xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"/>")]
        public async Task Xslt_invalido_degrada_com_200_e_hash_do_bruto(string xslt)
        {
            var doc = (await Load(xslt))!;
            Assert.Contains(doc.Diagnostics, d => d.Code == "XSLT_UNPARSEABLE");
            Assert.Empty(doc.Links); Assert.Empty(doc.Rules);
            Assert.Equal(StudioModelHasher.HashSource(xslt), doc.Artifact.RawHash);
            Assert.True(doc.Trees.Target.Available); // destino continua montado
        }

        [Fact]
        public async Task Xslt_sem_template_gera_diagnostico()
        {
            var doc = (await Load("<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"/>"))!;
            Assert.Contains(doc.Diagnostics, d => d.Code == "XSLT_NO_TEMPLATE");
        }

        [Fact]
        public async Task Layout_de_destino_ausente_ou_ilegivel_degrada()
        {
            var a = (await Load(Fixture("07-mapeador.xslt"), target: "<lixo"))!;
            Assert.False(a.Trees.Target.Available);
            Assert.Contains(a.Diagnostics, d => d.Code == "LAYOUT_UNAVAILABLE");
            Assert.NotEmpty(a.Links); // links permanecem, órfãos
        }

        [Fact]
        public async Task Fonte_sem_xslt_e_mapper_inexistente()
        {
            var doc = (await Load(null, failure: "Mapper sem XSLT associado."))!;
            Assert.Contains(doc.Diagnostics, d => d.Code == "XSLT_UNAVAILABLE");
            Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "XSLT_UNPARSEABLE");
            Assert.Null(await Load(null, exists: false));
        }

        [Fact]
        public async Task Fonte_fora_propaga_indisponibilidade()
        {
            var a = new XsltStudioModelAdapter(new FakeSource { Throw = new StudioModelUnavailableException("x") }, NullLogger<XsltStudioModelAdapter>.Instance);
            await Assert.ThrowsAsync<StudioModelUnavailableException>(() => a.LoadAsync(new StudioModelRequest(Guid.NewGuid(), "M"), CancellationToken.None));
        }

        [Fact]
        public async Task Service_roteia_engine_xslt_para_o_adaptador_xslt()
        {
            var adapter = new XsltStudioModelAdapter(new FakeSource { Result = new(true, Fixture("07-mapeador.xslt"), TargetLayoutGuid, TargetXml, null) }, NullLogger<XsltStudioModelAdapter>.Instance);
            var svc = new StudioModelService(new IStudioModelAdapter[] { adapter }, NullLogger<StudioModelService>.Instance);
            var doc = await svc.GetAsync(Guid.NewGuid(), MapperGuid, "XSLT", CancellationToken.None);
            Assert.Equal("xslt", doc!.Artifact.Engine);
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        [Fact]
        public async Task Snapshot_golden_da_amostra_07()
        {
            var actual = JsonSerializer.Serialize((await Load(Fixture("07-mapeador.xslt")))!, Json).Replace("\r\n", "\n");
            var dir = Environment.GetEnvironmentVariable("STUDIO_MODEL_GOLDEN_DIR");
            if (!string.IsNullOrEmpty(dir))
                File.WriteAllText(Path.Combine(dir, "studio-model-xslt.golden.json"), actual, new UTF8Encoding(false));
            Assert.Equal(Fixture("studio-model-xslt.golden.json").Replace("\r\n", "\n").TrimEnd(), actual.TrimEnd());
        }
    }
}
