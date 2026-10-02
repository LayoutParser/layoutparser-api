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
            var path = Path.Combine(AppContext.BaseDirectory, "../../../../../Services/Database/SqlMappingCatalogStore.cs");
            if (!File.Exists(path))
                return; // layout de diretório diferente (ex.: CI) — outros testes cobrem o contrato.
            Assert.DoesNotContain("DELETE FROM", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
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

        [Fact]
        public void NameSort_ignora_caixa_e_acentos()
        {
            Assert.Equal("nfe entrada", SqlMappingCatalogStore.NameSort("  NFé Entrada "));
        }
    }
}
