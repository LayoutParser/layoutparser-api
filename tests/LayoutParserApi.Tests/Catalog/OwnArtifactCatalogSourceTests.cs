using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Catalog
{
    public class OwnArtifactCatalogSourceTests
    {
        private sealed class FakeStore : IGeneratedMapperArtifactStore
        {
            public List<GeneratedMapperArtifactRecord> Rows = new();
            public int ListCalls;
            public bool Throw;

            public Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken ct)
                => Task.FromResult(Rows.FirstOrDefault(r => r.MapperGuid == mapperGuid));

            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? status, int skip, int take, CancellationToken ct)
            {
                ListCalls++;
                if (Throw) throw new InvalidOperationException("sql fora");
                var all = Rows.Where(r => status == null || r.Status == status).ToList();
                return Task.FromResult(((IReadOnlyList<GeneratedMapperArtifactRecord>)all.Skip(skip).Take(take).ToList(), all.Count));
            }

            public Task<bool> TryBeginGeneratingAsync(string g, string c, CancellationToken ct) => throw new InvalidOperationException("escrita proibida");
            public Task CompleteAsync(string g, string c, string cov, string vb, string h, string cid, CancellationToken ct) => throw new InvalidOperationException("escrita proibida");
            public Task FailAsync(string g, CancellationToken ct) => throw new InvalidOperationException("escrita proibida");
        }

        private static GeneratedMapperArtifactRecord Row(string guid, string status = "ready", string? content = "<xsl/>")
            => new(guid, status, content, null, null, "h-" + guid, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private static OwnArtifactCatalogSource New(FakeStore s) => new(s, NullLogger<OwnArtifactCatalogSource>.Instance);

        [Fact]
        public async Task So_ready_entra_na_pasta_sem_projeto_e_pagina_alem_de_200()
        {
            var store = new FakeStore();
            for (var i = 0; i < 450; i++) store.Rows.Add(Row("guid-" + i));
            store.Rows.Add(Row("gerando", "generating", null));

            var snap = await New(store).ReadAsync(CancellationToken.None);

            Assert.True(snap.Complete);
            Assert.Equal(450, snap.Items.Count);
            Assert.Equal(450, snap.Items.Select(i => i.CatalogId).Distinct().Count());
            var folder = Assert.Single(snap.Folders);
            Assert.Equal("-", folder.SourceProjectKey);
            Assert.Equal("Sem projeto", folder.Name);
            Assert.All(snap.Items, i => { Assert.Equal("xslt", i.Engine); Assert.Equal(folder.FolderId, i.FolderId); });
        }

        [Fact]
        public async Task Falha_do_store_propaga_para_o_sync_nao_retirar_nada()
        {
            var store = new FakeStore { Throw = true };
            await Assert.ThrowsAsync<InvalidOperationException>(() => New(store).ReadAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Conteudo_vem_do_artefato_e_nunca_chama_metodos_de_escrita()
        {
            var store = new FakeStore();
            store.Rows.Add(Row("g1", content: "<xsl:stylesheet/>"));
            var src = New(store);
            var item = (await src.ReadAsync(CancellationToken.None)).Items.Single();

            var content = await src.GetContentAsync(new MappingCatalogSourceRef(item.SourceItemKey, item.SourceRefJson), CancellationToken.None);
            Assert.Equal("<xsl:stylesheet/>", content!.Content);
            Assert.Null(await src.GetContentAsync(new MappingCatalogSourceRef("inexistente", null), CancellationToken.None));
        }
    }
}
