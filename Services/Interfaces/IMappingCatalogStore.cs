using LayoutParserApi.Models.Catalog;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Índice persistente (somente METADADOS) do catálogo unificado de mapeadores — issue #628,
    /// <c>docs/architecture/mapping-catalog-design.md</c> D1/D4/D7. Vive em <c>IdentityDatabase:*</c>;
    /// nunca escreve no banco compartilhado somente-leitura. Todos os métodos DEGRADAM: se o
    /// IdentityDatabase estiver indisponível, logam Warning e devolvem <c>false</c>/<c>null</c>/0
    /// em vez de lançar (o chamador decide expor 503/<c>unavailable</c>).
    /// </summary>
    public interface IMappingCatalogStore
    {
        /// <summary>Upsert idempotente do estado de uma origem (<c>tbMappingCatalogSource</c>).</summary>
        Task<bool> UpsertSourceAsync(MappingCatalogSourceDto source, CancellationToken cancellationToken);

        /// <summary>Upsert idempotente (MERGE por <c>FolderId</c>) de uma pasta. Reaparecer limpa <c>Retired</c>.</summary>
        Task<bool> UpsertFolderAsync(MappingCatalogFolderDto folder, CancellationToken cancellationToken);

        /// <summary>
        /// Upsert idempotente (MERGE por <c>CatalogId</c>) de um item; atualiza <c>LastSeenUtc</c>.
        /// O <c>CatalogId</c> é imutável: nunca é alterado pelo UPDATE.
        /// </summary>
        Task<bool> UpsertItemAsync(MappingCatalogItemDto item, CancellationToken cancellationToken);

        /// <summary>Marca o item como <c>Retired=1</c> (nunca DELETE). Devolve false se não existir/indisponível.</summary>
        Task<bool> RetireItemAsync(Guid catalogId, CancellationToken cancellationToken);

        /// <summary>
        /// Retira os itens da origem com <c>LastSeenUtc &lt; seenBeforeUtc</c> — chamar SÓ após sync
        /// COMPLETO e bem-sucedido (design D7). Devolve quantos foram retirados (0 se indisponível).
        /// </summary>
        Task<int> RetireUnseenAsync(SourceSystem sourceSystem, DateTime seenBeforeUtc, CancellationToken cancellationToken);

        /// <summary>Lê um item por <c>CatalogId</c> (inclusive retirado), ou null.</summary>
        Task<MappingCatalogItemDto?> GetItemAsync(Guid catalogId, CancellationToken cancellationToken);
    }
}
