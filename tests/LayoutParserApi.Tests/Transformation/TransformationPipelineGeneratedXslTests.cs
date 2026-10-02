using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>Issue #642 (b): XSL do artefato gerado como fallback do disco.</summary>
    public class TransformationPipelineGeneratedXslTests
    {
        private const string Layout = "LAY_TESTE";
        private const string Xsl =
            "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">" +
            "<xsl:output method=\"xml\"/><xsl:template match=\"/\"><{0}/></xsl:template></xsl:stylesheet>";

        private sealed class FakeResolver : IGeneratedXslResolver
        {
            public string? Content; public bool Throw; public int Calls;
            public Task<string?> ResolveReadyXslAsync(string layoutName, CancellationToken ct = default)
            {
                Calls++;
                if (Throw) throw new InvalidOperationException("sql");
                return Task.FromResult(Content);
            }
        }

        private sealed class Dirs : IDisposable
        {
            public string Tcl = Directory.CreateTempSubdirectory("lp-tcl-").FullName;
            public string XslDir = Directory.CreateTempSubdirectory("lp-xsl-").FullName;
            public TransformationPipelineService Make(IGeneratedXslResolver? r) => new(
                NullLogger<TransformationPipelineService>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["TransformationPipeline:TclPath"] = Tcl, ["TransformationPipeline:XslPath"] = XslDir }).Build(), r);
            public void Dispose() { Directory.Delete(Tcl, true); Directory.Delete(XslDir, true); }
        }

        [Fact]
        public async Task XmlParaXml_DiscoAusente_ArtefatoReady_UsaArtefato()
        {
            using var d = new Dirs();
            var r = new FakeResolver { Content = string.Format(Xsl, "DoArtefato") };
            var res = await d.Make(r).TransformXmlToXmlAsync("<a/>", "NFe", "NFe", Layout);
            Assert.True(res.Success, string.Join(";", res.Errors));
            Assert.Contains("DoArtefato", res.TransformedXml);
        }

        [Theory]
        [InlineData("<xsl:template match=\"/\"><r><xsl:value-of select=\"document('file:///etc/hostname')\"/></r></xsl:template>")]
        [InlineData("<xsl:template match=\"/\"><r><xsl:value-of select=\"msxsl:foo()\"/></r></xsl:template><msxsl:script language=\"C#\" implements-prefix=\"u\">x</msxsl:script>")]
        public async Task XmlParaXml_ArtefatoComDocumentOuScript_Falha(string body)
        {
            using var d = new Dirs();
            var xsl = "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" xmlns:msxsl=\"urn:schemas-microsoft-com:xslt\" xmlns:u=\"urn:u\">" + body + "</xsl:stylesheet>";
            var res = await d.Make(new FakeResolver { Content = xsl }).TransformXmlToXmlAsync("<a/>", "NFe", "NFe", Layout);
            Assert.False(res.Success);
        }

        [Fact]
        public async Task XmlParaXml_ArtefatoComDtd_Falha()
        {
            using var d = new Dirs();
            var xsl = "<!DOCTYPE xsl:stylesheet [<!ENTITY x SYSTEM \"file:///etc/hostname\">]>" + string.Format(Xsl, "R");
            var res = await d.Make(new FakeResolver { Content = xsl }).TransformXmlToXmlAsync("<a/>", "NFe", "NFe", Layout);
            Assert.False(res.Success);
        }

        [Fact]
        public async Task XmlParaXml_DiscoPresente_PriorizaDisco()
        {
            using var d = new Dirs();
            File.WriteAllText(Path.Combine(d.XslDir, $"MAP_X_{Layout}.xsl"), string.Format(Xsl, "DoDisco"));
            var r = new FakeResolver { Content = string.Format(Xsl, "DoArtefato") };
            var res = await d.Make(r).TransformXmlToXmlAsync("<a/>", "NFe", "NFe", Layout);
            Assert.True(res.Success);
            Assert.Contains("DoDisco", res.TransformedXml);
            Assert.Equal(0, r.Calls);
        }

        [Fact]
        public async Task XmlParaXml_ArtefatoNaoReady_ErroExato()
        {
            using var d = new Dirs();
            var res = await d.Make(new FakeResolver { Content = null }).TransformXmlToXmlAsync("<a/>", "NFe", "NFe", Layout);
            Assert.False(res.Success);
            Assert.Equal("xsl_not_found", res.ErrorCode);
        }

        [Fact]
        public async Task XmlParaXml_ResolverFalha_Degrada()
        {
            using var d = new Dirs();
            var res = await d.Make(new FakeResolver { Throw = true }).TransformXmlToXmlAsync("<a/>", "NFe", "NFe", Layout);
            Assert.False(res.Success);
            Assert.Equal("xsl_not_found", res.ErrorCode);
        }

        [Fact]
        public async Task Txt_SoXslGerado_SemTcl_RetornaMapNotFound_SemInventarTcl()
        {
            using var d = new Dirs();
            var r = new FakeResolver { Content = string.Format(Xsl, "DoArtefato") };
            var res = await d.Make(r).TransformTxtToXmlAsync("linha", Layout);
            Assert.False(res.Success);
            Assert.Equal("map_not_found", res.ErrorCode);
        }

        [Fact]
        public async Task Txt_TclEmDisco_XslSoNoArtefato_Funciona()
        {
            using var d = new Dirs();
            File.WriteAllText(Path.Combine(d.Tcl, $"{Layout}.tcl"),
                "<MAP><LINE identifier=\"HEADER\" name=\"HEADER\"><FIELD name=\"data\" length=\"8\"/></LINE></MAP>");
            var r = new FakeResolver { Content = string.Format(Xsl, "DoArtefato") };
            var res = await d.Make(r).TransformTxtToXmlAsync("20260812", Layout);
            Assert.True(res.Success, string.Join(";", res.Errors));
            Assert.Contains("DoArtefato", res.TransformedXml);
        }

        // --- Resolver real (store fake) ---
        private sealed class Store : IGeneratedMapperArtifactStore
        {
            public GeneratedMapperArtifactRecord? Rec; public bool Throw;
            public Task<GeneratedMapperArtifactRecord?> GetAsync(string g, CancellationToken c)
                => Throw ? throw new InvalidOperationException("sql") : Task.FromResult(Rec);
            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? s, int a, int b, CancellationToken c) => throw new NotImplementedException();
            public Task<bool> TryBeginGeneratingAsync(string g, string c, CancellationToken t) => throw new NotImplementedException();
            public Task CompleteAsync(string a, string b, string c, string d, string e, string f, CancellationToken t) => throw new NotImplementedException();
            public Task FailAsync(string g, CancellationToken c) => throw new NotImplementedException();
        }

        private sealed class TestResolver : GeneratedXslResolver
        {
            public string? Guid = "g1";
            public TestResolver(IServiceScopeFactory f, LayoutParserApi.Services.Transformation.Ai.IMissingMapperGenerationTrigger? t = null) : base(f, NullLogger<GeneratedXslResolver>.Instance, t) { }
            public string? CurrentHash = "H1";
            protected override Task<string?> ResolveMapperGuidAsync(string layoutName) => Task.FromResult(Guid);
            protected override Task<string?> ResolveMapperContentAsync(string mapperGuid) => Task.FromResult<string?>("<x/>");
            protected override string? ComputeCurrentHash(string? c) => CurrentHash;
        }

        private static TestResolver MakeResolver(Store s, string? currentHash = "H1")
        {
            var sp = new ServiceCollection().AddSingleton<IGeneratedMapperArtifactStore>(s).BuildServiceProvider();
            return new TestResolver(sp.GetRequiredService<IServiceScopeFactory>()) { CurrentHash = currentHash };
        }

        private static GeneratedMapperArtifactRecord Rec(string status, string? content) =>
            new("g1", status, content, null, null, "H1", null, null, DateTimeOffset.UtcNow);

        [Fact]
        public async Task Resolver_Ready_DevolveContent()
            => Assert.Equal("<x/>", await MakeResolver(new Store { Rec = Rec("ready", "<x/>") }).ResolveReadyXslAsync(Layout));

        [Theory]
        [InlineData("generating")]
        [InlineData("stale")]
        [InlineData("failed")]
        public async Task Resolver_NaoReady_Null(string status)
            => Assert.Null(await MakeResolver(new Store { Rec = Rec(status, "<x/>") }).ResolveReadyXslAsync(Layout));

        [Fact]
        public async Task Resolver_ReadyMasHashDivergente_Stale_Recusa()
            => Assert.Null(await MakeResolver(new Store { Rec = Rec("ready", "<x/>") }, "OUTRO").ResolveReadyXslAsync(Layout));

        [Fact]
        public async Task Resolver_ReadyMasHashNaoCalculavel_Recusa()
            => Assert.Null(await MakeResolver(new Store { Rec = Rec("ready", "<x/>") }, null).ResolveReadyXslAsync(Layout));

        [Fact]
        public async Task Resolver_Stale_DisparaGatilho()
        {
            var sp = new ServiceCollection().AddSingleton<IGeneratedMapperArtifactStore>(new Store { Rec = Rec("ready", "<x/>") }).BuildServiceProvider();
            var trig = new CountingTrigger();
            var r = new TestResolver(sp.GetRequiredService<IServiceScopeFactory>(), trig) { CurrentHash = "OUTRO" };
            Assert.Null(await r.ResolveReadyXslAsync(Layout));
            Assert.Equal(1, trig.Count);
        }

        private sealed class CountingTrigger : LayoutParserApi.Services.Transformation.Ai.IMissingMapperGenerationTrigger
        {
            public int Count;
            public void TriggerForLayout(string? l, string? e) => Count++;
        }

        [Fact]
        public async Task Resolver_SemRegistro_Null()
            => Assert.Null(await MakeResolver(new Store()).ResolveReadyXslAsync(Layout));

        [Fact]
        public async Task Resolver_SqlFalha_NaoLanca_Null()
            => Assert.Null(await MakeResolver(new Store { Throw = true }).ResolveReadyXslAsync(Layout));
    }
}
