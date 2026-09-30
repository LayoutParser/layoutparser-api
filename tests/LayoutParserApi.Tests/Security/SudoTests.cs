using System.Net;

using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>Papel sudo (item 9): só por config da API, sob a guarda de confiança, com UserId resolvido.</summary>
    public class SudoTests
    {
        private sealed class FakeIdentity : IIdentityWorkspaceService
        {
            public Guid? UserId { get; set; } = Guid.NewGuid();
            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken) => Task.FromResult(UserId);
            public Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private static async Task<HttpContext> RunAsync(IPAddress ip, string? email, Guid? userId, params string[] sudoEmails)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = ip;
            context.Request.Headers["x-layoutparser-identity-provider"] = "google";
            context.Request.Headers["x-layoutparser-identity-subject"] = "sub";
            if (email != null)
                context.Request.Headers["x-layoutparser-identity-email"] = email;

            var options = new TrustedIdentityOptions { SudoEmails = sudoEmails.ToList() };
            var middleware = new TrustedIdentityMiddleware(_ => Task.CompletedTask, Options.Create(options), NullLogger<TrustedIdentityMiddleware>.Instance);
            await middleware.InvokeAsync(context, new CurrentUser(), new FakeIdentity { UserId = userId });
            return context;
        }

        [Fact]
        public async Task Email_configurado_vira_sudo_ignorando_caixa()
        {
            var ctx = await RunAsync(IPAddress.Loopback, "Admin@Empresa.com", Guid.NewGuid(), "admin@empresa.com");
            Assert.True(SudoContext.IsSudo(ctx));
        }

        [Fact]
        public async Task Email_fora_da_lista_nao_e_sudo()
        {
            var ctx = await RunAsync(IPAddress.Loopback, "outro@empresa.com", Guid.NewGuid(), "admin@empresa.com");
            Assert.False(SudoContext.IsSudo(ctx));
        }

        [Fact]
        public async Task Lista_vazia_ninguem_e_sudo()
        {
            var ctx = await RunAsync(IPAddress.Loopback, "admin@empresa.com", Guid.NewGuid());
            Assert.False(SudoContext.IsSudo(ctx));
        }

        [Fact]
        public async Task Origem_nao_confiavel_nunca_e_sudo_mesmo_com_email_certo()
        {
            var ctx = await RunAsync(IPAddress.Parse("172.25.32.42"), "admin@empresa.com", Guid.NewGuid(), "admin@empresa.com");
            Assert.False(SudoContext.IsSudo(ctx));
        }

        [Fact]
        public async Task Sem_UserId_resolvido_nao_e_sudo()
        {
            var ctx = await RunAsync(IPAddress.Loopback, "admin@empresa.com", null, "admin@empresa.com");
            Assert.False(SudoContext.IsSudo(ctx));
        }

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name => "x";
            public IReadOnlyList<string> Roles => Array.Empty<string>();
            public bool IsAuthenticated => true;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        private static async Task<(ActionExecutingContext Ctx, bool NextCalled)> RunFilterAsync(bool sudo, Guid? userId)
        {
            var http = new DefaultHttpContext();
            if (sudo)
                http.Items[SudoContext.ItemKey] = true;

            var ctx = new ActionExecutingContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

            var nextCalled = false;
            var filter = new RequireSudoFilter(new FakeCurrentUser { UserId = userId }, NullLogger<RequireSudoFilter>.Instance);
            await filter.OnActionExecutionAsync(ctx, () =>
            {
                nextCalled = true;
                return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object()));
            });
            return (ctx, nextCalled);
        }

        [Fact]
        public async Task Filtro_nao_sudo_recebe_404_e_nao_executa_a_acao()
        {
            var (ctx, next) = await RunFilterAsync(sudo: false, userId: Guid.NewGuid());
            Assert.IsType<NotFoundResult>(ctx.Result);
            Assert.False(next);
        }

        [Fact]
        public async Task Filtro_sudo_sem_UserId_recebe_404()
        {
            var (ctx, next) = await RunFilterAsync(sudo: true, userId: null);
            Assert.IsType<NotFoundResult>(ctx.Result);
            Assert.False(next);
        }

        [Fact]
        public async Task Filtro_sudo_executa_a_acao()
        {
            var (ctx, next) = await RunFilterAsync(sudo: true, userId: Guid.NewGuid());
            Assert.Null(ctx.Result);
            Assert.True(next);
        }
    }
}
