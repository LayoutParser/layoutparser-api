namespace LayoutParserApi.Services.Interfaces
{
    public sealed record AdminWorkspaceInfo(Guid WorkspaceId, string Name, string Kind, Guid OwnerUserId, int MemberCount, DateTimeOffset CreatedAt);

    public sealed record AdminUserInfo(Guid UserId, string? Email, int WorkspaceCount, DateTimeOffset CreatedAt);

    public enum WorkspaceUpdateOutcome { Ok, NotFound }

    /// <summary>Consulta global (leitura + ajuste de nome/tipo de workspace) para o super-administrador — nunca expõe subject/identidade externa.</summary>
    public interface IAdminDirectoryStore
    {
        Task<IReadOnlyList<AdminWorkspaceInfo>> ListWorkspacesAsync(int skip, int take, CancellationToken cancellationToken);

        Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken);

        /// <summary>
        /// Renomeia e/ou promove um workspace pessoal a time (dono e dados mantidos). Só promove — nunca rebaixa.
        /// <paramref name="name"/> <c>null</c> = mantém o nome atual.
        /// </summary>
        Task<WorkspaceUpdateOutcome> UpdateWorkspaceAsync(Guid workspaceId, string? name, bool promoteToTeam, CancellationToken cancellationToken);

        Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(int skip, int take, CancellationToken cancellationToken);
    }
}
