using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Histórico de análises fiscais por workspace (issue #366, ADR
    /// <c>docs/architecture/adr-historico-analises-fiscais-366.md</c>). Uma "análise" é o conjunto de
    /// arquivos que o usuário anexou (documento + layout) em <c>POST /api/parse/upload</c> ou
    /// <c>/auto</c> com <c>workspaceId</c>. Isolamento por DONO: só quem criou a análise a lê, baixa ou
    /// apaga — análise de outro membro do workspace responde 404 (indistinguível de inexistente).
    /// O registro NÃO acontece aqui (evita reenviar arquivos de até 50 MB): é feito dentro do parse.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/analyses")]
    [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
    public class FiscalAnalysesController : ControllerBase
    {
        private const int DefaultPageSize = 20;
        private const int MaxPageSize = 100;

        private readonly IFiscalAnalysisService _service;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<FiscalAnalysesController> _logger;

        public FiscalAnalysesController(IFiscalAnalysisService service, ICurrentUser currentUser, ILogger<FiscalAnalysesController> logger)
        {
            _service = service;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Lista, do mais recente ao mais antigo, as análises do usuário atual neste workspace.</summary>
        /// <param name="workspaceId">Workspace da rota (membership conferida pelo filtro).</param>
        /// <param name="page">Página, base 1 (default 1).</param>
        /// <param name="pageSize">Itens por página (default 20, máximo 100).</param>
        /// <response code="200"><c>{ page, pageSize, total, items[] }</c> — só análises do dono e ainda não expiradas.
        /// Cada item: <c>analysisId</c>, <c>createdAt</c>, <c>expiresAt</c>, <c>source</c> ('upload'|'auto'),
        /// <c>layoutName</c> (NUNCA nulo/vazio: LayoutName gravado, senão o LayoutGuid, senão "Layout não informado";
        /// o dado gravado não é alterado), <c>layoutGuid</c> (NULLABLE: nulo em layouts anexados por arquivo),
        /// <c>detectedType</c> (NULLABLE), <c>fileCount</c>, <c>totalSizeBytes</c>.</response>
        /// <response code="400">Paginação inválida.</response>
        /// <response code="404">Não é membro do workspace.</response>
        /// <response code="503">Banco de identidade indisponível.</response>
        [HttpGet]
        public async Task<IActionResult> List(Guid workspaceId, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, CancellationToken cancellationToken = default)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();
            if (page < 1 || pageSize < 1 || pageSize > MaxPageSize)
                return BadRequest(new { error = $"page deve ser >= 1 e pageSize entre 1 e {MaxPageSize}." });

            try
            {
                var result = await _service.ListAsync(workspaceId, userId, page, pageSize, cancellationToken);
                return Ok(new
                {
                    result.Page,
                    result.PageSize,
                    result.Total,
                    Items = result.Items.Select(i => new
                    {
                        analysisId = i.AnalysisId,
                        createdAt = i.CreatedAtUtc,
                        expiresAt = i.ExpiresAtUtc,
                        source = i.Source,
                        layoutName = FiscalAnalysisLayoutDisplay.Resolve(i.LayoutName, i.LayoutGuid),
                        layoutGuid = i.LayoutGuid,
                        detectedType = i.DetectedType,
                        fileCount = i.FileCount,
                        totalSizeBytes = i.TotalSizeBytes
                    })
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Unavailable(ex, workspaceId);
            }
        }

        /// <summary>Detalhe de uma análise: layout usado e arquivos anexados (sem caminho de disco).</summary>
        /// <response code="200"><c>{ analysisId, workspaceId, createdAt, expiresAt, source, detectedType, layout, files[] }</c>.
        /// <c>layout.layoutName</c> nunca é nulo/vazio (mesmo fallback da listagem); <c>layout.layoutGuid</c> e
        /// <c>detectedType</c> são NULLABLE.</response>
        /// <response code="404">Análise inexistente, expirada, de outro usuário/workspace.</response>
        /// <response code="503">Banco de identidade indisponível.</response>
        [HttpGet("{analysisId:guid}")]
        public async Task<IActionResult> Get(Guid workspaceId, Guid analysisId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            try
            {
                var detail = await _service.GetAsync(workspaceId, userId, analysisId, cancellationToken);
                if (detail == null)
                    return NotFound();

                var a = detail.Analysis;
                var layoutFile = detail.Files.FirstOrDefault(f => f.Role == FiscalAnalysisFileRole.Layout);
                return Ok(new
                {
                    analysisId = a.AnalysisId,
                    workspaceId = a.WorkspaceId,
                    createdAt = a.CreatedAtUtc,
                    expiresAt = a.ExpiresAtUtc,
                    source = a.Source,
                    detectedType = a.DetectedType,
                    layout = new
                    {
                        mode = a.LayoutMode,
                        layoutGuid = a.LayoutGuid,
                        layoutName = FiscalAnalysisLayoutDisplay.Resolve(a.LayoutName, a.LayoutGuid),
                        fileId = layoutFile?.AnalysisFileId
                    },
                    files = detail.Files.Select(f => new
                    {
                        fileId = f.AnalysisFileId,
                        role = f.Role,
                        fileName = f.OriginalFileName,
                        sizeBytes = f.SizeBytes,
                        sha256 = f.Sha256,
                        downloadUrl = $"/api/workspaces/{a.WorkspaceId}/analyses/{a.AnalysisId}/files/{f.AnalysisFileId}"
                    })
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Unavailable(ex, workspaceId);
            }
        }

        /// <summary>Baixa um arquivo da análise (sha256 conferido na leitura).</summary>
        /// <response code="200">Bytes do arquivo (<c>application/octet-stream</c>, <c>nosniff</c>, <c>attachment</c>).</response>
        /// <response code="404">Análise/arquivo inexistente, expirado ou de outro usuário.</response>
        /// <response code="500">Integridade do arquivo quebrada (hash divergente) — conteúdo não é servido.</response>
        /// <response code="503">Banco de identidade indisponível.</response>
        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpGet("{analysisId:guid}/files/{fileId:guid}")]
        public async Task<IActionResult> DownloadFile(Guid workspaceId, Guid analysisId, Guid fileId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            try
            {
                var file = await _service.OpenFileAsync(workspaceId, userId, analysisId, fileId, cancellationToken);
                switch (file.Status)
                {
                    case FiscalAnalysisFileStatus.NotFound:
                        return NotFound();
                    case FiscalAnalysisFileStatus.IntegrityFailure:
                        return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Falha de integridade no arquivo armazenado." });
                }

                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(file.Content!, "application/octet-stream", file.FileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Unavailable(ex, workspaceId);
            }
        }

        /// <summary>Exclusão sob demanda pelo dono (apaga linha SQL + arquivos em disco).</summary>
        /// <response code="204">Removida.</response>
        /// <response code="404">Análise inexistente ou de outro usuário.</response>
        /// <response code="503">Banco de identidade indisponível.</response>
        [ServiceFilter(typeof(AuditActionFilter))]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpDelete("{analysisId:guid}")]
        public async Task<IActionResult> Delete(Guid workspaceId, Guid analysisId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            try
            {
                return await _service.DeleteAsync(workspaceId, userId, analysisId, cancellationToken) ? NoContent() : NotFound();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Unavailable(ex, workspaceId);
            }
        }

        private IActionResult Unavailable(Exception ex, Guid workspaceId)
        {
            _logger.LogError(ex, "Histórico de análises indisponível (workspace={WorkspaceId}).", workspaceId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Histórico de análises temporariamente indisponível." });
        }
    }
}
