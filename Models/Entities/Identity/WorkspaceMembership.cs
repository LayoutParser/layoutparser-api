namespace LayoutParserApi.Models.Entities.Identity
{
    /// <summary>
    /// Papéis de <see cref="WorkspaceMembership"/> previstos pela auditoria do Slice 1
    /// (docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md §1). Só "Owner" é
    /// atribuído nesta fase (workspace pessoal); os demais existem para não quebrar o modelo quando
    /// workspaces de time (Slice 2+) chegarem.
    /// </summary>
    public static class WorkspaceRole
    {
        public const string Owner = "owner";
        public const string FiscalAdmin = "fiscal_admin";
        public const string Mapper = "mapper";
        public const string Reviewer = "reviewer";
        public const string Operator = "operator";
        public const string Viewer = "viewer";

        /// <summary>
        /// Nível hierárquico do papel: viewer(1) &lt; operator(2) &lt; fiscal_admin(3) &lt; owner(4).
        /// Legados <c>mapper</c>/<c>reviewer</c> valem nível Operador. Papel desconhecido/nulo =&gt; 0 (nega, nunca eleva).
        /// </summary>
        public static WorkspaceRoleLevel LevelOf(string? role) => role?.Trim().ToLowerInvariant() switch
        {
            Owner => WorkspaceRoleLevel.Owner,
            FiscalAdmin => WorkspaceRoleLevel.Admin,
            Operator or Mapper or Reviewer => WorkspaceRoleLevel.Operator,
            Viewer => WorkspaceRoleLevel.Viewer,
            _ => WorkspaceRoleLevel.None
        };

        /// <summary>True se o papel tem nível &gt;= ao mínimo exigido.</summary>
        public static bool AtLeast(string? role, WorkspaceRoleLevel min) => LevelOf(role) >= min && min > WorkspaceRoleLevel.None;

        /// <summary>Id canônico: legados (mapper/reviewer) viram <c>operator</c>; demais em minúsculas; desconhecido volta como veio.</summary>
        public static string Canonical(string? role)
        {
            var r = role?.Trim().ToLowerInvariant();
            return r is Mapper or Reviewer ? Operator : (r ?? string.Empty);
        }
    }

    /// <summary>Níveis de papel de workspace (RBAC de 4 papéis, 2026-10-01).</summary>
    public enum WorkspaceRoleLevel
    {
        None = 0,
        Viewer = 1,
        Operator = 2,
        Admin = 3,
        Owner = 4
    }

    /// <summary>
    /// Vínculo N:N entre <see cref="FiscalUser"/> e <see cref="FiscalWorkspace"/>. Um usuário pode
    /// pertencer a vários workspaces; a existência (ou não) de membership é o único critério de
    /// autorização — "não existe" e "existe mas não é seu" respondem o MESMO 404 (nunca 403), para não
    /// permitir enumeração de workspace por ID.
    /// </summary>
    public class WorkspaceMembership
    {
        public Guid WorkspaceMembershipId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid UserId { get; set; }

        /// <summary>Ver <see cref="WorkspaceRole"/>.</summary>
        public string Role { get; set; } = WorkspaceRole.Owner;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
