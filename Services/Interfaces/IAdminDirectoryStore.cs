namespace LayoutParserApi.Services.Interfaces
{
    public sealed record AdminWorkspaceInfo(Guid WorkspaceId, string Name, string Kind, Guid OwnerUserId, int MemberCount, DateTimeOffset CreatedAt);

    public sealed record AdminUserInfo(Guid UserId, string? Email, int WorkspaceCount, DateTimeOffset CreatedAt);

    /// <summary>Consulta global (somente leitura) para o super-administrador — nunca expõe subject/identidade externa.</summary>
    public interface IAdminDirectoryStore
    {
        Task<IReadOnlyList<AdminWorkspaceInfo>> ListWorkspacesAsync(int skip, int take, CancellationToken cancellationToken);

        Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken);

        Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(int skip, int take, CancellationToken cancellationToken);
    }
}
