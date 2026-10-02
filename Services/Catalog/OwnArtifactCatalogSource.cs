using System.Text.Json;

using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Catalog
{
    /// <summary>
    /// Adaptador "Own" (issue #630): artefatos auto-gerados em <c>tbGeneratedMapperArtifact</c>
    /// (IdentityDatabase). SOMENTE LEITURA — reaproveita <see cref="IGeneratedMapperArtifactStore"/>
    /// (ListAsync/GetAsync, apenas SELECT); nunca grava na tabela de origem. Só entram candidatos
    /// <c>ready</c> (<c>generating</c> não tem corpo; <c>stale</c> é calculado só em leitura de detalhe).
    /// A tabela não tem projeto: tudo cai na pasta sintética "Sem projeto" (<c>-</c>), design D4.
    /// </summary>
    public sealed class OwnArtifactCatalogSource : IMappingCatalogSource
    {
        internal const string NoProjectName = "Sem projeto";
        private const int PageSize = 200;

        private readonly IGeneratedMapperArtifactStore _store;
        private readonly ILogger<OwnArtifactCatalogSource> _logger;

        public OwnArtifactCatalogSource(IGeneratedMapperArtifactStore store, ILogger<OwnArtifactCatalogSource> logger)
        {
            _store = store;
            _logger = logger;
        }

        public SourceSystem System => SourceSystem.Own;

        public async Task<MappingCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            var folderId = CatalogIdGenerator.ForFolder(SourceSystem.Own, CatalogIdGenerator.NoProjectKey);
            var folder = new MappingCatalogFolderDto(folderId, SourceSystem.Own, CatalogIdGenerator.NoProjectKey, null, NoProjectName, false);
            var items = new List<MappingCatalogItemDto>();

            // Falha em qualquer página propaga: o sync trata como sync falho e não retira nada.
            var total = int.MaxValue;
            for (var skip = 0; skip < total; skip += PageSize)
            {
                var (page, totalCount) = await _store.ListAsync(GeneratedMapperArtifactStatus.Ready, skip, PageSize, cancellationToken);
                total = totalCount;
                if (page.Count == 0)
                    break;
                foreach (var record in page)
                {
                    var key = record.MapperGuid;
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    items.Add(new MappingCatalogItemDto(
                        CatalogIdGenerator.ForItem(SourceSystem.Own, CatalogIdGenerator.NoProjectKey, key),
                        folderId, SourceSystem.Own, key, MappingCatalogEngine.Xslt, key, null, null,
                        record.MapperVoHash, JsonSerializer.Serialize(new { mapperGuid = key }), false));
                }
            }

            _logger.LogInformation("Own: {Items} artefatos ready lidos de tbGeneratedMapperArtifact.", items.Count);
            // Lista vazia legítima (nenhum gerado ainda) NÃO retira nada: sync só retira o que já foi visto
            // e, com 0 itens, o guard genérico do sync também não retira.
            return new MappingCatalogSnapshot(true, [folder], items);
        }

        public async Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef sourceRef, CancellationToken cancellationToken)
        {
            var record = await _store.GetAsync(sourceRef.SourceItemKey, cancellationToken);
            if (record?.Content == null)
                return null;
            return new MappingContent(record.Content, MappingCatalogEngine.Xslt);
        }
    }
}
