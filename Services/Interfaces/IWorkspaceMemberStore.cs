namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Membro (ativo) ou convite pendente de um workspace — corpo de <c>GET .../members</c>.</summary>
    /// <remarks>Para <c>status = pending</c>, <see cref="UserId"/> é o id do convite (usado em PATCH/DELETE).</remarks>
    public sealed record WorkspaceMemberInfo(
        Guid UserId,
        string? DisplayName,
        string? Email,
        string Role,
        string Status,
        DateTimeOffset CreatedAt);

    public enum AddMemberKind { Added, Invited, AlreadyMember, AlreadyInvited }

    public sealed record AddMemberResult(AddMemberKind Kind, WorkspaceMemberInfo Member);

    public enum MemberChangeOutcome { Ok, NotFound, LastAdmin, OwnerProtected }

    /// <summary>
    /// Persistência de e-mail de usuário, membros e convites de workspace (banco <c>IdentityDatabase:*</c>).
    /// Interface separada de <see cref="IIdentityWorkspaceStore"/> de propósito: não quebra os dublês existentes.
    /// </summary>
    public interface IWorkspaceMemberStore
    {
        /// <summary>
        /// Persiste o e-mail (já normalizado) do usuário, atualizando se mudou, e converte em membership
        /// qualquer convite pendente para esse e-mail (primeiro login com o e-mail convidado).
        /// </summary>
        Task SyncEmailAndRedeemInvitesAsync(Guid userId, string email, CancellationToken cancellationToken);

        Task<IReadOnlyList<WorkspaceMemberInfo>> ListAsync(Guid workspaceId, CancellationToken cancellationToken);

        /// <summary>Adiciona por e-mail: membership direto se o e-mail já logou; senão, convite pendente.</summary>
        Task<AddMemberResult> AddAsync(Guid workspaceId, string email, string role, Guid invitedByUserId, CancellationToken cancellationToken);

        Task<MemberChangeOutcome> ChangeRoleAsync(Guid workspaceId, Guid memberOrInviteId, string role, CancellationToken cancellationToken);

        Task<MemberChangeOutcome> RemoveAsync(Guid workspaceId, Guid memberOrInviteId, CancellationToken cancellationToken);
    }
}
