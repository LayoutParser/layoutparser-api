using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;

using System.Data;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IMappingCatalogStore"/> — issue #628. Banco DEDICADO do projeto
    /// (<c>IdentityDatabase:*</c>); NUNCA <c>Database:*</c>/172.31.249.51 (somente leitura, ver
    /// <c>.claude/rules/security.md</c>). Mesmo padrão ADO.NET cru de <see cref="SqlGeneratedMapperArtifactStore"/>.
    /// Só metadados + ponteiro; o corpo TCL/XSL não é copiado. Sem FK para tabelas de outros stores.
    /// </summary>
    public sealed class SqlMappingCatalogStore : IMappingCatalogStore
    {
        private readonly ILogger<SqlMappingCatalogStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlMappingCatalogStore(ILogger<SqlMappingCatalogStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public Task<bool> UpsertSourceAsync(MappingCatalogSourceDto source, CancellationToken cancellationToken)
            => ExecuteAsync("UpsertSource", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"MERGE dbo.tbMappingCatalogSource WITH (HOLDLOCK) AS t
                      USING (SELECT @SourceSystem AS SourceSystem) AS s ON t.SourceSystem = s.SourceSystem
                      WHEN MATCHED THEN UPDATE SET Enabled = @Enabled, LastSyncUtc = @LastSyncUtc,
                           LastStatus = @LastStatus, LastError = @LastError
                      WHEN NOT MATCHED THEN INSERT (SourceSystem, Enabled, LastSyncUtc, LastStatus, LastError)
                           VALUES (@SourceSystem, @Enabled, @LastSyncUtc, @LastStatus, @LastError);",
                    connection);
                Add(command, "@SourceSystem", SqlDbType.NVarChar, 30, source.SourceSystem.ToWireName());
                command.Parameters.Add("@Enabled", SqlDbType.Bit).Value = source.Enabled;
                command.Parameters.Add("@LastSyncUtc", SqlDbType.DateTime2).Value = (object?)source.LastSyncUtc ?? DBNull.Value;
                Add(command, "@LastStatus", SqlDbType.NVarChar, 20, Truncate(source.Status, 20));
                Add(command, "@LastError", SqlDbType.NVarChar, 2000, Truncate(source.LastError, 2000));
                await command.ExecuteNonQueryAsync(ct);
                return true;
            }, false, cancellationToken);

        public Task<bool> UpsertFolderAsync(MappingCatalogFolderDto folder, CancellationToken cancellationToken)
        {
            // ✅ Chave acima do limite do DDL é REJEITADA (truncar quebraria unicidade/idempotência).
            if (folder.SourceProjectKey is { Length: > MaxProjectKey })
            {
                _logger.LogWarning("MappingCatalogStore.UpsertFolder rejeitado: SourceProjectKey excede {Max} caracteres (tamanho {Length}); FolderId {FolderId}.",
                    MaxProjectKey, folder.SourceProjectKey.Length, folder.FolderId);
                return Task.FromResult(false);
            }
            var folderName = Truncate(folder.Name, MaxName) ?? string.Empty;
            return ExecuteAsync("UpsertFolder", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"MERGE dbo.tbMappingCatalogFolder WITH (HOLDLOCK) AS t
                      USING (SELECT @FolderId AS FolderId) AS s ON t.FolderId = s.FolderId
                      WHEN MATCHED THEN UPDATE SET SourceProjectId = @SourceProjectId, Name = @Name,
                           NameSort = @NameSort, Retired = 0
                      WHEN NOT MATCHED THEN INSERT (FolderId, SourceSystem, SourceProjectKey, SourceProjectId, Name, NameSort, Retired)
                           VALUES (@FolderId, @SourceSystem, @SourceProjectKey, @SourceProjectId, @Name, @NameSort, @Retired);",
                    connection);
                command.Parameters.Add("@FolderId", SqlDbType.UniqueIdentifier).Value = folder.FolderId;
                Add(command, "@SourceSystem", SqlDbType.NVarChar, 30, folder.SourceSystem.ToWireName());
                Add(command, "@SourceProjectKey", SqlDbType.NVarChar, MaxProjectKey, folder.SourceProjectKey);
                command.Parameters.Add("@SourceProjectId", SqlDbType.BigInt).Value = (object?)folder.SourceProjectId ?? DBNull.Value;
                Add(command, "@Name", SqlDbType.NVarChar, MaxName, folderName);
                Add(command, "@NameSort", SqlDbType.NVarChar, MaxName, Truncate(NameSort(folderName), MaxName));
                command.Parameters.Add("@Retired", SqlDbType.Bit).Value = folder.Retired;
                await command.ExecuteNonQueryAsync(ct);
                return true;
            }, false, cancellationToken);
        }

        public Task<bool> UpsertItemAsync(MappingCatalogItemDto original, CancellationToken cancellationToken)
        {
            var item = PrepareItem(original, out var rejection);
            if (item is null)
            {
                _logger.LogWarning("MappingCatalogStore.UpsertItem rejeitado: {Reason}; CatalogId {CatalogId}.", rejection, original.CatalogId);
                return Task.FromResult(false);
            }
            return ExecuteAsync("UpsertItem", async (connection, ct) =>
            {
                // ✅ MERGE por CatalogId (chave imutável): o UPDATE nunca toca CatalogId/SourceSystem/
                // SourceItemKey/FolderId. Item que reaparece volta a Retired=0.
                using var command = new SqlCommand(
                    @"MERGE dbo.tbMappingCatalogItem WITH (HOLDLOCK) AS t
                      USING (SELECT @CatalogId AS CatalogId) AS s ON t.CatalogId = s.CatalogId
                      WHEN MATCHED THEN UPDATE SET Engine = @Engine, Name = @Name, NameSort = @NameSort,
                           Version = @Version, DocType = @DocType, ContentHash = @ContentHash,
                           SourceRefJson = @SourceRefJson, PairedCatalogId = @PairedCatalogId,
                           Retired = 0, LastSeenUtc = SYSUTCDATETIME()
                      WHEN NOT MATCHED THEN INSERT
                           (CatalogId, FolderId, SourceSystem, SourceItemKey, Engine, Name, NameSort, Version, DocType,
                            ContentHash, SourceRefJson, PairedCatalogId, Retired, LastSeenUtc)
                           VALUES (@CatalogId, @FolderId, @SourceSystem, @SourceItemKey, @Engine, @Name, @NameSort, @Version, @DocType,
                            @ContentHash, @SourceRefJson, @PairedCatalogId, 0, SYSUTCDATETIME());",
                    connection);
                command.Parameters.Add("@CatalogId", SqlDbType.UniqueIdentifier).Value = item.CatalogId;
                command.Parameters.Add("@FolderId", SqlDbType.UniqueIdentifier).Value = item.FolderId;
                Add(command, "@SourceSystem", SqlDbType.NVarChar, 30, item.SourceSystem.ToWireName());
                Add(command, "@SourceItemKey", SqlDbType.NVarChar, MaxItemKey, item.SourceItemKey);
                Add(command, "@Engine", SqlDbType.NVarChar, 10, item.Engine);
                Add(command, "@Name", SqlDbType.NVarChar, MaxName, item.Name);
                Add(command, "@NameSort", SqlDbType.NVarChar, MaxName, Truncate(NameSort(item.Name), MaxName));
                Add(command, "@Version", SqlDbType.NVarChar, MaxVersion, item.Version);
                Add(command, "@DocType", SqlDbType.NVarChar, MaxDocType, item.DocType);
                Add(command, "@ContentHash", SqlDbType.NVarChar, MaxContentHash, item.ContentHash);
                Add(command, "@SourceRefJson", SqlDbType.NVarChar, -1, item.SourceRefJson);
                command.Parameters.Add("@PairedCatalogId", SqlDbType.UniqueIdentifier).Value = (object?)item.PairedCatalogId ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(ct);
                return true;
            }, false, cancellationToken);
        }

        public Task<bool> RetireItemAsync(Guid catalogId, CancellationToken cancellationToken)
            => ExecuteAsync("RetireItem", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    "UPDATE dbo.tbMappingCatalogItem SET Retired = 1 WHERE CatalogId = @CatalogId;", connection);
                command.Parameters.Add("@CatalogId", SqlDbType.UniqueIdentifier).Value = catalogId;
                return await command.ExecuteNonQueryAsync(ct) > 0;
            }, false, cancellationToken);

        public Task<int> RetireUnseenAsync(SourceSystem sourceSystem, DateTime seenBeforeUtc, CancellationToken cancellationToken)
            => ExecuteAsync("RetireUnseen", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"UPDATE dbo.tbMappingCatalogItem SET Retired = 1
                      WHERE SourceSystem = @SourceSystem AND Retired = 0 AND LastSeenUtc < @SeenBefore;", connection);
                Add(command, "@SourceSystem", SqlDbType.NVarChar, 30, sourceSystem.ToWireName());
                command.Parameters.Add("@SeenBefore", SqlDbType.DateTime2).Value = seenBeforeUtc;
                return await command.ExecuteNonQueryAsync(ct);
            }, 0, cancellationToken);

        public Task<MappingCatalogItemDto?> GetItemAsync(Guid catalogId, CancellationToken cancellationToken)
            => ExecuteAsync<MappingCatalogItemDto?>("GetItem", (c, ct) => QueryItemAsync(c, catalogId, ct), null, cancellationToken);

        public Task<CatalogReadResult<MappingCatalogItemDto?>> FindItemAsync(Guid catalogId, CancellationToken cancellationToken)
            => ExecuteAsync("FindItem", async (c, ct) => CatalogReadResult<MappingCatalogItemDto?>.Ok(await QueryItemAsync(c, catalogId, ct)),
                CatalogReadResult<MappingCatalogItemDto?>.Unavailable(), cancellationToken);

        private const string ItemColumns = @"CatalogId, FolderId, SourceSystem, SourceItemKey, Engine, Name, Version, DocType,
                             ContentHash, SourceRefJson, PairedCatalogId, Retired";

        private static async Task<MappingCatalogItemDto?> QueryItemAsync(SqlConnection connection, Guid catalogId, CancellationToken ct)
        {
            using var command = new SqlCommand($"SELECT {ItemColumns} FROM dbo.tbMappingCatalogItem WHERE CatalogId = @CatalogId;", connection);
            command.Parameters.Add("@CatalogId", SqlDbType.UniqueIdentifier).Value = catalogId;
            using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
        }

        private static MappingCatalogItemDto ReadItem(SqlDataReader reader)
        {
            string? Str(string c) => reader.IsDBNull(reader.GetOrdinal(c)) ? null : reader.GetString(reader.GetOrdinal(c));
            SourceSystemExtensions.TryParseWireName(reader.GetString(reader.GetOrdinal("SourceSystem")), out var system);
            var paired = reader.GetOrdinal("PairedCatalogId");
            return new MappingCatalogItemDto(
                reader.GetGuid(reader.GetOrdinal("CatalogId")),
                reader.GetGuid(reader.GetOrdinal("FolderId")),
                system,
                reader.GetString(reader.GetOrdinal("SourceItemKey")),
                reader.GetString(reader.GetOrdinal("Engine")),
                reader.GetString(reader.GetOrdinal("Name")),
                Str("Version"), Str("DocType"), Str("ContentHash"), Str("SourceRefJson"),
                reader.GetBoolean(reader.GetOrdinal("Retired")),
                reader.IsDBNull(paired) ? null : reader.GetGuid(paired));
        }

        public Task<CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>> ListSourcesAsync(CancellationToken cancellationToken)
            => ExecuteAsync("ListSources", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"SELECT s.SourceSystem, s.Enabled, s.LastSyncUtc, s.LastStatus, s.LastError,
                             (SELECT COUNT(*) FROM dbo.tbMappingCatalogFolder f WHERE f.SourceSystem = s.SourceSystem AND f.Retired = 0) AS FolderCount,
                             (SELECT COUNT(*) FROM dbo.tbMappingCatalogItem i WHERE i.SourceSystem = s.SourceSystem AND i.Retired = 0) AS ItemCount
                      FROM dbo.tbMappingCatalogSource s ORDER BY s.SourceSystem;", connection);
                var list = new List<MappingCatalogSourceSummary>();
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (!SourceSystemExtensions.TryParseWireName(reader.GetString(0), out var system))
                        continue; // origem desconhecida a esta versão: não quebra a listagem
                    list.Add(new MappingCatalogSourceSummary(system, reader.GetBoolean(1),
                        reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6)));
                }
                return CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>.Ok(list);
            }, CatalogReadResult<IReadOnlyList<MappingCatalogSourceSummary>>.Unavailable(), cancellationToken);

        public Task<CatalogReadResult<MappingCatalogSourceDto?>> GetSourceAsync(SourceSystem sourceSystem, CancellationToken cancellationToken)
            => ExecuteAsync("GetSource", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    "SELECT Enabled, LastSyncUtc, LastStatus, LastError FROM dbo.tbMappingCatalogSource WHERE SourceSystem = @SourceSystem;", connection);
                Add(command, "@SourceSystem", SqlDbType.NVarChar, 30, sourceSystem.ToWireName());
                using var reader = await command.ExecuteReaderAsync(ct);
                MappingCatalogSourceDto? dto = null;
                if (await reader.ReadAsync(ct))
                    dto = new MappingCatalogSourceDto(sourceSystem, reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                        reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
                return CatalogReadResult<MappingCatalogSourceDto?>.Ok(dto);
            }, CatalogReadResult<MappingCatalogSourceDto?>.Unavailable(), cancellationToken);

        public Task<CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>> ListFoldersAsync(
            SourceSystem sourceSystem, int skip, int take, CancellationToken cancellationToken)
            => ExecuteAsync("ListFolders", async (connection, ct) =>
            {
                int total;
                using (var count = new SqlCommand("SELECT COUNT(*) FROM dbo.tbMappingCatalogFolder WHERE SourceSystem = @S AND Retired = 0;", connection))
                {
                    Add(count, "@S", SqlDbType.NVarChar, 30, sourceSystem.ToWireName());
                    total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
                }
                using var command = new SqlCommand(
                    @"SELECT FolderId, SourceProjectKey, SourceProjectId, Name, Retired FROM dbo.tbMappingCatalogFolder
                      WHERE SourceSystem = @S AND Retired = 0 ORDER BY NameSort, FolderId
                      OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;", connection);
                Add(command, "@S", SqlDbType.NVarChar, 30, sourceSystem.ToWireName());
                command.Parameters.Add("@Skip", SqlDbType.Int).Value = skip;
                command.Parameters.Add("@Take", SqlDbType.Int).Value = take;
                var list = new List<MappingCatalogFolderDto>();
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    list.Add(new MappingCatalogFolderDto(reader.GetGuid(0), sourceSystem, reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.GetBoolean(4)));
                return CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>.Ok(new(list, total));
            }, CatalogReadResult<MappingCatalogPage<MappingCatalogFolderDto>>.Unavailable(), cancellationToken);

        public Task<CatalogReadResult<MappingCatalogFolderDto?>> GetFolderAsync(Guid folderId, CancellationToken cancellationToken)
            => ExecuteAsync("GetFolder", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    "SELECT SourceSystem, SourceProjectKey, SourceProjectId, Name, Retired FROM dbo.tbMappingCatalogFolder WHERE FolderId = @F;", connection);
                command.Parameters.Add("@F", SqlDbType.UniqueIdentifier).Value = folderId;
                using var reader = await command.ExecuteReaderAsync(ct);
                MappingCatalogFolderDto? dto = null;
                if (await reader.ReadAsync(ct) && SourceSystemExtensions.TryParseWireName(reader.GetString(0), out var system))
                    dto = new MappingCatalogFolderDto(folderId, system, reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.GetBoolean(4));
                return CatalogReadResult<MappingCatalogFolderDto?>.Ok(dto);
            }, CatalogReadResult<MappingCatalogFolderDto?>.Unavailable(), cancellationToken);

        public Task<CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>> ListItemsAsync(
            Guid folderId, string? engine, string? nameQuery, bool includeRetired, int skip, int take, CancellationToken cancellationToken)
            => ExecuteAsync("ListItems", async (connection, ct) =>
            {
                // WHERE só com fragmentos CONSTANTES; valores sempre por SqlParameter.
                const string where = @"FolderId = @F AND (@IncludeRetired = 1 OR Retired = 0)
                                       AND (@Engine IS NULL OR Engine = @Engine) AND (@Q IS NULL OR NameSort LIKE @Q ESCAPE '')";
                void Bind(SqlCommand c)
                {
                    c.Parameters.Add("@F", SqlDbType.UniqueIdentifier).Value = folderId;
                    c.Parameters.Add("@IncludeRetired", SqlDbType.Bit).Value = includeRetired;
                    Add(c, "@Engine", SqlDbType.NVarChar, 10, engine);
                    var q = string.IsNullOrWhiteSpace(nameQuery) ? null
                        : "%" + NameSort(nameQuery).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
                    Add(c, "@Q", SqlDbType.NVarChar, MaxName + 10, q);
                }
                int total;
                using (var count = new SqlCommand($"SELECT COUNT(*) FROM dbo.tbMappingCatalogItem WHERE {where};", connection))
                {
                    Bind(count);
                    total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
                }
                using var command = new SqlCommand(
                    $@"SELECT {ItemColumns} FROM dbo.tbMappingCatalogItem WHERE {where}
                       ORDER BY NameSort, CatalogId OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;", connection);
                Bind(command);
                command.Parameters.Add("@Skip", SqlDbType.Int).Value = skip;
                command.Parameters.Add("@Take", SqlDbType.Int).Value = take;
                var list = new List<MappingCatalogItemDto>();
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    list.Add(ReadItem(reader));
                return CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>.Ok(new(list, total));
            }, CatalogReadResult<MappingCatalogPage<MappingCatalogItemDto>>.Unavailable(), cancellationToken);

        /// <summary>Abre conexão + garante schema; qualquer falha (exceto cancelamento) degrada ao valor padrão.</summary>
        private async Task<T> ExecuteAsync<T>(string operation, Func<SqlConnection, CancellationToken, Task<T>> action,
            T fallback, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);
                return await action(connection, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Degrada: IdentityDatabase fora não derruba o chamador (o catálogo vira "unavailable").
                _logger.LogWarning(ex, "MappingCatalogStore.{Operation} falhou — IdentityDatabase indisponível ou erro SQL; degradando.", operation);
                return fallback;
            }
        }

        // Limites do DDL (SchemaDdl). Chaves (SourceItemKey/SourceProjectKey) NÃO são truncadas.
        internal const int MaxItemKey = 400, MaxProjectKey = 200, MaxName = 300, MaxVersion = 50, MaxDocType = 100, MaxContentHash = 64;

        /// <summary>
        /// Valida/normaliza o item contra os tamanhos do DDL. Política: chave (<c>SourceItemKey</c>) acima do
        /// limite é REJEITADA (retorna null + motivo) — truncar quebraria a unicidade e a idempotência do
        /// upsert. Campos descritivos (Name, Version, DocType, ContentHash) são TRUNCADOS.
        /// </summary>
        public static MappingCatalogItemDto? PrepareItem(MappingCatalogItemDto item, out string? rejection)
        {
            rejection = null;
            if (item.SourceItemKey is { Length: > MaxItemKey })
            {
                rejection = $"SourceItemKey excede {MaxItemKey} caracteres (tamanho {item.SourceItemKey.Length})";
                return null;
            }
            return item with
            {
                Name = Truncate(item.Name, MaxName) ?? string.Empty,
                Version = Truncate(item.Version, MaxVersion),
                DocType = Truncate(item.DocType, MaxDocType),
                ContentHash = Truncate(item.ContentHash, MaxContentHash),
            };
        }

        private static void Add(SqlCommand command, string name, SqlDbType type, int size, string? value)
            => command.Parameters.Add(name, type, size).Value = (object?)value ?? DBNull.Value;

        private static string? Truncate(string? value, int max)
            => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

        /// <summary>Chave de ordenação estável: minúsculas invariantes, sem acentos (design D4).</summary>
        public static string NameSort(string name)
        {
            var decomposed = (name ?? string.Empty).Trim().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        }

        // ✅ DDL SOMENTE em IdentityDatabase. Sem FK entre as tabelas (autossuficientes; consistência
        // garantida pelo sync) e sem nenhuma coluna de corpo TCL/XSL — só metadados + SourceRefJson.
        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbMappingCatalogSource', 'U') IS NULL
