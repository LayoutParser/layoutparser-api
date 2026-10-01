using LayoutParserApi.Services.Filters;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Identidade + workspace fiscal (Slice 1 — issue #225/#228). Implementa
    /// <c>GET /api/workspaces/me</c> e <c>GET /api/workspaces/{workspaceId}</c> conforme o contrato
    /// cross-repo <c>fiscal-workspace-and-mapping-explanation-api.md</c> §2. Não há <c>[Authorize]</c>
    /// global na API ainda (ver <c>.claude/rules/security.md</c>) — este controller exige
    /// <c>ICurrentUser.UserId</c> resolvido diretamente, fail-closed.
    /// </summary>
    [ApiController]
    [Route("api/workspaces")]
    public class WorkspacesController : ControllerBase
    {
        private readonly IIdentityWorkspaceService _identityWorkspaceService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<WorkspacesController> _logger;

        public WorkspacesController(
            IIdentityWorkspaceService identityWorkspaceService,
            ICurrentUser currentUser,
            ILogger<WorkspacesController> logger)
        {
            _identityWorkspaceService = identityWorkspaceService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>
        /// Usuário atual + workspaces em que é membro. Cria o workspace pessoal de forma idempotente
        /// na primeira chamada. Exige identidade resolvida (headers <c>x-layoutparser-identity-*</c>
        /// sob a guarda de loopback) — sem isso, 401 explícito (primeiro endpoint em que "anônimo
        /// responde algo" deixa de fazer sentido, per recomendação da auditoria §6).
        /// </summary>
        [HttpGet("me")]
        public async Task<IActionResult> GetMe(CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return Unauthorized(new { error = "Identidade não resolvida." });

            try
            {
                var result = await _identityWorkspaceService.GetOrCreateMyWorkspacesAsync(userId, cancellationToken);

                return Ok(new
                {
                    activeWorkspaceId = result.ActiveWorkspaceId,
                    // Flag de super-administrador (decidido pela API a partir do e-mail verificado do BFF).
                    isSudo = HttpContext is { } http && LayoutParserApi.Services.Security.SudoContext.IsSudo(http),
                    workspaces = result.Workspaces.Select(w => new
                    {
                        workspaceId = w.WorkspaceId,
                        name = w.Name,
                        kind = w.Kind,
                        // Id canônico: legados mapper/reviewer saem como operator.
                        role = WorkspaceRole.Canonical(w.Role),
                        permissions = new
                        {
                            canEdit = WorkspaceRole.AtLeast(w.Role, WorkspaceRoleLevel.Operator),
                            canPublish = WorkspaceRole.AtLeast(w.Role, WorkspaceRoleLevel.Admin),
                            canManageMembers = WorkspaceRole.AtLeast(w.Role, WorkspaceRoleLevel.Admin)
                        },
                        createdAt = w.CreatedAt
                    })
                });
            }
            catch (Exception ex)
            {
                // Fail-closed: SQL fora do ar não deve devolver workspace nenhum "de mentira".
                // Nunca logar UserId junto de dado sensível — aqui é só o GUID interno, não o subject.
                _logger.LogError(ex, "Falha ao resolver/criar workspaces do usuário {UserId}", userId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar workspaces no momento." });
            }
        }

        /// <summary>
        /// Detalhe de um workspace — só se o usuário atual for membro. "Não existe" e "existe, mas é
        /// de outro usuário" respondem o MESMO 404 (nunca 403), para não permitir enumeração de
        /// workspace por ID (contrato cross-repo §2, critério de aceite #2).
        /// </summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("{workspaceId:guid}")]
        public async Task<IActionResult> GetWorkspace(Guid workspaceId, CancellationToken cancellationToken)
        {
            // Sem identidade resolvida não há membership possível — mesmo 404 uniforme, não 401/403,
            // para não vazar se o recurso existe.
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            WorkspaceSummary? workspace;
            try
            {
                workspace = await _identityWorkspaceService.GetWorkspaceForMemberAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar workspace {WorkspaceId} para o usuário {UserId}", workspaceId, userId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o workspace no momento." });
            }

            if (workspace == null)
                return NotFound();

            return Ok(new
            {
                workspaceId = workspace.WorkspaceId,
                name = workspace.Name,
                kind = workspace.Kind,
                role = workspace.Role,
                createdAt = workspace.CreatedAt
            });
        }
    }
}
