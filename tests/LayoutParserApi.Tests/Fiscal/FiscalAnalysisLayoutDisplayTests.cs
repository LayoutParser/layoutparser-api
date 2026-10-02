using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Tests.Fiscal
{
    /// <summary>Fallback de exibição do layoutName no histórico de análises (nunca nulo/vazio).</summary>
    public class FiscalAnalysisLayoutDisplayTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SemNomeESemGuid_DevolveLayoutNaoInformado(string? nome)
            => Assert.Equal("Layout não informado", FiscalAnalysisLayoutDisplay.Resolve(nome, null));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  \t ")]
        public void SemNomeComGuid_DevolveGuid(string? nome)
            => Assert.Equal("abc-123", FiscalAnalysisLayoutDisplay.Resolve(nome, "abc-123"));

        [Fact]
        public void ComNome_DevolveNome()
            => Assert.Equal("Layout X", FiscalAnalysisLayoutDisplay.Resolve("Layout X", "abc-123"));

        [Fact]
        public void GuidSoEspacos_CaiNoPadrao()
            => Assert.Equal("Layout não informado", FiscalAnalysisLayoutDisplay.Resolve(null, "  "));
    }
}
