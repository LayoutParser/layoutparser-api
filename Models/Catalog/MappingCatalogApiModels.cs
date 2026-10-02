using System.Text.Json.Serialization;

namespace LayoutParserApi.Models.Catalog
{
    /// <summary>
    /// Resultado de leitura do índice que distingue "vazio" de "IdentityDatabase indisponível"
    /// (<see cref="Available"/> = false) — o chamador devolve 503 só do catálogo (design D8).
    /// </summary>
    public sealed record CatalogReadResult<T>(bool Available, T? Value)
    {
        public static CatalogReadResult<T> Unavailable() => new(false, default);
        public static CatalogReadResult<T> Ok(T value) => new(true, value);
    }

    /// <summary>Página de resultados com total (paginação por offset, ordem total estável).</summary>
    public sealed record MappingCatalogPage<T>(IReadOnlyList<T> Items, int TotalCount);

    /// <summary>Nível 1 da árvore com contagens (pastas/itens ativos). Sem nada específico de adaptador.</summary>
    public sealed record MappingCatalogSourceSummary(
        [property: JsonPropertyName("sourceSystem")] SourceSystem SourceSystem,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("lastSyncUtc")] DateTime? LastSyncUtc,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("lastError")] string? LastError,
        [property: JsonPropertyName("folderCount")] int FolderCount,
        [property: JsonPropertyName("itemCount")] int ItemCount);

    /// <summary>Item do catálogo como o portal enxerga (contrato D5): sem <c>SourceRefJson</c> (ponteiro interno).</summary>
    public sealed record MappingCatalogItemView(
        [property: JsonPropertyName("catalogId")] Guid CatalogId,
        [property: JsonPropertyName("sourceSystem")] SourceSystem SourceSystem,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("folderId")] Guid FolderId,
        [property: JsonPropertyName("projectId")] long? ProjectId,
        [property: JsonPropertyName("projectName")] string? ProjectName,
        [property: JsonPropertyName("sourceItemKey")] string SourceItemKey,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("docType")] string? DocType,
        [property: JsonPropertyName("contentHash")] string? ContentHash,
        [property: JsonPropertyName("retired")] bool Retired,
        [property: JsonPropertyName("detailUrl")] string DetailUrl,
        [property: JsonPropertyName("pairedCatalogId")] Guid? PairedCatalogId,
        [property: JsonPropertyName("sourceStatus")] string? SourceStatus = null);

    /// <summary>Envelope paginado: <c>{ items[], page, pageSize, totalCount, sourceStatus }</c>.</summary>
    public sealed record MappingCatalogListResponse<T>(
        [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
        [property: JsonPropertyName("page")] int Page,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("totalCount")] int TotalCount,
        [property: JsonPropertyName("sourceStatus")] string SourceStatus);

    /// <summary>Corpo de um mapeador devolvido por <c>items/{catalogId}/content</c>.</summary>
    public sealed record MappingCatalogContentResponse(
        [property: JsonPropertyName("catalogId")] Guid CatalogId,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("content")] string Content);
}
