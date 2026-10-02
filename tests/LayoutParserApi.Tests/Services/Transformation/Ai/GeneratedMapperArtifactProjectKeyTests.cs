using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Catalog;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Issue #635 — chave (MapperGuid, ProjectId) em tbGeneratedMapperArtifact: DDL aditivo/idempotente,
    /// store project-aware, serviço propagando o ProjectId do mapper e adaptador Own sem colisão.
    /// </summary>
    public sealed class GeneratedMapperArtifactProjectKeyTests
    {
        // ── DDL ────────────────────────────────────────────────────────────────────────────
        [Fact]
        public void Ddl_e_aditivo_idempotente_e_nao_destrutivo()
        {
            var ddl = SqlGeneratedMapperArtifactStore.SchemaDdl;
            Assert.Contains("ProjectId NVARCHAR(32) NULL", ddl);
            Assert.Contains("ProjectKey AS (ISNULL(ProjectId, N''))", ddl);
            Assert.Contains("IF COL_LENGTH('dbo.tbGeneratedMapperArtifact', 'ProjectId') IS NULL", ddl);
            Assert.Contains("IF COL_LENGTH('dbo.tbGeneratedMapperArtifact', 'ProjectKey') IS NULL", ddl);
            Assert.Contains("PRIMARY KEY (MapperGuid, ProjectKey)", ddl);
            // Troca de PK só quando ainda é a antiga (1 coluna), sob applock e transação.
            Assert.Contains("sp_getapplock", ddl);
            Assert.Contains("= 1", ddl);
            // Nada de apagar dado nem a tabela.
            Assert.DoesNotContain("DROP TABLE", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRUNCATE", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE dbo", ddl, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Store_usa_somente_IdentityDatabase()
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Server"] = "host-legado-readonly.invalid",
                ["IdentityDatabase:Server"] = "identity-teste.invalid",
                ["IdentityDatabase:Database"] = "X",
                ["IdentityDatabase:UserId"] = "u",
                ["IdentityDatabase:Password"] = "p",
            }).Build();
            var store = new SqlGeneratedMapperArtifactStore(NullLogger<SqlGeneratedMapperArtifactStore>.Instance, cfg);
            var cs = (string)typeof(SqlGeneratedMapperArtifactStore)
                .GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
            Assert.Contains("identity-teste.invalid", cs);
            Assert.DoesNotContain("legado-readonly", cs);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(" 42 ", "42")]
        public void NormalizeProjectId_vazio_vira_legado(string? input, string? expected)
            => Assert.Equal(expected, SqlGeneratedMapperArtifactStore.NormalizeProjectId(input));

        [Fact]
        public void NormalizeProjectId_trunca_no_limite_da_coluna()
            => Assert.Equal(32, SqlGeneratedMapperArtifactStore.NormalizeProjectId(new string('9', 80))!.Length);

        // ── Fake project-aware ─────────────────────────────────────────────────────────────
        internal sealed class ProjectAwareStore : IGeneratedMapperArtifactStore
        {
            public readonly Dictionary<(string Guid, string Project), GeneratedMapperArtifactRecord> Rows = new();
            public readonly List<string?> BeginProjects = new();
            public readonly TaskCompletionSource<bool> Completed = new();
            private static string K(string? p) => p ?? string.Empty;

            public Task<GeneratedMapperArtifactRecord?> GetAsync(string g, CancellationToken ct) => GetAsync(g, null, ct);
            public Task<GeneratedMapperArtifactRecord?> GetAsync(string g, string? p, CancellationToken ct)
            {
                if (Rows.TryGetValue((g, K(p)), out var r)) return Task.FromResult<GeneratedMapperArtifactRecord?>(r);
                return Task.FromResult(p is not null && Rows.TryGetValue((g, ""), out var legacy) ? legacy : null);
            }
            public Task<IReadOnlyList<GeneratedMapperArtifactRecord>> ListByMapperGuidAsync(string g, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<GeneratedMapperArtifactRecord>>(Rows.Where(x => x.Key.Guid == g).Select(x => x.Value).ToList());
            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? status, int skip, int take, CancellationToken ct)
            {
                var all = Rows.Values.Where(r => status == null || r.Status == status).ToList();
                return Task.FromResult(((IReadOnlyList<GeneratedMapperArtifactRecord>)all.Skip(skip).Take(take).ToList(), all.Count));
            }
            public Task<bool> TryBeginGeneratingAsync(string g, string c, CancellationToken ct) => TryBeginGeneratingAsync(g, null, c, ct);
            public Task<bool> TryBeginGeneratingAsync(string g, string? p, string c, CancellationToken ct)
            {
                BeginProjects.Add(p);
                if (Rows.TryGetValue((g, K(p)), out var e) && e.Status == GeneratedMapperArtifactStatus.Generating) return Task.FromResult(false);
                Rows[(g, K(p))] = new GeneratedMapperArtifactRecord(g, GeneratedMapperArtifactStatus.Generating, null, null, null, null, c, null, DateTimeOffset.UtcNow, p);
                return Task.FromResult(true);
            }
            public Task CompleteAsync(string g, string content, string cov, string vb, string h, string c, CancellationToken ct)
                => CompleteAsync(g, null, content, cov, vb, h, c, ct);
            public Task CompleteAsync(string g, string? p, string content, string cov, string vb, string h, string c, CancellationToken ct)
            {
                Rows[(g, K(p))] = new GeneratedMapperArtifactRecord(g, GeneratedMapperArtifactStatus.Ready, content, cov, vb, h, c, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, p);
                Completed.TrySetResult(true);
                return Task.CompletedTask;
            }
            public Task FailAsync(string g, CancellationToken ct) => FailAsync(g, null, ct);
            public Task FailAsync(string g, string? p, CancellationToken ct)
            {
                Rows.Remove((g, K(p)));
                Completed.TrySetResult(false);
                return Task.CompletedTask;
            }
        }

        private sealed class OneMapper(Mapper m) : ICachedMapperService
        {
            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(new List<Mapper> { m });
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string g) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string g) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        private const string Xml = """
            <MapperVO><MapperGuid>G1</MapperGuid><Name>N</Name><InputLayoutGuid>I</InputLayoutGuid><TargetLayoutGuid>T</TargetLayoutGuid>
              <LinkMappings><LinkMappingItem><Name>a</Name><Sequence>1</Sequence><InputLayoutGuid>r/a</InputLayoutGuid><TargetLayoutGuid>r/a</TargetLayoutGuid></LinkMappingItem></LinkMappings>
              <Rules></Rules></MapperVO>
            """;

        private static (GeneratedMapperArtifactService, ProjectAwareStore, ServiceProvider) Build(string? projectId)
        {
            var store = new ProjectAwareStore();
            var mapperSvc = new OneMapper(new Mapper { MapperGuid = "G1", Name = "N", DecryptedContent = Xml, ProjectId = projectId! });
            var services = new ServiceCollection();
            services.AddSingleton<ICachedMapperService>(mapperSvc);
            services.AddSingleton<IGeneratedMapperArtifactStore>(store);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            var provider = services.BuildServiceProvider();
            var svc = new GeneratedMapperArtifactService(
                mapperSvc, store, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<GeneratedMapperArtifactService>.Instance,
                Options.Create(new LayoutParserApi.Services.XmlAnalysis.OllamaOptions { Url = "http://127.0.0.1:1", Model = "n/a" }),
                new GeneratedMapperGenerationLimiter(Options.Create(new GeneratedMapperSweepOptions())));
            return (svc, store, provider);
        }

        [Fact]
        public async Task Servico_grava_o_artefato_sob_o_ProjectId_do_mapper_sem_tocar_na_linha_legada()
        {
            var (svc, store, provider) = Build("7");
            try
            {
                // Linha legada (ProjectId nulo) de hash antigo: vira stale e NÃO é sobrescrita.
                store.Rows[("G1", "")] = new GeneratedMapperArtifactRecord(
                    "G1", GeneratedMapperArtifactStatus.Ready, "<legado/>", "{}", "declared_dsl", "HASH_ANTIGO", "c0",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

                var first = await svc.GetOrTriggerAsync("G1", "c1", CancellationToken.None);
                Assert.Equal(GeneratedMapperArtifactStatus.Generating, first!.Status);
                Assert.True(await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30)));

                Assert.Equal(["7"], store.BeginProjects);
                Assert.Equal("<legado/>", store.Rows[("G1", "")].Content);
                Assert.Equal("7", store.Rows[("G1", "7")].ProjectId);
                Assert.Equal(GeneratedMapperArtifactStatus.Ready, store.Rows[("G1", "7")].Status);

                // Segunda leitura acha a linha exata do projeto (não a legada) e devolve ready.
                var second = await svc.GetOrTriggerAsync("G1", "c2", CancellationToken.None);
                Assert.Equal(GeneratedMapperArtifactStatus.Ready, second!.Status);
                Assert.NotEqual("<legado/>", second.Content);
            }
            finally { await provider.DisposeAsync(); }
        }

        [Fact]
        public async Task Mapper_sem_ProjectId_continua_usando_a_linha_legada()
        {
            var (svc, store, provider) = Build(null);
            try
            {
                await svc.GetOrTriggerAsync("G1", "c1", CancellationToken.None);
                Assert.True(await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal([(string?)null], store.BeginProjects);
                Assert.Single(store.Rows);
                Assert.True(store.Rows.ContainsKey(("G1", "")));
            }
            finally { await provider.DisposeAsync(); }
        }

        [Fact]
        public async Task Store_antigo_sem_overloads_de_projeto_continua_compilando_e_funcionando_via_default()
        {
            IGeneratedMapperArtifactStore legacy = new LegacyOnlyStore();
            Assert.NotNull(await legacy.GetAsync("G", "9", CancellationToken.None));
            Assert.True(await legacy.TryBeginGeneratingAsync("G", "9", "c", CancellationToken.None));
            Assert.Single(await legacy.ListByMapperGuidAsync("G", CancellationToken.None));
        }

        private sealed class LegacyOnlyStore : IGeneratedMapperArtifactStore
        {
            public Task<GeneratedMapperArtifactRecord?> GetAsync(string g, CancellationToken ct)
                => Task.FromResult<GeneratedMapperArtifactRecord?>(new GeneratedMapperArtifactRecord(g, "ready", "x", null, null, null, null, null, DateTimeOffset.UtcNow));
            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? s, int k, int t, CancellationToken ct) => throw new NotSupportedException();
            public Task<bool> TryBeginGeneratingAsync(string g, string c, CancellationToken ct) => Task.FromResult(true);
            public Task CompleteAsync(string g, string c, string cov, string vb, string h, string cid, CancellationToken ct) => Task.CompletedTask;
            public Task FailAsync(string g, CancellationToken ct) => Task.CompletedTask;
        }

        // ── Adaptador Own ──────────────────────────────────────────────────────────────────
        private static GeneratedMapperArtifactRecord Ready(string guid, string? project, string content)
            => new(guid, "ready", content, null, null, "h", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, project);

        [Fact]
        public async Task Own_mesmo_mapperGuid_em_dois_projetos_gera_dois_catalogIds_e_legado_preserva_o_id()
        {
            var store = new ProjectAwareStore();
            store.Rows[("DUP", "")] = Ready("DUP", null, "<legado/>");
            store.Rows[("DUP", "1")] = Ready("DUP", "1", "<p1/>");
            store.Rows[("DUP", "2")] = Ready("DUP", "2", "<p2/>");
            var source = new OwnArtifactCatalogSource(store, NullLogger<OwnArtifactCatalogSource>.Instance);

            var snap = await source.ReadAsync(CancellationToken.None);

            Assert.Equal(3, snap.Items.Count);
            Assert.Equal(3, snap.Items.Select(i => i.CatalogId).Distinct().Count());
            Assert.Equal(3, snap.Folders.Count);
            // Id do item legado é o MESMO de antes da migração (pasta "-").
            Assert.Contains(snap.Items, i => i.CatalogId == CatalogIdGenerator.ForItem(SourceSystem.Own, "-", "DUP"));

            foreach (var item in snap.Items)
            {
                var content = await source.GetContentAsync(new MappingCatalogSourceRef(item.SourceItemKey, item.SourceRefJson), CancellationToken.None);
                Assert.NotNull(content);
                var expected = item.CatalogId == CatalogIdGenerator.ForItem(SourceSystem.Own, "1", "DUP") ? "<p1/>"
                    : item.CatalogId == CatalogIdGenerator.ForItem(SourceSystem.Own, "2", "DUP") ? "<p2/>" : "<legado/>";
                Assert.Equal(expected, content!.Content);
            }
        }

        [Fact]
        public async Task Own_content_nao_devolve_linha_legada_no_lugar_da_do_projeto()
        {
            var store = new ProjectAwareStore();
            store.Rows[("DUP", "")] = Ready("DUP", null, "<legado/>");
            var source = new OwnArtifactCatalogSource(store, NullLogger<OwnArtifactCatalogSource>.Instance);

            var content = await source.GetContentAsync(
                new MappingCatalogSourceRef("DUP", "{\"mapperGuid\":\"DUP\",\"projectId\":\"9\"}"), CancellationToken.None);

            Assert.Null(content); // o store cairia no legado; o adaptador recusa (nunca adivinha entre projetos).
        }
    }
}
