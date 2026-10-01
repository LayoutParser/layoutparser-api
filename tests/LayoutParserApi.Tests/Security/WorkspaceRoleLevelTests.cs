using LayoutParserApi.Models.Entities.Identity;

using Xunit;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>Hierarquia de papéis de workspace (RBAC de 4 papéis, 2026-10-01).</summary>
    public class WorkspaceRoleLevelTests
    {
        [Theory]
        [InlineData("viewer", WorkspaceRoleLevel.Viewer, true)]
        [InlineData("viewer", WorkspaceRoleLevel.Operator, false)]
        [InlineData("operator", WorkspaceRoleLevel.Operator, true)]
        [InlineData("operator", WorkspaceRoleLevel.Admin, false)]
        [InlineData("mapper", WorkspaceRoleLevel.Operator, true)]
        [InlineData("reviewer", WorkspaceRoleLevel.Operator, true)]
        [InlineData("reviewer", WorkspaceRoleLevel.Admin, false)]
        [InlineData("fiscal_admin", WorkspaceRoleLevel.Admin, true)]
        [InlineData("fiscal_admin", WorkspaceRoleLevel.Owner, false)]
        [InlineData("owner", WorkspaceRoleLevel.Admin, true)]
        [InlineData("OWNER", WorkspaceRoleLevel.Owner, true)]
        [InlineData("desconhecido", WorkspaceRoleLevel.Viewer, false)]
        [InlineData(null, WorkspaceRoleLevel.Viewer, false)]
        public void AtLeast_respeita_hierarquia(string? role, WorkspaceRoleLevel min, bool esperado)
            => Assert.Equal(esperado, WorkspaceRole.AtLeast(role, min));

        [Theory]
        [InlineData("mapper", "operator")]
        [InlineData("reviewer", "operator")]
        [InlineData("viewer", "viewer")]
        [InlineData("fiscal_admin", "fiscal_admin")]
        public void Canonical_mapeia_legados_para_operator(string role, string esperado)
            => Assert.Equal(esperado, WorkspaceRole.Canonical(role));
    }
}