CREATE TABLE dbo.tbMappingCatalogSource (
    SourceSystem NVARCHAR(30) NOT NULL PRIMARY KEY,
    Enabled BIT NOT NULL DEFAULT 1,
    LastSyncUtc DATETIME2 NULL,
    LastStatus NVARCHAR(20) NOT NULL DEFAULT 'ok',
    LastError NVARCHAR(2000) NULL
);
IF OBJECT_ID('dbo.tbMappingCatalogFolder', 'U') IS NULL
CREATE TABLE dbo.tbMappingCatalogFolder (
    FolderId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    SourceSystem NVARCHAR(30) NOT NULL,
    SourceProjectKey NVARCHAR(200) NOT NULL,
    SourceProjectId BIGINT NULL,
    Name NVARCHAR(300) NOT NULL,
    NameSort NVARCHAR(300) NOT NULL,
    Retired BIT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_tbMappingCatalogFolder_Source_Project UNIQUE (SourceSystem, SourceProjectKey)
);
IF OBJECT_ID('dbo.tbMappingCatalogItem', 'U') IS NULL
CREATE TABLE dbo.tbMappingCatalogItem (
    CatalogId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    FolderId UNIQUEIDENTIFIER NOT NULL,
    SourceSystem NVARCHAR(30) NOT NULL,
    SourceItemKey NVARCHAR(400) NOT NULL,
    Engine NVARCHAR(10) NOT NULL,
    Name NVARCHAR(300) NOT NULL,
    NameSort NVARCHAR(300) NOT NULL,
    Version NVARCHAR(50) NULL,
    DocType NVARCHAR(100) NULL,
    ContentHash NVARCHAR(64) NULL,
    SourceRefJson NVARCHAR(MAX) NULL,
    PairedCatalogId UNIQUEIDENTIFIER NULL,
    Retired BIT NOT NULL DEFAULT 0,
    LastSeenUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbMappingCatalogItem_Source_Folder_Key UNIQUE (SourceSystem, FolderId, SourceItemKey)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingCatalogItem_Folder_NameSort' AND object_id = OBJECT_ID('dbo.tbMappingCatalogItem'))
CREATE INDEX IX_tbMappingCatalogItem_Folder_NameSort ON dbo.tbMappingCatalogItem (FolderId, NameSort, CatalogId);";

        internal static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            if (_schemaEnsured)
                return;

            await _schemaLock.WaitAsync(cancellationToken);
            try
            {
                if (_schemaEnsured)
                    return;

                using var command = new SqlCommand(SchemaDdl, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                _schemaEnsured = true;
            }
            finally
            {
                _schemaLock.Release();
            }
        }
    }
}
