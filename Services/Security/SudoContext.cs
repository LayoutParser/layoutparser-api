using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Security
{
    /// <summary>Marca de "sudo" da requisição atual (definida só pelo <c>TrustedIdentityMiddleware</c>).</summary>
    public static class SudoContext
    {
        public const string ItemKey = "lp.sudo";

        public static bool IsSudo(HttpContext context) =>
            context.Items.TryGetValue(ItemKey, out var v) && v is true;
    }

    /// <summary>
    /// Restringe o endpoint a sudo. Não-sudo recebe <b>404</b> (não revela que a rota admin existe).
    /// Cada acesso concedido é registrado em log estruturado (auditoria), além do <c>AuditActionFilter</c>.
    /// </summary>
    public sealed class RequireSudoAttribute : TypeFilterAttribute
    {
        public RequireSudoAttribute() : base(typeof(RequireSudoFilter)) { }
    }

    public sealed class RequireSudoFilter : IAsyncActionFilter
    {
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<RequireSudoFilter> _logger;

        public RequireSudoFilter(ICurrentUser currentUser, ILogger<RequireSudoFilter> logger)
        {
            _currentUser = currentUser;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (_currentUser.UserId is not Guid userId || !SudoContext.IsSudo(context.HttpContext))
            {
                context.Result = new NotFoundResult();
                return;
            }

            _logger.LogWarning("AUDITORIA sudo: usuário {UserId} acessou {Method} {Path}",
                userId, context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value);

            await next();
        }
    }
}
