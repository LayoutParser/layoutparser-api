#pragma warning disable CS0618 // a rota por mapperGuid é [Obsolete] de propósito (deprecated no Swagger, issue #639)
using System.Text.Json;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Tests.Catalog;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>Issue #634 — rota antiga por mapperGuid devolve 409 com candidatos quando o GUID é ambíguo no catálogo.</summary>
    public class GeneratedMapperArtifactControllerAmbiguityTests
    {
        private sealed class StubArtifactService : IGeneratedMapperArtifactService
        {
            public int Calls;
            public Task<GeneratedMapperArtifactResponse?> GetOrTriggerAsync(string mapperGuid, string correlationId, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult<GeneratedMapperArtifactResponse?>(
                    new GeneratedMapperArtifactResponse(mapperGuid, "ready", "<xsl/>", null, "declared_dsl", null, null));
            }
        }

        private sealed class ThrowingCatalog : IMappingCatalogService
        {
            public Task<CatalogResult<IReadOnlyList<MappingCatalogSourceSummary>>> ListSourcesAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<CatalogResult<MappingCatalogListResponse<MappingCatalogFolderDto>>> ListFoldersAsync(SourceSystem s, int p, int ps, CancellationToken ct) => throw new NotSupportedException();
            public Task<CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>> ListItemsAsync(Guid f, string? e, string? q, bool r, int p, int ps, CancellationToken ct) => throw new NotSupportedException();
            public Task<CatalogResult<MappingCatalogItemView>> GetItemAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<CatalogResult<MappingCatalogContentResponse>> GetContentAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<CatalogResult<IReadOnlyList<MappingCatalogItemView>>> FindByMapperGuidAsync(string g, CancellationToken ct) => throw new InvalidOperationException("catalogo fora");
        }

        private static (GeneratedMapperArtifactController Ctl, StubArtifactService Svc, FakeMappingCatalogStore Store) Build(bool throwingCatalog = false)
        {
            var store = new FakeMappingCatalogStore();
            var svc = new StubArtifactService();
            IMappingCatalogService catalog = throwingCatalog
                ? new ThrowingCatalog()
                : new MappingCatalogService(store, [], new TestOptionsMonitor<MappingCatalogOptions>(new MappingCatalogOptions()), NullLogger<MappingCatalogService>.Instance);
            var ctl = new GeneratedMapperArtifactController(svc, NullLogger<GeneratedMapperArtifactController>.Instance, catalog)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            return (ctl, svc, store);
        }

        private static void Seed(FakeMappingCatalogStore store, string mapperGuid, string projectId, SourceSystem system = SourceSystem.Own, bool retired = false)
        {
            var folder = CatalogIdGenerator.ForFolder(system, projectId);
            store.Folders[folder] = new MappingCatalogFolderDto(folder, system, projectId, long.Parse(projectId), "Projeto " + projectId, false);
            var id = CatalogIdGenerator.ForItem(system, projectId, mapperGuid);
            store.Items[id] = new MappingCatalogItemDto(id, folder, system, mapperGuid, "xslt", mapperGuid, null, null, null,
                JsonSerializer.Serialize(new { mapperGuid, projectId }), retired);
        }

        [Fact]
        public async Task Guid_ambiguo_devolve_409_com_candidatos_e_nao_chama_o_servico()
        {
            var (ctl, svc, store) = Build();
            Seed(store, "DUP", "1");
            Seed(store, "DUP", "2");
            Seed(store, "OUTRO", "1");

            var result = await ctl.GetGeneratedTransformation(Guid.NewGuid(), "dup", CancellationToken.None);

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
            var candidates = doc.RootElement.GetProperty("candidates").EnumerateArray().ToList();
            Assert.Equal(2, candidates.Count);
            Assert.Equal(
                new[] { CatalogIdGenerator.ForItem(SourceSystem.Own, "1", "DUP"), CatalogIdGenerator.ForItem(SourceSystem.Own, "2", "DUP") }.OrderBy(g => g),
                candidates.Select(c => c.GetProperty("catalogId").GetGuid()).OrderBy(g => g));
            Assert.All(candidates, c => Assert.StartsWith("/api/mapping-catalog/items/", c.GetProperty("detailUrl").GetString()));
            Assert.Equal("own", candidates[0].GetProperty("sourceSystem").GetString());
            Assert.Equal(0, svc.Calls); // nunca adivinha: não gera nem devolve nada de um dos projetos
        }

        [Fact]
        public async Task Guid_com_um_unico_item_ou_nenhum_segue_o_fluxo_antigo()
        {
            var (ctl, svc, store) = Build();
            Seed(store, "UNICO", "1");

            Assert.IsType<OkObjectResult>(await ctl.GetGeneratedTransformation(Guid.NewGuid(), "UNICO", CancellationToken.None));
            Assert.IsType<OkObjectResult>(await ctl.GetGeneratedTransformation(Guid.NewGuid(), "NAO-INDEXADO", CancellationToken.None));
            Assert.Equal(2, svc.Calls);
        }

        [Fact]
        public async Task Item_retirado_nao_conta_como_candidato()
        {
            var (ctl, svc, store) = Build();
            Seed(store, "DUP", "1");
            Seed(store, "DUP", "2", retired: true);

            Assert.IsType<OkObjectResult>(await ctl.GetGeneratedTransformation(Guid.NewGuid(), "DUP", CancellationToken.None));
            Assert.Equal(1, svc.Calls);
        }

        [Fact]
        public async Task Catalogo_fora_ou_com_falha_degrada_para_o_fluxo_antigo()
        {
            var (ctl, svc, store) = Build();
            Seed(store, "DUP", "1");
            Seed(store, "DUP", "2");
            store.Down = true;
            Assert.IsType<OkObjectResult>(await ctl.GetGeneratedTransformation(Guid.NewGuid(), "DUP", CancellationToken.None));

            var (ctl2, svc2, _) = Build(throwingCatalog: true);
            Assert.IsType<OkObjectResult>(await ctl2.GetGeneratedTransformation(Guid.NewGuid(), "DUP", CancellationToken.None));
            Assert.Equal(1, svc.Calls);
            Assert.Equal(1, svc2.Calls);
        }

        [Fact]
        public void Rotas_por_mapperGuid_e_reference_examples_estao_marcadas_deprecated_no_swagger()
        {
            // Swashbuckle (sem IgnoreObsoleteActions) publica [Obsolete] como "deprecated: true".
            static void AssertObsolete(Type controller, string action)
                => Assert.NotNull(Attribute.GetCustomAttribute(controller.GetMethod(action)!, typeof(ObsoleteAttribute)));
            AssertObsolete(typeof(GeneratedMapperArtifactController), nameof(GeneratedMapperArtifactController.GetGeneratedTransformation));
            AssertObsolete(typeof(ReferenceExamplesController), nameof(ReferenceExamplesController.List));
            AssertObsolete(typeof(ReferenceExamplesController), nameof(ReferenceExamplesController.GetContent));
            // O catálogo novo NÃO é deprecated.
            foreach (var m in typeof(MappingCatalogController).GetMethods().Where(m => m.DeclaringType == typeof(MappingCatalogController)))
                Assert.Null(Attribute.GetCustomAttribute(m, typeof(ObsoleteAttribute)));
        }
    }
}
