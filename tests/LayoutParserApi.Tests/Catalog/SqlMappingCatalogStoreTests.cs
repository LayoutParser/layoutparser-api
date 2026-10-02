using System.Reflection;

using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Database;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Catalog
{
    public class SqlMappingCatalogStoreTests
    {
        private static SqlMappingCatalogStore NewStore(string identityHost = "identity-teste.invalid")
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Server"] = "host-legado-readonly.invalid",
                ["IdentityDatabase:Server"] = identityHost,
                ["IdentityDatabase:Database"] = "LayoutParserIdentity",
                ["IdentityDatabase:UserId"] = "u",
                ["IdentityDatabase:Password"] = "p",
            }).Build();
            return new SqlMappingCatalogStore(NullLogger<SqlMappingCatalogStore>.Instance, cfg);
        }

        [Fact]
        public void Usa_IdentityDatabase_nunca_Database()
        {
            var cs = (string)typeof(SqlMappingCatalogStore)
                .GetField("_connectionString", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(NewStore())!;
            Assert.Contains("identity-teste.invalid", cs);
            Assert.DoesNotContain("legado-readonly", cs);
        }

        [Fact]
        public void Ddl_so_cria_as_tres_tabelas_sem_corpo_e_sem_delete()
        {
            var ddl = SqlMappingCatalogStore.SchemaDdl;
            Assert.Contains("tbMappingCatalogSource", ddl);
            Assert.Contains("tbMappingCatalogFolder", ddl);
            Assert.Contains("tbMappingCatalogItem", ddl);
            Assert.DoesNotContain("Content ", ddl);
            Assert.DoesNotContain("DELETE", ddl, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Fonte_do_store_nunca_usa_DELETE()
        {
            // Sobe a árvore até achar o arquivo-fonte; se não achar, FALHA (não passa em silêncio).
            string? path = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Services", "Database", "SqlMappingCatalogStore.cs");
                if (File.Exists(candidate))
                    path = candidate;
            }
            Assert.True(path != null, "SqlMappingCatalogStore.cs não encontrado a partir de AppContext.BaseDirectory.");
            Assert.DoesNotContain("DELETE FROM", File.ReadAllText(path!), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Banco_indisponivel_degrada_sem_lancar()
        {
            var store = NewStore();
            var folderId = CatalogIdGenerator.ForFolder(SourceSystem.Own, null);
            var catalogId = CatalogIdGenerator.ForItem(SourceSystem.Own, null, "m1");
            var item = new MappingCatalogItemDto(catalogId, folderId, SourceSystem.Own, "m1", MappingCatalogEngine.Tcl,
                "M1", null, null, null, null, false);

            Assert.False(await store.UpsertItemAsync(item, CancellationToken.None));
            Assert.False(await store.UpsertFolderAsync(new MappingCatalogFolderDto(folderId, SourceSystem.Own, "-", null, "Sem projeto", false), CancellationToken.None));
            Assert.False(await store.RetireItemAsync(catalogId, CancellationToken.None));
            Assert.Equal(0, await store.RetireUnseenAsync(SourceSystem.Own, DateTime.UtcNow, CancellationToken.None));
            Assert.Null(await store.GetItemAsync(catalogId, CancellationToken.None));
        }

        private static MappingCatalogItemDto Item(string key, string name = "N", string? version = null, string? docType = null, string? hash = null)
            => new(CatalogIdGenerator.ForItem(SourceSystem.Own, null, "k"), CatalogIdGenerator.ForFolder(SourceSystem.Own, null),
                SourceSystem.Own, key, MappingCatalogEngine.Tcl, name, version, docType, hash, null, false);

        [Fact]
        public void Chave_de_item_acima_do_limite_e_rejeitada_nao_truncada()
        {
            Assert.NotNull(SqlMappingCatalogStore.PrepareItem(Item(new string('a', 400)), out var ok) );
            Assert.Null(ok);
            Assert.Null(SqlMappingCatalogStore.PrepareItem(Item(new string('a', 401)), out var motivo));
            Assert.Contains("SourceItemKey", motivo);
        }

        [Fact]
        public void Campos_descritivos_sao_truncados_e_chave_preservada()
        {
            var r = SqlMappingCatalogStore.PrepareItem(
                Item("chave", new string('n', 400), new string('v', 80), new string('d', 150), new string('h', 100)), out _)!;
            Assert.Equal(300, r.Name.Length);
            Assert.Equal(50, r.Version!.Length);
            Assert.Equal(100, r.DocType!.Length);
            Assert.Equal(64, r.ContentHash!.Length);
            Assert.Equal("chave", r.SourceItemKey);
        }

        [Fact]
        public async Task Upsert_com_chave_longa_retorna_false_sem_tocar_o_banco()
        {
            Assert.False(await NewStore().UpsertItemAsync(Item(new string('a', 401)), CancellationToken.None));
            var folder = new MappingCatalogFolderDto(CatalogIdGenerator.ForFolder(SourceSystem.Own, "p"), SourceSystem.Own, new string('p', 201), null, "P", false);
            Assert.False(await NewStore().UpsertFolderAsync(folder, CancellationToken.None));
        }

        [Fact]
        public async Task Cancelamento_propaga_em_GetItem_e_RetireUnseen()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewStore().GetItemAsync(Guid.NewGuid(), cts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewStore().RetireUnseenAsync(SourceSystem.Own, DateTime.UtcNow, cts.Token));
        }

        [Fact]
        public void NameSort_ignora_caixa_e_acentos()
        {
            Assert.Equal("nfe entrada", SqlMappingCatalogStore.NameSort("  NFé Entrada "));
        }
    }
}
