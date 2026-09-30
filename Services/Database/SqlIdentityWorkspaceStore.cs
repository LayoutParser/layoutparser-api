using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IIdentityWorkspaceStore"/> — Slice 1 (issue #225/#228).
    /// Usa um **banco SQL Server dedicado** (config <c>IdentityDatabase:*</c>), local à máquina onde
    /// a API roda — NÃO reusa mais o <c>ConnectUS_Macgyver</c> do Sysmiddle. O reuso original causava
    /// erro em produção (FK inválida) porque <c>dbo.tbLpUser</c> já existia como tabela LEGADA do
    /// próprio Sysmiddle, com schema incompatível — o <see cref="EnsureSchemaAsync"/> pulava a criação
    /// (tabela "já existe") e a FK falhava contra o schema errado. Ver
    /// <c>.claude/rules/security.md</c> para o histórico de credenciais compartilhadas que motivou a
    /// separação.
    /// </summary>
    /// <remarks>
    /// As tabelas (prefixo <c>tbLp*</c> para não colidir com nada do host — mesmo em banco próprio,
    /// é defesa em profundidade) são criadas de forma idempotente (<c>IF OBJECT_ID(...) IS NULL</c>)
    /// na primeira chamada de cada instância de processo — não há projeto de migração dedicado nesta
    /// API ainda. A garantia de "não duplicar sob concorrência" É o UNIQUE constraint
    /// (<c>UQ_tbLpExternalIdentity</c>, índice filtrado por workspace pessoal, <c>UQ_tbLpWorkspaceMembership</c>):
    /// o INSERT tenta direto e, se colidir (erro 2601/2627), relê a linha existente — não usa
    /// <c>SELECT</c> prévio como garantia (só como fast-path), porque um <c>SELECT</c> antes do
    /// <c>INSERT</c> tem janela de corrida entre processos.
    /// </remarks>
    public sealed class SqlIdentityWorkspaceStore : IIdentityWorkspaceStore
    {
        private readonly ILogger<SqlIdentityWorkspaceStore> _logger;
        private readonly string _connectionString;

        // Erros de violação de UNIQUE/PRIMARY KEY do SQL Server.
        private static readonly HashSet<int> UniqueViolationErrorNumbers = new() { 2601, 2627 };

        // DDL roda uma vez por processo (não por chamada) — flag estática protegida por lock.
        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlIdentityWorkspaceStore(ILogger<SqlIdentityWorkspaceStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            // ✅ Banco dedicado (não é mais o ConnectUS_Macgyver do Sysmiddle) — credencial separada.
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Fast-path: já existe.
            var existing = await SelectUserIdByExternalIdentityAsync(connection, provider, tenantOrIssuer, subject, cancellationToken);
            if (existing != null)
                return existing.Value;

            var newUserId = Guid.NewGuid();
            using (var tx = connection.BeginTransaction())
            {
                try
                {
                    using (var insertUser = new SqlCommand(
                        "INSERT INTO dbo.tbLpUser (UserId, CreatedAt) VALUES (@UserId, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertUser.Parameters.AddWithValue("@UserId", newUserId);
                        await insertUser.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using (var insertIdentity = new SqlCommand(
                        @"INSERT INTO dbo.tbLpExternalIdentity (ExternalIdentityId, UserId, Provider, TenantOrIssuer, Subject, CreatedAt)
                          VALUES (@ExternalIdentityId, @UserId, @Provider, @TenantOrIssuer, @Subject, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertIdentity.Parameters.AddWithValue("@ExternalIdentityId", Guid.NewGuid());
                        insertIdentity.Parameters.AddWithValue("@UserId", newUserId);
                        insertIdentity.Parameters.AddWithValue("@Provider", provider);
                        insertIdentity.Parameters.AddWithValue("@TenantOrIssuer", tenantOrIssuer);
                        insertIdentity.Parameters.AddWithValue("@Subject", subject);
                        await insertIdentity.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                    return newUserId;
                }
                catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
                {
                    // Corrida perdida: outra requisição criou a ExternalIdentity entre o SELECT e o
                    // INSERT. Não é erro — relê o UserId que já existe (idempotência sob concorrência,
                    // critério de aceite #3 do contrato cross-repo).
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogInformation("Corrida de criação de identidade externa detectada (provider={Provider}); relendo UserId existente.", Services.Logging.LogMessageSanitizer.Sanitize(provider));
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            var afterRace = await SelectUserIdByExternalIdentityAsync(connection, provider, tenantOrIssuer, subject, cancellationToken);
            return afterRace
                ?? throw new InvalidOperationException("Colisão de UNIQUE em tbLpExternalIdentity, mas a releitura não encontrou a linha — estado inconsistente.");
        }

        public async Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var existing = await SelectPersonalWorkspaceAsync(connection, userId, cancellationToken);
            if (existing != null)
                return existing;

            var workspaceId = Guid.NewGuid();
            using (var tx = connection.BeginTransaction())
            {
                try
                {
                    using (var insertWorkspace = new SqlCommand(
                        @"INSERT INTO dbo.tbLpFiscalWorkspace (WorkspaceId, Name, Kind, OwnerUserId, CreatedAt)
                          VALUES (@WorkspaceId, @Name, @Kind, @OwnerUserId, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertWorkspace.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                        insertWorkspace.Parameters.AddWithValue("@Name", "Meu workspace fiscal");
                        insertWorkspace.Parameters.AddWithValue("@Kind", WorkspaceKind.Personal);
                        insertWorkspace.Parameters.AddWithValue("@OwnerUserId", userId);
                        await insertWorkspace.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using (var insertMembership = new SqlCommand(
                        @"INSERT INTO dbo.tbLpWorkspaceMembership (WorkspaceMembershipId, WorkspaceId, UserId, Role, CreatedAt)
                          VALUES (@Id, @WorkspaceId, @UserId, @Role, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertMembership.Parameters.AddWithValue("@Id", Guid.NewGuid());
                        insertMembership.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                        insertMembership.Parameters.AddWithValue("@UserId", userId);
                        insertMembership.Parameters.AddWithValue("@Role", WorkspaceRole.Owner);
                        await insertMembership.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                }
                catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
                {
                    // Corrida perdida: outra requisição já criou o workspace pessoal deste usuário
                    // (índice filtrado UX_tbLpFiscalWorkspace_PersonalOwner). Relê abaixo.
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogInformation("Corrida de criação de workspace pessoal detectada (userId={UserId}); relendo workspace existente.", userId);
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            var afterRace = await SelectPersonalWorkspaceAsync(connection, userId, cancellationToken);
            return afterRace
                ?? throw new InvalidOperationException("Colisão de UNIQUE em tbLpFiscalWorkspace, mas a releitura não encontrou o workspace pessoal — estado inconsistente.");
        }

        public async Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var result = new List<WorkspaceSummary>();
            using var command = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, m.Role, w.CreatedAt
                  FROM dbo.tbLpWorkspaceMembership m
                  JOIN dbo.tbLpFiscalWorkspace w ON w.WorkspaceId = m.WorkspaceId
                  WHERE m.UserId = @UserId
                  ORDER BY w.CreatedAt ASC;",
                connection);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadWorkspaceSummary(reader));

            return result;
        }

        public async Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, m.Role, w.CreatedAt
                  FROM dbo.tbLpWorkspaceMembership m
                  JOIN dbo.tbLpFiscalWorkspace w ON w.WorkspaceId = m.WorkspaceId
                  WHERE m.WorkspaceId = @WorkspaceId AND m.UserId = @UserId;",
                connection);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null; // Sem membership: "não existe" e "não é seu" ficam indistinguíveis daqui pra cima.

            return ReadWorkspaceSummary(reader);
        }

        private static WorkspaceSummary ReadWorkspaceSummary(SqlDataReader reader) => new(
            reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
            reader.GetString(reader.GetOrdinal("Name")),
            reader.GetString(reader.GetOrdinal("Kind")),
            reader.GetString(reader.GetOrdinal("Role")),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero));

        private static async Task<Guid?> SelectUserIdByExternalIdentityAsync(SqlConnection connection, string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                "SELECT UserId FROM dbo.tbLpExternalIdentity WHERE Provider = @Provider AND TenantOrIssuer = @TenantOrIssuer AND Subject = @Subject;",
                connection);
            command.Parameters.AddWithValue("@Provider", provider);
            command.Parameters.AddWithValue("@TenantOrIssuer", tenantOrIssuer);
            command.Parameters.AddWithValue("@Subject", subject);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is Guid guid ? guid : null;
        }

        private static async Task<WorkspaceSummary?> SelectPersonalWorkspaceAsync(SqlConnection connection, Guid userId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, m.Role, w.CreatedAt
                  FROM dbo.tbLpFiscalWorkspace w
                  JOIN dbo.tbLpWorkspaceMembership m ON m.WorkspaceId = w.WorkspaceId AND m.UserId = w.OwnerUserId
                  WHERE w.OwnerUserId = @UserId AND w.Kind = @Kind;",
                connection);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Kind", WorkspaceKind.Personal);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadWorkspaceSummary(reader);
        }

        // ✅ Campo público-de-assembly (não mais `const string` local) para o
        // `FiscalSchemaInitializer` poder disparar o schema de identidade no startup, junto com o
        // de `SqlAiUserSessionStore` (mesmo banco `IdentityDatabase:*`). Autossuficiente — todas as
        // FKs abaixo apontam para tabelas criadas neste mesmo bloco, então não há dependência de
        // ordem com outro store.
        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbLpUser', 'U') IS NULL
CREATE TABLE dbo.tbLpUser (
    UserId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.tbLpExternalIdentity', 'U') IS NULL
CREATE TABLE dbo.tbLpExternalIdentity (
    ExternalIdentityId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    UserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpUser(UserId),
    Provider NVARCHAR(64) NOT NULL,
    TenantOrIssuer NVARCHAR(256) NOT NULL,
    Subject NVARCHAR(256) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbLpExternalIdentity UNIQUE (Provider, TenantOrIssuer, Subject)
);

IF OBJECT_ID('dbo.tbLpFiscalWorkspace', 'U') IS NULL
CREATE TABLE dbo.tbLpFiscalWorkspace (
    WorkspaceId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Name NVARCHAR(256) NOT NULL,
    Kind NVARCHAR(32) NOT NULL,
    OwnerUserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpUser(UserId),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_tbLpFiscalWorkspace_PersonalOwner' AND object_id = OBJECT_ID('dbo.tbLpFiscalWorkspace'))
CREATE UNIQUE INDEX UX_tbLpFiscalWorkspace_PersonalOwner ON dbo.tbLpFiscalWorkspace(OwnerUserId) WHERE Kind = 'personal';

IF OBJECT_ID('dbo.tbLpWorkspaceMembership', 'U') IS NULL
CREATE TABLE dbo.tbLpWorkspaceMembership (
    WorkspaceMembershipId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpFiscalWorkspace(WorkspaceId),
    UserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpUser(UserId),
    Role NVARCHAR(32) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbLpWorkspaceMembership UNIQUE (WorkspaceId, UserId)
);

-- E-mail normalizado (minúsculas) do usuário — atributo de exibição/convite, NUNCA chave de identidade.
IF COL_LENGTH('dbo.tbLpUser', 'Email') IS NULL
    ALTER TABLE dbo.tbLpUser ADD Email NVARCHAR(320) NULL;

-- Índice via EXEC: a coluna acima acabou de ser criada neste mesmo batch (compilação diferida).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbLpUser_Email' AND object_id = OBJECT_ID('dbo.tbLpUser'))
    EXEC('CREATE INDEX IX_tbLpUser_Email ON dbo.tbLpUser(Email) WHERE Email IS NOT NULL');

IF OBJECT_ID('dbo.tbLpWorkspaceInvite', 'U') IS NULL
CREATE TABLE dbo.tbLpWorkspaceInvite (
    InviteId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpFiscalWorkspace(WorkspaceId),
    Email NVARCHAR(320) NOT NULL,
    Role NVARCHAR(32) NOT NULL,
    InvitedByUserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpUser(UserId),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbLpWorkspaceInvite UNIQUE (WorkspaceId, Email)
);

IF OBJECT_ID('dbo.tbLpEmailOutbox', 'U') IS NULL
CREATE TABLE dbo.tbLpEmailOutbox (
    EmailId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    ToEmail NVARCHAR(320) NOT NULL,
    Template NVARCHAR(64) NOT NULL,
    DedupeKey NVARCHAR(128) NOT NULL,
    Subject NVARCHAR(300) NOT NULL,
    Body NVARCHAR(MAX) NOT NULL,
    Status NVARCHAR(16) NOT NULL,
    Attempts INT NOT NULL DEFAULT 0,
    NextAttemptAt DATETIME2 NOT NULL,
    LastError NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    SentAt DATETIME2 NULL
);";

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
