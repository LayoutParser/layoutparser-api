using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Models.Entities.Fiscal;

using Xunit;

namespace LayoutParserApi.Tests.Models.Fiscal
{
    /// <summary>
    /// Contrato JSON das releases de mapeamento: campo nullable é SEMPRE serializado (valor
    /// <c>null</c>), mesmo com o <c>WhenWritingNull</c> global do <c>AddJsonOptions</c> (Program.cs).
    /// O front valida a presença da chave, então a ausência dela é quebra de contrato.
    /// </summary>
    public class MappingReleaseJsonContractTests
    {
        // Espelha o AddJsonOptions do Program.cs.
        private static readonly JsonSerializerOptions ApiOptions = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static JsonElement Serialize<T>(T value) => JsonDocument.Parse(JsonSerializer.Serialize(value, ApiOptions)).RootElement;

        private static void AssertNullPresent(JsonElement obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                Assert.True(obj.TryGetProperty(key, out var v), $"chave '{key}' ausente do JSON");
                Assert.Equal(JsonValueKind.Null, v.ValueKind);
            }
        }

        [Fact]
        public void MappingReleaseResponse_CamposNullable_SaemComoNull()
        {
            var json = Serialize(new MappingReleaseResponse());

            AssertNullPresent(json, "testRunSummary", "divergencesByRuleId", "derivedFromReleaseId",
                "manualEditReason", "manuallyEditedArtifactKinds", "fiscalProfile", "requiredCoverage");
        }

        [Fact]
        public void FiscalProfileResponse_ResolvedXsdNulo_SaiComoNull()
        {
            AssertNullPresent(Serialize(new FiscalProfileResponse()), "resolvedXsd");
        }

        [Fact]
        public void MappingReleaseGovernanceResponse_CamposNullable_SaemComoNull()
        {
            var json = Serialize(new MappingReleaseGovernanceResponse());

            AssertNullPresent(json, "approvedByUserId", "approvedAt", "approvalJustification",
                "publishedByUserId", "publishedAt", "previousPublishedReleaseId");
        }

        [Fact]
        public void MappingTestRunDivergence_CamposNullable_SaemComoNull()
        {
            var json = Serialize(new MappingTestRunDivergence("added", "/x", null, null, null, null, null));

            AssertNullPresent(json, "expected", "actual", "ruleId", "sourceRefs", "evidence");
        }

        [Fact]
        public void MappingTestRunSummary_XmlsNulos_SaemComoNull()
        {
            var json = Serialize(new MappingTestRunSummary(1, 0, 100, true, true,
                Array.Empty<string>(), Array.Empty<MappingTestRunDivergence>()));

            AssertNullPresent(json, "actualXml", "expectedXml");
        }

        [Fact]
        public void MappingTestRunDivergenceGroup_CamposNullable_SaemComoNull()
        {
            var json = Serialize(new MappingTestRunDivergenceGroup(Guid.NewGuid(), null, null,
                Array.Empty<MappingTestRunDivergence>()));

            AssertNullPresent(json, "sourceRefs", "evidence");
        }

        [Fact]
        public void MappingReleaseResponse_CamposNaoNulos_MantemNomeEFormato()
        {
            var id = Guid.NewGuid();
            var json = Serialize(new MappingReleaseResponse { ReleaseId = id, Engine = "xslt", RulesDesynced = true });

            Assert.Equal(id, json.GetProperty("releaseId").GetGuid());
            Assert.Equal("xslt", json.GetProperty("engine").GetString());
            Assert.True(json.GetProperty("rulesDesynced").GetBoolean());
            Assert.Equal(JsonValueKind.Array, json.GetProperty("artifacts").ValueKind);
        }

        [Fact]
        public void FiscalResolvedXsd_ExpoeXsdVersionNamespaceRootElement_SemXsdPath()
        {
            var json = Serialize(new FiscalProfileResponse
            {
                ResolvedXsd = new FiscalResolvedXsd("PL_009_V4", "http://www.portalfiscal.inf.br/nfe", "NFe"),
            }).GetProperty("resolvedXsd");

            Assert.Equal("PL_009_V4", json.GetProperty("xsdVersion").GetString());
            Assert.Equal("NFe", json.GetProperty("rootElement").GetString());
            Assert.False(json.TryGetProperty("xsdPath", out _));
        }
    }
}
