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

        /// <summary>
        /// <c>catalogId</c> de um artefato próprio (issue #634): linha legada (ProjectId nulo/vazio) usa a pasta "-";
        /// com projeto, a chave do projeto. Mesma regra de <see cref="ReadAsync"/> — fonte única do cálculo.
        /// </summary>
        public static Guid CatalogIdFor(string mapperGuid, string? projectId)
            => CatalogIdGenerator.ForItem(SourceSystem.Own,
                string.IsNullOrWhiteSpace(projectId) ? CatalogIdGenerator.NoProjectKey : projectId.Trim(), mapperGuid);

        public async Task<MappingCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            // Issue #635: linha legada (ProjectId nulo) segue na pasta "Sem projeto" com o MESMO catalogId de
            // antes; linha com ProjectId vai para a pasta do projeto (mesmo mapperGuid em 2 projetos => 2 itens).
            var folderId = CatalogIdGenerator.ForFolder(SourceSystem.Own, CatalogIdGenerator.NoProjectKey);
            var folders = new Dictionary<string, MappingCatalogFolderDto>(StringComparer.Ordinal)
            {
                [CatalogIdGenerator.NoProjectKey] = new MappingCatalogFolderDto(
                    folderId, SourceSystem.Own, CatalogIdGenerator.NoProjectKey, null, NoProjectName, false),
            };
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
                    var projectId = string.IsNullOrWhiteSpace(record.ProjectId) ? null : record.ProjectId.Trim();
                    var projectKey = projectId ?? CatalogIdGenerator.NoProjectKey;
                    if (!folders.TryGetValue(projectKey, out var folder))
                    {
                        folder = new MappingCatalogFolderDto(
                            CatalogIdGenerator.ForFolder(SourceSystem.Own, projectKey), SourceSystem.Own, projectKey,
                            long.TryParse(projectId, out var numericId) ? numericId : null, $"Projeto {projectId}", false);
                        folders[projectKey] = folder;
                    }
                    items.Add(new MappingCatalogItemDto(
                        CatalogIdGenerator.ForItem(SourceSystem.Own, projectKey, key),
                        folder.FolderId, SourceSystem.Own, key, MappingCatalogEngine.Xslt, key, null, null,
                        record.MapperVoHash, JsonSerializer.Serialize(new { mapperGuid = key, projectId }), false));
                }
            }

            _logger.LogInformation("Own: {Items} artefatos ready lidos de tbGeneratedMapperArtifact.", items.Count);
            // Lista vazia legítima (nenhum gerado ainda) NÃO retira nada: sync só retira o que já foi visto
            // e, com 0 itens, o guard genérico do sync também não retira.
            return new MappingCatalogSnapshot(true, folders.Values.ToList(), items);
        }

        public async Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef sourceRef, CancellationToken cancellationToken)
        {
            string? projectId = null;
            if (!string.IsNullOrWhiteSpace(sourceRef.SourceRefJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(sourceRef.SourceRefJson);
                    if (doc.RootElement.TryGetProperty("projectId", out var p) && p.ValueKind == JsonValueKind.String)
                        projectId = p.GetString();
                }
                catch (JsonException)
                {
                    // ponteiro ilegível: trata como legado (sem projeto).
                }
            }

            var record = await _store.GetAsync(sourceRef.SourceItemKey, projectId, cancellationToken);
            if (record?.Content == null)
                return null;
            // O store pode cair na linha legada quando falta a do projeto — para o catálogo isso seria
            // conteúdo de OUTRO item: exige a mesma chave (nunca adivinhar entre projetos).
            var recordProject = string.IsNullOrWhiteSpace(record.ProjectId) ? null : record.ProjectId.Trim();
            var wanted = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
            if (!string.Equals(recordProject, wanted, StringComparison.Ordinal))
                return null;
            return new MappingContent(record.Content, MappingCatalogEngine.Xslt);
        }
    }
}
