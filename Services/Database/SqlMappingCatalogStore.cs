using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;

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
                    @"MERGE dbo.tbMappingCatalogSource AS t
                      USING (SELECT @SourceSystem AS SourceSystem) AS s ON t.SourceSystem = s.SourceSystem
                      WHEN MATCHED THEN UPDATE SET Enabled = @Enabled, LastSyncUtc = @LastSyncUtc,
                           LastStatus = @LastStatus, LastError = @LastError
                      WHEN NOT MATCHED THEN INSERT (SourceSystem, Enabled, LastSyncUtc, LastStatus, LastError)
                           VALUES (@SourceSystem, @Enabled, @LastSyncUtc, @LastStatus, @LastError);",
                    connection);
                command.Parameters.AddWithValue("@SourceSystem", source.SourceSystem.ToWireName());
                command.Parameters.AddWithValue("@Enabled", source.Enabled);
                command.Parameters.AddWithValue("@LastSyncUtc", (object?)source.LastSyncUtc ?? DBNull.Value);
                command.Parameters.AddWithValue("@LastStatus", source.Status);
                command.Parameters.AddWithValue("@LastError", (object?)Truncate(source.LastError, 2000) ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
                return true;
            }, false, cancellationToken);

        public Task<bool> UpsertFolderAsync(MappingCatalogFolderDto folder, CancellationToken cancellationToken)
            => ExecuteAsync("UpsertFolder", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"MERGE dbo.tbMappingCatalogFolder AS t
                      USING (SELECT @FolderId AS FolderId) AS s ON t.FolderId = s.FolderId
                      WHEN MATCHED THEN UPDATE SET SourceProjectId = @SourceProjectId, Name = @Name,
                           NameSort = @NameSort, Retired = 0
                      WHEN NOT MATCHED THEN INSERT (FolderId, SourceSystem, SourceProjectKey, SourceProjectId, Name, NameSort, Retired)
                           VALUES (@FolderId, @SourceSystem, @SourceProjectKey, @SourceProjectId, @Name, @NameSort, @Retired);",
                    connection);
                command.Parameters.AddWithValue("@FolderId", folder.FolderId);
                command.Parameters.AddWithValue("@SourceSystem", folder.SourceSystem.ToWireName());
                command.Parameters.AddWithValue("@SourceProjectKey", folder.SourceProjectKey);
                command.Parameters.AddWithValue("@SourceProjectId", (object?)folder.SourceProjectId ?? DBNull.Value);
                command.Parameters.AddWithValue("@Name", folder.Name);
                command.Parameters.AddWithValue("@NameSort", NameSort(folder.Name));
                command.Parameters.AddWithValue("@Retired", folder.Retired);
                await command.ExecuteNonQueryAsync(ct);
                return true;
            }, false, cancellationToken);

        public Task<bool> UpsertItemAsync(MappingCatalogItemDto item, CancellationToken cancellationToken)
            => ExecuteAsync("UpsertItem", async (connection, ct) =>
            {
                // ✅ MERGE por CatalogId (chave imutável): o UPDATE nunca toca CatalogId/SourceSystem/
                // SourceItemKey/FolderId. Item que reaparece volta a Retired=0.
                using var command = new SqlCommand(
                    @"MERGE dbo.tbMappingCatalogItem AS t
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
                command.Parameters.AddWithValue("@CatalogId", item.CatalogId);
                command.Parameters.AddWithValue("@FolderId", item.FolderId);
                command.Parameters.AddWithValue("@SourceSystem", item.SourceSystem.ToWireName());
                command.Parameters.AddWithValue("@SourceItemKey", item.SourceItemKey);
                command.Parameters.AddWithValue("@Engine", item.Engine);
                command.Parameters.AddWithValue("@Name", item.Name);
                command.Parameters.AddWithValue("@NameSort", NameSort(item.Name));
                command.Parameters.AddWithValue("@Version", (object?)item.Version ?? DBNull.Value);
                command.Parameters.AddWithValue("@DocType", (object?)item.DocType ?? DBNull.Value);
                command.Parameters.AddWithValue("@ContentHash", (object?)item.ContentHash ?? DBNull.Value);
                command.Parameters.AddWithValue("@SourceRefJson", (object?)item.SourceRefJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@PairedCatalogId", (object?)item.PairedCatalogId ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
                return true;
            }, false, cancellationToken);

        public Task<bool> RetireItemAsync(Guid catalogId, CancellationToken cancellationToken)
            => ExecuteAsync("RetireItem", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    "UPDATE dbo.tbMappingCatalogItem SET Retired = 1 WHERE CatalogId = @CatalogId;", connection);
                command.Parameters.AddWithValue("@CatalogId", catalogId);
                return await command.ExecuteNonQueryAsync(ct) > 0;
            }, false, cancellationToken);

        public Task<int> RetireUnseenAsync(SourceSystem sourceSystem, DateTime seenBeforeUtc, CancellationToken cancellationToken)
            => ExecuteAsync("RetireUnseen", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"UPDATE dbo.tbMappingCatalogItem SET Retired = 1
                      WHERE SourceSystem = @SourceSystem AND Retired = 0 AND LastSeenUtc < @SeenBefore;", connection);
                command.Parameters.AddWithValue("@SourceSystem", sourceSystem.ToWireName());
                command.Parameters.AddWithValue("@SeenBefore", seenBeforeUtc);
                return await command.ExecuteNonQueryAsync(ct);
            }, 0, cancellationToken);

        public Task<MappingCatalogItemDto?> GetItemAsync(Guid catalogId, CancellationToken cancellationToken)
            => ExecuteAsync<MappingCatalogItemDto?>("GetItem", async (connection, ct) =>
            {
                using var command = new SqlCommand(
                    @"SELECT CatalogId, FolderId, SourceSystem, SourceItemKey, Engine, Name, Version, DocType,
                             ContentHash, SourceRefJson, PairedCatalogId, Retired
                      FROM dbo.tbMappingCatalogItem WHERE CatalogId = @CatalogId;", connection);
                command.Parameters.AddWithValue("@CatalogId", catalogId);
                using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return null;

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
            }, null, cancellationToken);

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
