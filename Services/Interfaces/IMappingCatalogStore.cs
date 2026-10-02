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

        // ---- Leituras do índice (issue #631) — distinguem "vazio" de "indisponível" via CatalogReadResult ----

        /// <summary>Linhas de <c>tbMappingCatalogSource</c> com contagens de pastas/itens ativos.</summary>
        Task<CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>> ListSourcesAsync(CancellationToken cancellationToken);

        /// <summary>Estado de uma origem (null se nunca sincronizada).</summary>
        Task<CatalogReadResult<MappingCatalogSourceDto?>> GetSourceAsync(SourceSystem sourceSystem, CancellationToken cancellationToken);

        /// <summary>Pastas ativas da origem, ordem <c>NameSort, FolderId</c>. <paramref name="skip"/>/<paramref name="take"/> por offset.</summary>
        Task<CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>> ListFoldersAsync(SourceSystem sourceSystem, int skip, int take, CancellationToken cancellationToken);

        Task<CatalogReadResult<MappingCatalogFolderDto?>> GetFolderAsync(Guid folderId, CancellationToken cancellationToken);

        /// <summary>
        /// Itens da pasta, ordem <c>NameSort, CatalogId</c> (total e estável). <paramref name="engine"/> filtra por motor;
        /// <paramref name="nameQuery"/> é busca por trecho no nome (case/acento-insensível); retirados só com <paramref name="includeRetired"/>.
        /// </summary>
        Task<CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>> ListItemsAsync(
            Guid folderId, string? engine, string? nameQuery, bool includeRetired, int skip, int take, CancellationToken cancellationToken);

        /// <summary>Como <see cref="GetItemAsync"/>, mas distingue "não existe" de "indisponível".</summary>
        Task<CatalogReadResult<MappingCatalogItemDto?>> FindItemAsync(Guid catalogId, CancellationToken cancellationToken);

        // ---- Issue #634 (defaults retrocompatíveis; o store SQL sobrescreve) ----

        /// <summary>
        /// Dos <paramref name="catalogIds"/> informados, quais existem no índice (inclusive retirados). Usado para só
        /// anunciar <c>catalogId</c> em <c>mapping-releases</c> quando o item já foi indexado.
        /// </summary>
        async Task<CatalogReadResult<IReadOnlyCollection<Guid>>> FindExistingItemIdsAsync(IReadOnlyCollection<Guid> catalogIds, CancellationToken cancellationToken)
        {
            var found = new List<Guid>();
            foreach (var id in catalogIds)
            {
                var item = await FindItemAsync(id, cancellationToken);
                if (!item.Available)
                    return CatalogReadResult<IReadOnlyCollection<Guid>>.Unavailable();
                if (item.Value != null)
                    found.Add(id);
            }
            return CatalogReadResult<IReadOnlyCollection<Guid>>.Ok(found);
        }

        /// <summary>
        /// Itens ATIVOS cujo ponteiro de origem (<c>SourceRefJson.mapperGuid</c>) casa com o <c>mapperGuid</c> informado —
        /// base do 409 de ambiguidade da rota antiga por <c>mapperGuid</c> (design D6). O padrão não conhece o campo.
        /// </summary>
        Task<CatalogReadResult<IReadOnlyList<MappingCatalogItemDto>>> FindItemsByMapperGuidAsync(string mapperGuid, CancellationToken cancellationToken)
            => Task.FromResult(CatalogReadResult<IReadOnlyList<MappingCatalogItemDto>>.Ok(Array.Empty<MappingCatalogItemDto>()));

        // ---- Suporte ao sync (issue #632) ----

        /// <summary>Hora UTC do SERVIDOR SQL (mesmo relógio de <c>LastSeenUtc</c>); null se indisponível. Base do corte do RetireUnseen.</summary>
        Task<DateTime?> GetServerUtcNowAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Trava exclusiva por origem (<c>sp_getapplock</c>, dono Session, sem espera) numa conexão dedicada,
        /// mantida aberta até o <c>DisposeAsync</c> do retorno. Null = outra instância já está sincronizando OU
        /// IdentityDatabase indisponível (o motivo é logado).
        /// </summary>
        Task<IAsyncDisposable?> TryAcquireSyncLockAsync(SourceSystem sourceSystem, CancellationToken cancellationToken);
    }
}
