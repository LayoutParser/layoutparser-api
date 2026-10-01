using LayoutParserApi.Services.Filters;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Pacote de mapeamento fiscal (Slice 2 — issue #229). Só os dois endpoints previstos neste slice:
    /// upload (cria pacote + primeira revisão) e consulta. Isolamento por workspace fail-closed, mesmo
    /// padrão do <see cref="WorkspacesController"/> (Slice 1) — "não existe" e "não é seu" respondem o
    /// mesmo 404.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}")]
    public class FiscalMappingPackagesController : ControllerBase
    {
        /// <summary>Limite de artefatos por upload — evita form-data com centenas de partes por engano/abuso.</summary>
        private const int MaxArtifactsPerUpload = 10;

        private readonly IFiscalPackageService _packageService;
        private readonly IIdentityWorkspaceService _identityWorkspaceService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<FiscalMappingPackagesController> _logger;

        public FiscalMappingPackagesController(
            IFiscalPackageService packageService,
            IIdentityWorkspaceService identityWorkspaceService,
            ICurrentUser currentUser,
            ILogger<FiscalMappingPackagesController> logger)
        {
            _packageService = packageService;
            _identityWorkspaceService = identityWorkspaceService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>
        /// Cria um <c>FiscalMappingPackage</c> (com sua primeira revisão) a partir de um upload
        /// multipart. Cada arquivo é identificado pelo NOME DO CAMPO do form (ex.: campo "sample" =
        /// <see cref="ArtifactKind.Sample"/>) — nomes fora de <see cref="ArtifactKind.All"/> são
        /// rejeitados com 422, sem inferência silenciosa.
        /// </summary>
        // Limite de tamanho total do request: 10 artefatos * limite por artefato — margem generosa
        // sobre MaxArtifactsPerUpload, rejeitado explicitamente antes de bufferizar tudo em memória.
        // SCS0016 (issue #88): mesmo padrão já aceito em ParseController.Upload — sem cookie de
        // sessão, identidade via BFF/TrustedIdentityMiddleware com guarda de loopback.
#pragma warning disable SCS0016
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpPost("projects/{projectId:guid}/mapping-packages")]
        [RequestSizeLimit(10 * Services.Validation.MultipartUploadValidator.MaxArtifactSizeBytes)]
        public async Task<IActionResult> CreatePackage(
            Guid workspaceId,
            Guid projectId,
            [FromForm] string? name,
            CancellationToken cancellationToken)
#pragma warning restore SCS0016
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound(); // Fail-closed uniforme — mesmo padrão do Slice 1.

            // Isolamento por workspace: confirma membership ANTES de tocar em qualquer artefato.
            WorkspaceSummary? membership;
            try
            {
                membership = await _identityWorkspaceService.GetWorkspaceForMemberAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao verificar membership do workspace {WorkspaceId} para upload de pacote.", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível verificar o workspace no momento." });
            }

            if (membership == null)
                return NotFound();

            if (Request.Form.Files.Count == 0)
                return UnprocessableEntity(new { error = "Nenhum artefato enviado." });

            if (Request.Form.Files.Count > MaxArtifactsPerUpload)
                return UnprocessableEntity(new { error = $"Excede o limite de {MaxArtifactsPerUpload} artefatos por upload." });

            var artifacts = new List<UploadedArtifactInput>();
            foreach (var file in Request.Form.Files)
            {
                // O NOME DO CAMPO do multipart é o Kind — allowlist explícita, sem adivinhar pela extensão.
                if (!ArtifactKind.IsValid(file.Name))
                    return UnprocessableEntity(new { error = $"Campo de upload desconhecido: \"{file.Name}\". Esperado um de: {string.Join(", ", ArtifactKind.All)}." });

                if (file.Length == 0)
                    return UnprocessableEntity(new { error = $"Artefato \"{file.Name}\" está vazio." });

                if (file.Length > Services.Validation.MultipartUploadValidator.MaxArtifactSizeBytes)
                    return UnprocessableEntity(new { error = $"Artefato \"{file.Name}\" excede o limite de tamanho." });

                using var memoryStream = new MemoryStream();
                await file.CopyToAsync(memoryStream, cancellationToken);

                // ✅ issue #341: campo de texto opcional "{kind}Provenance" (ex.: "sampleProvenance")
                // — proveniência declarada pelo analista, NUNCA inferida. Ausente/inválido vira null
                // e o serviço trata como amostra real (fail-closed, ver ArtifactProvenance.IsValid).
                var provenance = Request.Form.TryGetValue($"{file.Name}Provenance", out var provenanceValue)
                    ? provenanceValue.ToString()
                    : null;

                artifacts.Add(new UploadedArtifactInput(file.Name, file.FileName, file.ContentType, memoryStream.ToArray(), provenance));
            }

            var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var headerValue) ? headerValue.ToString() : null;
            var packageName = string.IsNullOrWhiteSpace(name) ? $"Pacote {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : name;

            CreatePackageOutcome outcome;
            try
            {
                outcome = await _packageService.CreatePackageAsync(workspaceId, projectId, userId, packageName, idempotencyKey, artifacts, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar pacote de mapeamento fiscal (workspace={WorkspaceId}, project={ProjectId}).", workspaceId, projectId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível criar o pacote no momento." });
            }

            if (!outcome.Success)
                return UnprocessableEntity(new { error = outcome.Error });

            var package = outcome.Package!;
            return CreatedAtAction(nameof(GetPackage), new { workspaceId, packageId = package.PackageId }, ToResponse(package));
        }

        /// <summary>
        /// Lista os projetos fiscais do workspace (Gap 1 — issue #201/#229). Leitura pura — NÃO é o
        /// CRUD completo de projeto descartado na decisão original da issue #229 (ver
        /// <see cref="FiscalProject"/>); existe só para o front-end navegar/selecionar projeto sem
        /// exigir o GUID colado manualmente.
        /// </summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("projects")]
        public async Task<IActionResult> ListProjects(Guid workspaceId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            WorkspaceSummary? membership;
            try
            {
                membership = await _identityWorkspaceService.GetWorkspaceForMemberAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao verificar membership do workspace {WorkspaceId} para listar projetos.", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível listar os projetos no momento." });
            }

            if (membership == null)
                return NotFound();

            IReadOnlyList<Services.Interfaces.ProjectSummary> projects;
            try
            {
                projects = await _packageService.ListProjectsAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao listar projetos do workspace {WorkspaceId}.", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível listar os projetos no momento." });
            }

            return Ok(new
            {
                projects = projects.Select(p => new
                {
                    projectId = p.ProjectId,
                    workspaceId = p.WorkspaceId,
                    name = p.Name,
                    createdAt = p.CreatedAt
                })
            });
        }

        /// <summary>
        /// Cria uma nova revisão de um pacote já existente (Gap 2 — issue #201). Mesmo formato
        /// multipart de <see cref="CreatePackage"/> — cada arquivo identificado pelo NOME DO CAMPO.
        /// </summary>
