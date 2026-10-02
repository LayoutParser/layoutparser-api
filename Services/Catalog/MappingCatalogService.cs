using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Catalog
{
    public enum CatalogOutcome { Ok, NotFound, Unavailable }

    /// <summary>Resultado de uma consulta ao catálogo; o controller só traduz para HTTP.</summary>
    public sealed record CatalogResult<T>(CatalogOutcome Outcome, T? Value = default, string? Message = null)
    {
        public static CatalogResult<T> Ok(T v) => new(CatalogOutcome.Ok, v);
        public static CatalogResult<T> NotFound(string msg) => new(CatalogOutcome.NotFound, default, msg);
        public static CatalogResult<T> Unavailable(string msg) => new(CatalogOutcome.Unavailable, default, msg);
    }

    /// <summary>Consultas do catálogo unificado (issue #631): árvore de 3 níveis lida do índice local.</summary>
    public interface IMappingCatalogService
    {
        Task<CatalogResult<IReadOnlyList<MappingCatalogSourceSummary>>> ListSourcesAsync(CancellationToken ct);
        Task<CatalogResult<MappingCatalogListResponse<MappingCatalogFolderDto>>> ListFoldersAsync(SourceSystem system, int page, int pageSize, CancellationToken ct);
        Task<CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>> ListItemsAsync(Guid folderId, string? engine, string? q, bool includeRetired, int page, int pageSize, CancellationToken ct);
        Task<CatalogResult<MappingCatalogItemView>> GetItemAsync(Guid catalogId, CancellationToken ct);
        Task<CatalogResult<MappingCatalogContentResponse>> GetContentAsync(Guid catalogId, CancellationToken ct);
    }

    public sealed class MappingCatalogService : IMappingCatalogService
    {
        public const int MaxPageSize = 200;
        private const string StoreDown = "Catálogo indisponível no momento (índice local fora do ar).";

        private readonly IMappingCatalogStore _store;
        private readonly IEnumerable<IMappingCatalogSource> _sources;
        private readonly IOptionsMonitor<MappingCatalogOptions> _options;
        private readonly ILogger<MappingCatalogService> _logger;

        public MappingCatalogService(IMappingCatalogStore store, IEnumerable<IMappingCatalogSource> sources,
            IOptionsMonitor<MappingCatalogOptions> options, ILogger<MappingCatalogService> logger)
        {
            _store = store;
            _sources = sources;
            _options = options;
            _logger = logger;
        }

        public async Task<CatalogResult<IReadOnlyList<MappingCatalogSourceSummary>>> ListSourcesAsync(CancellationToken ct)
        {
            var read = await _store.ListSourcesAsync(ct);
            if (!read.Available)
                return CatalogResult<IReadOnlyList<MappingCatalogSourceSummary>>.Unavailable(StoreDown);

            var rows = read.Value!.ToDictionary(r => r.SourceSystem);
            var systems = _sources.Select(s => s.System).Concat(rows.Keys).Distinct().OrderBy(s => s.ToWireName(), StringComparer.Ordinal);
            var list = new List<MappingCatalogSourceSummary>();
            foreach (var system in systems)
            {
                var enabled = _options.CurrentValue.IsEnabled(system); // a flag da CONFIG manda, não a linha persistida
                if (rows.TryGetValue(system, out var row))
                    list.Add(row with { Enabled = enabled, Status = NormalizeStatus(row.Status) });
                else
                    list.Add(new MappingCatalogSourceSummary(system, enabled, null, MappingCatalogSourceStatus.Unavailable, null, 0, 0));
            }
            return CatalogResult<IReadOnlyList<MappingCatalogSourceSummary>>.Ok(list);
        }

        public async Task<CatalogResult<MappingCatalogListResponse<MappingCatalogFolderDto>>> ListFoldersAsync(
            SourceSystem system, int page, int pageSize, CancellationToken ct)
        {
            (page, pageSize) = Clamp(page, pageSize);
            var folders = await _store.ListFoldersAsync(system, (page - 1) * pageSize, pageSize, ct);
            if (!folders.Available)
                return CatalogResult<MappingCatalogListResponse<MappingCatalogFolderDto>>.Unavailable(StoreDown);
            var status = await StatusOfAsync(system, ct);
            return CatalogResult<MappingCatalogListResponse<MappingCatalogFolderDto>>.Ok(
                new(folders.Value!.Items, page, pageSize, folders.Value.TotalCount, status));
        }

        public async Task<CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>> ListItemsAsync(
            Guid folderId, string? engine, string? q, bool includeRetired, int page, int pageSize, CancellationToken ct)
        {
            (page, pageSize) = Clamp(page, pageSize);
            var folder = await _store.GetFolderAsync(folderId, ct);
            if (!folder.Available)
                return CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>.Unavailable(StoreDown);
            if (folder.Value == null)
                return CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>.NotFound("Pasta não encontrada.");

            var items = await _store.ListItemsAsync(folderId, engine, q, includeRetired, (page - 1) * pageSize, pageSize, ct);
            if (!items.Available)
                return CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>.Unavailable(StoreDown);

            var status = await StatusOfAsync(folder.Value.SourceSystem, ct);
            var views = items.Value!.Items.Select(i => ToView(i, folder.Value, null)).ToList();
            return CatalogResult<MappingCatalogListResponse<MappingCatalogItemView>>.Ok(
                new(views, page, pageSize, items.Value.TotalCount, status));
        }

        public async Task<CatalogResult<MappingCatalogItemView>> GetItemAsync(Guid catalogId, CancellationToken ct)
        {
            var item = await _store.FindItemAsync(catalogId, ct);
            if (!item.Available)
                return CatalogResult<MappingCatalogItemView>.Unavailable(StoreDown);
            if (item.Value == null)
                return CatalogResult<MappingCatalogItemView>.NotFound("Item não encontrado.");
            var folder = await _store.GetFolderAsync(item.Value.FolderId, ct);
            if (!folder.Available)
                return CatalogResult<MappingCatalogItemView>.Unavailable(StoreDown);
            var status = await StatusOfAsync(item.Value.SourceSystem, ct);
            return CatalogResult<MappingCatalogItemView>.Ok(ToView(item.Value, folder.Value, status));
        }

        public async Task<CatalogResult<MappingCatalogContentResponse>> GetContentAsync(Guid catalogId, CancellationToken ct)
        {
            var item = await _store.FindItemAsync(catalogId, ct);
            if (!item.Available)
                return CatalogResult<MappingCatalogContentResponse>.Unavailable(StoreDown);
            if (item.Value == null)
                return CatalogResult<MappingCatalogContentResponse>.NotFound("Item não encontrado.");

            var adapter = _sources.FirstOrDefault(s => s.System == item.Value.SourceSystem);
            if (adapter == null)
                return CatalogResult<MappingCatalogContentResponse>.Unavailable("Origem do item não está disponível nesta instalação.");
            try
            {
                var content = await adapter.GetContentAsync(new MappingCatalogSourceRef(item.Value.SourceItemKey, item.Value.SourceRefJson), ct);
                return content == null
                    ? CatalogResult<MappingCatalogContentResponse>.NotFound("Conteúdo não encontrado na origem.")
                    : CatalogResult<MappingCatalogContentResponse>.Ok(new(catalogId, content.Engine, content.Content));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Nunca loga o corpo; só a origem e o id.
                _logger.LogWarning(ex, "Falha ao buscar conteúdo na origem {SourceSystem} para o item {CatalogId}.", item.Value.SourceSystem.ToWireName(), catalogId);
                return CatalogResult<MappingCatalogContentResponse>.Unavailable("Origem indisponível para leitura do conteúdo.");
            }
        }

        private async Task<string> StatusOfAsync(SourceSystem system, CancellationToken ct)
        {
            var row = await _store.GetSourceAsync(system, ct);
            return row is { Available: true, Value: not null } ? NormalizeStatus(row.Value.Status) : MappingCatalogSourceStatus.Unavailable;
        }

        private static string NormalizeStatus(string? s) => s switch
        {
            MappingCatalogSourceStatus.Ok => MappingCatalogSourceStatus.Ok,
            MappingCatalogSourceStatus.Stale => MappingCatalogSourceStatus.Stale,
            _ => MappingCatalogSourceStatus.Unavailable,
        };

        private static (int Page, int PageSize) Clamp(int page, int pageSize)
            => (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

        private static MappingCatalogItemView ToView(MappingCatalogItemDto i, MappingCatalogFolderDto? f, string? status)
            => new(i.CatalogId, i.SourceSystem, i.Engine, i.Name, i.FolderId, f?.SourceProjectId, f?.Name, i.SourceItemKey,
                i.Version, i.DocType, i.ContentHash, i.Retired, $"/api/mapping-catalog/items/{i.CatalogId}", i.PairedCatalogId, status);
    }
}
