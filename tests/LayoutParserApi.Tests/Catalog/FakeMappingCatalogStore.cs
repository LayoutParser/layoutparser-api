using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Tests.Catalog
{
    /// <summary>Store em memória para testar service/controller/sync sem SQL. <see cref="Down"/> simula IdentityDatabase fora.</summary>
    public sealed class FakeMappingCatalogStore : IMappingCatalogStore
    {
        public bool Down;
        public Dictionary<SourceSystem, MappingCatalogSourceDto> Sources = new();
        public Dictionary<Guid, MappingCatalogFolderDto> Folders = new();
        public Dictionary<Guid, MappingCatalogItemDto> Items = new();

        public Task<bool> UpsertSourceAsync(MappingCatalogSourceDto s, CancellationToken ct) { if (Down) return Task.FromResult(false); Sources[s.SourceSystem] = s; return Task.FromResult(true); }
        public Task<bool> UpsertFolderAsync(MappingCatalogFolderDto f, CancellationToken ct) { if (Down) return Task.FromResult(false); Folders[f.FolderId] = f; return Task.FromResult(true); }
        public Task<bool> UpsertItemAsync(MappingCatalogItemDto i, CancellationToken ct) { if (Down) return Task.FromResult(false); Items[i.CatalogId] = i with { Retired = false }; return Task.FromResult(true); }
        public Task<bool> RetireItemAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
        public Task<int> RetireUnseenAsync(SourceSystem s, DateTime before, CancellationToken ct) => Task.FromResult(0);
        public Task<MappingCatalogItemDto?> GetItemAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>> ListSourcesAsync(CancellationToken ct)
        {
            if (Down) return Task.FromResult(CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>.Unavailable());
            IReadOnlyList<MappingCatalogSourceSummary> l = Sources.Values.Select(s => new MappingCatalogSourceSummary(
                s.SourceSystem, s.Enabled, s.LastSyncUtc, s.Status, s.LastError,
                Folders.Values.Count(f => f.SourceSystem == s.SourceSystem && !f.Retired),
                Items.Values.Count(i => i.SourceSystem == s.SourceSystem && !i.Retired))).ToList();
            return Task.FromResult(CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>.Ok(l));
        }

        public Task<CatalogReadResult<MappingCatalogSourceDto?>> GetSourceAsync(SourceSystem s, CancellationToken ct)
            => Task.FromResult(Down ? CatalogReadResult<MappingCatalogSourceDto?>.Unavailable() : CatalogReadResult<MappingCatalogSourceDto?>.Ok(Sources.GetValueOrDefault(s)));

        public Task<CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>> ListFoldersAsync(SourceSystem s, int skip, int take, CancellationToken ct)
        {
            if (Down) return Task.FromResult(CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>.Unavailable());
            var all = Folders.Values.Where(f => f.SourceSystem == s && !f.Retired).OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.FolderId).ToList();
            return Task.FromResult(CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>.Ok(new(all.Skip(skip).Take(take).ToList(), all.Count)));
        }

        public Task<CatalogReadResult<MappingCatalogFolderDto?>> GetFolderAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Down ? CatalogReadResult<MappingCatalogFolderDto?>.Unavailable() : CatalogReadResult<MappingCatalogFolderDto?>.Ok(Folders.GetValueOrDefault(id)));

        public Task<CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>> ListItemsAsync(Guid folderId, string? engine, string? q, bool includeRetired, int skip, int take, CancellationToken ct)
        {
            if (Down) return Task.FromResult(CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>.Unavailable());
            var all = Items.Values.Where(i => i.FolderId == folderId && (includeRetired || !i.Retired)
                    && (engine == null || i.Engine == engine)
                    && (string.IsNullOrWhiteSpace(q) || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.CatalogId).ToList();
            return Task.FromResult(CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>.Ok(new(all.Skip(skip).Take(take).ToList(), all.Count)));
        }

        public Task<CatalogReadResult<MappingCatalogItemDto?>> FindItemAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Down ? CatalogReadResult<MappingCatalogItemDto?>.Unavailable() : CatalogReadResult<MappingCatalogItemDto?>.Ok(Items.GetValueOrDefault(id)));
    }
}
