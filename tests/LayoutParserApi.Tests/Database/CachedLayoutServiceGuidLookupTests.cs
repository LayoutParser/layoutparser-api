using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Issue #433: <c>GET .../mappings/{mapperGuid}/layout-tree</c> devolvia <c>target.roots</c>
    /// vazio para mappers reais mesmo com <c>source.roots</c> populado. Causa raiz: o fallback de
    /// banco de <see cref="CachedLayoutService.GetLayoutByGuidAsync"/> reaproveitava a mesma busca
    /// usada pelo warmup do cache Redis (<see cref="LayoutDatabaseService"/>), que filtra
    /// silenciosamente qualquer layout que não seja <c>TextPositional</c> — o layout de DESTINO de
    /// um mapper é <c>XmlLayoutVO</c>, então nunca era encontrado, e <c>LayoutTreeService</c>
    /// degradava esse lado para árvore vazia sem sinalizar o motivo.
    ///
    /// <para>Este teste reproduz a condição com um fake de <see cref="ILayoutDatabaseService"/> que
    /// simula exatamente esse comportamento (só devolve o layout quando
    /// <see cref="LayoutSearchRequest.IncludeAllLayoutTypes"/> é <c>true</c>) — a mesma regra hoje
    /// aplicada de verdade em <see cref="LayoutDatabaseService"/> contra o filtro
    /// <c>IsTextPositionalLayout</c>.</para>
    /// </summary>
    public sealed class CachedLayoutServiceGuidLookupTests
    {
        /// <summary>
        /// Fake que reproduz o filtro real de <see cref="LayoutDatabaseService"/>: só devolve o
        /// layout XML de destino quando o chamador pede explicitamente todos os tipos.
        /// </summary>
        private sealed class FakeLayoutDatabaseService : ILayoutDatabaseService
        {
            public LayoutRecord XmlTargetLayout { get; } = new()
            {
                Id = 42,
                LayoutGuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Layout de destino (XML)",
                LayoutType = "Xml",
                DecryptedContent = "<LayoutVO><LayoutType>Xml</LayoutType></LayoutVO>",
            };

            public LayoutSearchRequest? LastRequest { get; private set; }
            public string? LastGuidLookup { get; private set; }

            // Busca real por GUID (a busca por nome/SearchTerm nunca achava o layout de destino).
            public Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid)
            {
                LastGuidLookup = layoutGuid;
                return Task.FromResult<LayoutRecord?>(
                    layoutGuid.EndsWith(XmlTargetLayout.LayoutGuid.ToString(), StringComparison.OrdinalIgnoreCase) ? XmlTargetLayout : null);
            }

            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request)
            {
                LastRequest = request;

                // Mesma regra de LayoutDatabaseService.SearchLayoutsFromDatabase: sem
                // IncludeAllLayoutTypes, um layout não-TextPositional (aqui, Xml) é descartado.
                if (!request.IncludeAllLayoutTypes)
                    return Task.FromResult(new LayoutSearchResponse { Success = true, Layouts = new List<LayoutRecord>(), TotalFound = 0 });

                return Task.FromResult(new LayoutSearchResponse
                {
                    Success = true,
                    Layouts = new List<LayoutRecord> { XmlTargetLayout },
                    TotalFound = 1,
                });
            }

            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => throw new NotSupportedException();
        }

        private sealed class FakeLayoutCacheService : ILayoutCacheService
        {
            // Cache Redis "vazio" — só contém warmup TextPositional, nunca o layout XML de destino.
            public Task<List<LayoutRecord>?> GetCachedLayoutsAsync(string searchTerm) => Task.FromResult<List<LayoutRecord>?>(null);
            public Task SetCachedLayoutsAsync(string searchTerm, List<LayoutRecord> layouts, TimeSpan? expiry = null) => Task.CompletedTask;
            public Task<LayoutRecord?> GetCachedLayoutByIdAsync(int id) => Task.FromResult<LayoutRecord?>(null);
            public Task SetCachedLayoutByIdAsync(int id, LayoutRecord layout, TimeSpan? expiry = null) => Task.CompletedTask;
            public Task ClearCacheAsync() => Task.CompletedTask;
        }

        [Fact]
        public async Task GetLayoutByGuidAsync_LayoutXmlDeDestino_EncontradoComIncludeAllLayoutTypes()
        {
            var db = new FakeLayoutDatabaseService();
            var cache = new FakeLayoutCacheService();
            var service = new CachedLayoutService(db, cache, NullLogger<CachedLayoutService>.Instance);

            var result = await service.GetLayoutByGuidAsync(db.XmlTargetLayout.LayoutGuid.ToString());

            Assert.NotNull(result);
            Assert.Equal("Layout de destino (XML)", result!.Name);

            // Trava a causa raiz: o fallback precisa buscar por GUID real, nao por SearchTerm (nome).
            Assert.Equal(db.XmlTargetLayout.LayoutGuid.ToString(), db.LastGuidLookup);
            Assert.Null(db.LastRequest);
        }

        [Theory]
        [InlineData("11111111-1111-1111-1111-111111111111")]
        [InlineData("LAY_11111111-1111-1111-1111-111111111111")]
        [InlineData("{11111111-1111-1111-1111-111111111111}")]
        public void BuildGuidLookupQuery_ParametrizaGuidComESemPrefixo_SemFiltroDeProjeto(string entrada)
        {
            var (sql, parametros) = LayoutDatabaseService.BuildGuidLookupQuery(entrada);

            Assert.Equal("11111111-1111-1111-1111-111111111111", parametros["@Guid"]);
            Assert.Equal("LAY_11111111-1111-1111-1111-111111111111", parametros["@GuidPrefixado"]);
            Assert.Contains("IN (@Guid, @GuidPrefixado)", sql);
            Assert.DoesNotContain("[ProjectId] =", sql);
            Assert.DoesNotContain("LIKE", sql);
            Assert.DoesNotContain("11111111", sql); // valor nunca concatenado no SQL
            Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
