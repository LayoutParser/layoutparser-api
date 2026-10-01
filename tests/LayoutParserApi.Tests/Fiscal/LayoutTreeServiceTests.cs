using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Fiscal
{
    /// <summary>
    /// Issue #425 — <see cref="LayoutTreeService"/>. Cobre a montagem do contrato de
    /// <c>GET .../mappings/{mappingId}/layout-tree</c>: árvore de origem/destino resolvida por
    /// GUID (via <see cref="ICachedLayoutService"/>), regras extraídas do MapperVO real
    /// (<see cref="ICachedMapperService"/>), mapper não encontrado → <c>null</c> (controller traduz
    /// pra 404), e degradação graciosa de layout ausente/ilegível (nunca derruba o request inteiro).
    /// </summary>
    public sealed class LayoutTreeServiceTests
    {
        private sealed class FakeCachedMapperService : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();

            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        private sealed class FakeCachedLayoutService : ICachedLayoutService
        {
            public Dictionary<string, LayoutRecord> LayoutsByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);

            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request) => throw new NotSupportedException();
            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => throw new NotSupportedException();
            public Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid)
                => Task.FromResult(LayoutsByGuid.TryGetValue(layoutGuid, out var l) ? l : null);
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
            public Task ClearCacheAsync() => Task.CompletedTask;
            public ILayoutDatabaseService GetLayoutDatabaseService() => throw new NotSupportedException();
        }

        // Mesma forma de LayoutVO real usada pelo ADR (issue #425): grupo com filho e cardinalidade.
        private const string SourceLayoutXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Layout xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="TextLayoutVO">
              <LayoutGuid>LAY_SOURCE</LayoutGuid>
              <Elements>
                <Element xsi:type="GroupTagElementVO">
                  <ElementGuid>LIN_LINHA000</ElementGuid>
                  <Name>LINHA000</Name>
                  <MinimalOccurrence>1</MinimalOccurrence>
                  <MaximumOccurrence>1</MaximumOccurrence>
                  <Elements>
                    <Element xsi:type="TagElementVO">
                      <ElementGuid>FLD_ChaveAcesso</ElementGuid>
                      <Name>ChaveAcesso</Name>
                      <MinimalOccurrence>1</MinimalOccurrence>
                      <MaximumOccurrence>1</MaximumOccurrence>
                    </Element>
                  </Elements>
                </Element>
              </Elements>
            </Layout>
            """;

        private const string TargetLayoutXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Layout xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="XmlLayoutVO">
              <LayoutGuid>LAY_TARGET</LayoutGuid>
              <Elements>
                <Element xsi:type="GroupTagElementVO">
                  <ElementGuid>TAG_infNFe</ElementGuid>
                  <Name>infNFe</Name>
                  <MinimalOccurrence>1</MinimalOccurrence>
                  <MaximumOccurrence>1</MaximumOccurrence>
                  <Elements>
                    <Element xsi:type="AttributeElementVO">
                      <ElementGuid>ATT_Id</ElementGuid>
                      <Name>Id</Name>
                    </Element>
                  </Elements>
                </Element>
              </Elements>
            </Layout>
            """;

        private static string BuildMapperXml(string mapperGuid) => $"""
            <MapperVO>
                <MapperGuid>{mapperGuid}</MapperGuid>
                <Name>Mapper de teste</Name>
                <InputLayoutGuid>LAY_SOURCE</InputLayoutGuid>
                <TargetLayoutGuid>LAY_TARGET</TargetLayoutGuid>
                <LinkMappings>
                    <LinkMappingItem>
                        <Name>Id</Name>
                        <Sequence>1</Sequence>
                        <ElementGuid>LNK_1</ElementGuid>
                        <InputLayoutGuid>FLD_ChaveAcesso</InputLayoutGuid>
                        <TargetLayoutGuid>ATT_Id</TargetLayoutGuid>
                    </LinkMappingItem>
                </LinkMappings>
            </MapperVO>
            """;

        private static (LayoutTreeService Service, FakeCachedMapperService Mappers, FakeCachedLayoutService Layouts) BuildService()
        {
            var mappers = new FakeCachedMapperService();
            var layouts = new FakeCachedLayoutService();
            var service = new LayoutTreeService(mappers, layouts, NullLogger<LayoutTreeService>.Instance);
            return (service, mappers, layouts);
        }

        [Fact]
        public async Task Mapper_inexistente_retorna_null()
        {
            var (service, _, _) = BuildService();

            var result = await service.GetLayoutTreeAsync("MAP_NAO_EXISTE", CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task Mapper_real_monta_arvores_e_regras_pelos_guids()
        {
            var (service, mappers, layouts) = BuildService();
            mappers.Mappers.Add(new Mapper
            {
                MapperGuid = "MAP_1",
                InputLayoutGuid = "LAY_SOURCE",
                TargetLayoutGuid = "LAY_TARGET",
                DecryptedContent = BuildMapperXml("MAP_1"),
            });
            layouts.LayoutsByGuid["LAY_SOURCE"] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Origem", DecryptedContent = SourceLayoutXml };
            layouts.LayoutsByGuid["LAY_TARGET"] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Destino", DecryptedContent = TargetLayoutXml };

            var result = await service.GetLayoutTreeAsync("MAP_1", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("MAP_1", result!.MapperGuid);

            var sourceRoot = Assert.Single(result.Source.Roots);
            Assert.Equal("LIN_LINHA000", sourceRoot.ElementGuid);
            Assert.Equal("group", sourceRoot.Kind);
            var sourceLeaf = Assert.Single(sourceRoot.Children);
            Assert.Equal("FLD_ChaveAcesso", sourceLeaf.ElementGuid);
            Assert.Equal(1, sourceLeaf.Cardinality!.Min);
            Assert.Equal(1, sourceLeaf.Cardinality.Max);

            var targetRoot = Assert.Single(result.Target.Roots);
            var targetAttr = Assert.Single(targetRoot.Children);
            Assert.Equal("attribute", targetAttr.Kind);
            Assert.Null(targetAttr.Cardinality);

            var rule = Assert.Single(result.Rules);
            Assert.Equal("LNK_1", rule.RuleId);
            Assert.Equal("FLD_ChaveAcesso", rule.SourceElementGuid);
            Assert.Equal("ATT_Id", rule.TargetElementGuid);
            Assert.Empty(result.Limitations); // sem regra DSL no mapper, sem limitação a sinalizar
        }

        // Mesmo mapper do teste principal, mas com uma <Rule> (DSL) além do LinkMapping.
        private static string BuildMapperXmlComRegraDsl(string mapperGuid) => $"""
            <MapperVO>
                <MapperGuid>{mapperGuid}</MapperGuid>
                <Name>Mapper de teste</Name>
                <InputLayoutGuid>LAY_SOURCE</InputLayoutGuid>
                <TargetLayoutGuid>LAY_TARGET</TargetLayoutGuid>
                <LinkMappings>
                    <LinkMappingItem>
                        <Name>Id</Name>
                        <Sequence>1</Sequence>
                        <ElementGuid>LNK_1</ElementGuid>
                        <InputLayoutGuid>FLD_ChaveAcesso</InputLayoutGuid>
                        <TargetLayoutGuid>ATT_Id</TargetLayoutGuid>
                    </LinkMappingItem>
                </LinkMappings>
                <Rule>
                    <Name>RegraCondicional</Name>
                    <Sequence>2</Sequence>
                    <ElementGuid>ATT_Cond</ElementGuid>
                    <TargetElementGuid>ATT_Cond</TargetElementGuid>
                    <ContentValue>%beginRuleContent;T.xCond=I.LINHA1/Campo;%endRuleContent;</ContentValue>
                </Rule>
            </MapperVO>
            """;

        [Fact]
        public async Task Mapper_com_regra_DSL_nao_entra_em_Rules_mas_sinaliza_em_Limitations()
        {
            var (service, mappers, layouts) = BuildService();
            mappers.Mappers.Add(new Mapper
            {
                MapperGuid = "MAP_DSL",
                InputLayoutGuid = "LAY_SOURCE",
                TargetLayoutGuid = "LAY_TARGET",
                DecryptedContent = BuildMapperXmlComRegraDsl("MAP_DSL"),
            });
            layouts.LayoutsByGuid["LAY_SOURCE"] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Origem", DecryptedContent = SourceLayoutXml };
            layouts.LayoutsByGuid["LAY_TARGET"] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Destino", DecryptedContent = TargetLayoutXml };

            var result = await service.GetLayoutTreeAsync("MAP_DSL", CancellationToken.None);

            Assert.NotNull(result);

            // Só o LinkMapping direto entra em Rules — a regra DSL (ATT_Cond) não aparece.
            var rule = Assert.Single(result!.Rules);
            Assert.Equal("LNK_1", rule.RuleId);
            Assert.DoesNotContain(result.Rules, r => r.RuleId.Contains("ATT_Cond"));

            // A ausência é sinalizada, não silenciosa.
            Assert.NotEmpty(result.Limitations);
        }

        [Fact]
        public async Task Layout_de_um_dos_lados_ausente_degrada_para_arvore_vazia_sem_derrubar_o_outro_lado()
        {
            var (service, mappers, layouts) = BuildService();
            mappers.Mappers.Add(new Mapper
            {
                MapperGuid = "MAP_2",
                InputLayoutGuid = "LAY_SOURCE",
                TargetLayoutGuid = "LAY_NAO_CADASTRADO",
                DecryptedContent = BuildMapperXml("MAP_2"),
            });
            layouts.LayoutsByGuid["LAY_SOURCE"] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Origem", DecryptedContent = SourceLayoutXml };
            // Propositalmente NÃO registra LAY_NAO_CADASTRADO.

            var result = await service.GetLayoutTreeAsync("MAP_2", CancellationToken.None);

            Assert.NotNull(result);
            Assert.NotEmpty(result!.Source.Roots);
            Assert.Empty(result.Target.Roots);
        }

        private static async Task<LayoutTreeSide> TargetComAsync(string? targetGuid, string? targetXml)
        {
            var (service, mappers, layouts) = BuildService();
            mappers.Mappers.Add(new Mapper
            {
                MapperGuid = "MAP_R",
                InputLayoutGuid = "LAY_SOURCE",
                TargetLayoutGuid = targetGuid,
                DecryptedContent = BuildMapperXml("MAP_R"),
            });
            layouts.LayoutsByGuid["LAY_SOURCE"] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Origem", DecryptedContent = SourceLayoutXml };
            if (targetGuid != null && targetXml != null)
                layouts.LayoutsByGuid[targetGuid] = new LayoutRecord { LayoutGuid = Guid.Empty, Name = "Destino", DecryptedContent = targetXml };
            var result = await service.GetLayoutTreeAsync("MAP_R", CancellationToken.None);
            return result!.Target;
        }

        [Fact]
        public async Task Caso_feliz_nao_tem_UnavailableReason()
        {
            var t = await TargetComAsync("LAY_TARGET", TargetLayoutXml);
            Assert.Null(t.UnavailableReason);
            Assert.Equal("xml", t.Kind);
        }

        [Fact]
        public async Task Layout_nao_encontrado_ou_guid_ausente_gera_layout_not_found()
        {
            Assert.Equal("layout-not-found", (await TargetComAsync("LAY_X", null)).UnavailableReason);
            Assert.Equal("layout-not-found", (await TargetComAsync(null, null)).UnavailableReason);
        }

        [Fact]
        public async Task Xml_ilegivel_gera_layout_unreadable()
        {
            var t = await TargetComAsync("LAY_BAD", "<Layout><nao fechado");
            Assert.Equal("layout-unreadable", t.UnavailableReason);
            Assert.Equal("unknown", t.Kind);
        }

        [Fact]
        public async Task Tipo_sem_leitor_gera_unsupported_kind()
        {
            var t = await TargetComAsync("LAY_J", "<Layout xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"JsonLayoutVO\"><LayoutGuid>LAY_J</LayoutGuid></Layout>");
            Assert.Equal("unsupported-kind", t.UnavailableReason);
        }

        [Fact]
        public async Task Xml_sem_nos_materializaveis_gera_xsd_unresolved()
        {
            var t = await TargetComAsync("LAY_XSD", "<Layout xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"XmlLayoutVO\"><LayoutGuid>LAY_XSD</LayoutGuid></Layout>");
            Assert.Equal("xml", t.Kind);
            Assert.Empty(t.Roots);
            Assert.Equal("xsd-unresolved", t.UnavailableReason);
        }
    }
}
