using System.Xml.Linq;

using LayoutParserApi.Services.Transformation.Ai;

using Xunit;

using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>Issue #642 — ressalvas do QA: classificação de regras, nome XML válido, raiz inválida e hash.</summary>
    public sealed class GeneratedMapperArtifactHardeningTests
    {
        private static RuleTranslation Tr(string name, TranslationSource src)
            => new(new MapperRule { Name = name }, "a/b", "<x/>", src);

        [Fact]
        public void ClassifyNonReproducibleRules_SeparaUntranslatedDeApproximate()
        {
            var r = GeneratedMapperArtifactService.ClassifyNonReproducibleRules(new[]
            {
                Tr("ok1", TranslationSource.DslInterpreter), Tr("ok2", TranslationSource.Ollama),
                Tr("u", TranslationSource.Untranslated), Tr("m", TranslationSource.MockFallback),
            });
            Assert.Equal(2, r.Count);
            Assert.Equal("untranslated", r.Single(x => x.Rule == "u").Reason);
            Assert.Equal("approximate", r.Single(x => x.Rule == "m").Reason);
        }

        [Theory]
        [InlineData("nfeProc", true)]
        [InlineData("_a-b.c", true)]
        [InlineData("dd30f390", true)]
        [InlineData("00d03083", false)]
        [InlineData("1abc", false)]
        [InlineData("a b", false)]
        [InlineData("a:b", false)]
        [InlineData("", false)]
        public void IsValidXmlName_Casos(string n, bool esperado)
            => Assert.Equal(esperado, GeneratedMapperArtifactService.IsValidXmlName(n));

        [Fact]
        public void RaizInvalida_DerrubariaCandidateBuilder_EPorIssoEDescartada()
        {
            // Regressão MAP_dd30f390/MAP_00d03083: raiz iniciada em dígito faz o builder lançar.
            Assert.False(GeneratedMapperArtifactService.IsValidXmlName("00d03083"));
            Assert.ThrowsAny<Exception>(() =>
                new CandidateBuilder().Build("00d03083", new List<XElement>(), new List<RuleTranslation>()));
            // Com raiz válida (fallback aplicado após o descarte) não lança.
            var (xslt, _) = new CandidateBuilder().Build("nfeProc", new List<XElement>(), new List<RuleTranslation>());
            Assert.NotNull(xslt);
        }

        [Fact]
        public void TryComputeMapperVoHash_InvalidoOuVazio_Null()
        {
            Assert.Null(GeneratedMapperArtifactService.TryComputeMapperVoHash(null));
            Assert.Null(GeneratedMapperArtifactService.TryComputeMapperVoHash("  "));
            Assert.Null(GeneratedMapperArtifactService.TryComputeMapperVoHash("<nao-fechado"));
        }
    }
}
