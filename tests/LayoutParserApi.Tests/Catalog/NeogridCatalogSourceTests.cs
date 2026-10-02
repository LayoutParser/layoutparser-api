using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Fiscal;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Catalog
{
    public class NeogridCatalogSourceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lp-neogrid-" + Guid.NewGuid().ToString("N"));

        private void Write(string rel, string body)
        {
            var path = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        private NeogridCatalogSource NewSource(string? basePath = null)
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferenceExamples:BasePath"] = basePath ?? _root,
            }).Build();
            var svc = new ReferenceExampleCatalogService(cfg, NullLogger<ReferenceExampleCatalogService>.Instance);
            return new NeogridCatalogSource(svc, NullLogger<NeogridCatalogSource>.Instance);
        }

        [Fact]
        public async Task Par_tcl_xsl_vira_dois_itens_ligados_e_pastas_vem_do_corpus()
        {
            Write("tcl/nfe/4.00/EnvioNFe_NeoGridToSefaz.tcl", "TCL-A");
            Write("xsl/nfe/4.00/EnvioNFe_NeoGridToSefaz.xsl", "XSL-A");
            Write("tcl/cte/3.00/Cte_X.tcl", "TCL-B");

            var snap = await NewSource().ReadAsync(CancellationToken.None);

            Assert.True(snap.Complete);
            Assert.Equal(new[] { "cte", "nfe" }, snap.Folders.Select(f => f.SourceProjectKey).OrderBy(x => x));
            Assert.Equal(3, snap.Items.Count);
            var tcl = snap.Items.Single(i => i.Engine == "tcl" && i.DocType == "nfe");
            var xsl = snap.Items.Single(i => i.Engine == "xsl");
            Assert.NotEqual(tcl.CatalogId, xsl.CatalogId);
            Assert.Equal(xsl.CatalogId, tcl.PairedCatalogId);
            Assert.Equal(tcl.CatalogId, xsl.PairedCatalogId);
            Assert.Null(snap.Items.Single(i => i.DocType == "cte").PairedCatalogId);
            Assert.All(snap.Items, i => Assert.Equal(64, i.ContentHash!.Length));
            Assert.Equal(CatalogIdGenerator.ForFolder(SourceSystem.Neogrid, "nfe"), tcl.FolderId);
        }

        [Fact]
        public async Task CatalogId_e_estavel_entre_leituras()
        {
            Write("tcl/nfe/4.00/A.tcl", "x");
            var a = await NewSource().ReadAsync(CancellationToken.None);
            var b = await NewSource().ReadAsync(CancellationToken.None);
            Assert.Equal(a.Items.Select(i => i.CatalogId), b.Items.Select(i => i.CatalogId));
        }

        [Fact]
        public async Task Conteudo_resolve_pelo_ponteiro_e_distingue_tcl_de_xsl()
        {
            Write("tcl/nfe/4.00/A.tcl", "TCL-BODY");
            Write("xsl/nfe/4.00/A.xsl", "XSL-BODY");
            var source = NewSource();
            var snap = await source.ReadAsync(CancellationToken.None);
            var tcl = snap.Items.Single(i => i.Engine == "tcl");
            var xsl = snap.Items.Single(i => i.Engine == "xsl");

            var ct = await source.GetContentAsync(new MappingCatalogSourceRef(tcl.SourceItemKey, tcl.SourceRefJson), CancellationToken.None);
            var cx = await source.GetContentAsync(new MappingCatalogSourceRef(xsl.SourceItemKey, xsl.SourceRefJson), CancellationToken.None);
            Assert.Equal("TCL-BODY", ct!.Content);
            Assert.Equal("XSL-BODY", cx!.Content);
            Assert.Null(await source.GetContentAsync(new MappingCatalogSourceRef("k", null), CancellationToken.None));
        }

        [Fact]
        public async Task Corpus_ausente_nao_e_completo_para_nao_retirar_tudo()
        {
            var snap = await NewSource(Path.Combine(_root, "nao-existe")).ReadAsync(CancellationToken.None);
            Assert.False(snap.Complete);
            Assert.Empty(snap.Items);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
