using System.Data;
using System.Globalization;
using System.Text.Json;

using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Catalog
{
    /// <summary>Projeto do ConnectUs (nível 1 do catálogo).</summary>
    public sealed record ConnectUsProjectRow(int Id, string Name);

    /// <summary>Metadados de um mapeador do ConnectUs — SEM o corpo (<c>ValueContent</c>).</summary>
    public sealed record ConnectUsMapperRow(int Id, string MapperGuid, int? ProjectId, string Name, DateTime LastUpdateDate);

    /// <summary>
    /// Acesso SOMENTE LEITURA ao ConnectUS_Macgyver (172.31.249.51 é read-only: só SELECT). Abstração
    /// existente para o adaptador ser testável com fake, sem SQL real.
    /// </summary>
    public interface IConnectUsMapperReader
    {
        Task<IReadOnlyList<ConnectUsProjectRow>> ListProjectsAsync(CancellationToken ct);

        /// <summary>Página por chave (<c>Id &gt; afterId</c>, ordenada por <c>Id</c>): sem OFFSET e sem JOIN.</summary>
        Task<IReadOnlyList<ConnectUsMapperRow>> ListMappersAsync(int afterId, int take, CancellationToken ct);

        /// <summary>Corpo (<c>ValueContent</c>) de um mapeador por (MapperGuid, ProjectId), ou <c>null</c>.</summary>
        Task<string?> GetMapperValueContentAsync(string mapperGuid, int? projectId, CancellationToken ct);
    }

    /// <summary>Implementação SQL: apenas SELECT ... WITH (NOLOCK), CommandTimeout curto, config <c>Database:*</c>.</summary>
    public sealed class SqlConnectUsMapperReader : IConnectUsMapperReader
    {
        private const int ListTimeoutSeconds = 20;
        private const int ContentTimeoutSeconds = 15;
        private readonly string _connectionString;

        public SqlConnectUsMapperReader(IConfiguration configuration)
        {
            _connectionString =
                $"Server={configuration["Database:Server"]};Database={configuration["Database:Database"]};" +
                $"User Id={configuration["Database:UserId"]};Password={configuration["Database:Password"]};" +
                "TrustServerCertificate=True;Connect Timeout=10;ApplicationIntent=ReadOnly;";
        }

        public async Task<IReadOnlyList<ConnectUsProjectRow>> ListProjectsAsync(CancellationToken ct)
        {
            var rows = new List<ConnectUsProjectRow>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT [Id], [Name] FROM [tbProject] WITH (NOLOCK) ORDER BY [Id]", conn)
            { CommandTimeout = ListTimeoutSeconds };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new ConnectUsProjectRow(r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            return rows;
        }

        public async Task<IReadOnlyList<ConnectUsMapperRow>> ListMappersAsync(int afterId, int take, CancellationToken ct)
        {
            var rows = new List<ConnectUsMapperRow>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT TOP (@take) [Id], [MapperGuid], [ProjectId], [Name], [LastUpdateDate] " +
                "FROM [tbMapper] WITH (NOLOCK) WHERE [Id] > @after ORDER BY [Id]", conn)
            { CommandTimeout = ListTimeoutSeconds };
            cmd.Parameters.Add("@take", SqlDbType.Int).Value = take;
            cmd.Parameters.Add("@after", SqlDbType.Int).Value = afterId;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new ConnectUsMapperRow(
                    r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                    r.IsDBNull(3) ? "" : r.GetString(3), r.GetDateTime(4)));
            return rows;
        }

        public async Task<string?> GetMapperValueContentAsync(string mapperGuid, int? projectId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT TOP (1) [ValueContent] FROM [tbMapper] WITH (NOLOCK) WHERE [MapperGuid] = @g AND " +
                (projectId.HasValue ? "[ProjectId] = @p" : "[ProjectId] IS NULL"), conn)
            { CommandTimeout = ContentTimeoutSeconds };
            cmd.Parameters.Add("@g", SqlDbType.VarChar, 48).Value = mapperGuid;
            if (projectId.HasValue)
                cmd.Parameters.Add("@p", SqlDbType.Int).Value = projectId.Value;
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is string s ? s : null;
        }
    }

    /// <summary>
    /// Adaptador ConnectUs (issue #633, fase F3). SOMENTE LEITURA, só metadados no sync: pasta = projeto
    /// (<c>sourceProjectKey</c> = <c>tbProject.Id</c>); mapeador sem projeto cai em "Sem projeto" (<c>-</c>).
    /// <c>MapperGuid</c> repete entre projetos, então <c>sourceItemKey</c> = <c>{projectId|-}/{MapperGuid}</c>.
    /// Engine "tcl": o mapeador Sysmiddle é o modelo TCL; o contrato só admite tcl|xsl|xslt.
    /// Sem hash de conteúdo (exigiria ler o <c>ValueContent</c> inteiro); <c>Version</c> = LastUpdateDate.
    /// </summary>
    public sealed class ConnectUsCatalogSource : IMappingCatalogSource
    {
        internal const string NoProjectName = "Sem projeto";
        public const int PageSize = 500;

        private readonly IConnectUsMapperReader _reader;
        private readonly IDecryptionService? _decryption;
        private readonly ILogger<ConnectUsCatalogSource> _logger;

        public ConnectUsCatalogSource(IConnectUsMapperReader reader, ILogger<ConnectUsCatalogSource> logger, IDecryptionService? decryption = null)
        {
            _reader = reader;
            _logger = logger;
            _decryption = decryption;
        }

        public SourceSystem System => SourceSystem.ConnectUs;

        internal static string ProjectKey(int? projectId)
            => projectId.HasValue ? projectId.Value.ToString(CultureInfo.InvariantCulture) : CatalogIdGenerator.NoProjectKey;

        public async Task<MappingCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            // Qualquer falha propaga: o sync marca stale/unavailable e NÃO retira nada.
            var projects = (await _reader.ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);
            var folders = new Dictionary<string, MappingCatalogFolderDto>();
            var items = new List<MappingCatalogItemDto>();

            MappingCatalogFolderDto EnsureFolder(int? projectId)
            {
                var key = ProjectKey(projectId);
                if (folders.TryGetValue(key, out var f))
                    return f;
                var name = projectId.HasValue
                    ? (projects.TryGetValue(projectId.Value, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"Projeto {projectId}")
                    : NoProjectName;
                f = new MappingCatalogFolderDto(CatalogIdGenerator.ForFolder(SourceSystem.ConnectUs, key),
                    SourceSystem.ConnectUs, key, projectId, name, false);
                folders[key] = f;
                return f;
            }

            foreach (var id in projects.Keys)
                EnsureFolder(id);

            var after = 0;
            while (true)
            {
                var page = await _reader.ListMappersAsync(after, PageSize, cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (var m in page)
                {
                    after = Math.Max(after, m.Id);
                    if (string.IsNullOrWhiteSpace(m.MapperGuid))
                        continue;
                    var folder = EnsureFolder(m.ProjectId);
                    var projectKey = ProjectKey(m.ProjectId);
                    var itemKey = $"{projectKey}/{m.MapperGuid}";
                    items.Add(new MappingCatalogItemDto(
                        CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, projectKey, itemKey),
                        folder.FolderId, SourceSystem.ConnectUs, itemKey, MappingCatalogEngine.Tcl,
                        string.IsNullOrWhiteSpace(m.Name) ? m.MapperGuid : m.Name,
                        m.LastUpdateDate.ToString("O", CultureInfo.InvariantCulture), null, null,
                        JsonSerializer.Serialize(new { mapperGuid = m.MapperGuid, projectId = m.ProjectId }), false));
                }
                if (page.Count < PageSize)
                    break;
            }

            _logger.LogInformation("ConnectUs: {Folders} pastas e {Items} mapeadores lidos (somente metadados).", folders.Count, items.Count);
            return new MappingCatalogSnapshot(true, folders.Values.ToList(), items);
        }

        public async Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef sourceRef, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourceRef.SourceRefJson))
                return null;
            string? guid;
            int? projectId = null;
            try
            {
                using var doc = JsonDocument.Parse(sourceRef.SourceRefJson);
                guid = doc.RootElement.GetProperty("mapperGuid").GetString();
                if (doc.RootElement.TryGetProperty("projectId", out var p) && p.ValueKind == JsonValueKind.Number)
                    projectId = p.GetInt32();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "ConnectUs: SourceRefJson inválido para {Key}.", sourceRef.SourceItemKey);
                return null;
            }
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            var raw = await _reader.GetMapperValueContentAsync(guid, projectId, cancellationToken);
            if (string.IsNullOrEmpty(raw))
                return null;
            // ✅ Nunca devolve a cifra como se fosse texto claro (P1.1 do IDecryptionService): sem decryptor
            // ou com falha na descriptografia o conteúdo fica indisponível (null), em vez de expor ValueContent bruto.
            if (_decryption == null)
            {
                _logger.LogWarning("ConnectUs: decryptor indisponível; conteúdo de {Key} não será exposto.", sourceRef.SourceItemKey);
                return null;
            }
            try { return new MappingContent(await _decryption.DecryptContentAsync(raw), MappingCatalogEngine.Tcl); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ConnectUs: falha ao descriptografar {Key}; conteúdo indisponível.", sourceRef.SourceItemKey);
                return null;
            }
        }
    }
}
