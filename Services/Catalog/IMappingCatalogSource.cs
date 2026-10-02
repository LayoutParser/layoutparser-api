using LayoutParserApi.Models.Catalog;

namespace LayoutParserApi.Services.Catalog
{
    /// <summary>
    /// Ponteiro opaco para o conteúdo de um item na origem (nunca o corpo). Montado a partir do que o
    /// índice persistiu (<c>SourceItemKey</c> + <c>SourceRefJson</c>).
    /// </summary>
    public sealed record MappingCatalogSourceRef(string SourceItemKey, string? SourceRefJson);

    /// <summary>Corpo de um mapeador buscado sob demanda na origem (nunca persistido no índice).</summary>
    public sealed record MappingContent(string Content, string Engine);

    /// <summary>
    /// Fotografia de uma origem (somente metadados). <see cref="Complete"/> = a leitura cobriu a origem
    /// inteira; só então o sync pode retirar itens não vistos (design D7). Na dúvida, <c>false</c>.
    /// </summary>
    public sealed record MappingCatalogSnapshot(
        bool Complete,
        IReadOnlyList<MappingCatalogFolderDto> Folders,
        IReadOnlyList<MappingCatalogItemDto> Items);

    /// <summary>
    /// Adaptador de uma origem do catálogo unificado (issue #629, desenho D3). Um por
    /// <see cref="SourceSystem"/>, registrado por DI. Somente leitura da origem.
    /// </summary>
    public interface IMappingCatalogSource
    {
        SourceSystem System { get; }

        /// <summary>Lê projetos/pastas + itens (metadados). Pode lançar; o sync trata como falha e não retira nada.</summary>
        Task<MappingCatalogSnapshot> ReadAsync(CancellationToken cancellationToken);

        /// <summary>Busca o corpo de um item na origem, ou <c>null</c> se não existir mais.</summary>
        Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef sourceRef, CancellationToken cancellationToken);
    }
}
