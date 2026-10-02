using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IGeneratedMapperArtifactStore"/> — issue #438, ADR
    /// <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c>. Banco DEDICADO do
    /// projeto (<c>IdentityDatabase:*</c>), mesmo padrão ADO.NET cru de <see cref="SqlFieldCorrectionStore"/>.
    /// Tabela autossuficiente, sem FK para <c>tbMapper</c> (esse vive no banco compartilhado
    /// somente-leitura <c>172.31.249.51</c>, ver <c>.claude/rules/security.md</c>).
    /// Issue #635: a chave passou a ser <c>(MapperGuid, ProjectId)</c> (ProjectId nulo = legado).
    /// </summary>
    public sealed class SqlGeneratedMapperArtifactStore : IGeneratedMapperArtifactStore
    {
        private const int ProjectIdMaxLength = 32;
        private const string SelectColumns =
            "MapperGuid, ProjectId, Status, Content, CoverageJson, ValidationBasis, MapperVoHash, CorrelationId, GeneratedAtUtc, UpdatedAtUtc";

        private readonly ILogger<SqlGeneratedMapperArtifactStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlGeneratedMapperArtifactStore(ILogger<SqlGeneratedMapperArtifactStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        /// <summary>Normaliza o ProjectId: vazio/espaço = <c>null</c> (linha legada); trunca no limite da coluna.</summary>
        public static string? NormalizeProjectId(string? projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return null;
            var trimmed = projectId.Trim();
            return trimmed.Length > ProjectIdMaxLength ? trimmed[..ProjectIdMaxLength] : trimmed;
        }

        // Legado (sem projeto): só a linha com ProjectId NULL — não "adivinha" entre projetos.
        public Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken cancellationToken)
            => GetExactAsync(mapperGuid, null, cancellationToken);

        public async Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, string? projectId, CancellationToken cancellationToken)
        {
            var normalized = NormalizeProjectId(projectId);
            var exact = await GetExactAsync(mapperGuid, normalized, cancellationToken);
            // Sem linha exata do projeto: cai na legada (ProjectId NULL). O chamador valida o hash do MapperVo.
            if (exact is null && normalized is not null)
                return await GetExactAsync(mapperGuid, null, cancellationToken);
            return exact;
        }

        private async Task<GeneratedMapperArtifactRecord?> GetExactAsync(string mapperGuid, string? projectId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                $@"SELECT {SelectColumns}
                   FROM dbo.tbGeneratedMapperArtifact
                   WHERE MapperGuid = @MapperGuid AND ProjectKey = @ProjectKey;",
                connection);
            command.Parameters.AddWithValue("@MapperGuid", mapperGuid);
            command.Parameters.AddWithValue("@ProjectKey", projectId ?? string.Empty);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
        }

        public async Task<IReadOnlyList<GeneratedMapperArtifactRecord>> ListByMapperGuidAsync(string mapperGuid, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                $"SELECT {SelectColumns} FROM dbo.tbGeneratedMapperArtifact WHERE MapperGuid = @MapperGuid ORDER BY ProjectKey;",
                connection);
            command.Parameters.AddWithValue("@MapperGuid", mapperGuid);

            var items = new List<GeneratedMapperArtifactRecord>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(Map(reader));
            return items;
        }

        public async Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(
            string? status, int skip, int take, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // WHERE só com fragmento CONSTANTE; o valor do filtro entra por SqlParameter (mesmo
            // padrão do SqlMappingReleaseStore.ListByWorkspaceAsync). Content NÃO é selecionado.
            var whereClause = string.IsNullOrWhiteSpace(status) ? "" : "WHERE Status = @Status";

            using var command = new SqlCommand(
                $@"SELECT MapperGuid, ProjectId, Status, CoverageJson, ValidationBasis, MapperVoHash,
                          CorrelationId, GeneratedAtUtc, UpdatedAtUtc,
                          COUNT(*) OVER() AS TotalCount
                   FROM dbo.tbGeneratedMapperArtifact
                   {whereClause}
                   ORDER BY COALESCE(GeneratedAtUtc, UpdatedAtUtc) DESC, MapperGuid, ProjectKey
                   OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;",
                connection);
            command.Parameters.AddWithValue("@Skip", skip);
            command.Parameters.AddWithValue("@Take", take);
            if (!string.IsNullOrWhiteSpace(status))
                command.Parameters.AddWithValue("@Status", status);

            var items = new List<GeneratedMapperArtifactRecord>();
            var totalCount = 0;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new GeneratedMapperArtifactRecord(
                    reader.GetString(reader.GetOrdinal("MapperGuid")),
                    reader.GetString(reader.GetOrdinal("Status")),
                    null,
                    reader.IsDBNull(reader.GetOrdinal("CoverageJson")) ? null : reader.GetString(reader.GetOrdinal("CoverageJson")),
                    reader.IsDBNull(reader.GetOrdinal("ValidationBasis")) ? null : reader.GetString(reader.GetOrdinal("ValidationBasis")),
                    reader.IsDBNull(reader.GetOrdinal("MapperVoHash")) ? null : reader.GetString(reader.GetOrdinal("MapperVoHash")),
                    reader.IsDBNull(reader.GetOrdinal("CorrelationId")) ? null : reader.GetString(reader.GetOrdinal("CorrelationId")),
                    reader.IsDBNull(reader.GetOrdinal("GeneratedAtUtc")) ? null : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("GeneratedAtUtc")), TimeSpan.Zero),
                    new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")), TimeSpan.Zero),
                    reader.IsDBNull(reader.GetOrdinal("ProjectId")) ? null : reader.GetString(reader.GetOrdinal("ProjectId"))));
                totalCount = reader.GetInt32(reader.GetOrdinal("TotalCount"));
            }

            // Página além do fim devolve 0 linhas (COUNT(*) OVER() some junto) — sem o total real a
            // paginação combinada com as releases ficaria errada; conta à parte nesse caso raro.
            if (items.Count == 0 && skip > 0)
            {
                await reader.CloseAsync();
                using var count = new SqlCommand(
                    $"SELECT COUNT(*) FROM dbo.tbGeneratedMapperArtifact {whereClause};", connection);
                if (!string.IsNullOrWhiteSpace(status))
                    count.Parameters.AddWithValue("@Status", status);
                totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
            }

            return (items, totalCount);
        }

        public Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken cancellationToken)
            => TryBeginGeneratingAsync(mapperGuid, null, correlationId, cancellationToken);

        public async Task<bool> TryBeginGeneratingAsync(string mapperGuid, string? projectId, string correlationId, CancellationToken cancellationToken)
        {
            var project = NormalizeProjectId(projectId);
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Tenta inserir primeiro — se a linha não existe, esta chamada "ganha" a corrida sem
            // precisar de lock explícito (a PK em (MapperGuid, ProjectKey) garante atomicidade no SQL Server).
            try
            {
                using var insert = new SqlCommand(
                    @"INSERT INTO dbo.tbGeneratedMapperArtifact (MapperGuid, ProjectId, Status, CorrelationId, UpdatedAtUtc)
                      VALUES (@MapperGuid, @ProjectId, @Generating, @CorrelationId, SYSUTCDATETIME());",
                    connection);
                insert.Parameters.AddWithValue("@MapperGuid", mapperGuid);
                insert.Parameters.AddWithValue("@ProjectId", (object?)project ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Generating", GeneratedMapperArtifactStatus.Generating);
                insert.Parameters.AddWithValue("@CorrelationId", correlationId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                return true;
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                // Linha já existe (outra chamada chegou primeiro, ou é uma regeração de ready/stale) —
                // só assume se NÃO estiver "generating" agora (WHERE no UPDATE é o que evita a corrida
                // de duas chamadas concorrentes disparando geração duplicada).
                using var update = new SqlCommand(
                    @"UPDATE dbo.tbGeneratedMapperArtifact
                         SET Status = @Generating, CorrelationId = @CorrelationId, UpdatedAtUtc = SYSUTCDATETIME()
                       WHERE MapperGuid = @MapperGuid AND ProjectKey = @ProjectKey AND Status <> @Generating;",
                    connection);
                update.Parameters.AddWithValue("@MapperGuid", mapperGuid);
                update.Parameters.AddWithValue("@ProjectKey", project ?? string.Empty);
                update.Parameters.AddWithValue("@Generating", GeneratedMapperArtifactStatus.Generating);
                update.Parameters.AddWithValue("@CorrelationId", correlationId);
                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                return affected == 1;
            }
        }

        public Task CompleteAsync(
            string mapperGuid, string content, string coverageJson, string validationBasis,
            string mapperVoHash, string correlationId, CancellationToken cancellationToken)
            => CompleteAsync(mapperGuid, null, content, coverageJson, validationBasis, mapperVoHash, correlationId, cancellationToken);

        public async Task CompleteAsync(
            string mapperGuid, string? projectId, string content, string coverageJson, string validationBasis,
            string mapperVoHash, string correlationId, CancellationToken cancellationToken)
        {
            var project = NormalizeProjectId(projectId);
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"UPDATE dbo.tbGeneratedMapperArtifact
                     SET Status = @Ready, Content = @Content, CoverageJson = @CoverageJson,
                         ValidationBasis = @ValidationBasis, MapperVoHash = @MapperVoHash,
                         CorrelationId = @CorrelationId, GeneratedAtUtc = SYSUTCDATETIME(),
                         UpdatedAtUtc = SYSUTCDATETIME()
                   WHERE MapperGuid = @MapperGuid AND ProjectKey = @ProjectKey;",
                connection);
            command.Parameters.AddWithValue("@Ready", GeneratedMapperArtifactStatus.Ready);
            command.Parameters.AddWithValue("@Content", content);
            command.Parameters.AddWithValue("@CoverageJson", coverageJson);
            command.Parameters.AddWithValue("@ValidationBasis", validationBasis);
            command.Parameters.AddWithValue("@MapperVoHash", mapperVoHash);
            command.Parameters.AddWithValue("@CorrelationId", correlationId);
            command.Parameters.AddWithValue("@MapperGuid", mapperGuid);
            command.Parameters.AddWithValue("@ProjectKey", project ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public Task FailAsync(string mapperGuid, CancellationToken cancellationToken)
            => FailAsync(mapperGuid, null, cancellationToken);

        public async Task FailAsync(string mapperGuid, string? projectId, CancellationToken cancellationToken)
        {
            var project = NormalizeProjectId(projectId);
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                using var command = new SqlCommand(
                    "DELETE FROM dbo.tbGeneratedMapperArtifact WHERE MapperGuid = @MapperGuid AND ProjectKey = @ProjectKey;", connection);
                command.Parameters.AddWithValue("@MapperGuid", mapperGuid);
                command.Parameters.AddWithValue("@ProjectKey", project ?? string.Empty);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Best-effort: se nem o rollback conseguir gravar, o mapper fica preso em
                // "generating" até o SQL voltar — próximo GET vê status=generating (não quebra),
                // e uma nova tentativa manual sempre pode ser feita depois.
                _logger.LogWarning(ex, "Falha ao reverter status de geração para o mapper {MapperGuid} — pode ficar preso em 'generating' até o SQL voltar.", mapperGuid);
            }
        }

        private static GeneratedMapperArtifactRecord Map(SqlDataReader reader) => new(
            reader.GetString(reader.GetOrdinal("MapperGuid")),
            reader.GetString(reader.GetOrdinal("Status")),
            reader.IsDBNull(reader.GetOrdinal("Content")) ? null : reader.GetString(reader.GetOrdinal("Content")),
            reader.IsDBNull(reader.GetOrdinal("CoverageJson")) ? null : reader.GetString(reader.GetOrdinal("CoverageJson")),
            reader.IsDBNull(reader.GetOrdinal("ValidationBasis")) ? null : reader.GetString(reader.GetOrdinal("ValidationBasis")),
            reader.IsDBNull(reader.GetOrdinal("MapperVoHash")) ? null : reader.GetString(reader.GetOrdinal("MapperVoHash")),
            reader.IsDBNull(reader.GetOrdinal("CorrelationId")) ? null : reader.GetString(reader.GetOrdinal("CorrelationId")),
            reader.IsDBNull(reader.GetOrdinal("GeneratedAtUtc")) ? null : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("GeneratedAtUtc")), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")), TimeSpan.Zero),
            reader.IsDBNull(reader.GetOrdinal("ProjectId")) ? null : reader.GetString(reader.GetOrdinal("ProjectId")));

        // Issue #635 — migração ADITIVA e idempotente (só IdentityDatabase; nunca Database:*).
        //  * ProjectId NVARCHAR(32) NULL: NULL = linha legada (anterior à migração); nada é reescrito.
        //  * ProjectKey: coluna calculada persistida ISNULL(ProjectId, '') — chave de lookup sem NULL.
        //  * PK passa de (MapperGuid) para (MapperGuid, ProjectKey): linhas existentes viram ProjectKey=''
        //    sem perda; a troca roda em transação com applock e só se a PK ainda for a antiga.
        public static readonly string SchemaDdl = @"
-- Todo o bloco (CREATE + ADD COLUMN + troca de PK) roda sob UM applock transacional: as condições são
-- (re)verificadas DENTRO do lock, então instâncias simultâneas serializam e a 2ª encontra tudo pronto.
BEGIN TRY
    BEGIN TRANSACTION;
    EXEC sp_getapplock @Resource = 'lp-ddl-tbGeneratedMapperArtifact', @LockMode = 'Exclusive',
                       @LockOwner = 'Transaction', @LockTimeout = 30000;

    IF OBJECT_ID('dbo.tbGeneratedMapperArtifact', 'U') IS NULL
    CREATE TABLE dbo.tbGeneratedMapperArtifact (
        MapperGuid NVARCHAR(64) NOT NULL,
        ProjectId NVARCHAR(32) NULL,
        ProjectKey AS (ISNULL(ProjectId, N'')) PERSISTED NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        Content NVARCHAR(MAX) NULL,
        CoverageJson NVARCHAR(MAX) NULL,
        ValidationBasis NVARCHAR(30) NULL,
        MapperVoHash NVARCHAR(64) NULL,
        CorrelationId NVARCHAR(100) NULL,
        GeneratedAtUtc DATETIME2 NULL,
        UpdatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_tbGeneratedMapperArtifact PRIMARY KEY (MapperGuid, ProjectKey)
    );

    IF COL_LENGTH('dbo.tbGeneratedMapperArtifact', 'ProjectId') IS NULL
        EXEC(N'ALTER TABLE dbo.tbGeneratedMapperArtifact ADD ProjectId NVARCHAR(32) NULL;');

    IF COL_LENGTH('dbo.tbGeneratedMapperArtifact', 'ProjectKey') IS NULL
        EXEC(N'ALTER TABLE dbo.tbGeneratedMapperArtifact ADD ProjectKey AS (ISNULL(ProjectId, N'''')) PERSISTED NOT NULL;');

    IF (SELECT COUNT(*) FROM sys.indexes i
          JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
         WHERE i.object_id = OBJECT_ID('dbo.tbGeneratedMapperArtifact') AND i.is_primary_key = 1) = 1
    BEGIN
        DECLARE @pk SYSNAME = (SELECT name FROM sys.key_constraints
                                WHERE parent_object_id = OBJECT_ID('dbo.tbGeneratedMapperArtifact') AND type = 'PK');
        DECLARE @drop NVARCHAR(400) = N'ALTER TABLE dbo.tbGeneratedMapperArtifact DROP CONSTRAINT ' + QUOTENAME(@pk);
        EXEC(@drop);
        EXEC(N'ALTER TABLE dbo.tbGeneratedMapperArtifact ADD CONSTRAINT PK_tbGeneratedMapperArtifact PRIMARY KEY (MapperGuid, ProjectKey);');
    END
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH";

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
