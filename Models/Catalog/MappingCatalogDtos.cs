using System.Text.Json.Serialization;

namespace LayoutParserApi.Models.Catalog
{
    /// <summary>Motor do artefato de mapeamento (design D4: <c>tcl|xsl|xslt</c>).</summary>
    public static class MappingCatalogEngine
    {
        public const string Tcl = "tcl";
        public const string Xsl = "xsl";
        public const string Xslt = "xslt";
    }

    /// <summary>Status de sincronização da origem (design D5/D8).</summary>
    public static class MappingCatalogSourceStatus
    {
        public const string Ok = "ok";
        public const string Stale = "stale";
        public const string Unavailable = "unavailable";
    }

    /// <summary>Nível 1 da árvore: uma origem, com estado do último sync. Só metadados.</summary>
    public sealed record MappingCatalogSourceDto(
        [property: JsonPropertyName("sourceSystem")] SourceSystem SourceSystem,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("lastSyncUtc")] DateTime? LastSyncUtc,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("lastError")] string? LastError);

    /// <summary>Nível 2: projeto/pasta dentro de uma origem. <c>SourceProjectKey</c> "-" = "Sem projeto".</summary>
    public sealed record MappingCatalogFolderDto(
        [property: JsonPropertyName("folderId")] Guid FolderId,
        [property: JsonPropertyName("sourceSystem")] SourceSystem SourceSystem,
        [property: JsonPropertyName("sourceProjectKey")] string SourceProjectKey,
        [property: JsonPropertyName("sourceProjectId")] long? SourceProjectId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("retired")] bool Retired);

    /// <summary>
    /// Nível 3: mapeador. Metadados + ponteiro (<see cref="SourceRefJson"/>) — nunca o corpo TCL/XSL.
    /// <c>mapperGuid</c> NÃO é chave: aparece só como <see cref="SourceItemKey"/> informativo.
    /// </summary>
    public sealed record MappingCatalogItemDto(
        [property: JsonPropertyName("catalogId")] Guid CatalogId,
        [property: JsonPropertyName("folderId")] Guid FolderId,
        [property: JsonPropertyName("sourceSystem")] SourceSystem SourceSystem,
        [property: JsonPropertyName("sourceItemKey")] string SourceItemKey,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("docType")] string? DocType,
        [property: JsonPropertyName("contentHash")] string? ContentHash,
        [property: JsonPropertyName("sourceRefJson")] string? SourceRefJson,
        [property: JsonPropertyName("retired")] bool Retired,
        [property: JsonPropertyName("pairedCatalogId")] Guid? PairedCatalogId = null);
}
