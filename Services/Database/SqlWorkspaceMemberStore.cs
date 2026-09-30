using System.Data;

using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IWorkspaceMemberStore"/>. Mesmo banco dedicado do
    /// <see cref="SqlIdentityWorkspaceStore"/> (<c>IdentityDatabase:*</c>) — nunca o SQL compartilhado
    /// do Sysmiddle (ver <c>.claude/rules/security.md</c>). Regras de "último admin" e de unicidade são
    /// aplicadas dentro de transação (UPDLOCK) e reforçadas por UNIQUE no banco.
    /// </summary>
    public sealed class SqlWorkspaceMemberStore : IWorkspaceMemberStore
    {
        private static readonly HashSet<int> UniqueViolationErrorNumbers = new() { 2601, 2627 };

        private readonly ILogger<SqlWorkspaceMemberStore> _logger;
        private readonly string _connectionString;

        public SqlWorkspaceMemberStore(ILogger<SqlWorkspaceMemberStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await SqlIdentityWorkspaceStore.EnsureSchemaAsync(connection, cancellationToken);
            return connection;
        }

        public async Task SyncEmailAndRedeemInvitesAsync(Guid userId, string email, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction();
            try
            {
                using (var update = new SqlCommand(
                    "UPDATE dbo.tbLpUser SET Email = @Email WHERE UserId = @UserId AND (Email IS NULL OR Email <> @Email);",
                    connection, tx))
                {
                    update.Parameters.AddWithValue("@Email", email);
                    update.Parameters.AddWithValue("@UserId", userId);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                // Convite pendente vira membership no primeiro login com o e-mail convidado.
                using (var redeem = new SqlCommand(
                    @"INSERT INTO dbo.tbLpWorkspaceMembership (WorkspaceMembershipId, WorkspaceId, UserId, Role, CreatedAt)
                      SELECT NEWID(), i.WorkspaceId, @UserId, i.Role, SYSUTCDATETIME()
                      FROM dbo.tbLpWorkspaceInvite i
                      WHERE i.Email = @Email
                        AND NOT EXISTS (SELECT 1 FROM dbo.tbLpWorkspaceMembership m WHERE m.WorkspaceId = i.WorkspaceId AND m.UserId = @UserId);
                      DELETE FROM dbo.tbLpWorkspaceInvite WHERE Email = @Email;",
                    connection, tx))
                {
                    redeem.Parameters.AddWithValue("@UserId", userId);
                    redeem.Parameters.AddWithValue("@Email", email);
                    await redeem.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<IReadOnlyList<WorkspaceMemberInfo>> ListAsync(Guid workspaceId, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            var result = new List<WorkspaceMemberInfo>();

            using var command = new SqlCommand(
                @"SELECT m.UserId AS Id, u.Email, m.Role, 'active' AS Status, m.CreatedAt
                  FROM dbo.tbLpWorkspaceMembership m
                  JOIN dbo.tbLpUser u ON u.UserId = m.UserId
                  WHERE m.WorkspaceId = @WorkspaceId
                  UNION ALL
                  SELECT i.InviteId, i.Email, i.Role, 'pending', i.CreatedAt
                  FROM dbo.tbLpWorkspaceInvite i
                  WHERE i.WorkspaceId = @WorkspaceId
                  ORDER BY CreatedAt ASC;",
                connection);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadMember(reader));

            return result;
        }

        public async Task<AddMemberResult> AddAsync(Guid workspaceId, string email, string role, Guid invitedByUserId, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                // Usuários que já logaram com esse e-mail (pode haver mais de uma identidade externa).
                var userIds = new List<Guid>();
                using (var find = new SqlCommand("SELECT UserId FROM dbo.tbLpUser WHERE Email = @Email;", connection, tx))
                {
                    find.Parameters.AddWithValue("@Email", email);
                    using var reader = await find.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        userIds.Add(reader.GetGuid(0));
                }

                AddMemberResult result;
                if (userIds.Count > 0)
                {
                    var added = 0;
                    foreach (var uid in userIds)
                    {
                        using var insert = new SqlCommand(
                            @"IF NOT EXISTS (SELECT 1 FROM dbo.tbLpWorkspaceMembership WHERE WorkspaceId = @WorkspaceId AND UserId = @UserId)
                              BEGIN
                                INSERT INTO dbo.tbLpWorkspaceMembership (WorkspaceMembershipId, WorkspaceId, UserId, Role, CreatedAt)
                                VALUES (NEWID(), @WorkspaceId, @UserId, @Role, SYSUTCDATETIME());
                                SELECT 1;
                              END
                              ELSE SELECT 0;",
                            connection, tx);
                        insert.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                        insert.Parameters.AddWithValue("@UserId", uid);
                        insert.Parameters.AddWithValue("@Role", role);
                        if ((int)(await insert.ExecuteScalarAsync(cancellationToken))! == 1)
                            added++;
                    }

                    var kind = added > 0 ? AddMemberKind.Added : AddMemberKind.AlreadyMember;
                    // Remove convite pendente redundante para o mesmo e-mail, se houver.
                    using (var cleanup = new SqlCommand("DELETE FROM dbo.tbLpWorkspaceInvite WHERE WorkspaceId = @WorkspaceId AND Email = @Email;", connection, tx))
                    {
                        cleanup.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                        cleanup.Parameters.AddWithValue("@Email", email);
                        await cleanup.ExecuteNonQueryAsync(cancellationToken);
                    }

                    result = new AddMemberResult(kind, new WorkspaceMemberInfo(userIds[0], null, email, role, "active", DateTimeOffset.UtcNow));
                }
                else
                {
                    var inviteId = Guid.NewGuid();
                    using var invite = new SqlCommand(
                        @"IF NOT EXISTS (SELECT 1 FROM dbo.tbLpWorkspaceInvite WHERE WorkspaceId = @WorkspaceId AND Email = @Email)
                          BEGIN
                            INSERT INTO dbo.tbLpWorkspaceInvite (InviteId, WorkspaceId, Email, Role, InvitedByUserId, CreatedAt)
                            VALUES (@InviteId, @WorkspaceId, @Email, @Role, @InvitedBy, SYSUTCDATETIME());
                            SELECT 1;
                          END
                          ELSE SELECT 0;",
                        connection, tx);
                    invite.Parameters.AddWithValue("@InviteId", inviteId);
                    invite.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                    invite.Parameters.AddWithValue("@Email", email);
                    invite.Parameters.AddWithValue("@Role", role);
                    invite.Parameters.AddWithValue("@InvitedBy", invitedByUserId);
                    var created = (int)(await invite.ExecuteScalarAsync(cancellationToken))! == 1;

                    result = new AddMemberResult(
                        created ? AddMemberKind.Invited : AddMemberKind.AlreadyInvited,
                        new WorkspaceMemberInfo(inviteId, null, email, role, "pending", DateTimeOffset.UtcNow));
                }

                await tx.CommitAsync(cancellationToken);
                return result;
            }
            catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogInformation("Corrida ao adicionar membro ao workspace {WorkspaceId}; tratando como já existente.", workspaceId);
                return new AddMemberResult(AddMemberKind.AlreadyMember, new WorkspaceMemberInfo(Guid.Empty, null, email, role, "active", DateTimeOffset.UtcNow));
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<MemberChangeOutcome> ChangeRoleAsync(Guid workspaceId, Guid memberOrInviteId, string role, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var current = await SelectRoleAsync(connection, tx, workspaceId, memberOrInviteId, cancellationToken);
                if (current == null)
                {
                    // Convite pendente: só troca o papel do convite.
                    using var inviteUpdate = new SqlCommand(
                        "UPDATE dbo.tbLpWorkspaceInvite SET Role = @Role WHERE InviteId = @Id AND WorkspaceId = @WorkspaceId;",
                        connection, tx);
                    inviteUpdate.Parameters.AddWithValue("@Role", role);
                    inviteUpdate.Parameters.AddWithValue("@Id", memberOrInviteId);
                    inviteUpdate.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                    var rows = await inviteUpdate.ExecuteNonQueryAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return rows > 0 ? MemberChangeOutcome.Ok : MemberChangeOutcome.NotFound;
                }

                if (string.Equals(current, WorkspaceRole.Owner, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MemberChangeOutcome.OwnerProtected;
                }

                if (IsAdminRole(current) && !IsAdminRole(role) && await CountAdminsAsync(connection, tx, workspaceId, cancellationToken) <= 1)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MemberChangeOutcome.LastAdmin;
                }

                using var update = new SqlCommand(
                    "UPDATE dbo.tbLpWorkspaceMembership SET Role = @Role WHERE WorkspaceId = @WorkspaceId AND UserId = @UserId;",
                    connection, tx);
                update.Parameters.AddWithValue("@Role", role);
                update.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                update.Parameters.AddWithValue("@UserId", memberOrInviteId);
                await update.ExecuteNonQueryAsync(cancellationToken);

                await tx.CommitAsync(cancellationToken);
                return MemberChangeOutcome.Ok;
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<MemberChangeOutcome> RemoveAsync(Guid workspaceId, Guid memberOrInviteId, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var current = await SelectRoleAsync(connection, tx, workspaceId, memberOrInviteId, cancellationToken);
                if (current == null)
                {
                    using var inviteDelete = new SqlCommand(
                        "DELETE FROM dbo.tbLpWorkspaceInvite WHERE InviteId = @Id AND WorkspaceId = @WorkspaceId;",
                        connection, tx);
                    inviteDelete.Parameters.AddWithValue("@Id", memberOrInviteId);
                    inviteDelete.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                    var rows = await inviteDelete.ExecuteNonQueryAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return rows > 0 ? MemberChangeOutcome.Ok : MemberChangeOutcome.NotFound;
                }

                if (string.Equals(current, WorkspaceRole.Owner, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MemberChangeOutcome.OwnerProtected;
                }

                if (IsAdminRole(current) && await CountAdminsAsync(connection, tx, workspaceId, cancellationToken) <= 1)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MemberChangeOutcome.LastAdmin;
                }

                using var delete = new SqlCommand(
                    "DELETE FROM dbo.tbLpWorkspaceMembership WHERE WorkspaceId = @WorkspaceId AND UserId = @UserId;",
                    connection, tx);
                delete.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                delete.Parameters.AddWithValue("@UserId", memberOrInviteId);
                await delete.ExecuteNonQueryAsync(cancellationToken);

                await tx.CommitAsync(cancellationToken);
                return MemberChangeOutcome.Ok;
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        private static bool IsAdminRole(string role) =>
            string.Equals(role, WorkspaceRole.Owner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, WorkspaceRole.FiscalAdmin, StringComparison.OrdinalIgnoreCase);

        private static async Task<string?> SelectRoleAsync(SqlConnection connection, SqlTransaction tx, Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                "SELECT Role FROM dbo.tbLpWorkspaceMembership WITH (UPDLOCK) WHERE WorkspaceId = @WorkspaceId AND UserId = @UserId;",
                connection, tx);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@UserId", userId);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        private static async Task<int> CountAdminsAsync(SqlConnection connection, SqlTransaction tx, Guid workspaceId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.tbLpWorkspaceMembership WITH (UPDLOCK) WHERE WorkspaceId = @WorkspaceId AND Role IN (@Owner, @Admin);",
                connection, tx);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@Owner", WorkspaceRole.Owner);
            command.Parameters.AddWithValue("@Admin", WorkspaceRole.FiscalAdmin);
            return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        private static WorkspaceMemberInfo ReadMember(SqlDataReader reader) => new(
            reader.GetGuid(reader.GetOrdinal("Id")),
            null, // DisplayName ainda não é persistido pela API (só e-mail) — campo mantido no contrato.
            reader.IsDBNull(reader.GetOrdinal("Email")) ? null : reader.GetString(reader.GetOrdinal("Email")),
            reader.GetString(reader.GetOrdinal("Role")),
            reader.GetString(reader.GetOrdinal("Status")),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero));
    }
}
