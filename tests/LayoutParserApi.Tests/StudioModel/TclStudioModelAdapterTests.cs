using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Services.StudioModel;
using LayoutParserApi.Services.StudioModel.Tcl;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using static LayoutParserApi.Tests.StudioModel.StudioModelTestSupport;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Adaptador TCL (Fase 2). SOMENTE amostras sintéticas (06 + variantes).</summary>
    public sealed class TclStudioModelAdapterTests
    {
        private sealed class FakeSource : ITclSource
        {
            public TclSourceResult Result { get; set; } = new(true, null, "LAY_X", null);
            public Exception? Throw { get; set; }
            public Task<TclSourceResult> GetAsync(string mapperGuid, CancellationToken ct) => Throw != null ? throw Throw : Task.FromResult(Result);
        }

        private static async Task<LayoutParserApi.Models.Dtos.StudioModel.StudioModelDocument?> Load(string? tcl, string? failure = null, bool exists = true)
        {
            var a = new TclStudioModelAdapter(new FakeSource { Result = new(exists, tcl, "LAY_X", failure) }, NullLogger<TclStudioModelAdapter>.Instance);
            return await a.LoadAsync(new StudioModelRequest(Guid.NewGuid(), MapperGuid), CancellationToken.None);
        }

        [Fact]
        public async Task Amostra_06_monta_linhas_campos_offset_e_child_aninhado()
        {
            var doc = (await Load(Fixture("06-equivalente.tcl")))!;
            Assert.Equal("tcl", doc.Artifact.Engine);
            Assert.False(doc.Capabilities.Edit);
            Assert.Empty(doc.Links); Assert.Empty(doc.Rules);
            Assert.Equal(new[] { "tcl:LINHA_CAB" }, doc.Trees.Input.RootIds); // LINHA_ITEM é filha, não raiz
            Assert.Equal("LINHA_CAB/LINHA_ITEM/Quantidade", doc.Nodes["tcl:LINHA_CAB/LINHA_ITEM/Quantidade"].Path);
            Assert.Equal("tcl:LINHA_CAB", doc.Nodes["tcl:LINHA_CAB/LINHA_ITEM"].ParentId);
            Assert.Equal(0, doc.Nodes["tcl:LINHA_CAB/TipoRegistro"].Props["offset"]);
            Assert.Equal(3, doc.Nodes["tcl:LINHA_CAB/NumeroPedido"].Props["offset"]);
            Assert.Equal(8, doc.Nodes["tcl:LINHA_CAB/LINHA_ITEM/Quantidade"].Props["offset"]); // 3 + 5
            Assert.Equal("NumeroPedido    (-, ?)", doc.Nodes["tcl:LINHA_CAB/NumeroPedido"].Display.Text);
            Assert.Empty(doc.Diagnostics);
        }

        [Fact]
        public async Task Hash_e_etag_sao_sha256_do_tcl_e_estaveis()
        {
            var tcl = Fixture("06-equivalente.tcl");
            var a = (await Load(tcl))!; var b = (await Load(tcl))!; var c = (await Load(tcl + " "))!;
            Assert.Equal(StudioModelHasher.HashSource(tcl), a.Artifact.RawHash);
            Assert.Equal(a.Artifact.ETag, b.Artifact.ETag);
            Assert.NotEqual(a.Artifact.ETag, c.Artifact.ETag);
        }

        [Fact]
        public async Task Homonimos_irmaos_ganham_sufixo_e_diagnostico()
        {
            var doc = (await Load("<MAP><LINE name='L'><FIELD name='A' length='2'/><FIELD name='A' length='3'/><FIELD name='A' length='1'/></LINE></MAP>"))!;
            Assert.Contains("tcl:L/A", doc.Nodes.Keys); Assert.Contains("tcl:L/A~2", doc.Nodes.Keys); Assert.Contains("tcl:L/A~3", doc.Nodes.Keys);
            Assert.Equal(2, doc.Nodes["tcl:L/A~2"].Props["offset"]);
            Assert.Equal("L/A", doc.Nodes["tcl:L/A~2"].Path);
            var d = Assert.Single(doc.Diagnostics, x => x.Code == "AMBIGUOUS_NAME_PATH");
            Assert.Equal(3, d.Ids!.Count);
        }

        [Fact]
        public async Task Linhas_homonimas_na_raiz_nao_colidem_e_ids_dos_descendentes_divergem()
        {
            var doc = (await Load("<MAP><LINE name='L'><FIELD name='A' length='1'/></LINE><LINE name='L'><FIELD name='A' length='1'/></LINE></MAP>"))!;
            Assert.Equal(new[] { "tcl:L", "tcl:L~2" }, doc.Trees.Input.RootIds);
            Assert.Contains("tcl:L~2/A", doc.Nodes.Keys);
            Assert.Equal(4, doc.Nodes.Count);
        }

        [Fact]
        public async Task Campo_sem_length_nao_quebra_e_marca_offset_nao_resolvido()
        {
            var doc = (await Load("<MAP><LINE name='L'><FIELD name='A' length='2'/><FIELD name='B'/><FIELD name='C' length='4'/></LINE></MAP>"))!;
            Assert.Equal(2, doc.Nodes["tcl:L/B"].Props["offset"]);
            Assert.False(doc.Nodes["tcl:L/B"].Props.ContainsKey("length"));
            Assert.False(doc.Nodes["tcl:L/C"].Props.ContainsKey("offset"));
            Assert.Contains(doc.Diagnostics, d => d.Code == "OFFSET_UNRESOLVED" && d.Id == "tcl:L/C");
        }

        [Fact]
        public async Task Length_decimal_compacto_e_atributos_desconhecidos_sao_tolerados()
        {
            var doc = (await Load("<MAP x='1'><LINE name='L' foo='bar'><FIELD name='V' length='15,2,0' zzz='1'/><FIELD name='W' length='3'/></LINE><OUTRO/></MAP>"))!;
            Assert.Equal(15, doc.Nodes["tcl:L/V"].Props["length"]);
            Assert.Equal(2, doc.Nodes["tcl:L/V"].Props["decimals"]);
            Assert.Equal(15, doc.Nodes["tcl:L/W"].Props["offset"]);
        }

        [Fact]
        public async Task Ordem_trocada_filha_antes_do_pai_e_child_no_meio_preservam_ordem_do_documento()
        {
            var doc = (await Load("<MAP><LINE name='ITEM'><FIELD name='Q' length='4'/></LINE><LINE name='CAB'><CHILD>ITEM</CHILD><FIELD name='N' length='6'/></LINE></MAP>"))!;
            Assert.Equal(new[] { "tcl:CAB" }, doc.Trees.Input.RootIds);
            Assert.Equal(1, doc.Nodes["tcl:CAB/ITEM"].Order);
            Assert.Equal(2, doc.Nodes["tcl:CAB/N"].Order);
            Assert.Equal(0, doc.Nodes["tcl:CAB/N"].Props["offset"]); // offset é por linha, CHILD não conta
        }

        [Fact]
        public async Task Child_inexistente_e_ciclo_viram_diagnostico()
        {
            var doc = (await Load("<MAP><LINE name='A'><CHILD>NAOEXISTE</CHILD><CHILD>B</CHILD></LINE><LINE name='B'><CHILD>A</CHILD></LINE></MAP>"))!;
            Assert.Contains(doc.Diagnostics, d => d.Code == "TCL_UNKNOWN_CHILD");
            Assert.Contains(doc.Diagnostics, d => d.Code == "TCL_CHILD_CYCLE");
            Assert.Equal(2, doc.Nodes.Count); // nada some
        }

        [Fact]
        public async Task Nome_ausente_gera_nome_sintetico_com_diagnostico()
        {
            var doc = (await Load("<MAP><LINE><FIELD length='2'/></LINE></MAP>"))!;
            Assert.Contains("tcl:LINE#1", doc.Nodes.Keys);
            Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "TCL_MISSING_NAME"));
        }

        [Theory]
        [InlineData("<MAP><LINE name='A'>")]
        [InlineData("isto nao e xml")]
        [InlineData("<!DOCTYPE x [<!ENTITY e 'a'>]><MAP><LINE name='A'/></MAP>")]
        [InlineData("<MAP/>")]
        public async Task Tcl_malformado_ou_vazio_degrada_sem_lancar(string tcl)
        {
            var doc = (await Load(tcl))!;
            Assert.Empty(doc.Nodes);
            Assert.False(doc.Trees.Input.Available);
            Assert.NotEmpty(doc.Diagnostics);
            Assert.Equal(StudioModelHasher.HashSource(tcl), doc.Artifact.RawHash);
        }

        [Fact]
        public async Task Layout_indisponivel_responde_200_com_diagnostico_e_mapper_inexistente_e_nulo()
        {
            var doc = (await Load(null, "Layout de entrada não encontrado."))!;
            Assert.Contains(doc.Diagnostics, d => d.Code == "LAYOUT_UNAVAILABLE");
            Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "TCL_UNPARSEABLE");
            Assert.Null(await Load(null, exists: false));
        }

        [Fact]
        public async Task Fonte_fora_propaga_indisponibilidade()
        {
            var a = new TclStudioModelAdapter(new FakeSource { Throw = new StudioModelUnavailableException("x") }, NullLogger<TclStudioModelAdapter>.Instance);
            await Assert.ThrowsAsync<StudioModelUnavailableException>(() => a.LoadAsync(new StudioModelRequest(Guid.NewGuid(), "M"), CancellationToken.None));
        }

        [Fact]
        public async Task Service_roteia_engine_tcl_para_o_adaptador_tcl()
        {
            var svc = new StudioModelService(new IStudioModelAdapter[] { new TclStudioModelAdapter(new FakeSource { Result = new(true, Fixture("06-equivalente.tcl"), "LAY_X", null) }, NullLogger<TclStudioModelAdapter>.Instance) }, NullLogger<StudioModelService>.Instance);
            var doc = await svc.GetAsync(Guid.NewGuid(), MapperGuid, "TCL", CancellationToken.None);
            Assert.Equal("tcl", doc!.Artifact.Engine);
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        [Fact]
        public async Task Snapshot_golden_da_amostra_06()
        {
            var actual = JsonSerializer.Serialize((await Load(Fixture("06-equivalente.tcl")))!, Json).Replace("\r\n", "\n");
            var dir = Environment.GetEnvironmentVariable("STUDIO_MODEL_GOLDEN_DIR");
            if (!string.IsNullOrEmpty(dir))
                File.WriteAllText(Path.Combine(dir, "studio-model-tcl.golden.json"), actual, new UTF8Encoding(false));
            Assert.Equal(Fixture("studio-model-tcl.golden.json").Replace("\r\n", "\n").TrimEnd(), actual.TrimEnd());
        }
    }
}