#pragma warning disable SCS0016
        [RequireWorkspaceRole(WorkspaceRoleLevel.Operator)]
        [HttpPost("mapping-packages/{packageId:guid}/revisions")]
        [RequestSizeLimit(10 * Services.Validation.MultipartUploadValidator.MaxArtifactSizeBytes)]
        public async Task<IActionResult> CreateRevision(
            Guid workspaceId,
            Guid packageId,
            CancellationToken cancellationToken)
#pragma warning restore SCS0016
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (Request.Form.Files.Count == 0)
                return UnprocessableEntity(new { error = "Nenhum artefato enviado." });

            if (Request.Form.Files.Count > MaxArtifactsPerUpload)
                return UnprocessableEntity(new { error = $"Excede o limite de {MaxArtifactsPerUpload} artefatos por upload." });

            var artifacts = new List<UploadedArtifactInput>();
            foreach (var file in Request.Form.Files)
            {
                if (!ArtifactKind.IsValid(file.Name))
                    return UnprocessableEntity(new { error = $"Campo de upload desconhecido: \"{file.Name}\". Esperado um de: {string.Join(", ", ArtifactKind.All)}." });

                if (file.Length == 0)
                    return UnprocessableEntity(new { error = $"Artefato \"{file.Name}\" está vazio." });

                if (file.Length > Services.Validation.MultipartUploadValidator.MaxArtifactSizeBytes)
                    return UnprocessableEntity(new { error = $"Artefato \"{file.Name}\" excede o limite de tamanho." });

                using var memoryStream = new MemoryStream();
                await file.CopyToAsync(memoryStream, cancellationToken);

                // ✅ issue #341: campo de texto opcional "{kind}Provenance" (ex.: "sampleProvenance")
                // — proveniência declarada pelo analista, NUNCA inferida. Ausente/inválido vira null
                // e o serviço trata como amostra real (fail-closed, ver ArtifactProvenance.IsValid).
                var provenance = Request.Form.TryGetValue($"{file.Name}Provenance", out var provenanceValue)
                    ? provenanceValue.ToString()
                    : null;

                artifacts.Add(new UploadedArtifactInput(file.Name, file.FileName, file.ContentType, memoryStream.ToArray(), provenance));
            }

            CreateRevisionOutcome outcome;
            try
            {
                outcome = await _packageService.CreateRevisionAsync(workspaceId, packageId, userId, artifacts, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar revisão do pacote de mapeamento fiscal {PackageId} (workspace={WorkspaceId}).", packageId, workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível criar a revisão no momento." });
            }

            if (outcome.NotFound)
                return NotFound();

            if (!outcome.Success)
                return UnprocessableEntity(new { error = outcome.Error });

            return CreatedAtAction(nameof(GetPackage), new { workspaceId, packageId }, ToResponse(outcome.Package!));
        }

        /// <summary>
        /// Inventário de estrutura (abas/colunas/linhas) de um artefato <c>spec</c> (XLSX) da revisão
        /// mais recente (Gap 3 — issue #201) — reusa <see cref="Services.Fiscal.FiscalMappingRuleExtractor"/>,
        /// sem devolver o conteúdo bruto da planilha.
        /// </summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("mapping-packages/{packageId:guid}/artifacts/{artifactId:guid}/excel-inventory")]
        public async Task<IActionResult> GetExcelInventory(Guid workspaceId, Guid packageId, Guid artifactId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            ExcelInventoryOutcome outcome;
            try
            {
                outcome = await _packageService.GetExcelInventoryAsync(workspaceId, packageId, artifactId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao gerar inventário do artefato {ArtifactId} (pacote={PackageId}).", artifactId, packageId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível gerar o inventário no momento." });
            }

            if (outcome.NotFound)
                return NotFound();

            if (!outcome.Success)
                return UnprocessableEntity(new { error = outcome.Error });

            var inventory = outcome.Inventory!;
            return Ok(new
            {
                decisionSheets = inventory.DecisionSheets.Select(s => new
                {
                    sheetName = s.SheetName,
                    columns = s.Columns,
                    ruleCount = s.RuleCount
                }),
                skippedSheets = inventory.SkippedSheets
            });
        }

        /// <summary>Pacote + inventário de artefatos da revisão mais recente. Nunca expõe conteúdo bruto.</summary>
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        [HttpGet("mapping-packages/{packageId:guid}")]
        public async Task<IActionResult> GetPackage(Guid workspaceId, Guid packageId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            PackageDetail? package;
            try
            {
                package = await _packageService.GetPackageIfMemberAsync(packageId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar pacote {PackageId} para o usuário {UserId}.", packageId, userId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o pacote no momento." });
            }

            // "Não existe" e "existe mas é de outro workspace" respondem o mesmo 404 — mesmo padrão do
            // isolamento cross-workspace do Slice 1. Também cobre o caso de workspaceId da rota não
            // bater com o dono real do pacote (não confiamos no parâmetro de rota sozinho).
            if (package == null || package.WorkspaceId != workspaceId)
                return NotFound();

            // ✅ issue #424: sinais de qualidade são ADITIVOS e opcionais — qualquer falha aqui degrada
            // (log + resposta sem os campos), nunca derruba o GET da revisão.
            IReadOnlyDictionary<Guid, Services.Fiscal.SpecQualityResult>? quality = null;
            try
            {
                quality = await _packageService.GetSpecQualityAsync(package, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao calcular sinais de qualidade do pacote {PackageId}.", packageId);
            }

            return Ok(ToResponse(package, quality));
        }

        private static object ToResponse(PackageDetail package, IReadOnlyDictionary<Guid, Services.Fiscal.SpecQualityResult>? quality = null) => new
        {
            packageId = package.PackageId,
            workspaceId = package.WorkspaceId,
            projectId = package.ProjectId,
            name = package.Name,
            createdAt = package.CreatedAt,
            revisions = new[]
            {
                new
                {
                    revisionId = package.LatestRevision.RevisionId,
                    revisionNumber = package.LatestRevision.RevisionNumber,
                    createdAt = package.LatestRevision.CreatedAt,
                    artifacts = package.LatestRevision.Artifacts.Select(a => new
                    {
                        artifactId = a.ArtifactId,
                        kind = a.Kind,
                        sha256 = a.Sha256,
                        sizeBytes = a.SizeBytes,
                        originalFileName = a.OriginalFileName,
                        inspectionStatus = a.InspectionStatus,
                        uploadedAt = a.UploadedAt,
                        provenance = a.Provenance,
                        // ✅ issue #424 (aditivo): só artefatos spec analisados. checksRun diz o que foi
                        // realmente verificado — array vazio só é "sem problema" se o check constar lá.
                        qualityStatus = quality != null && quality.TryGetValue(a.ArtifactId, out var q) ? q.Status : null,
                        qualityError = quality != null && quality.TryGetValue(a.ArtifactId, out var qe) ? qe.Error : null,
                        qualitySignals = quality != null && quality.TryGetValue(a.ArtifactId, out var qs) && qs.Signals != null
                            ? new
                            {
                                missingRequiredColumns = qs.Signals.MissingRequiredColumns,
                                conflicts = qs.Signals.Conflicts.Select(c => new { field = c.Field, reason = c.Reason }),
                                absentReferences = qs.Signals.AbsentReferences,
                                skippedSheets = qs.Signals.SkippedSheets,
                                emptySheets = qs.Signals.EmptySheets,
                                checksRun = qs.Signals.ChecksRun,
                            }
                            : null,
                    })
                }
            }
        };
    }
}
