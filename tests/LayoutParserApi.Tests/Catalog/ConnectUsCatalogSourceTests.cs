using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Catalog
{
    public class ConnectUsCatalogSourceTests
    {
        private sealed class FakeReader : IConnectUsMapperReader
        {
            public List<ConnectUsProjectRow> Projects = new();
            public List<ConnectUsMapperRow> Mappers = new();
            public Dictionary<(string, int?), string> Contents = new();
            public bool Throw;
            public int ContentCalls;

            public Task<IReadOnlyList<ConnectUsProjectRow>> ListProjectsAsync(CancellationToken ct)
            {
                if (Throw) throw new InvalidOperationException("sql fora");
                return Task.FromResult<IReadOnlyList<ConnectUsProjectRow>>(Projects);
            }

            public Task<IReadOnlyList<ConnectUsMapperRow>> ListMappersAsync(int afterId, int take, CancellationToken ct)
            {
                if (Throw) throw new InvalidOperationException("sql fora");
                return Task.FromResult<IReadOnlyList<ConnectUsMapperRow>>(Mappers.Where(m => m.Id > afterId).OrderBy(m => m.Id).Take(take).ToList());
            }

            public Task<string?> GetMapperValueContentAsync(string g, int? p, CancellationToken ct)
            {
                ContentCalls++;
                return Task.FromResult(Contents.TryGetValue((g, p), out var c) ? c : null);
            }
        }

        private static ConnectUsMapperRow M(int id, string guid, int? project) => new(id, guid, project, "n" + id, new DateTime(2026, 1, 1));
        private static ConnectUsCatalogSource New(FakeReader r) => new(r, NullLogger<ConnectUsCatalogSource>.Instance);

        [Fact]
        public async Task Mesmo_MapperGuid_em_dois_projetos_gera_dois_catalogIds()
        {
            var r = new FakeReader();
            r.Projects.AddRange([new(1, "A"), new(2, "B")]);
            r.Mappers.AddRange([M(1, "same", 1), M(2, "same", 2), M(3, "same", null)]);

            var snap = await New(r).ReadAsync(CancellationToken.None);

            Assert.True(snap.Complete);
            Assert.Equal(3, snap.Items.Select(i => i.CatalogId).Distinct().Count());
            Assert.Equal(3, snap.Items.Select(i => i.SourceItemKey).Distinct().Count());
            Assert.All(snap.Items, i => Assert.Equal(MappingCatalogEngine.Tcl, i.Engine));
        }

        [Fact]
        public async Task Sem_projeto_cai_na_pasta_sintetica_e_projetos_viram_pastas()
        {
            var r = new FakeReader();
            r.Projects.Add(new(7, "Proj"));
            r.Mappers.AddRange([M(1, "x", null), M(2, "y", 7)]);

            var snap = await New(r).ReadAsync(CancellationToken.None);

            Assert.Equal(2, snap.Folders.Count);
            Assert.Contains(snap.Folders, f => f.SourceProjectKey == "-" && f.Name == "Sem projeto");
            Assert.Contains(snap.Folders, f => f.SourceProjectKey == "7" && f.SourceProjectId == 7 && f.Name == "Proj");
        }

        [Fact]
        public async Task Pagina_por_chave_alem_de_um_lote()
        {
            var r = new FakeReader();
            r.Projects.Add(new(1, "P"));
            for (var i = 1; i <= ConnectUsCatalogSource.PageSize * 2 + 10; i++) r.Mappers.Add(M(i, "g" + i, 1));

            var snap = await New(r).ReadAsync(CancellationToken.None);

            Assert.Equal(r.Mappers.Count, snap.Items.Count);
        }

        [Fact]
        public async Task Fonte_fora_propaga_excecao_sem_snapshot()
        {
            var r = new FakeReader { Throw = true };
            await Assert.ThrowsAsync<InvalidOperationException>(() => New(r).ReadAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ReadAsync_nao_le_conteudo_e_GetContent_usa_projeto_do_ponteiro()
        {
            var r = new FakeReader();
            r.Projects.AddRange([new(1, "A"), new(2, "B")]);
            r.Mappers.AddRange([M(1, "same", 1), M(2, "same", 2)]);
            r.Contents[("same", 1)] = "corpo-1";
            r.Contents[("same", 2)] = "corpo-2";
            var src = New(r);

            var snap = await src.ReadAsync(CancellationToken.None);
            Assert.Equal(0, r.ContentCalls);

            var item2 = snap.Items.Single(i => i.SourceItemKey.StartsWith("2/"));
            var withDecrypt = new ConnectUsCatalogSource(r, NullLogger<ConnectUsCatalogSource>.Instance, new FakeDecryption());
            var c = await withDecrypt.GetContentAsync(new MappingCatalogSourceRef(item2.SourceItemKey, item2.SourceRefJson), CancellationToken.None);
            Assert.Equal("corpo-2", c!.Content);
        }

        private sealed class FakeDecryption : LayoutParserApi.Services.Interfaces.IDecryptionService
        {
            public bool Fail;
            public bool IsDecryptorAvailable => !Fail;
            public Task<string> DecryptContentAsync(string encryptedContent)
                => Fail ? throw new InvalidOperationException("decryptor fora") : Task.FromResult(encryptedContent);
        }

        [Fact]
        public async Task Conteudo_nunca_devolve_cifra_bruta_sem_decryptor_ou_com_falha()
        {
            var r = new FakeReader();
            r.Projects.Add(new(2, "B"));
            r.Mappers.Add(M(1, "g", 2));
            r.Contents[("g", 2)] = "CIFRA-SENSIVEL";
            var snap = await New(r).ReadAsync(CancellationToken.None);
            var it = snap.Items.Single();
            var sref = new MappingCatalogSourceRef(it.SourceItemKey, it.SourceRefJson);

            // sem decryptor registrado
            Assert.Null(await New(r).GetContentAsync(sref, CancellationToken.None));
            // decryptor que falha
            var falho = new ConnectUsCatalogSource(r, NullLogger<ConnectUsCatalogSource>.Instance, new FakeDecryption { Fail = true });
            Assert.Null(await falho.GetContentAsync(sref, CancellationToken.None));
        }
    }
}
