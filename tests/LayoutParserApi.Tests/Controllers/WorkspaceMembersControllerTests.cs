using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Email;
using LayoutParserApi.Services.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    public class WorkspaceMembersControllerTests
    {
        private static readonly Guid WorkspaceId = Guid.NewGuid();
        private static readonly Guid AdminId = Guid.NewGuid();

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name => "admin";
            public IReadOnlyList<string> Roles => Array.Empty<string>();
            public bool IsAuthenticated => true;
            public Guid? UserId { get; set; } = AdminId;
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeWorkspaces : IIdentityWorkspaceService
        {
            public string Kind { get; set; } = WorkspaceKind.Team;
            public bool IsMember { get; set; } = true;

            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult<WorkspaceSummary?>(IsMember
                    ? new WorkspaceSummary(workspaceId, "Time", Kind, WorkspaceRole.FiscalAdmin, DateTimeOffset.UtcNow)
                    : null);
        }

        private sealed class FakeMembers : IWorkspaceMemberStore
        {
            public AddMemberKind AddKind { get; set; } = AddMemberKind.Added;
            public MemberChangeOutcome Outcome { get; set; } = MemberChangeOutcome.Ok;
            public string? LastAddedRole { get; private set; }
            public string? LastAddedEmail { get; private set; }

            public Task SyncEmailAndRedeemInvitesAsync(Guid userId, string email, CancellationToken cancellationToken) => Task.CompletedTask;

            public List<WorkspaceMemberInfo> Items { get; } = new() { new WorkspaceMemberInfo(Guid.NewGuid(), null, "a@b.com", WorkspaceRole.Viewer, "active", DateTimeOffset.UtcNow) };
            public Task<IReadOnlyList<WorkspaceMemberInfo>> ListAsync(Guid workspaceId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<WorkspaceMemberInfo>>(Items.ToList());

            public Task<AddMemberResult> AddAsync(Guid workspaceId, string email, string role, Guid invitedByUserId, CancellationToken cancellationToken)
            {
                LastAddedEmail = email;
                LastAddedRole = role;
                return Task.FromResult(new AddMemberResult(AddKind,
                    new WorkspaceMemberInfo(Guid.NewGuid(), null, email, role, AddKind == AddMemberKind.Invited ? "pending" : "active", DateTimeOffset.UtcNow)));
            }

            public Task<MemberChangeOutcome> ChangeRoleAsync(Guid workspaceId, Guid memberOrInviteId, string role, CancellationToken cancellationToken)
                => Task.FromResult(Outcome);

            public Task<MemberChangeOutcome> RemoveAsync(Guid workspaceId, Guid memberOrInviteId, CancellationToken cancellationToken)
                => Task.FromResult(Outcome);
        }

        private sealed class FakeSender : IEmailSender
        {
            public bool Configured { get; set; }
            public bool IsConfigured => Configured;
            public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeOutbox : IEmailOutboxStore
        {
            public List<(string To, string Subject, string Body)> Enqueued { get; } = new();
            public List<string> Keys { get; } = new();
            public int Sent { get; set; }
            public bool Throw { get; set; }
            public Task<EnqueueResult> EnqueueAsync(string toEmail, string template, string dedupeKey, string subject, string body, CancellationToken cancellationToken)
            {
                if (Throw) throw new InvalidOperationException("sql fora");
                Enqueued.Add((toEmail, subject, body));
                Keys.Add(dedupeKey);
                return Task.FromResult(new EnqueueResult(true, Guid.NewGuid()));
            }
            public List<OutboxEmailStatus> Rows { get; } = new();
            public Task<IReadOnlyList<OutboxEmailStatus>> ListAsync(string dedupeKey, string? toEmail, int skip, int take, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<OutboxEmailStatus>>(Rows.Where(r => toEmail == null || r.ToEmail == toEmail).Skip(skip).Take(take).ToList());
            public Task<IReadOnlyDictionary<string, string>> GetLatestStatusByEmailAsync(string dedupeKey, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyDictionary<string, string>>(Rows.ToDictionary(r => r.ToEmail, r => r.Status));
            public Task<OutboxEmail?> ClaimNextAsync(int maxAttempts, CancellationToken cancellationToken) => Task.FromResult<OutboxEmail?>(null);
            public Task MarkSentAsync(Guid emailId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task MarkFailedAsync(Guid emailId, string error, int maxAttempts, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<int> CountSentLast24hAsync(CancellationToken cancellationToken) => Task.FromResult(Sent);
        }

        [Fact]
        public async Task Add_enfileira_boas_vindas_sem_segredo_quando_smtp_configurado()
        {
            var outbox = new FakeOutbox();
            var result = await Create(new FakeMembers(), outbox: outbox, configured: true)
                .Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", "viewer"), default);

            Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
            var mail = Assert.Single(outbox.Enqueued);
            Assert.Equal("a@b.com", mail.To);
            Assert.Contains("https://portal", mail.Body);
            Assert.DoesNotContain("token", mail.Body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Falha_do_outbox_nao_desfaz_o_vinculo()
        {
            var result = await Create(new FakeMembers(), outbox: new FakeOutbox { Throw = true }, configured: true)
                .Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", "viewer"), default);
            Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        [Fact]
        public async Task Sem_smtp_configurado_ainda_enfileira_para_rastreio()
        {
            var outbox = new FakeOutbox();
            await Create(new FakeMembers(), outbox: outbox, configured: false)
                .Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", "viewer"), default);
            Assert.Single(outbox.Enqueued);
        }

        [Theory]
        [InlineData("luan.mota0102@gmail.com", "l***@gmail.com")]
        [InlineData("a@b.com", "a***@b.com")]
        [InlineData("semarroba", "***")]
        [InlineData(null, "***")]
        public void Mascara_destinatario_sem_expor_o_endereco(string? raw, string esperado)
            => Assert.Equal(esperado, EmailMasking.Mask(raw));

        private static (WorkspaceMemberInfo Pending, FakeMembers Members) PendingMember(string status = "pending")
        {
            var m = new WorkspaceMemberInfo(Guid.NewGuid(), null, "novo@b.com", WorkspaceRole.Viewer, status, DateTimeOffset.UtcNow);
            var fm = new FakeMembers();
            fm.Items.Add(m);
            return (m, fm);
        }

        [Fact]
        public async Task Reenvio_de_pendente_enfileira_com_dedupeKey_proprio()
        {
            var (m, fm) = PendingMember();
            var outbox = new FakeOutbox();
            var result = await Create(fm, outbox: outbox, configured: true).ResendInvite(WorkspaceId, m.UserId, default);

            Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal("novo@b.com", Assert.Single(outbox.Enqueued).To);
            var key = Assert.Single(outbox.Keys);
            Assert.NotEqual(WorkspaceId.ToString("N"), key);
            Assert.StartsWith(WorkspaceId.ToString("N") + ":resend:", key);
        }

        [Fact]
        public async Task Reenvio_para_membro_ativo_retorna_409_member_already_active()
        {
            var (m, fm) = PendingMember("active");
            var result = await Create(fm).ResendInvite(WorkspaceId, m.UserId, default);
            var obj = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("member_already_active", System.Text.Json.JsonSerializer.Serialize(obj.Value));
        }

        [Fact]
        public async Task Reenvio_dentro_do_cooldown_retorna_429_com_retry_after()
        {
            var (m, fm) = PendingMember();
            var outbox = new FakeOutbox();
            outbox.Rows.Add(new OutboxEmailStatus(Guid.NewGuid(), "novo@b.com", "welcome-v1", "sent", 1, DateTime.UtcNow.AddMinutes(-1), null, null, null));
            var ctrl = Create(fm, outbox: outbox);
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await ctrl.ResendInvite(WorkspaceId, m.UserId, default);

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(429, obj.StatusCode);
            Assert.Contains("resend_cooldown", System.Text.Json.JsonSerializer.Serialize(obj.Value));
            Assert.True(int.Parse(ctrl.Response.Headers["Retry-After"].ToString()) > 0);
            Assert.Empty(outbox.Enqueued);
        }

        [Fact]
        public async Task Reenvio_apos_o_cooldown_e_aceito()
        {
            var (m, fm) = PendingMember();
            var outbox = new FakeOutbox();
            outbox.Rows.Add(new OutboxEmailStatus(Guid.NewGuid(), "novo@b.com", "welcome-v1", "sent", 1, DateTime.UtcNow.AddMinutes(-30), null, null, null));
            var result = await Create(fm, outbox: outbox).ResendInvite(WorkspaceId, m.UserId, default);
            Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        [Fact]
        public async Task Reenvio_com_teto_diario_atingido_retorna_429_daily_limit()
        {
            var (m, fm) = PendingMember();
            var result = await Create(fm, outbox: new FakeOutbox { Sent = 400 }).ResendInvite(WorkspaceId, m.UserId, default);
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(429, obj.StatusCode);
            Assert.Contains("daily_limit", System.Text.Json.JsonSerializer.Serialize(obj.Value));
        }

        [Fact]
        public async Task Reenvio_de_membro_inexistente_ou_de_outro_workspace_retorna_404()
        {
            var result = await Create(new FakeMembers()).ResendInvite(WorkspaceId, Guid.NewGuid(), default);
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task Reenvio_por_nao_membro_do_workspace_retorna_404()
        {
            var (m, fm) = PendingMember();
            var result = await Create(fm, new FakeWorkspaces { IsMember = false }).ResendInvite(WorkspaceId, m.UserId, default);
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task Reenvio_sem_smtp_informa_smtpConfigured_false()
        {
            var (m, fm) = PendingMember();
            var result = await Create(fm, configured: false).ResendInvite(WorkspaceId, m.UserId, default);
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(202, obj.StatusCode);
            var json = System.Text.Json.JsonSerializer.Serialize(obj.Value);
            Assert.Contains("\"smtpConfigured\":false", json);
            Assert.Contains("\"enqueued\":true", json);
        }

        [Fact]
        public void Reenvio_exige_papel_de_admin_e_auditoria()
        {
            var method = typeof(WorkspaceMembersController).GetMethod(nameof(WorkspaceMembersController.ResendInvite))!;
            Assert.NotNull(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ServiceFilterAttribute), false).FirstOrDefault());
            Assert.NotNull(typeof(WorkspaceMembersController).GetCustomAttributes(typeof(LayoutParserApi.Services.Filters.RequireWorkspaceRoleAttribute), false).FirstOrDefault());
        }

        private static WorkspaceMembersController Create(FakeMembers members, FakeWorkspaces? workspaces = null, FakeOutbox? outbox = null, bool configured = false)
            => new(members, workspaces ?? new FakeWorkspaces(), new FakeCurrentUser(), outbox ?? new FakeOutbox(), new FakeSender { Configured = configured },
                Microsoft.Extensions.Options.Options.Create(new EmailOptions { PortalUrl = "https://portal" }), NullLogger<WorkspaceMembersController>.Instance);

        [Theory]
        [InlineData("  Fulano@Empresa.COM ", "fulano@empresa.com")]
        [InlineData("a@b.co", "a@b.co")]
        [InlineData("Nome <a@b.co>", null)]
        [InlineData("sem-arroba", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Email_e_normalizado_ou_rejeitado(string? raw, string? esperado)
            => Assert.Equal(esperado, WorkspaceEmail.Normalize(raw));

        [Fact]
        public async Task Add_com_email_invalido_retorna_400_com_error()
        {
            var result = await Create(new FakeMembers()).Add(WorkspaceId, new AddWorkspaceMemberRequest("xx", "viewer"), default);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Theory]
        [InlineData("owner")]
        [InlineData("root")]
        [InlineData(null)]
        public async Task Add_nao_aceita_owner_nem_papel_desconhecido(string? role)
        {
            var result = await Create(new FakeMembers()).Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", role), default);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Theory]
        [InlineData("mapper")]
        [InlineData("reviewer")]
        public async Task Add_papel_legado_retorna_400_com_mensagem(string role)
        {
            var result = await Create(new FakeMembers()).Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", role), default);
            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("legado", bad.Value!.ToString());
        }

        [Fact]
        public async Task Add_normaliza_email_e_retorna_201()
        {
            var members = new FakeMembers();
            var result = await Create(members).Add(WorkspaceId, new AddWorkspaceMemberRequest(" A@B.com ", "operator"), default);

            Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal("a@b.com", members.LastAddedEmail);
            Assert.Equal("operator", members.LastAddedRole);
        }

        [Fact]
        public async Task Add_de_email_ja_membro_retorna_409()
        {
            var result = await Create(new FakeMembers { AddKind = AddMemberKind.AlreadyMember })
                .Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", "viewer"), default);
            Assert.IsType<ConflictObjectResult>(result);
        }

        [Fact]
        public async Task Add_convite_repetido_e_idempotente_200()
        {
            var result = await Create(new FakeMembers { AddKind = AddMemberKind.AlreadyInvited })
                .Add(WorkspaceId, new AddWorkspaceMemberRequest("a@b.com", "viewer"), default);
            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task Workspace_pessoal_nao_aceita_membros_409()
        {
            var result = await Create(new FakeMembers(), new FakeWorkspaces { Kind = WorkspaceKind.Personal }).List(WorkspaceId, default);
            Assert.IsType<ConflictObjectResult>(result);
        }

        [Fact]
        public async Task Nao_membro_recebe_404()
        {
            var result = await Create(new FakeMembers(), new FakeWorkspaces { IsMember = false }).List(WorkspaceId, default);
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Theory]
        [InlineData(MemberChangeOutcome.Ok, 204)]
        [InlineData(MemberChangeOutcome.NotFound, 404)]
        [InlineData(MemberChangeOutcome.LastAdmin, 409)]
        [InlineData(MemberChangeOutcome.OwnerProtected, 409)]
        public async Task Remove_mapeia_resultado_para_status(MemberChangeOutcome outcome, int status)
        {
            var result = await Create(new FakeMembers { Outcome = outcome }).Remove(WorkspaceId, Guid.NewGuid(), default);
            Assert.Equal(status, ((Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)result).StatusCode);
        }

        [Fact]
        public async Task ChangeRole_rejeita_owner()
        {
            var result = await Create(new FakeMembers()).ChangeRole(WorkspaceId, Guid.NewGuid(), new ChangeWorkspaceMemberRoleRequest("owner"), default);
            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
