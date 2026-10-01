using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Sysmiddle;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Fiscal
{
    /// <summary>Catálogo de funções Sysmiddle + explicador de regras sem ramo T. (sem dado real de cliente).</summary>
    public class SysmiddleFunctionExplanationTests
    {
        private sealed class FakeCache : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();
            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string g) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string g) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        private static async Task<MappingExplanation> Explain(string dsl)
        {
            var cache = new FakeCache();
            cache.Mappers.Add(new Mapper
            {
                MapperGuid = "MAP_F", Name = "n", InputLayoutGuid = "I", TargetLayoutGuid = "T",
                DecryptedContent = $"<MapperVO><MapperGuid>MAP_F</MapperGuid><Rule><Name>R</Name><ElementGuid>E1</ElementGuid>" +
                                    $"<ContentValue>{System.Security.SecurityElement.Escape(dsl)}</ContentValue></Rule></MapperVO>",
            });
            var adapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);
            var e = await adapter.ExplainAsync(new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), "MAP_F", "current"), CancellationToken.None);
            return e!;
        }

        [Fact]
        public void Catalogo_SemDll_DegradaParaBuiltinsEFuncoesCuradas()
        {
            var cat = SysmiddleFunctionCatalog.Build("/caminho/inexistente.dll", null);
            Assert.Equal("builtin", cat.Lookup("IsNullOrEmpty")!.Origin);
            Assert.Equal("bool", cat.Lookup("isnullorempty")!.ReturnType);
            Assert.Equal("ndd-custom", cat.Lookup("FormaterDecimal")!.Origin);
            Assert.NotEmpty(cat.Lookup("Substring")!.Parameters);
            Assert.All(cat.GetAll(), f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
        }

        [Fact]
        public void Catalogo_ComDllReal_ExtraiFuncoesNdd_QuandoDllPresente()
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "LowCodeRunner", "Functions", "ndd.ConnectUs.Functions.dll");
            if (!File.Exists(dll)) return; // DLL é opcional — degradação coberta no teste acima
            var cat = SysmiddleFunctionCatalog.Build(Path.GetFullPath(dll), null);
            Assert.Contains(cat.GetAll(), f => f.Name == "GetConfigParametersValue" && f.Origin == "ndd-custom");
            Assert.True(cat.GetAll().Count(f => f.Origin == "ndd-custom") > 20, "esperava as ~80 classes de função da DLL");
            Assert.Contains(cat.GetAll(), f => f.Origin == "builtin");
        }

        [Fact]
        public void Endpoint_RetornaLista()
        {
            var ok = Assert.IsType<OkObjectResult>(new SysmiddleFunctionsController(SysmiddleFunctionCatalog.BuiltinOnly()).GetAll());
            var list = Assert.IsAssignableFrom<IReadOnlyList<SysmiddleFunctionInfo>>(ok.Value);
            Assert.Contains(list, f => f.Name == "Trim");
        }

        [Fact]
        public async Task SoTemps_ComIf_ViraBestEffort_NaoOpaca()
        {
            var e = await Explain("#.a = I.LINHA1/Campo; if (#.a = 'X') begin #.b = I.LINHA1/Outro; end else begin #.b = I.LINHA1/Campo; end");
            var r = Assert.Single(e.Rules);
            Assert.True(MappingExplanationSupportLevel.BestEffort == r.SupportLevel, r.SupportLevel + " | " + r.HumanDescription + " | " + string.Join(",", r.TargetRefs));
            Assert.Contains("#.a = I.LINHA1/Campo", r.HumanDescription);
            Assert.Contains("I.LINHA1/Campo", r.SourceRefs);
        }

        [Fact]
        public async Task FuncaoNdd_SemRamoT_FicaOpacaComFunctions()
        {
            var e = await Explain("#.v = FormaterDecimal(I.LINHA1/Valor, 2);");
            var r = Assert.Single(e.Rules);
            Assert.Equal(MappingExplanationSupportLevel.Opaque, r.SupportLevel);
            Assert.Contains("FormaterDecimal", r.Functions!);
            Assert.Contains("usa função NDD FormaterDecimal", r.HumanDescription);
        }

        [Fact]
        public async Task FuncaoNdd_ComRamoT_ExpoeFunctionsEDescricao()
        {
            var e = await Explain("T.vProd = RemoveZerosLeft(I.LINHA1/Valor);");
            var r = Assert.Single(e.Rules);
            Assert.Equal(MappingExplanationSupportLevel.Opaque, r.SupportLevel);
            Assert.Equal(new[] { "RemoveZerosLeft" }, r.Functions);
            Assert.Contains("Usa função NDD RemoveZerosLeft", r.HumanDescription);
        }

        [Fact]
        public async Task Builtin_Trim_NaoMaisOpaco()
        {
            var e = await Explain("T.xNome = Trim(I.LINHA1/Nome);");
            Assert.Equal(MappingExplanationSupportLevel.Authoritative, Assert.Single(e.Rules).SupportLevel);
        }

        [Fact]
        public async Task SemNadaReconhecivel_MantemTextoGenericoOpaco()
        {
            var e = await Explain("while (x) { }");
            var r = Assert.Single(e.Rules);
            Assert.Equal(MappingExplanationSupportLevel.Opaque, r.SupportLevel);
            Assert.Contains("fora da gramática DSL", r.HumanDescription);
        }
    }
}
