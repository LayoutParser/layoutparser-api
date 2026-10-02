using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #438 (ADR adr-unificacao-generated-artifact-mapping-release.md, opção b) — listagem
    /// unificada em <c>GET .../mapping-releases</c>: releases reais (<c>draft_compile</c>) +
    /// candidatos gerados automaticamente (<c>auto_generated</c>). Usa o
    /// <see cref="GeneratedMapperListService"/> REAL com store/catálogo dublês.
    /// </summary>
    public class MappingReleaseUnifiedListTests
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // como o AddJsonOptions do MVC (camelCase)
        };

        // --- dublês ---

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name => "u";
            public IReadOnlyList<string> Roles => Array.Empty<string>();
            public bool IsAuthenticated => true;
            public Guid? UserId => Guid.NewGuid();
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public List<MappingReleaseDetail> Releases { get; } = new();
            public int ListCalls;

            public Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? status, Guid? draftId, string? environment, CancellationToken cancellationToken)
            {
                ListCalls++;
                var q = Releases.Where(r => r.WorkspaceId == workspaceId);
                if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
                if (draftId is Guid d) q = q.Where(r => r.DraftId == d);
                if (!string.IsNullOrWhiteSpace(environment)) q = q.Where(r => r.Environment == environment);
                var all = q.OrderByDescending(r => r.CreatedAt).ToList();
                return Task.FromResult(((IReadOnlyList<MappingReleaseDetail>)all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count));
            }

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken, FiscalProfile? fiscalProfile = null) => throw new NotSupportedException();
            public Task<CreateManualEditOutcome> CreateManualEditArtifactReleaseAsync(Guid workspaceId, Guid draftId, string engine, string content, string manualEditReason, string expectedArtifactHash, Guid actorUserId, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail> DeprecateAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingReleaseDetail> ArchiveAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FakeGeneratedStore : IGeneratedMapperArtifactStore
        {
            public List<GeneratedMapperArtifactRecord> Records { get; } = new();
            public bool Throw;
            public int ListCalls;

            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken)
            {
                ListCalls++;
                if (Throw) throw new InvalidOperationException("SQL fora do ar");
                var q = Records.Where(r => string.IsNullOrWhiteSpace(status) || r.Status == status)
                    .OrderByDescending(r => r.GeneratedAt ?? r.UpdatedAt).ThenBy(r => r.MapperGuid).ToList();
                return Task.FromResult(((IReadOnlyList<GeneratedMapperArtifactRecord>)q.Skip(skip).Take(take).ToList(), q.Count));
            }

            public Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task CompleteAsync(string mapperGuid, string content, string coverageJson, string validationBasis, string mapperVoHash, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task FailAsync(string mapperGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FakeCatalog : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();
            public bool Throw;
            public Task<List<Mapper>> GetAllMappersAsync() => Throw ? throw new InvalidOperationException("tbMapper fora") : Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => throw new NotSupportedException();
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => throw new NotSupportedException();
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        // --- helpers ---

        private static MappingReleaseDetail NewRelease(Guid workspaceId, string status = MappingReleaseStatus.DraftCompiled, string environment = "development", int ageMinutes = 0) => new(
            Guid.NewGuid(), workspaceId, Guid.NewGuid(), "xslt", Array.Empty<MappingReleaseArtifact>(), Array.Empty<Guid>(),
            Array.Empty<MappingReleaseCompileDiagnostic>(), "hash", null, status, "corr-0", DateTimeOffset.UtcNow.AddMinutes(-ageMinutes), "AAAA",
            environment, null, null, null, null, null, null);

        private static GeneratedMapperArtifactRecord NewGenerated(string guid, string status = GeneratedMapperArtifactStatus.Ready, int ageMinutes = 0) => new(
            guid, status, null,
            status == GeneratedMapperArtifactStatus.Ready ? "{\"compiles\":true,\"linkPct\":0.87}" : null,
            status == GeneratedMapperArtifactStatus.Ready ? "declared_dsl" : null,
            "h", "corr-g", DateTimeOffset.UtcNow.AddMinutes(-ageMinutes), DateTimeOffset.UtcNow.AddMinutes(-ageMinutes));

        private sealed record Fixture(
            MappingGovernanceController Controller, FakeReleaseStore Releases, FakeGeneratedStore Generated, FakeCatalog Catalog, Guid WorkspaceId);

        private static Fixture Build(IMappingCatalogStore? catalogStore = null)
        {
            var releases = new FakeReleaseStore();
            var generated = new FakeGeneratedStore();
            var catalog = new FakeCatalog();
            var service = new GeneratedMapperListService(generated, catalog, NullLogger<GeneratedMapperListService>.Instance, catalogStore);
            var controller = new MappingGovernanceController(releases, service, new FakeCurrentUser(), NullLogger<MappingGovernanceController>.Instance);
            return new Fixture(controller, releases, generated, catalog, Guid.NewGuid());
        }

        /// <summary>Serializa como o pipeline real (WhenWritingNull) e devolve o JSON — é o que prova "omitido, não null".</summary>
        private static (int TotalCount, List<JsonElement> Items, bool Unavailable) Read(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, Json));
            var root = doc.RootElement;
            return (
                root.GetProperty("totalCount").GetInt32(),
                root.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList(),
                root.GetProperty("autoGeneratedUnavailable").GetBoolean());
        }

        private static string Origin(JsonElement e) => e.GetProperty("origin").GetString()!;

        // --- testes ---

        [Fact]
        public async Task Lista_mista_devolve_releases_reais_primeiro_e_auto_gerados_com_origin_correto()
        {
            var f = Build();
            f.Releases.Releases.Add(NewRelease(f.WorkspaceId));
            f.Generated.Records.Add(NewGenerated("GUID-A"));
            f.Generated.Records.Add(NewGenerated("GUID-B", GeneratedMapperArtifactStatus.Generating, ageMinutes: 5));
            f.Catalog.Mappers.Add(new Mapper { MapperGuid = "guid-a", Name = "MAP_CNHI_ENVNFE", DecryptedContent = "" });

            var (total, items, unavailable) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));

            Assert.False(unavailable);
            Assert.Equal(3, total);
            Assert.Equal(new[] { "draft_compile", "auto_generated", "auto_generated" }, items.Select(Origin));

            var ready = items.Single(i => i.TryGetProperty("mapperGuid", out var g) && g.GetString() == "GUID-A");
            Assert.Equal("MAP_CNHI_ENVNFE", ready.GetProperty("mapperName").GetString()); // GUID casa case-insensitive.
            Assert.Equal("ready", ready.GetProperty("status").GetString());
            Assert.Equal("declared_dsl", ready.GetProperty("validationBasis").GetString());
            Assert.True(ready.GetProperty("coverage").GetProperty("compiles").GetBoolean()); // objeto, não string opaca.
            Assert.Equal($"/api/workspaces/{f.WorkspaceId}/mappings/GUID-A/generated-transformation", ready.GetProperty("detailUrl").GetString());
            Assert.False(ready.TryGetProperty("content", out _)); // conteúdo só no detalhe.

            var generating = items.Single(i => i.TryGetProperty("mapperGuid", out var g) && g.GetString() == "GUID-B");
            Assert.Equal("generating", generating.GetProperty("status").GetString());
            Assert.False(generating.TryGetProperty("mapperName", out _)); // sem nome no catálogo → omitido, segue o guid.
        }

        [Fact]
        public async Task Item_auto_gerado_omite_campos_de_release_e_o_item_real_traz_origin()
        {
            var f = Build();
            f.Releases.Releases.Add(NewRelease(f.WorkspaceId));
            f.Generated.Records.Add(NewGenerated("G1"));

            var (_, items, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));

            var real = items.Single(i => Origin(i) == "draft_compile");
            Assert.True(real.TryGetProperty("releaseId", out _));
            Assert.True(real.TryGetProperty("draftId", out _));

            var auto = items.Single(i => Origin(i) == "auto_generated");
            foreach (var omitido in new[] { "releaseId", "draftId", "testRunSummary", "approvedByUserId", "publishedAt", "environment", "engine", "workspaceId" })
                Assert.False(auto.TryGetProperty(omitido, out _), $"'{omitido}' deveria estar omitido no item auto_generated");
        }

        [Fact]
        public async Task Paginacao_combinada_cada_pagina_respeita_pageSize_e_nao_repete_nem_perde_item()
        {
            var f = Build();
            for (var i = 0; i < 3; i++) f.Releases.Releases.Add(NewRelease(f.WorkspaceId, ageMinutes: i));
            for (var i = 0; i < 4; i++) f.Generated.Records.Add(NewGenerated($"G{i}", ageMinutes: i));

            var vistos = new List<string>();
            foreach (var page in new[] { 1, 2, 3, 4 })
            {
                var (total, items, _) = Read(await f.Controller.List(f.WorkspaceId, page, 2));
                Assert.Equal(7, total);
                Assert.True(items.Count <= 2);
                vistos.AddRange(items.Select(i => i.TryGetProperty("releaseId", out var r) ? r.GetString()! : i.GetProperty("mapperGuid").GetString()!));
            }

            Assert.Equal(7, vistos.Count);
            Assert.Equal(7, vistos.Distinct().Count());

            // Página 2 mistura o último release real com o 1º auto-gerado (fronteira da sequência).
            var (_, p2, _) = Read(await f.Controller.List(f.WorkspaceId, 2, 2));
            Assert.Equal(new[] { "draft_compile", "auto_generated" }, p2.Select(Origin));

            // Página além do fim: vazia, mas totalCount continua correto.
            var (totalFim, itensFim, _) = Read(await f.Controller.List(f.WorkspaceId, 9, 2));
            Assert.Equal(7, totalFim);
            Assert.Empty(itensFim);
        }

        [Fact]
        public async Task Filtro_origin_isola_cada_lado_e_nao_consulta_a_fonte_excluida()
        {
            var f = Build();
            f.Releases.Releases.Add(NewRelease(f.WorkspaceId));
            f.Generated.Records.Add(NewGenerated("G1"));
            f.Generated.Records.Add(NewGenerated("G2"));

            var (totalAuto, auto, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, origin: "auto_generated"));
            Assert.Equal(2, totalAuto);
            Assert.All(auto, i => Assert.Equal("auto_generated", Origin(i)));
            Assert.Equal(0, f.Releases.ListCalls);

            var (totalReal, real, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, origin: "draft_compile"));
            Assert.Equal(1, totalReal);
            Assert.All(real, i => Assert.Equal("draft_compile", Origin(i)));
            Assert.Equal(1, f.Generated.ListCalls); // só a chamada da 1ª leitura.
        }

        [Fact]
        public async Task Origin_invalido_retorna_400()
        {
            var f = Build();
            Assert.IsType<BadRequestObjectResult>(await f.Controller.List(f.WorkspaceId, 1, 20, origin: "manual"));
        }

        [Fact]
        public async Task Filtros_draftId_e_environment_excluem_os_auto_gerados()
        {
            var f = Build();
            var release = NewRelease(f.WorkspaceId, environment: "production");
            f.Releases.Releases.Add(release);
            f.Generated.Records.Add(NewGenerated("G1"));

            var (t1, i1, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, draftId: release.DraftId.ToString()));
            var (t2, i2, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, environment: "production"));

            Assert.Equal(1, t1);
            Assert.Equal(1, t2);
            Assert.All(i1.Concat(i2), i => Assert.Equal("draft_compile", Origin(i)));
            Assert.Equal(0, f.Generated.ListCalls);
        }

        [Fact]
        public async Task Status_de_release_exclui_auto_gerados_e_status_gerado_exclui_releases()
        {
            var f = Build();
            f.Releases.Releases.Add(NewRelease(f.WorkspaceId, MappingReleaseStatus.Published));
            f.Generated.Records.Add(NewGenerated("G-READY"));
            f.Generated.Records.Add(NewGenerated("G-GEN", GeneratedMapperArtifactStatus.Generating));

            var (tPub, iPub, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, status: MappingReleaseStatus.Published));
            Assert.Equal(1, tPub);
            Assert.All(iPub, i => Assert.Equal("draft_compile", Origin(i)));

            var (tGen, iGen, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, status: "generating"));
            Assert.Equal(1, tGen);
            Assert.Equal("G-GEN", iGen.Single().GetProperty("mapperGuid").GetString());

            // stale não é persistido: aceito (200), lista vazia.
            var (tStale, iStale, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20, status: "stale"));
            Assert.Equal(0, tStale);
            Assert.Empty(iStale);
        }

        [Fact]
        public async Task Store_de_gerados_falhando_nao_quebra_a_lista_de_releases_reais()
        {
            var f = Build();
            f.Releases.Releases.Add(NewRelease(f.WorkspaceId));
            f.Releases.Releases.Add(NewRelease(f.WorkspaceId));
            f.Generated.Throw = true;

            var (total, items, unavailable) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));

            Assert.True(unavailable);
            Assert.Equal(2, total);
            Assert.All(items, i => Assert.Equal("draft_compile", Origin(i)));
        }

        [Fact]
        public async Task Catalogo_de_mappers_falhando_degrada_para_item_sem_nome()
        {
            var f = Build();
            f.Generated.Records.Add(NewGenerated("G1"));
            f.Catalog.Throw = true;

            var (total, items, unavailable) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));

            Assert.False(unavailable); // o store funcionou; só o nome ficou de fora.
            Assert.Equal(1, total);
            Assert.Equal("G1", items.Single().GetProperty("mapperGuid").GetString());
            Assert.False(items.Single().TryGetProperty("mapperName", out _));
        }

        [Fact]
        public async Task Todo_workspace_ve_os_mesmos_auto_gerados_decisao_provisoria_417()
        {
            var f = Build();
            f.Generated.Records.Add(NewGenerated("G1"));

            var (t1, _, _) = Read(await f.Controller.List(Guid.NewGuid(), 1, 20));
            var (t2, _, _) = Read(await f.Controller.List(Guid.NewGuid(), 1, 20));

            Assert.Equal(1, t1);
            Assert.Equal(1, t2);
        }

        // --- Issue #634: campos aditivos catalogId/catalogDetailUrl ---

        private static LayoutParserApi.Models.Catalog.MappingCatalogItemDto IndexedItem(Guid id) => new(
            id, Guid.NewGuid(), LayoutParserApi.Models.Catalog.SourceSystem.Own, "k", "xslt", "n", null, null, null, "{}", false);

        [Fact]
        public async Task Auto_gerado_indexado_traz_catalogId_e_catalogDetailUrl_sem_alterar_os_campos_existentes()
        {
            var catalogStore = new LayoutParserApi.Tests.Catalog.FakeMappingCatalogStore();
            var id = LayoutParserApi.Services.Catalog.OwnArtifactCatalogSource.CatalogIdFor("GUID-A", null);
            catalogStore.Items[id] = IndexedItem(id);
            var f = Build(catalogStore);
            f.Generated.Records.Add(NewGenerated("GUID-A"));

            var (_, items, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));

            var item = items.Single();
            Assert.Equal(id, item.GetProperty("catalogId").GetGuid());
            Assert.Equal($"/api/mapping-catalog/items/{id}", item.GetProperty("catalogDetailUrl").GetString());
            // Contrato antigo intacto: mesmos campos e mesmos valores de antes.
            Assert.Equal("auto_generated", Origin(item));
            Assert.Equal("GUID-A", item.GetProperty("mapperGuid").GetString());
            Assert.Equal("ready", item.GetProperty("status").GetString());
            Assert.Equal("declared_dsl", item.GetProperty("validationBasis").GetString());
            Assert.Equal($"/api/workspaces/{f.WorkspaceId}/mappings/GUID-A/generated-transformation", item.GetProperty("detailUrl").GetString());
        }

        [Fact]
        public async Task Auto_gerado_nao_indexado_ou_catalogo_fora_omite_os_campos_novos()
        {
            var catalogStore = new LayoutParserApi.Tests.Catalog.FakeMappingCatalogStore(); // índice vazio
            var f = Build(catalogStore);
            f.Generated.Records.Add(NewGenerated("GUID-A"));
            var (_, naoIndexado, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));
            Assert.False(naoIndexado.Single().TryGetProperty("catalogId", out _));
            Assert.False(naoIndexado.Single().TryGetProperty("catalogDetailUrl", out _));

            var down = new LayoutParserApi.Tests.Catalog.FakeMappingCatalogStore { Down = true };
            var f2 = Build(down);
            f2.Generated.Records.Add(NewGenerated("GUID-A"));
            var (total, fora, unavailable) = Read(await f2.Controller.List(f2.WorkspaceId, 1, 20));
            Assert.Equal(1, total);
            Assert.False(unavailable); // índice fora NÃO derruba nem sinaliza a listagem
            Assert.False(fora.Single().TryGetProperty("catalogId", out _));

            var f3 = Build(); // sem catalog store nenhum (cenário legado)
            f3.Generated.Records.Add(NewGenerated("GUID-A"));
            Assert.False(Read(await f3.Controller.List(f3.WorkspaceId, 1, 20)).Items.Single().TryGetProperty("catalogId", out _));
        }

        [Fact]
        public async Task Auto_gerado_com_projeto_usa_o_catalogId_da_pasta_do_projeto()
        {
            var catalogStore = new LayoutParserApi.Tests.Catalog.FakeMappingCatalogStore();
            var idProjeto = LayoutParserApi.Services.Catalog.OwnArtifactCatalogSource.CatalogIdFor("DUP", "7");
            var idLegado = LayoutParserApi.Services.Catalog.OwnArtifactCatalogSource.CatalogIdFor("DUP", null);
            Assert.NotEqual(idProjeto, idLegado);
            catalogStore.Items[idProjeto] = IndexedItem(idProjeto);
            var f = Build(catalogStore);
            f.Generated.Records.Add(NewGenerated("DUP") with { ProjectId = "7" });

            var (_, items, _) = Read(await f.Controller.List(f.WorkspaceId, 1, 20));

            Assert.Equal(idProjeto, items.Single().GetProperty("catalogId").GetGuid());
        }
    }
}
