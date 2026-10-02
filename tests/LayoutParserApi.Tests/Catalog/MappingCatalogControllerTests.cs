using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Catalog
{
    public class MappingCatalogControllerTests
    {
        private sealed class StubSource : IMappingCatalogSource
        {
            public SourceSystem System => SourceSystem.Neogrid;
            public Func<MappingContent?> Content = () => new MappingContent("<x/>", "tcl");
            public Task<MappingCatalogSnapshot> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef r, CancellationToken ct) => Task.FromResult(Content());
        }

        private sealed class StubTrigger : IMappingCatalogSyncTrigger
        {
            public CatalogSyncTriggerResult Result = CatalogSyncTriggerResult.Accepted;
            public CatalogSyncTriggerResult Trigger(SourceSystem? only) => Result;
        }

        private static (MappingCatalogController Ctl, FakeMappingCatalogStore Store, StubSource Src) Build(bool neogridEnabled = true)
        {
            var store = new FakeMappingCatalogStore();
            var src = new StubSource();
            var opts = new MappingCatalogOptions();
            opts.Sources["Neogrid"] = new MappingCatalogSourceToggle { Enabled = neogridEnabled };
            var svc = new MappingCatalogService(store, [src], new TestOptionsMonitor<MappingCatalogOptions>(opts), NullLogger<MappingCatalogService>.Instance);
            return (new MappingCatalogController(svc, new StubTrigger()), store, src);
        }

        private static MappingCatalogItemDto Item(string name, Guid folder, string engine = "tcl", bool retired = false)
            => new(CatalogIdGenerator.ForItem(SourceSystem.Neogrid, "nfe", name), folder, SourceSystem.Neogrid, name, engine, name, "4.00", "NFe", null, "{}", retired);

        private static Guid Seed(FakeMappingCatalogStore store, int items)
        {
            var folder = CatalogIdGenerator.ForFolder(SourceSystem.Neogrid, "nfe");
            store.Folders[folder] = new MappingCatalogFolderDto(folder, SourceSystem.Neogrid, "nfe", null, "NFe", false);
            store.Sources[SourceSystem.Neogrid] = new MappingCatalogSourceDto(SourceSystem.Neogrid, true, DateTime.UtcNow, "ok", null);
            for (var i = 0; i < items; i++) store.Items[Item("item" + i.ToString("D3"), folder).CatalogId] = Item("item" + i.ToString("D3"), folder);
            return folder;
        }

        [Fact]
        public async Task Items_pagina_em_ordem_estavel_sem_repeticao_e_clampa_pageSize()
        {
            var (ctl, store, _) = Build();
            var folder = Seed(store, 130);

            var seen = new List<Guid>();
            for (var page = 1; page <= 2; page++)
            {
                var ok = Assert.IsType<OkObjectResult>(await ctl.Items(folder, page, 100));
                var body = Assert.IsType<MappingCatalogListResponse<MappingCatalogItemView>>(ok.Value);
                Assert.Equal(130, body.TotalCount);
                Assert.Equal("ok", body.SourceStatus);
                seen.AddRange(body.Items.Select(i => i.CatalogId));
            }
            Assert.Equal(130, seen.Distinct().Count());

            var clamped = Assert.IsType<MappingCatalogListResponse<MappingCatalogItemView>>(
                Assert.IsType<OkObjectResult>(await ctl.Items(folder, 1, 9999)).Value);
            Assert.Equal(200, clamped.PageSize);
            Assert.StartsWith("/api/mapping-catalog/items/", clamped.Items[0].DetailUrl);
        }

        [Fact]
        public async Task Items_valida_engine_e_pasta_inexistente()
        {
            var (ctl, store, _) = Build();
            var folder = Seed(store, 1);
            Assert.IsType<BadRequestObjectResult>(await ctl.Items(folder, engine: "java"));
            Assert.IsType<NotFoundObjectResult>(await ctl.Items(Guid.NewGuid()));
        }

        [Fact]
        public async Task Folders_rejeita_sourceSystem_desconhecido_e_lista_snake_case()
        {
            var (ctl, store, _) = Build();
            Seed(store, 0);
            Assert.IsType<BadRequestObjectResult>(await ctl.Folders("xpto"));
            var ok = Assert.IsType<OkObjectResult>(await ctl.Folders("neogrid"));
            Assert.Single(Assert.IsType<MappingCatalogListResponse<MappingCatalogFolderDto>>(ok.Value).Items);
            Assert.Contains("\"neogrid\"", System.Text.Json.JsonSerializer.Serialize(((MappingCatalogListResponse<MappingCatalogFolderDto>)ok.Value!).Items[0]));
        }

        [Fact]
        public async Task Indice_fora_do_ar_responde_503_em_todas_as_leituras()
        {
            var (ctl, store, _) = Build();
            var folder = Seed(store, 1);
            var id = store.Items.Keys.First();
            store.Down = true;
            foreach (var r in new[] { await ctl.Sources(default), await ctl.Folders("neogrid"), await ctl.Items(folder), await ctl.GetItem(id), await ctl.GetContent(id) })
                Assert.Equal(503, Assert.IsType<ObjectResult>(r).StatusCode);
        }

        [Fact]
        public async Task Sources_flag_vem_da_config_e_origem_sem_linha_fica_unavailable()
        {
            var (ctl, store, _) = Build(neogridEnabled: false);
            var ok = Assert.IsType<OkObjectResult>(await ctl.Sources(default));
            var s = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MappingCatalogSourceSummary>>(ok.Value));
            Assert.False(s.Enabled);
            Assert.Equal("unavailable", s.Status);
        }

        [Fact]
        public void Sync_mapeia_aceito_desligado_e_origem_invalida()
        {
            var (ctl, _, _) = Build();
            Assert.IsType<AcceptedResult>(ctl.Sync("neogrid"));
            Assert.IsType<BadRequestObjectResult>(ctl.Sync("xpto"));
        }

        [Fact]
        public async Task Content_404_quando_origem_nao_tem_e_503_quando_origem_lanca()
        {
            var (ctl, store, src) = Build();
            Seed(store, 1);
            var id = store.Items.Keys.First();
            Assert.Equal("<x/>", Assert.IsType<MappingCatalogContentResponse>(Assert.IsType<OkObjectResult>(await ctl.GetContent(id)).Value).Content);
            src.Content = () => null;
            Assert.IsType<NotFoundObjectResult>(await ctl.GetContent(id));
            src.Content = () => throw new IOException("disco");
            Assert.Equal(503, Assert.IsType<ObjectResult>(await ctl.GetContent(id)).StatusCode);
            Assert.IsType<NotFoundObjectResult>(await ctl.GetContent(Guid.NewGuid()));
        }
    }

    internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
