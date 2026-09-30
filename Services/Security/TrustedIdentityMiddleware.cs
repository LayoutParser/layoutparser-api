using System.Net;
using System.Security.Claims;

using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Extrai a identidade que o BFF injeta (<c>x-iis-user</c> / <c>x-iis-roles</c>) e a expõe à
    /// requisição via <see cref="ICurrentUser"/> (e no slot padrão <c>HttpContext.User</c>), para
    /// autorização por papel e auditoria.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>A GUARDA DE LOOPBACK É O QUE TORNA ISTO SEGURO.</b> A API escuta em
    /// <c>0.0.0.0:5000</c> (toda a rede alcança). Se confiasse nos headers sem guarda, qualquer host
    /// mandaria <c>x-iis-user: admin</c> direto na <c>:5000</c> e viraria admin, pulando BFF e Entra.
    /// O BFF é co-hospedado, então o salto BFF→API chega como <b>loopback</b> (<c>127.0.0.1</c>/
    /// <c>::1</c>). Uma conexão de outro host <b>não</b> é loopback → os headers são <b>ignorados por
    /// completo</b> e a identidade fica anônima. Isso fecha o vetor mesmo antes de a rede ser trancada
    /// (bindar em <c>127.0.0.1</c>, trabalho do <c>@lp-devops</c>) — a trava de rede é a segunda camada.</para>
    ///
    /// <para>Degrada sem exceção: fora de loopback, sem header, ou header vazio → segue anônimo. A
    /// postura (guarda ativa/inativa) é logada UMA vez no arranque (Program.cs), nunca por request —
    /// o log é relido pelo painel.</para>
    /// </remarks>
    public class TrustedIdentityMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly TrustedIdentityOptions _options;
        private readonly ILogger<TrustedIdentityMiddleware> _logger;

        public TrustedIdentityMiddleware(
            RequestDelegate next,
            IOptions<TrustedIdentityOptions> options,
            ILogger<TrustedIdentityMiddleware> logger)
        {
            _next = next;
            _options = options.Value;
            _logger = logger;
        }

        // ICurrentUser é resolvido do escopo da requisição (Scoped) — a MESMA instância que controllers
        // e filtros recebem. Preenchê-la aqui torna a identidade disponível para todo o pipeline abaixo.
        // identityWorkspaceService também é Scoped (Slice 1, issue #225) — resolve
        // x-layoutparser-identity-* para o UserId interno, sob a MESMA guarda de loopback.
        public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, IIdentityWorkspaceService identityWorkspaceService)
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLoopback = remoteIp != null && IPAddress.IsLoopback(remoteIp);

            var inTrustedNetwork = TrustedIdentityPolicy.IsInTrustedNetworks(remoteIp, _options.TrustedProxyNetworks);

            if (TrustedIdentityPolicy.ShouldTrust(isLoopback, _options.TrustIdentityFromLoopbackOnly, inTrustedNetwork))
            {
                var nome = context.Request.Headers[_options.TrustedUserHeader].ToString().Trim();

                if (!string.IsNullOrEmpty(nome))
                {
                    var papeis = TrustedIdentityPolicy.ParseRoles(
                        context.Request.Headers[_options.TrustedRolesHeader].ToString());

                    // Fonte da verdade da identidade da requisição (auditoria lê daqui).
                    if (currentUser is CurrentUser cu)
                        cu.Set(nome, papeis);

                    // Também popula o slot padrão do ASP.NET: deixa o enforcement por papel PRONTO
                    // (um [Authorize(Roles=...)] futuro, quando UseAuthorization entrar) sem ligá-lo agora.
                    context.User = ConstruirPrincipal(nome, papeis);
                }

                // ✅ Slice 1 (issue #225): headers novos convivem com os legados acima (x-iis-*) —
                // nenhum dos dois é removido nesta fase. Ausência dos headers novos = UserId fica
                // null, sem inferir workspace a partir de Name (recriaria o anti-padrão que #225
                // quer eliminar — ver auditoria-slice1-identidade-workspaces-2026-08-31.md §5).
                var provider = context.Request.Headers[_options.IdentityProviderHeader].ToString().Trim();
                var subject = context.Request.Headers[_options.IdentitySubjectHeader].ToString().Trim();

                if (!string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(subject))
                {
                    var tenant = context.Request.Headers[_options.IdentityTenantHeader].ToString().Trim();

                    // Nunca lança: ResolveOrCreateUserAsync já degrada para null em falha de SQL
                    // (fail-closed — ver IdentityWorkspaceService). subject não é logado aqui.
                    var userId = await identityWorkspaceService.ResolveOrCreateUserAsync(provider, tenant, subject, context.RequestAborted);
                    if (userId is Guid resolvedUserId && currentUser is CurrentUser cuIdentity)
                        cuIdentity.SetUserId(resolvedUserId);

                    // E-mail é best-effort: falha aqui NUNCA derruba a requisição nem invalida o UserId.
                    var email = LayoutParserApi.Services.Identity.WorkspaceEmail.Normalize(context.Request.Headers[_options.IdentityEmailHeader].ToString());
                    // Sudo: só sob a guarda de confiança, com UserId resolvido e e-mail (verificado pelo BFF)
                    // na lista configurada. Marca só o HttpContext desta requisição (Items não vem do cliente).
                    if (userId != null && email != null && _options.SudoEmails.Any(e =>
                            string.Equals(LayoutParserApi.Services.Identity.WorkspaceEmail.Normalize(e), email, StringComparison.Ordinal)))
                        context.Items[SudoContext.ItemKey] = true;

                    if (userId is Guid emailUserId && email != null
                        && context.RequestServices?.GetService<IWorkspaceMemberStore>() is { } memberStore)
                    {
                        try
                        {
                            await memberStore.SyncEmailAndRedeemInvitesAsync(emailUserId, email, context.RequestAborted);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Falha ao sincronizar e-mail/convites do usuário {UserId}.", emailUserId);
                        }
                    }
                }
            }
            // else / sem header / header vazio: identidade anônima. Nunca lança.

            await _next(context);
        }

        private static ClaimsPrincipal ConstruirPrincipal(string nome, IReadOnlyList<string> papeis)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, nome) };
            foreach (var papel in papeis)
                claims.Add(new Claim(ClaimTypes.Role, papel));

            // authenticationType não-vazio é o que faz Identity.IsAuthenticated ser true.
            var identity = new ClaimsIdentity(claims, authenticationType: "TrustedHeader");
            return new ClaimsPrincipal(identity);
        }
    }
}
