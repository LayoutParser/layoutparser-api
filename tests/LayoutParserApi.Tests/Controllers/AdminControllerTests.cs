using LayoutParserApi.Controllers;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    public class AdminControllerTests
    {
        private sealed class FakeDirectory : IAdminDirectoryStore
        {
            public WorkspaceUpdateOutcome Outcome { get; set; } = WorkspaceUpdateOutcome.Ok;
            public (string? Name, bool Promote)? Last { get; private set; }

            public Task<IReadOnlyList<AdminWorkspaceInfo>> ListWorkspacesAsync(int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AdminWorkspaceInfo>>(Array.Empty<AdminWorkspaceInfo>());
            public Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult(true);
            public Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AdminUserInfo>>(Array.Empty<AdminUserInfo>());

            public Task<WorkspaceUpdateOutcome> UpdateWorkspaceAsync(Guid workspaceId, string? name, bool promoteToTeam, CancellationToken cancellationToken)
            {
                Last = (name, promoteToTeam);
                return Task.FromResult(Outcome);
            }
        }

        private sealed class NoMembers : IWorkspaceMemberStore
        {
            public Task SyncEmailAndRedeemInvitesAsync(Guid userId, string email, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<IReadOnlyList<WorkspaceMemberInfo>> ListAsync(Guid workspaceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkspaceMemberInfo>>(Array.Empty<WorkspaceMemberInfo>());
            public Task<AddMemberResult> AddAsync(Guid workspaceId, string email, string role, Guid invitedByUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MemberChangeOutcome> ChangeRoleAsync(Guid workspaceId, Guid memberOrInviteId, string role, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MemberChangeOutcome> RemoveAsync(Guid workspaceId, Guid memberOrInviteId, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private static AdminController Create(FakeDirectory dir) => new(dir, new NoMembers(), NullLogger<AdminController>.Instance);

        [Fact]
        public async Task Promove_a_time_e_renomeia()
        {
            var dir = new FakeDirectory();
            var result = await Create(dir).UpdateWorkspace(Guid.NewGuid(), new UpdateAdminWorkspaceRequest("team", "  NDD  "), default);

            Assert.IsType<NoContentResult>(result);
            Assert.Equal(("NDD", true), dir.Last);
        }

        [Fact]
        public async Task Somente_renomear_nao_promove()
        {
            var dir = new FakeDirectory();
            await Create(dir).UpdateWorkspace(Guid.NewGuid(), new UpdateAdminWorkspaceRequest(null, "Time"), default);
            Assert.Equal(("Time", false), dir.Last);
        }

        [Theory]
        [InlineData("personal", null)]   // nunca rebaixa
        [InlineData("admin", null)]
        [InlineData(null, "")]
        [InlineData(null, "   ")]
        [InlineData(null, null)]
        public async Task Entradas_invalidas_retornam_400_sem_tocar_o_banco(string? kind, string? name)
        {
            var dir = new FakeDirectory();
            var result = await Create(dir).UpdateWorkspace(Guid.NewGuid(), new UpdateAdminWorkspaceRequest(kind, name), default);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Null(dir.Last);
        }

        [Fact]
        public async Task Nome_acima_de_120_retorna_400()
        {
            var result = await Create(new FakeDirectory()).UpdateWorkspace(Guid.NewGuid(), new UpdateAdminWorkspaceRequest(null, new string('a', 121)), default);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Workspace_inexistente_retorna_404()
        {
            var result = await Create(new FakeDirectory { Outcome = WorkspaceUpdateOutcome.NotFound })
                .UpdateWorkspace(Guid.NewGuid(), new UpdateAdminWorkspaceRequest("team", null), default);
            Assert.IsType<NotFoundObjectResult>(result);
        }
    }
}
