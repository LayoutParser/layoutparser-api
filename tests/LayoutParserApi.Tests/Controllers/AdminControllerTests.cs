using LayoutParserApi.Controllers;
using LayoutParserApi.Services.Email;
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

        private sealed class FakeOutbox : IEmailOutboxStore
        {
            public List<OutboxEmailStatus> Rows { get; } = new();
            public Task<EnqueueResult> EnqueueAsync(string toEmail, string template, string dedupeKey, string subject, string body, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<OutboxEmail?> ClaimNextAsync(int maxAttempts, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task MarkSentAsync(Guid emailId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task MarkFailedAsync(Guid emailId, string error, int maxAttempts, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<int> CountSentLast24hAsync(CancellationToken cancellationToken) => Task.FromResult(0);
            public Task<IReadOnlyList<OutboxEmailStatus>> ListAsync(string dedupeKey, string? toEmail, int skip, int take, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<OutboxEmailStatus>>(Rows.Where(r => toEmail == null || r.ToEmail == toEmail).ToList());
            public Task<IReadOnlyDictionary<string, string>> GetLatestStatusByEmailAsync(string dedupeKey, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        private sealed class FakeSender(bool configured) : IEmailSender
        {
            public bool IsConfigured => configured;
            public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private static AdminController Create(FakeDirectory dir, FakeOutbox? outbox = null, bool smtp = false)
            => new(dir, new NoMembers(), outbox ?? new FakeOutbox(), new FakeSender(smtp), NullLogger<AdminController>.Instance);

        [Fact]
        public async Task EmailOutbox_mascara_destinatario_e_expoe_smtpConfigured()
        {
            var outbox = new FakeOutbox();
            outbox.Rows.Add(new OutboxEmailStatus(Guid.NewGuid(), "luan@gmail.com", "welcome-v1", "pending", 0, DateTime.UtcNow, DateTime.UtcNow, null, null));
            var ok = Assert.IsType<OkObjectResult>(await Create(new FakeDirectory(), outbox, smtp: false).MemberEmailOutbox(Guid.NewGuid(), null, 0, 50, default));
            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            Assert.Contains("\"smtpConfigured\":false", json);
            Assert.Contains("l***@gmail.com", json);
            Assert.DoesNotContain("luan@gmail.com", json);
            Assert.Contains("\"status\":\"pending\"", json);
        }

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
