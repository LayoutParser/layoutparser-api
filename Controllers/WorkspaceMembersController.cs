using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>Corpo de <c>POST</c> de membro.</summary>
    public sealed record AddWorkspaceMemberRequest(string? Email, string? Role);

    /// <summary>Corpo de <c>PATCH</c> de membro.</summary>
    public sealed record ChangeWorkspaceMemberRoleRequest(string? Role);

    /// <summary>
    /// Membros de workspace de time (pedido do portal, thread "adicionar membros por e-mail").
    /// Autorização: papel <c>owner</c>/<c>fiscal_admin</c> do workspace da rota
    /// (<see cref="RequireWorkspaceRoleAttribute"/>) — não-membro = 404, membro sem papel = 403.
    /// Erros sempre com corpo JSON <c>{ error }</c>.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/members")]
    [RequireWorkspaceRole(WorkspaceRole.Owner, WorkspaceRole.FiscalAdmin)]
    public class WorkspaceMembersController : ControllerBase
    {
        // "owner" só é atribuído pelo sistema (criação do workspace) — nunca via API.
        private static readonly HashSet<string> AssignableRoles = new(StringComparer.Ordinal)
        {
            WorkspaceRole.FiscalAdmin, WorkspaceRole.Mapper, WorkspaceRole.Reviewer,
            WorkspaceRole.Operator, WorkspaceRole.Viewer
        };

        private readonly IWorkspaceMemberStore _members;
        private readonly IIdentityWorkspaceService _workspaces;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<WorkspaceMembersController> _logger;

        public WorkspaceMembersController(
            IWorkspaceMemberStore members,
            IIdentityWorkspaceService workspaces,
            ICurrentUser currentUser,
            ILogger<WorkspaceMembersController> logger)
        {
            _members = members;
            _workspaces = workspaces;
            _currentUser = currentUser;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> List(Guid workspaceId, CancellationToken cancellationToken)
        {
            return await Guarded(workspaceId, cancellationToken, async () =>
            {
                var list = await _members.ListAsync(workspaceId, cancellationToken);
                return Ok(list.Select(ToDto));
            });
        }

        [HttpPost]
        public async Task<IActionResult> Add(Guid workspaceId, [FromBody] AddWorkspaceMemberRequest? body, CancellationToken cancellationToken)
        {
            var email = WorkspaceEmail.Normalize(body?.Email);
            if (email == null)
                return BadRequest(new { error = "E-mail inválido." });

            var role = body?.Role?.Trim();
            if (string.IsNullOrEmpty(role) || !AssignableRoles.Contains(role))
                return BadRequest(new { error = "Papel inválido. Aceitos: fiscal_admin, mapper, reviewer, operator, viewer." });

            return await Guarded(workspaceId, cancellationToken, async () =>
            {
                var result = await _members.AddAsync(workspaceId, email, role, _currentUser.UserId!.Value, cancellationToken);
                return result.Kind switch
                {
                    AddMemberKind.Added or AddMemberKind.Invited => StatusCode(StatusCodes.Status201Created, ToDto(result.Member)),
                    AddMemberKind.AlreadyInvited => Ok(ToDto(result.Member)), // convite idempotente
                    _ => Conflict(new { error = "Este e-mail já é membro do workspace." })
                };
            });
        }

        [HttpPatch("{userId:guid}")]
        public async Task<IActionResult> ChangeRole(Guid workspaceId, Guid userId, [FromBody] ChangeWorkspaceMemberRoleRequest? body, CancellationToken cancellationToken)
        {
            var role = body?.Role?.Trim();
            if (string.IsNullOrEmpty(role) || !AssignableRoles.Contains(role))
                return BadRequest(new { error = "Papel inválido. Aceitos: fiscal_admin, mapper, reviewer, operator, viewer." });

            return await Guarded(workspaceId, cancellationToken, async () =>
                Outcome(await _members.ChangeRoleAsync(workspaceId, userId, role, cancellationToken), noContentOnOk: true));
        }

        [HttpDelete("{userId:guid}")]
        public async Task<IActionResult> Remove(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        {
            return await Guarded(workspaceId, cancellationToken, async () =>
                Outcome(await _members.RemoveAsync(workspaceId, userId, cancellationToken), noContentOnOk: true));
        }

        private IActionResult Outcome(MemberChangeOutcome outcome, bool noContentOnOk) => outcome switch
        {
            MemberChangeOutcome.Ok => noContentOnOk ? NoContent() : Ok(),
            MemberChangeOutcome.NotFound => NotFound(new { error = "Membro não encontrado." }),
            MemberChangeOutcome.LastAdmin => Conflict(new { error = "Não é possível remover ou rebaixar o último owner/fiscal_admin do workspace." }),
            _ => Conflict(new { error = "O papel owner não pode ser alterado nem removido pela API." })
        };

        /// <summary>Só workspace de time aceita membros; SQL fora do ar → 503 (fail-closed), nunca 500 opaco.</summary>
        private async Task<IActionResult> Guarded(Guid workspaceId, CancellationToken cancellationToken, Func<Task<IActionResult>> action)
        {
            try
            {
                var workspace = await _workspaces.GetWorkspaceForMemberAsync(workspaceId, _currentUser.UserId!.Value, cancellationToken);
                if (workspace == null)
                    return NotFound(new { error = "Workspace não encontrado." });

                if (!string.Equals(workspace.Kind, WorkspaceKind.Team, StringComparison.Ordinal))
                    return Conflict(new { error = "Workspace pessoal não aceita membros." });

                return await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao operar membros do workspace {WorkspaceId}", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível processar a operação no momento." });
            }
        }

        private static object ToDto(WorkspaceMemberInfo m) => new
        {
            userId = m.UserId,
            displayName = m.DisplayName,
            email = m.Email,
            role = m.Role,
            status = m.Status,
            createdAt = m.CreatedAt
        };
    }
}
