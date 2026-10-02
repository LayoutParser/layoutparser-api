using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Email;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Controllers
{
    /// <summary>Corpo de <c>POST</c> de membro.</summary>
    public sealed record AddWorkspaceMemberRequest(string? Email, string? Role);

    /// <summary>Corpo de <c>PATCH</c> de membro.</summary>
    public sealed record ChangeWorkspaceMemberRoleRequest(string? Role);

    /// <summary>
    /// Membros de workspace de time (pedido do portal, thread "adicionar membros por e-mail").
    /// Autorização: nível Administrador (<c>fiscal_admin</c>/<c>owner</c>) no workspace da rota
    /// (<see cref="RequireWorkspaceRoleAttribute"/>) — não-membro = 404, membro sem papel = 403.
    /// Erros sempre com corpo JSON <c>{ error }</c>.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/members")]
    [RequireWorkspaceRole(WorkspaceRoleLevel.Admin)]
    public class WorkspaceMembersController : ControllerBase
    {
        // "owner" só é atribuído pelo sistema (criação do workspace) — nunca via API.
        private static readonly HashSet<string> AssignableRoles = new(StringComparer.Ordinal)
        {
            WorkspaceRole.FiscalAdmin, WorkspaceRole.Operator, WorkspaceRole.Viewer
        };

        private const string PapelInvalido = "Papel inválido. Aceitos: fiscal_admin, operator, viewer.";
        private const string PapelLegado = "Papel legado: use operator no lugar de mapper/reviewer.";

        /// <summary>Valida o papel pedido; null = ok, senão a mensagem de 400 (legado tem mensagem própria).</summary>
        private static string? ValidarPapel(string? role)
        {
            if (string.IsNullOrEmpty(role)) return PapelInvalido;
            if (role is WorkspaceRole.Mapper or WorkspaceRole.Reviewer) return PapelLegado;
            return AssignableRoles.Contains(role) ? null : PapelInvalido;
        }

        private readonly IWorkspaceMemberStore _members;
        private readonly IIdentityWorkspaceService _workspaces;
        private readonly ICurrentUser _currentUser;
        private readonly IEmailOutboxStore _outbox;
        private readonly IEmailSender _emailSender;
        private readonly EmailOptions _emailOptions;
        private readonly ILogger<WorkspaceMembersController> _logger;

        public WorkspaceMembersController(
            IWorkspaceMemberStore members,
            IIdentityWorkspaceService workspaces,
            ICurrentUser currentUser,
            IEmailOutboxStore outbox,
            IEmailSender emailSender,
            IOptions<EmailOptions> emailOptions,
            ILogger<WorkspaceMembersController> logger)
        {
            _outbox = outbox;
            _emailSender = emailSender;
            _emailOptions = emailOptions.Value;
            _members = members;
            _workspaces = workspaces;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Lista membros (Admin+; D4 assumido, a confirmar).</summary>
        [HttpGet]
        public async Task<IActionResult> List(Guid workspaceId, CancellationToken cancellationToken)
        {
            return await Guarded(workspaceId, cancellationToken, async workspace =>
            {
                var list = await _members.ListAsync(workspaceId, cancellationToken);
                var invite = await InviteStatusesAsync(workspaceId, cancellationToken);
                return Ok(list.Select(m => ToDto(m, invite)));
            });
        }

        [HttpPost]
        public async Task<IActionResult> Add(Guid workspaceId, [FromBody] AddWorkspaceMemberRequest? body, CancellationToken cancellationToken)
        {
            var email = WorkspaceEmail.Normalize(body?.Email);
            if (email == null)
                return BadRequest(new { error = "E-mail inválido." });

            var role = body?.Role?.Trim();
            var erroPapel = ValidarPapel(role);
            if (erroPapel != null)
                return BadRequest(new { error = erroPapel });

            return await Guarded(workspaceId, cancellationToken, async workspace =>
            {
                var result = await _members.AddAsync(workspaceId, email, role, _currentUser.UserId!.Value, cancellationToken);
                if (result.Kind is AddMemberKind.Added or AddMemberKind.Invited)
                    await TryEnqueueWelcomeAsync(workspace, email, cancellationToken);
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
            var erroPapel = ValidarPapel(role);
            if (erroPapel != null)
                return BadRequest(new { error = erroPapel });

            return await Guarded(workspaceId, cancellationToken, async workspace =>
                Outcome(await _members.ChangeRoleAsync(workspaceId, userId, role, cancellationToken), noContentOnOk: true));
        }

        [HttpDelete("{userId:guid}")]
        public async Task<IActionResult> Remove(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        {
            return await Guarded(workspaceId, cancellationToken, async workspace =>
                Outcome(await _members.RemoveAsync(workspaceId, userId, cancellationToken), noContentOnOk: true));
        }

        /// <summary>
        /// Reenvia o e-mail de convite (template <c>welcome-v1</c>) a um convite PENDENTE do workspace.
        /// O destinatário é sempre o e-mail do convite identificado por <paramref name="memberId"/> (o mesmo
        /// <c>userId</c> devolvido na listagem) — nunca um e-mail arbitrário.
        /// Respostas: 202 <c>{ emailId, status, enqueued, smtpConfigured, nextResendAvailableAt }</c>
        /// (<c>smtpConfigured=false</c> = enfileirado para rastreio, mas NÃO será enviado até configurar o SMTP);
        /// 404 membro/convite inexistente neste workspace; 409 <c>member_already_active</c>;
        /// 429 <c>resend_cooldown</c> (com <c>Retry-After</c>) ou <c>daily_limit</c>; 503 SQL indisponível.
        /// </summary>
        [HttpPost("{memberId:guid}/resend-invite")]
        [ServiceFilter(typeof(AuditActionFilter))]
        public async Task<IActionResult> ResendInvite(Guid workspaceId, Guid memberId, CancellationToken cancellationToken)
        {
            return await Guarded(workspaceId, cancellationToken, async workspace =>
            {
                var member = (await _members.ListAsync(workspaceId, cancellationToken)).FirstOrDefault(m => m.UserId == memberId);
                if (member == null || string.IsNullOrEmpty(member.Email))
                    return NotFound(new { error = "Membro não encontrado." });

                if (!string.Equals(member.Status, "pending", StringComparison.Ordinal))
                    return Conflict(new { code = "member_already_active", error = "Este membro já está ativo no workspace; não há convite a reenviar." });

                var key = workspaceId.ToString("N");
                var cooldown = TimeSpan.FromMinutes(Math.Max(1, _emailOptions.ResendCooldownMinutes));
                var now = DateTime.UtcNow;

                var ultimo = (await _outbox.ListAsync(key, member.Email, 0, 1, cancellationToken)).FirstOrDefault();
                if (ultimo != null)
                {
                    var liberaEm = DateTime.SpecifyKind(ultimo.CreatedAt, DateTimeKind.Utc) + cooldown;
                    if (liberaEm > now)
                    {
                        var segundos = (int)Math.Ceiling((liberaEm - now).TotalSeconds);
                        Response.Headers["Retry-After"] = segundos.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return StatusCode(StatusCodes.Status429TooManyRequests, new
                        {
                            code = "resend_cooldown",
                            error = $"Aguarde antes de reenviar o convite para este e-mail ({segundos}s).",
                            nextResendAvailableAt = new DateTimeOffset(liberaEm)
                        });
                    }
                }

                var enviadosHoje = await _outbox.CountSentLast24hAsync(cancellationToken);
                if (enviadosHoje >= _emailOptions.DailyLimit)
                    return StatusCode(StatusCodes.Status429TooManyRequests, new { code = "daily_limit", error = "Teto diário de e-mails atingido; tente novamente mais tarde." });

                // Chave nova por janela de cooldown: não é engolida pelo dedupe de 24h do convite original,
                // mas dois cliques dentro da mesma janela caem na mesma chave (idempotência).
                var bucket = (long)(now - DateTime.UnixEpoch).TotalSeconds / (long)cooldown.TotalSeconds;
                var dedupeKey = $"{key}:resend:{bucket}";
                var (subject, body) = WelcomeEmailTemplate.Render(workspace.Name, _emailOptions.PortalUrl);
                EnqueueResult r;
                try
                {
                    r = await _outbox.EnqueueAsync(member.Email, WelcomeEmailTemplate.Name, dedupeKey, subject, body, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Timeout do lock de dedupe (50001) ou SQL fora do ar: 503 claro e retentável, nunca 500 cru.
                    _logger.LogWarning(ex, "Outbox indisponível ao reenviar convite (WorkspaceId={WorkspaceId})",
                        workspaceId);
                    Response.Headers["Retry-After"] = "5";
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                    {
                        code = "outbox_busy",
                        error = "A fila de e-mails está ocupada no momento; tente novamente em instantes."
                    });
                }
                _logger.LogInformation("Reenvio de convite: e-mail {EmailId} (Enfileirado={Enqueued}, Template={Template}, WorkspaceId={WorkspaceId}, SmtpConfigured={SmtpConfigured})",
                    r.EmailId, r.Enqueued, WelcomeEmailTemplate.Name, workspaceId, _emailSender.IsConfigured);

                return StatusCode(StatusCodes.Status202Accepted, new
                {
                    emailId = r.EmailId,
                    status = "pending",
                    enqueued = r.Enqueued,
                    smtpConfigured = _emailSender.IsConfigured,
                    nextResendAvailableAt = new DateTimeOffset(now + cooldown)
                });
            });
        }

        /// <summary>Boas-vindas best-effort: qualquer falha aqui NUNCA desfaz o vínculo já gravado.</summary>
        private async Task TryEnqueueWelcomeAsync(WorkspaceSummary workspace, string email, CancellationToken cancellationToken)
        {
            // Sempre registra a linha (mesmo sem SMTP): o rastreio mostra "pending" + smtpConfigured=false.
            try
            {
                var (subject, body) = WelcomeEmailTemplate.Render(workspace.Name, _emailOptions.PortalUrl);
                var r = await _outbox.EnqueueAsync(email, WelcomeEmailTemplate.Name, workspace.WorkspaceId.ToString("N"), subject, body, cancellationToken);
                if (r.Enqueued)
                    _logger.LogInformation("E-mail {EmailId} enfileirado (Template={Template}, WorkspaceId={WorkspaceId}, SmtpConfigured={SmtpConfigured})",
                        r.EmailId, WelcomeEmailTemplate.Name, workspace.WorkspaceId, _emailSender.IsConfigured);
                else
                    _logger.LogInformation("E-mail deduplicado (já existe {EmailId} nas últimas 24h; Template={Template}, WorkspaceId={WorkspaceId})",
                        r.EmailId, WelcomeEmailTemplate.Name, workspace.WorkspaceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Não foi possível enfileirar o e-mail de boas-vindas do workspace {WorkspaceId}.", workspace.WorkspaceId);
            }
        }

        private IActionResult Outcome(MemberChangeOutcome outcome, bool noContentOnOk) => outcome switch
        {
            MemberChangeOutcome.Ok => noContentOnOk ? NoContent() : Ok(),
            MemberChangeOutcome.NotFound => NotFound(new { error = "Membro não encontrado." }),
            MemberChangeOutcome.LastAdmin => Conflict(new { error = "Não é possível remover ou rebaixar o último owner/fiscal_admin do workspace." }),
            _ => Conflict(new { error = "O papel owner não pode ser alterado nem removido pela API." })
        };

        /// <summary>Só workspace de time aceita membros; SQL fora do ar → 503 (fail-closed), nunca 500 opaco.</summary>
        private async Task<IActionResult> Guarded(Guid workspaceId, CancellationToken cancellationToken, Func<WorkspaceSummary, Task<IActionResult>> action)
        {
            try
            {
                var workspace = await _workspaces.GetWorkspaceForMemberAsync(workspaceId, _currentUser.UserId!.Value, cancellationToken);
                if (workspace == null)
                    return NotFound(new { error = "Workspace não encontrado." });

                if (!string.Equals(workspace.Kind, WorkspaceKind.Team, StringComparison.Ordinal))
                    return Conflict(new { error = "Workspace pessoal não aceita membros." });

                return await action(workspace);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao operar membros do workspace {WorkspaceId}", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível processar a operação no momento." });
            }
        }

        /// <summary>Status do último e-mail de convite por destinatário; best-effort (falha = sem o campo).</summary>
        private async Task<IReadOnlyDictionary<string, string>?> InviteStatusesAsync(Guid workspaceId, CancellationToken ct)
        {
            try { return await _outbox.GetLatestStatusByEmailAsync(workspaceId.ToString("N"), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Não foi possível obter o status dos e-mails de convite do workspace {WorkspaceId}.", workspaceId);
                return null;
            }
        }

        private static object ToDto(WorkspaceMemberInfo m, IReadOnlyDictionary<string, string>? invite = null) => new
        {
            inviteEmailStatus = m.Email != null && invite != null && invite.TryGetValue(m.Email, out var st) ? st : null,
            userId = m.UserId,
            displayName = m.DisplayName,
            email = m.Email,
            role = WorkspaceRole.Canonical(m.Role),
            status = m.Status,
            createdAt = m.CreatedAt
        };
    }
}
