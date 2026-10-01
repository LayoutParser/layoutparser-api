using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>Corpo do <c>PUT .../questions/{questionIndex}/answer</c> (issue #422).</summary>
    public sealed class SaveQuestionAnswerRequest
    {
        public string? Answer { get; set; }
    }

    /// <summary>
    /// Resposta livre do revisor a uma pergunta em aberto da IA (<c>OpenQuestions</c> da regra) —
    /// issue #422. Persistência apenas: NÃO alimenta nenhum dataset de treino. Se a resposta puder
    /// virar exemplo de treino no futuro, isso passa pela curadoria da issue #346 (fora deste escopo).
    /// </summary>
    /// <remarks>
    /// A pergunta não tem id estável (é <c>string</c> em lista), então a chave é o índice 0-based em
    /// <c>OpenQuestions</c>. Risco: se a lista de perguntas de uma regra mudasse, o índice apontaria
    /// para outra pergunta — hoje a lista é imutável após o INSERT da regra (o PATCH não a altera; regra
    /// nova substitui via <c>superseded</c>), e cada resposta guarda um snapshot do texto da pergunta.
    /// </remarks>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public class MappingRuleAnswersController : ControllerBase
    {
        /// <summary>Limite do texto da resposta (caracteres).</summary>
        public const int MaxAnswerLength = 4000;

        private readonly IMappingDraftStore _draftStore;
        private readonly IMappingRuleAnswerStore _answerStore;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingRuleAnswersController> _logger;

        public MappingRuleAnswersController(
            IMappingDraftStore draftStore,
            IMappingRuleAnswerStore answerStore,
            ICurrentUser currentUser,
            ILogger<MappingRuleAnswersController> logger)
        {
            _draftStore = draftStore;
            _answerStore = answerStore;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>
        /// Registra a resposta do revisor a uma pergunta da IA. Idempotente por chave
        /// (draft+regra+pergunta): reenviar o MESMO texto não cria nada (200, mesma versão); enviar texto
        /// DIFERENTE cria uma nova versão (histórico append-only, nada é sobrescrito).
        /// </summary>
        /// <remarks>
        /// RBAC: owner/fiscal_admin/mapper/reviewer. <c>400</c> texto vazio ou &gt; 4000 caracteres;
        /// <c>404</c> sem identidade, não-membro, draft de outro workspace, regra ou pergunta inexistente.
        /// Corpo: <c>{ "answer": "texto" }</c> (aparado com Trim antes de comparar/gravar).
        /// </remarks>
        /// <param name="workspaceId">Workspace da rota (membership conferida pelo filtro de RBAC).</param>
        /// <param name="draftId">Draft dono da regra.</param>
        /// <param name="ruleId">Regra que contém a pergunta.</param>
        /// <param name="questionIndex">Índice 0-based da pergunta na lista <c>OpenQuestions</c> da regra.</param>
        /// <param name="request">Corpo com o campo <c>answer</c>.</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200">Resposta vigente: <c>answerId</c>, <c>draftId</c>, <c>ruleId</c>, <c>questionIndex</c>, <c>question</c> (snapshot do texto da pergunta), <c>answer</c>, <c>answeredByUserId</c>, <c>answeredByName</c>, <c>answeredAt</c>, <c>version</c>. Reenvio idêntico devolve a versão existente; texto diferente cria a versão N+1.</response>
        /// <response code="400">Texto vazio ou acima de 4000 caracteres.</response>
        /// <response code="404">Sem identidade, não-membro, draft de outro workspace, regra ou <c>questionIndex</c> inexistente.</response>
        /// <response code="503">Falha ao gravar (detalhe só no log).</response>
        [HttpPut("mapping-drafts/{draftId:guid}/rules/{ruleId:guid}/questions/{questionIndex:int}/answer")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        public async Task<IActionResult> SaveAnswer(
            Guid workspaceId, Guid draftId, Guid ruleId, int questionIndex,
            [FromBody] SaveQuestionAnswerRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var answer = request?.Answer?.Trim();
            if (string.IsNullOrWhiteSpace(answer))
                return BadRequest(new { error = "O campo \"answer\" é obrigatório e não pode ser vazio." });
            if (answer.Length > MaxAnswerLength)
                return BadRequest(new { error = $"O campo \"answer\" excede o limite de {MaxAnswerLength} caracteres." });

            try
            {
                var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
                if (draft == null || draft.WorkspaceId != workspaceId)
                    return NotFound();

                var rule = draft.Rules.FirstOrDefault(r => r.RuleId == ruleId);
                if (rule == null || questionIndex < 0 || questionIndex >= rule.OpenQuestions.Count)
                    return NotFound();

                var (saved, created) = await _answerStore.SaveAnswerAsync(
                    workspaceId, draftId, ruleId, questionIndex, rule.OpenQuestions[questionIndex],
                    answer, userId, _currentUser.Name, cancellationToken);

                _logger.LogInformation(
                    "Resposta do revisor à pergunta {QuestionIndex} da regra {RuleId} (draft {DraftId}): versão {Version}, criada={Created}.",
                    questionIndex, ruleId, draftId, saved.Version, created);

                return Ok(ToResponse(saved));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao gravar resposta do revisor (draft {DraftId}, regra {RuleId}).", draftId, ruleId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível gravar a resposta no momento." });
            }
        }

        /// <summary>
        /// Lê as respostas de um draft (todas as regras). Só a versão mais recente por pergunta, salvo
        /// <c>includeHistory=true</c>. Qualquer papel de membro.
        /// </summary>
        /// <param name="workspaceId">Workspace da rota (membership conferida pelo filtro de RBAC).</param>
        /// <param name="draftId">Draft cujas respostas serão listadas.</param>
        /// <param name="includeHistory">Se <c>true</c>, devolve todas as versões (append-only) de cada pergunta, não só a mais recente.</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200"><c>{ draftId, ruleId, includeHistory, items[] }</c> (<c>ruleId</c> nulo neste endpoint; itens no formato do <c>PUT</c>).</response>
        /// <response code="404">Sem identidade, não-membro ou draft de outro workspace.</response>
        /// <response code="503">Falha ao consultar (detalhe só no log).</response>
        [HttpGet("mapping-drafts/{draftId:guid}/question-answers")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        public Task<IActionResult> ListByDraft(Guid workspaceId, Guid draftId, [FromQuery] bool includeHistory = false, CancellationToken cancellationToken = default)
            => ListAsync(workspaceId, draftId, null, includeHistory, cancellationToken);

        /// <summary>Lê as respostas de uma regra específica. Mesmo contrato de <see cref="ListByDraft"/>.</summary>
        /// <param name="workspaceId">Workspace da rota (membership conferida pelo filtro de RBAC).</param>
        /// <param name="draftId">Draft dono da regra.</param>
        /// <param name="ruleId">Regra cujas respostas serão listadas.</param>
        /// <param name="includeHistory">Se <c>true</c>, devolve todas as versões de cada pergunta.</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200"><c>{ draftId, ruleId, includeHistory, items[] }</c>.</response>
        /// <response code="404">Sem identidade, não-membro, draft de outro workspace ou regra inexistente no draft.</response>
        /// <response code="503">Falha ao consultar (detalhe só no log).</response>
        [HttpGet("mapping-drafts/{draftId:guid}/rules/{ruleId:guid}/question-answers")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        public Task<IActionResult> ListByRule(Guid workspaceId, Guid draftId, Guid ruleId, [FromQuery] bool includeHistory = false, CancellationToken cancellationToken = default)
            => ListAsync(workspaceId, draftId, ruleId, includeHistory, cancellationToken);

        private async Task<IActionResult> ListAsync(Guid workspaceId, Guid draftId, Guid? ruleId, bool includeHistory, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            try
            {
                var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
                if (draft == null || draft.WorkspaceId != workspaceId)
                    return NotFound();

                if (ruleId.HasValue && draft.Rules.All(r => r.RuleId != ruleId.Value))
                    return NotFound();

                var items = await _answerStore.ListAsync(draftId, ruleId, includeHistory, cancellationToken);
                return Ok(new { draftId, ruleId, includeHistory, items = items.Select(ToResponse) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar respostas do revisor (draft {DraftId}).", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar as respostas no momento." });
            }
        }

        private static object ToResponse(MappingRuleAnswer a) => new
        {
            answerId = a.AnswerId,
            draftId = a.DraftId,
            ruleId = a.RuleId,
            questionIndex = a.QuestionIndex,
            question = a.QuestionText,
            answer = a.AnswerText,
            answeredByUserId = a.AnsweredByUserId,
            answeredByName = a.AnsweredByName,
            answeredAt = a.AnsweredAt,
            version = a.Version,
        };
    }
}
