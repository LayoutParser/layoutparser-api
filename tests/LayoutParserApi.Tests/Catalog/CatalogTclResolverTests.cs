using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Catalog
{
    /// <summary>Issue #636 — o pipeline de TXT resolve o TCL por catalogId (disco primeiro, catálogo como fallback).</summary>
    public class CatalogTclResolverTests : IDisposable
    {
        private const string Layout = "LAY_CAT";
        private const string Tcl = "<MAP><LINE identifier=\"HEADER\" name=\"HEADER\"><FIELD name=\"data\" length=\"8\"/></LINE></MAP>";

        private readonly string _corpus = Directory.CreateTempSubdirectory("lp-cat-corpus-").FullName;
        private readonly string _tclDir = Directory.CreateTempSubdirectory("lp-cat-tcl-").FullName;
        private readonly string _xslDir = Directory.CreateTempSubdirectory("lp-cat-xsl-").FullName;

        public void Dispose()
        {
            foreach (var d in new[] { _corpus, _tclDir, _xslDir }) Directory.Delete(d, true);
        }

        private sealed class StubSource(SourceSystem system, MappingContent? content, bool throws = false) : IMappingCatalogSource
        {
            public int Calls;
            public SourceSystem System => system;
            public Task<MappingCatalogSnapshot> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef r, CancellationToken ct)
            {
                Calls++;
                if (throws) throw new InvalidOperationException("origem fora");
                return Task.FromResult(content);
            }
        }

        private static IOptionsMonitor<MappingCatalogOptions> Opts(params SourceSystem[] enabled)
        {
            var o = new MappingCatalogOptions();
            foreach (var s in enabled) o.Sources[s.ToString()] = new MappingCatalogSourceToggle { Enabled = true };
            return new StaticMonitor(o);
        }

        private sealed class StaticMonitor(MappingCatalogOptions v) : IOptionsMonitor<MappingCatalogOptions>
        {
            public MappingCatalogOptions CurrentValue => v;
            public MappingCatalogOptions Get(string? name) => v;
            public IDisposable? OnChange(Action<MappingCatalogOptions, string?> listener) => null;
        }

        private static MappingCatalogItemDto Item(string engine = "tcl", SourceSystem system = SourceSystem.Own, bool retired = false, string? hash = "abc123")
            => new(Guid.NewGuid(), Guid.NewGuid(), system, "k", engine, "n", null, null, hash, "{}", retired);

        private static CatalogTclResolver Resolver(FakeMappingCatalogStore store, IMappingCatalogSource source, IOptionsMonitor<MappingCatalogOptions> opts)
            => new(store, [source], opts, NullLogger<CatalogTclResolver>.Instance);

        private static FakeMappingCatalogStore StoreWith(MappingCatalogItemDto item)
        {
            var s = new FakeMappingCatalogStore();
            s.Items[item.CatalogId] = item;
            return s;
        }

        // ---- Resolver ----
        [Fact]
        public async Task Item_tcl_com_origem_ligada_devolve_conteudo_e_hash()
        {
            var item = Item(hash: "h1");
            var r = await Resolver(StoreWith(item), new StubSource(SourceSystem.Own, new(Tcl, "tcl")), Opts(SourceSystem.Own))
                .ResolveTclAsync(item.CatalogId);
            Assert.NotNull(r);
            Assert.Equal(Tcl, r!.Content);
            Assert.Equal("h1", r.ContentHash);
            Assert.Equal(item.CatalogId, r.CatalogId);
        }

        [Theory]
        [InlineData("xsl")]
        [InlineData("xslt")]
        public async Task Engine_diferente_de_tcl_e_recusado_sem_ler_a_origem(string engine)
        {
            var item = Item(engine);
            var src = new StubSource(SourceSystem.Own, new(Tcl, "tcl"));
            Assert.Null(await Resolver(StoreWith(item), src, Opts(SourceSystem.Own)).ResolveTclAsync(item.CatalogId));
            Assert.Equal(0, src.Calls);
        }

        [Fact]
        public async Task Origem_desligada_nao_e_lida()
        {
            var item = Item();
            var src = new StubSource(SourceSystem.Own, new(Tcl, "tcl"));
            Assert.Null(await Resolver(StoreWith(item), src, Opts()).ResolveTclAsync(item.CatalogId));
            Assert.Equal(0, src.Calls);
        }

        [Fact]
        public async Task Conteudo_que_nao_e_xml_cifra_e_descartado()
        {
            var item = Item(system: SourceSystem.ConnectUs);
            var src = new StubSource(SourceSystem.ConnectUs, new("U2FsdGVkX1+cifra/base64==", "tcl"));
            Assert.Null(await Resolver(StoreWith(item), src, Opts(SourceSystem.ConnectUs)).ResolveTclAsync(item.CatalogId));
            Assert.Equal(1, src.Calls);
        }

        [Fact]
        public async Task Origem_indisponivel_ou_item_inexistente_ou_store_fora_degradam_sem_lancar()
        {
            var item = Item();
            Assert.Null(await Resolver(StoreWith(item), new StubSource(SourceSystem.Own, null, throws: true), Opts(SourceSystem.Own)).ResolveTclAsync(item.CatalogId));
            Assert.Null(await Resolver(StoreWith(item), new StubSource(SourceSystem.Own, null), Opts(SourceSystem.Own)).ResolveTclAsync(item.CatalogId));
            Assert.Null(await Resolver(new FakeMappingCatalogStore(), new StubSource(SourceSystem.Own, new(Tcl, "tcl")), Opts(SourceSystem.Own)).ResolveTclAsync(Guid.NewGuid()));
            var down = StoreWith(item); down.Down = true;
            Assert.Null(await Resolver(down, new StubSource(SourceSystem.Own, new(Tcl, "tcl")), Opts(SourceSystem.Own)).ResolveTclAsync(item.CatalogId));
            Assert.Null(await Resolver(StoreWith(item), new StubSource(SourceSystem.Own, new(Tcl, "tcl")), Opts(SourceSystem.Own)).ResolveTclAsync(Guid.Empty));
        }

        [Fact]
        public async Task Item_retirado_continua_resolvivel_e_sinalizado()
        {
            var item = Item(retired: true);
            var r = await Resolver(StoreWith(item), new StubSource(SourceSystem.Own, new(Tcl, "tcl")), Opts(SourceSystem.Own)).ResolveTclAsync(item.CatalogId);
            Assert.True(r!.Retired);
        }

        // ---- Pipeline ponta a ponta (Neogrid real sobre corpus em disco) ----
        private TransformationPipelineService Pipeline(ICatalogTclResolver? resolver) => new(
            NullLogger<TransformationPipelineService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TransformationPipeline:TclPath"] = _tclDir, ["TransformationPipeline:XslPath"] = _xslDir }).Build(),
            null, resolver);

        private void WriteXsl() => File.WriteAllText(Path.Combine(_xslDir, $"MAP_X_{Layout}.xsl"),
            "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"><xsl:output method=\"xml\"/><xsl:template match=\"/\"><r/></xsl:template></xsl:stylesheet>");

        private async Task<(CatalogTclResolver Resolver, Guid TclId)> NeogridCatalogAsync(bool enabled = true)
        {
            var path = Path.Combine(_corpus, "tcl", "nfe", "4.00", "Mapa.tcl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Tcl);
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ReferenceExamples:BasePath"] = _corpus }).Build();
            var source = new NeogridCatalogSource(new ReferenceExampleCatalogService(cfg, NullLogger<ReferenceExampleCatalogService>.Instance), NullLogger<NeogridCatalogSource>.Instance);
            var snap = await source.ReadAsync(CancellationToken.None);
            var store = new FakeMappingCatalogStore();
            foreach (var i in snap.Items) store.Items[i.CatalogId] = i;
            var opts = enabled ? Opts(SourceSystem.Neogrid) : Opts();
            return (new CatalogTclResolver(store, [source], opts, NullLogger<CatalogTclResolver>.Instance), snap.Items.Single().CatalogId);
        }

        [Fact]
        public async Task Parse_ponta_a_ponta_sem_TCL_em_disco_usa_o_catalogo_por_catalogId()
        {
            var (resolver, id) = await NeogridCatalogAsync();
            WriteXsl();
            var res = await Pipeline(resolver).TransformTxtToXmlAsync("HEADER01", Layout, "NFe", id);

            Assert.True(res.Success, string.Join(";", res.Errors));
            Assert.Contains("<data>HEADER01</data>", res.StepResults["IntermediateXml"]);
            Assert.Contains(res.Warnings, w => w.Contains("TCL obtido do catálogo") && w.Contains(id.ToString()));
            Assert.Null(res.ErrorCode);
            Assert.Null(res.TclPath); // não veio de arquivo em disco
        }

        [Fact]
        public async Task Disco_continua_primeira_fonte_catalogo_nao_e_consultado()
        {
            File.WriteAllText(Path.Combine(_tclDir, $"{Layout}.tcl"), Tcl);
            var spy = new SpyResolver();
            WriteXsl();
            var res = await Pipeline(spy).TransformTxtToXmlAsync("20260812", Layout, "NFe", Guid.NewGuid());
            Assert.True(res.Success, string.Join(";", res.Errors));
            Assert.Equal(0, spy.Calls);
        }

        [Fact]
        public async Task Sem_catalogId_ou_catalogo_sem_resultado_mantem_map_not_found_exato()
        {
            var (resolver, id) = await NeogridCatalogAsync();
            var noId = await Pipeline(resolver).TransformTxtToXmlAsync("x", Layout);
            Assert.Equal("map_not_found", noId.ErrorCode);
            Assert.Equal($"Arquivo MAP não encontrado para layout: {Layout}", Assert.Single(noId.Errors));

            var (off, offId) = await NeogridCatalogAsync(enabled: false);
            var disabled = await Pipeline(off).TransformTxtToXmlAsync("x", Layout, "NFe", offId);
            Assert.Equal("map_not_found", disabled.ErrorCode);

            var unknown = await Pipeline(resolver).TransformTxtToXmlAsync("x", Layout, "NFe", Guid.NewGuid());
            Assert.Equal("map_not_found", unknown.ErrorCode);
        }

        [Fact]
        public async Task Resolver_que_lanca_degrada_para_map_not_found()
        {
            var res = await Pipeline(new SpyResolver { Throw = true }).TransformTxtToXmlAsync("x", Layout, "NFe", Guid.NewGuid());
            Assert.False(res.Success);
            Assert.Equal("map_not_found", res.ErrorCode);
        }

        private sealed class SpyResolver : ICatalogTclResolver
        {
            public int Calls; public bool Throw;
            public Task<CatalogTclResult?> ResolveTclAsync(Guid catalogId, CancellationToken ct = default)
            {
                Calls++;
                if (Throw) throw new InvalidOperationException("boom");
                return Task.FromResult<CatalogTclResult?>(null);
            }
        }
    }
}
