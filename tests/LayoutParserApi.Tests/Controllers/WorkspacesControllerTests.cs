using LayoutParserApi.Controllers;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Slice 1 (issue #225/#228) — gate de segurança do isolamento cross-workspace. O teste que mais
    /// importa é <see cref="GetWorkspace_usuario_B_nao_le_workspace_de_usuario_A"/>: usa um dublê de
    /// <see cref="IIdentityWorkspaceService"/> que reproduz o MESMO filtro de membership que o
    /// <c>SqlIdentityWorkspaceStore</c> real faz na cláusula <c>WHERE m.UserId = @UserId</c> — se
    /// alguém remover/quebrar esse filtro (no dublê ou no SQL real), este teste fica VERMELHO. Mesmo
    /// racional do "teste que morde" da guarda de loopback em
    /// <c>TrustedIdentityMiddlewareTests.Origem_nao_loopback_ignora_os_headers</c>.
    /// </summary>
    public class WorkspacesControllerTests
    {
        // --- fakes ---

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Dublê que reproduz o mesmo contrato de membership do store real: um workspace só é
        /// retornado se <c>userId</c> constar em <see cref="Memberships"/> para aquele
        /// <c>workspaceId</c>. É a "cláusula WHERE" do fake — propositalmente explícita, não um
        /// atalho que sempre devolve o workspace.
        /// </summary>
        private sealed class FakeIdentityWorkspaceService : IIdentityWorkspaceService
        {
            public Dictionary<Guid, WorkspaceSummary> Workspaces { get; } = new();
            public HashSet<(Guid WorkspaceId, Guid UserId)> Memberships { get; } = new();
            public Exception? ThrowOnGetMe { get; set; }
            public Exception? ThrowOnGetWorkspace { get; set; }

            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado pelos testes de controller.");

            public Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
            {
                if (ThrowOnGetMe != null)
                    throw ThrowOnGetMe;

                var mine = Memberships
                    .Where(m => m.UserId == userId)
                    .Select(m => Workspaces[m.WorkspaceId])
                    .ToList();

                return Task.FromResult(new WorkspaceMeResult(mine.First().WorkspaceId, mine));
            }

            public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
            {
                if (ThrowOnGetWorkspace != null)
                    throw ThrowOnGetWorkspace;

                // 🔴 O FILTRO QUE IMPORTA: sem membership, null — nunca o workspace de outro dono.
                if (!Memberships.Contains((workspaceId, userId)))
                    return Task.FromResult<WorkspaceSummary?>(null);

                return Task.FromResult<WorkspaceSummary?>(Workspaces[workspaceId]);
            }
        }

        private static WorkspacesController BuildController(FakeIdentityWorkspaceService service, FakeCurrentUser user)
            => new(service, user, NullLogger<WorkspacesController>.Instance);

        // --- GET /api/workspaces/me ---

        [Fact]
        public async Task GetMe_sem_identidade_resolvida_retorna_401()
        {
            var controller = BuildController(new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = null });

            var result = await controller.GetMe(CancellationToken.None);

            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        [Fact]
        public async Task GetMe_com_identidade_retorna_workspaces_do_usuario()
        {
            var service = new FakeIdentityWorkspaceService();
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            service.Workspaces[workspaceId] = new WorkspaceSummary(workspaceId, "Meu workspace fiscal", "personal", "owner", DateTimeOffset.UtcNow);
            service.Memberships.Add((workspaceId, userId));

            var controller = BuildController(service, new FakeCurrentUser { UserId = userId, Name = "alice" });

            var result = await controller.GetMe(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
        }

        [Theory]
        [InlineData("viewer", "viewer", false, false, false)]
        [InlineData("operator", "operator", true, false, false)]
        [InlineData("mapper", "operator", true, false, false)]
        [InlineData("reviewer", "operator", true, false, false)]
        [InlineData("fiscal_admin", "fiscal_admin", true, true, true)]
        [InlineData("owner", "owner", true, true, true)]
        public async Task GetMe_expoe_role_canonico_e_permissions_por_papel(
            string roleBanco, string roleEsperado, bool canEdit, bool canPublish, bool canManage)
        {
            var service = new FakeIdentityWorkspaceService();
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            service.Workspaces[workspaceId] = new WorkspaceSummary(workspaceId, "WS", "shared", roleBanco, DateTimeOffset.UtcNow);
            service.Memberships.Add((workspaceId, userId));
            var controller = BuildController(service, new FakeCurrentUser { UserId = userId, Name = "u" });

            var ok = Assert.IsType<OkObjectResult>(await controller.GetMe(CancellationToken.None));

            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var ws = doc.RootElement.GetProperty("workspaces")[0];
            Assert.Equal(roleEsperado, ws.GetProperty("role").GetString());
            var perm = ws.GetProperty("permissions");
            Assert.Equal(canEdit, perm.GetProperty("canEdit").GetBoolean());
            Assert.Equal(canPublish, perm.GetProperty("canPublish").GetBoolean());
            Assert.Equal(canManage, perm.GetProperty("canManageMembers").GetBoolean());
        }

        [Fact]
        public async Task GetMe_falha_no_servico_degrada_para_503_nao_para_workspace_vazio_de_mentira()
        {
            var service = new FakeIdentityWorkspaceService { ThrowOnGetMe = new InvalidOperationException("SQL fora do ar") };
            var controller = BuildController(service, new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.GetMe(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
        }

        // --- GET /api/workspaces/{workspaceId} — isolamento cross-workspace ---

        [Fact]
        public async Task GetWorkspace_membro_recebe_200()
        {
            var service = new FakeIdentityWorkspaceService();
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            service.Workspaces[workspaceId] = new WorkspaceSummary(workspaceId, "Workspace A", "personal", "owner", DateTimeOffset.UtcNow);
            service.Memberships.Add((workspaceId, userId));

            var controller = BuildController(service, new FakeCurrentUser { UserId = userId });

            var result = await controller.GetWorkspace(workspaceId, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
        }

        /// <summary>
        /// 🔴 O TESTE QUE MORDE (critério de aceite #2 do contrato cross-repo + instrução literal do
        /// dono no pedido de #225: "se o filtro de membership for removido/quebrado, o teste deve
        /// falhar"). Usuário B nunca tem membership no workspace de A — se o filtro do dublê (ou do
        /// SQL real, cujo shape é o mesmo <c>WHERE m.UserId = @UserId</c>) for removido, esta asserção
        /// vira <c>OkObjectResult</c> e o teste fica vermelho.
        /// </summary>
        [Fact]
        public async Task GetWorkspace_usuario_B_nao_le_workspace_de_usuario_A()
        {
            var service = new FakeIdentityWorkspaceService();
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();
            var workspaceDeA = Guid.NewGuid();
            service.Workspaces[workspaceDeA] = new WorkspaceSummary(workspaceDeA, "Workspace da Alice", "personal", "owner", DateTimeOffset.UtcNow);
            service.Memberships.Add((workspaceDeA, userA));
            // userB deliberadamente SEM membership em workspaceDeA.

            var controllerComoB = BuildController(service, new FakeCurrentUser { UserId = userB });

            var result = await controllerComoB.GetWorkspace(workspaceDeA, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        /// <summary>
        /// "Não existe" e "existe, mas não é meu" devem ser INDISTINGUÍVEIS pelo status HTTP — os
        /// dois casos abaixo respondem exatamente o mesmo <c>NotFoundResult</c>, sem corpo diferente
        /// que permita enumeração de workspace por ID.
        /// </summary>
        [Fact]
        public async Task GetWorkspace_inexistente_e_nao_membro_respondem_o_mesmo_404()
        {
            var service = new FakeIdentityWorkspaceService();
            var userId = Guid.NewGuid();
            var outroDono = Guid.NewGuid();
            var workspaceDeOutroDono = Guid.NewGuid();
            var workspaceInexistente = Guid.NewGuid();
            service.Workspaces[workspaceDeOutroDono] = new WorkspaceSummary(workspaceDeOutroDono, "Não é meu", "personal", "owner", DateTimeOffset.UtcNow);
            service.Memberships.Add((workspaceDeOutroDono, outroDono));

            var controller = BuildController(service, new FakeCurrentUser { UserId = userId });

            var resultadoNaoMembro = await controller.GetWorkspace(workspaceDeOutroDono, CancellationToken.None);
            var resultadoInexistente = await controller.GetWorkspace(workspaceInexistente, CancellationToken.None);

            Assert.IsType<NotFoundResult>(resultadoNaoMembro);
            Assert.IsType<NotFoundResult>(resultadoInexistente);
        }

        [Fact]
        public async Task GetWorkspace_sem_identidade_resolvida_retorna_404_uniforme_nao_401()
        {
            // Sem UserId não há membership possível — mesmo 404 dos outros casos, para não revelar
            // se o recurso existe a quem não provou identidade.
            var controller = BuildController(new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = null });

            var result = await controller.GetWorkspace(Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task GetWorkspace_falha_no_servico_degrada_para_503()
        {
            var service = new FakeIdentityWorkspaceService { ThrowOnGetWorkspace = new InvalidOperationException("SQL fora do ar") };
            var controller = BuildController(service, new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.GetWorkspace(Guid.NewGuid(), CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
        }
    }
}
