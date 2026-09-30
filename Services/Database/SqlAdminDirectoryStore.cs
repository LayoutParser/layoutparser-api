using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>Leitura global no <c>IdentityDatabase:*</c> (banco dedicado; nunca o SQL compartilhado do Sysmiddle).</summary>
    public sealed class SqlAdminDirectoryStore : IAdminDirectoryStore
    {
        private readonly string _connectionString;

        public SqlAdminDirectoryStore(IConfiguration configuration)
        {
            _connectionString =
                $"Server={configuration["IdentityDatabase:Server"]};Database={configuration["IdentityDatabase:Database"]};" +
                $"User Id={configuration["IdentityDatabase:UserId"]};Password={configuration["IdentityDatabase:Password"]};TrustServerCertificate=True;";
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken ct)
        {
            var c = new SqlConnection(_connectionString);
            await c.OpenAsync(ct);
            await SqlIdentityWorkspaceStore.EnsureSchemaAsync(c, ct);
            return c;
        }

        public async Task<IReadOnlyList<AdminWorkspaceInfo>> ListWorkspacesAsync(int skip, int take, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, w.OwnerUserId, w.CreatedAt,
                         (SELECT COUNT(*) FROM dbo.tbLpWorkspaceMembership m WHERE m.WorkspaceId = w.WorkspaceId) AS MemberCount
                  FROM dbo.tbLpFiscalWorkspace w
                  ORDER BY w.CreatedAt, w.WorkspaceId
                  OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;", c);
            cmd.Parameters.AddWithValue("@Skip", skip);
            cmd.Parameters.AddWithValue("@Take", take);

            var list = new List<AdminWorkspaceInfo>();
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new AdminWorkspaceInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetInt32(5),
                    new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero)));
            return list;
        }

        public async Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.tbLpFiscalWorkspace WHERE WorkspaceId = @Id;", c);
            cmd.Parameters.AddWithValue("@Id", workspaceId);
            return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
        }

        public async Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(int skip, int take, CancellationToken ct)
        {
            using var c = await OpenAsync(ct);
            using var cmd = new SqlCommand(
                @"SELECT u.UserId, u.Email, u.CreatedAt,
                         (SELECT COUNT(*) FROM dbo.tbLpWorkspaceMembership m WHERE m.UserId = u.UserId) AS WorkspaceCount
                  FROM dbo.tbLpUser u
                  ORDER BY u.CreatedAt, u.UserId
                  OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;", c);
            cmd.Parameters.AddWithValue("@Skip", skip);
            cmd.Parameters.AddWithValue("@Take", take);

            var list = new List<AdminUserInfo>();
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new AdminUserInfo(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(3),
                    new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero)));
            return list;
        }
    }
}
