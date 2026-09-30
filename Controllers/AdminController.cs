using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Visão global do super-administrador (<b>sudo</b>) — somente leitura. Separada dos endpoints de
    /// workspace: o 404 para membros comuns continua igual. Não-sudo recebe 404 (rota não revela existência)
    /// e cada acesso concedido é auditado (<see cref="RequireSudoAttribute"/> + <see cref="AuditActionFilter"/>).
    /// </summary>
    /// <summary>Corpo de <c>PATCH /api/admin/workspaces/{id}</c>: <c>kind</c> só aceita <c>team</c> (promoção); <c>name</c> 1..120.</summary>
    public sealed record UpdateAdminWorkspaceRequest(string? Kind, string? Name);

    [ApiController]
    [Route("api/admin")]
    [RequireSudo]
    [ServiceFilter(typeof(AuditActionFilter))]
    public class AdminController : ControllerBase
    {
        private const int MaxPageSize = 200;

        private readonly IAdminDirectoryStore _directory;
        private readonly IWorkspaceMemberStore _members;
        private readonly ILogger<AdminController> _logger;

        public AdminController(IAdminDirectoryStore directory, IWorkspaceMemberStore members, ILogger<AdminController> logger)
        {
            _directory = directory;
            _members = members;
            _logger = logger;
        }

        [HttpGet("workspaces")]
        public Task<IActionResult> Workspaces([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
            => Run(async () =>
            {
                var (s, t) = Page(skip, take);
                var list = await _directory.ListWorkspacesAsync(s, t, cancellationToken);
                return Ok(list.Select(w => new
                {
                    workspaceId = w.WorkspaceId, name = w.Name, kind = w.Kind, ownerUserId = w.OwnerUserId,
                    memberCount = w.MemberCount, createdAt = w.CreatedAt
                }));
            });

        /// <summary>
        /// Promove um workspace pessoal a time e/ou renomeia, mantendo dono e dados. Só sudo; auditado.
        /// </summary>
        [HttpPatch("workspaces/{workspaceId:guid}")]
        public Task<IActionResult> UpdateWorkspace(Guid workspaceId, [FromBody] UpdateAdminWorkspaceRequest? body, CancellationToken cancellationToken = default)
        {
            var kind = body?.Kind?.Trim();
            if (kind != null && !string.Equals(kind, LayoutParserApi.Models.Entities.Identity.WorkspaceKind.Team, StringComparison.Ordinal))
                return Task.FromResult<IActionResult>(BadRequest(new { error = "kind só aceita 'team' (promoção de workspace pessoal)." }));

            string? name = null;
            if (body?.Name != null)
            {
                name = new string(body.Name.Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (name.Length is < 1 or > 120)
                    return Task.FromResult<IActionResult>(BadRequest(new { error = "Nome deve ter entre 1 e 120 caracteres." }));
            }

            if (kind == null && name == null)
                return Task.FromResult<IActionResult>(BadRequest(new { error = "Informe kind e/ou name." }));

            return Run(async () =>
            {
                var outcome = await _directory.UpdateWorkspaceAsync(workspaceId, name, kind != null, cancellationToken);
                if (outcome == WorkspaceUpdateOutcome.NotFound)
                    return NotFound(new { error = "Workspace não encontrado." });

                _logger.LogWarning("AUDITORIA sudo: workspace {WorkspaceId} atualizado (promoveuParaTime={Promoveu}, renomeou={Renomeou})",
                    workspaceId, kind != null, name != null);
                return NoContent();
            });
        }

        [HttpGet("workspaces/{workspaceId:guid}/members")]
        public Task<IActionResult> WorkspaceMembers(Guid workspaceId, CancellationToken cancellationToken = default)
            => Run(async () =>
            {
                if (!await _directory.WorkspaceExistsAsync(workspaceId, cancellationToken))
                    return NotFound(new { error = "Workspace não encontrado." });

                var list = await _members.ListAsync(workspaceId, cancellationToken);
                return Ok(list.Select(m => new
                {
                    userId = m.UserId, displayName = m.DisplayName, email = m.Email,
                    role = m.Role, status = m.Status, createdAt = m.CreatedAt
                }));
            });

        [HttpGet("users")]
        public Task<IActionResult> Users([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
            => Run(async () =>
            {
                var (s, t) = Page(skip, take);
                var list = await _directory.ListUsersAsync(s, t, cancellationToken);
                return Ok(list.Select(u => new
                {
                    userId = u.UserId, email = u.Email, workspaceCount = u.WorkspaceCount, createdAt = u.CreatedAt
                }));
            });

        private static (int Skip, int Take) Page(int skip, int take) =>
            (Math.Max(0, skip), Math.Clamp(take, 1, MaxPageSize));

        private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha em consulta administrativa.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar no momento." });
            }
        }
    }
}
