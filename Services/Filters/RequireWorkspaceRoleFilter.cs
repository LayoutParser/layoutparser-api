using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    /// <summary>
    /// RBAC mínimo escopado (Slice 7 — issue #94, design §2): checa se o usuário atual tem um dos
    /// papéis informados no <c>WorkspaceMembership</c> do workspace da rota (<c>{workspaceId}</c>).
    /// Não é RBAC genérico pra toda a API — nasce só pros 3 endpoints de governança de
    /// <c>MappingRelease</c> (approve/publish/rollback), mas reutilizável depois via
    /// <see cref="RequireWorkspaceRoleAttribute"/> em qualquer controller com <c>{workspaceId:guid}</c>
    /// na rota.
    /// </summary>
    /// <remarks>
    /// Distinção de status (design §2 + spec): sem <c>ICurrentUser.UserId</c> ou sem membership no
    /// workspace → <b>404</b> (mesmo padrão fail-closed dos Slices 1-5, indistinguível de "não existe").
    /// Membro do workspace mas com papel insuficiente → <b>403</b> — aqui o recurso É seu, só falta
    /// permissão, então não faz sentido escondê-lo atrás de um 404.
    /// </remarks>
    public sealed class RequireWorkspaceRoleAttribute : TypeFilterAttribute
    {
        /// <summary>Exige papel de nível &gt;= <paramref name="minimum"/> (hierarquia viewer &lt; operator &lt; fiscal_admin &lt; owner).</summary>
        public RequireWorkspaceRoleAttribute(WorkspaceRoleLevel minimum) : base(typeof(RequireWorkspaceRoleFilter))
        {
            Arguments = new object[] { minimum };
        }
    }

    public sealed class RequireWorkspaceRoleFilter : IAsyncActionFilter
    {
        private readonly WorkspaceRoleLevel _minimum;
        private readonly ICurrentUser _currentUser;
        private readonly IIdentityWorkspaceStore _workspaceStore;
        private readonly ILogger<RequireWorkspaceRoleFilter> _logger;

        public RequireWorkspaceRoleFilter(
            WorkspaceRoleLevel minimum,
            ICurrentUser currentUser,
            IIdentityWorkspaceStore workspaceStore,
            ILogger<RequireWorkspaceRoleFilter> logger)
        {
            _minimum = minimum;
            _currentUser = currentUser;
            _workspaceStore = workspaceStore;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (_currentUser.UserId is not Guid userId)
            {
                context.Result = new NotFoundResult();
                return;
            }

            if (!context.RouteData.Values.TryGetValue("workspaceId", out var routeValue) ||
                !Guid.TryParse(routeValue?.ToString(), out var workspaceId))
            {
                context.Result = new NotFoundResult();
                return;
            }

            var workspace = await _workspaceStore.GetWorkspaceIfMemberAsync(workspaceId, userId, context.HttpContext.RequestAborted);
            if (workspace == null)
            {
                // "Não existe" e "existe mas não é seu" continuam indistinguíveis — mesmo padrão dos slices anteriores.
                context.Result = new NotFoundResult();
                return;
            }

            if (!WorkspaceRole.AtLeast(workspace.Role, _minimum))
            {
                _logger.LogWarning(
                    "Acesso negado por papel: usuário {UserId} tem papel {Role} no workspace {WorkspaceId}; endpoint exige nível mínimo {MinimumLevel}.",
                    userId, workspace.Role, workspaceId, _minimum);
                context.Result = new ObjectResult(new { error = "Papel insuficiente para esta operação." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }

            await next();
        }
    }
}
