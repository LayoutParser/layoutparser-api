using LayoutParserApi.Models.Entities.Identity;
using System.Collections.Concurrent;
using System.Xml.Linq;

using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Models;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.StructuralResolution;
using LayoutParserApi.Services.Database;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LayoutParserApi.Services.XmlAnalysis.Models;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Fiscal;

using XslSynth.Core;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Pathway 2 de transformação - <b>canônico</b> (decisão de arquitetura, item 2.1 do
    /// dispatch de IA em docs/architecture/ai-roadmap-dispatch.md, 2026-07-21): é o pathway
    /// que o front-end de fato chama hoje. Novo trabalho de transformação (validação XSD,
    /// diagnóstico via Ollama, etc.) deve entrar aqui - ou na camada de serviço por trás
    /// dele (<see cref="TransformationPipelineService"/>/<see cref="TransformationValidatorService"/>),
    /// nunca no controller (ver item 2.2).
    /// Ver também <see cref="TransformationController"/> (Pathway 1 - legado).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ServiceFilter(typeof(AuditActionFilter))]
    public class TransformationExecutionController : ControllerBase
    {
        private readonly ILogger<TransformationExecutionController> _logger;
        private readonly TransformationPipelineService _pipelineService;
        private readonly TransformationValidatorService _validatorService;
        private readonly TransformationLearningService _learningService;
        private readonly AutoTransformationGeneratorService _autoGenerator;
        private readonly LowCodeTransformationService _lowCode;
        private readonly LowCodeAutoTransformationService _lowCodeAuto;
        private readonly ILayoutDatabaseService _layoutDb;
        private readonly LowCodeRunnerOptions _lowCodeOpt;
        private readonly IAiTransformationCandidateService _aiCandidateService;
        private readonly IAiFallbackSuppressionGate _aiFallbackGate;
        private readonly AiUserInstructionStore _aiUserInstructionStore;
        private readonly Services.Database.SqlAiUserSessionStore _aiUserSessionStore;
        private readonly ICurrentUser _currentUser;
        private readonly MapperDatabaseService _mapperDb;
        private readonly ILayoutParserService _layoutParser;
        private readonly FieldMappingCompositionService _fieldMappingComposition;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly Services.Security.ICanaryAlertService _canaryAlert;
        private readonly IFieldCorrectionStore _fieldCorrectionStore;
        private readonly TrainingDataCaptureService _trainingDataCapture;
        private readonly Services.Interfaces.IIdentityWorkspaceService? _identityWorkspaces;
        private readonly IMissingMapperGenerationTrigger? _missingMapperTrigger;

        public TransformationExecutionController(
            ILogger<TransformationExecutionController> logger,
            TransformationPipelineService pipelineService,
            TransformationValidatorService validatorService,
            TransformationLearningService learningService,
            AutoTransformationGeneratorService autoGenerator,
            LowCodeTransformationService lowCode,
            LowCodeAutoTransformationService lowCodeAuto,
            ILayoutDatabaseService layoutDb,
            IOptions<LowCodeRunnerOptions> lowCodeOptions,
            IAiTransformationCandidateService aiCandidateService,
            IAiFallbackSuppressionGate aiFallbackGate,
            AiUserInstructionStore aiUserInstructionStore,
            Services.Database.SqlAiUserSessionStore aiUserSessionStore,
            ICurrentUser currentUser,
            MapperDatabaseService mapperDb,
            ILayoutParserService layoutParser,
            FieldMappingCompositionService fieldMappingComposition,
            IServiceScopeFactory scopeFactory,
            Services.Security.ICanaryAlertService canaryAlert,
            IFieldCorrectionStore fieldCorrectionStore,
            TrainingDataCaptureService trainingDataCapture,
            Services.Interfaces.IIdentityWorkspaceService? identityWorkspaces = null,
            IMissingMapperGenerationTrigger? missingMapperTrigger = null)
        {
            _identityWorkspaces = identityWorkspaces;
            _missingMapperTrigger = missingMapperTrigger;
            _logger = logger;
            _pipelineService = pipelineService;
            _validatorService = validatorService;
            _learningService = learningService;
            _autoGenerator = autoGenerator;
            _lowCode = lowCode;
            _lowCodeAuto = lowCodeAuto;
            _layoutDb = layoutDb;
            _lowCodeOpt = lowCodeOptions.Value;
            _aiCandidateService = aiCandidateService;
            _aiFallbackGate = aiFallbackGate;
            _aiUserInstructionStore = aiUserInstructionStore;
            _aiUserSessionStore = aiUserSessionStore;
            _currentUser = currentUser;
            _mapperDb = mapperDb;
            _layoutParser = layoutParser;
            _fieldMappingComposition = fieldMappingComposition;
            _scopeFactory = scopeFactory;
            _canaryAlert = canaryAlert;
            _fieldCorrectionStore = fieldCorrectionStore;
            _trainingDataCapture = trainingDataCapture;
        }

        // Issue #92: chave de particionamento da AiCandidateStore. ICurrentUser.Name é null quando
        // anônimo (sem [Authorize] em algum endpoint futuro ou identidade não confiável) — a store já
        // trata esse caso como um bucket fixo próprio, nunca cai no de outro usuário real.
        private string CurrentUserId => _currentUser.Name ?? string.Empty;

        /// <summary>
        /// Executa transformação completa (TXT -> XML ou XML -> XML) usando o pathway tcl-xsl
        /// (canônico, único candidato). Para obter todos os candidatos plausíveis dos dois
        /// pathways, use <see cref="ExecuteTransformationCandidates"/>.
        /// </summary>
        /// <param name="request"><c>InputContent</c> e <c>LayoutName</c> são obrigatórios; <c>Validate</c> dispara validação XSD/diff do resultado.</param>
        /// <returns><c>transformedXml</c> + <c>segmentMappings</c> (indexado por linha, só para entrada MQSeries) e, se <c>Validate=true</c>, o objeto <c>validation</c>.</returns>
        /// <response code="200">Transformação concluída.</response>
        /// <response code="400"><c>InputContent</c>/<c>LayoutName</c> ausente, ou pipeline não conseguiu transformar (ver <c>errors</c>/<c>warnings</c>).</response>
        /// <response code="500">Falha não catalogada no pipeline.</response>
        [HttpPost("execute")]
        public async Task<IActionResult> ExecuteTransformation([FromBody] TransformationRequest request)
        {
            try
            {
                _logger.LogInformation("Executando transformação para layout: {LayoutName}", request.LayoutName);

                if (string.IsNullOrEmpty(request.InputContent))
                {
                    return BadRequest(new { error = "InputContent é obrigatório" });
                }

                if (string.IsNullOrEmpty(request.LayoutName))
                {
                    return BadRequest(new { error = "LayoutName é obrigatório" });
                }

                // Detectar tipo de entrada
                var isXmlInput = request.InputContent.TrimStart().StartsWith("<");

                TransformationPipelineResult result;

                if (isXmlInput)
                {
                    // Transformação XML -> XML
                    result = await _pipelineService.TransformXmlToXmlAsync(
                        request.InputContent,
                        request.SourceDocumentType ?? "NFe",
                        request.TargetDocumentType ?? "NFe",
                        request.LayoutName);
                }
                else
                {
                    // Transformação TXT -> XML
                    result = await _pipelineService.TransformTxtToXmlAsync(
                        request.InputContent,
                        request.LayoutName,
                        request.TargetDocumentType ?? "NFe");
                }

                if (result.Success)
                {
                    // Validar transformação se solicitado
                    if (request.Validate)
                    {
                        var validationResult = await _validatorService.ValidateTransformationAsync(
                            isXmlInput ? null : request.InputContent,
                            request.LayoutName,
                            result.TclPath,
                            result.XslPath,
                            request.ExpectedOutput);

                        return Ok(new
                        {
                            success = true,
                            transformedXml = result.TransformedXml,
                            validation = validationResult,
                            segmentMappings = result.SegmentMappings
                        });
                    }

                    return Ok(new
                    {
                        success = true,
                        transformedXml = result.TransformedXml,
                        segmentMappings = result.SegmentMappings
                    });
                }
                else
                {
                    // Issue #642: mesmo gatilho no endpoint execute (só para os códigos de mapeador ausente).
                    if (result.ErrorCode is "map_not_found" or "xsl_not_found")
                        _missingMapperTrigger?.TriggerForLayout(request.LayoutName, result.ErrorCode);
                    return BadRequest(new
                    {
                        success = false,
                        errors = result.Errors,
                        warnings = result.Warnings
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Endpoint-isca (honeypot) — ADR M2M
        /// (<c>docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md</c>), Parte 2.
        /// </summary>
        /// <remarks>
        /// <para>🔴 <b>ISTO É DETECÇÃO, NÃO PREVENÇÃO — não implementa nenhuma lógica de negócio
        /// real.</b> Não substitui os controles de auth reais (<see cref="TrustedIdentityMiddleware"/>,
        /// esquema <c>ServiceClient</c> da Parte 1). Imita, de propósito, um endpoint "legado"
        /// plausível dado o padrão de nomes já existente neste controller (<c>execute</c>,
        /// <c>execute-lowcode</c>, <c>execute-candidates</c>) — nenhum consumidor legítimo (React,
        /// MCP, Cypress real) jamais deveria chamar esta rota.</para>
        ///
        /// <para>Aceita QUALQUER requisição, sem exigir <c>[Authorize]</c> — é isso que torna a
        /// rota atrativa para quem está enumerando endpoints. Não parseia nem repassa o corpo a
        /// nenhum serviço real (risco zero de virar vetor de fato). Todo hit — independente do
        /// conteúdo — dispara <see cref="ICanaryAlertService"/> e responde com algo plausível
        /// (202), para não denunciar a armadilha por um comportamento diferente do resto da API.</para>
        /// </remarks>
        /// <response code="202">Sempre retornado — resposta genérica, propositalmente plausível.</response>
        [HttpPost("execute-legacy")]
        public IActionResult ExecuteLegacyHoneypot()
        {
            _canaryAlert.Raise(Services.Security.CanaryConstants.EndpointCanaryType, HttpContext);

            // Resposta plausível e genérica — não denuncia a detecção nem executa nada real.
            return Accepted(new { success = true, ticket = Guid.NewGuid().ToString() });
        }

        /// <summary>
        /// Executa transformação retornando TODOS os candidatos plausíveis dos dois pathways
        /// (sysmiddle/low-code e tcl-xsl/canônico) em vez de um resultado singular. Contrato completo
        /// (casos-limite de zero candidatos, falha parcial, timeout etc.) em
        /// docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md (Gap 1).
        /// Quando nenhum pathway resolve (Estado A — não encontrado, distinto de Estado B de falha
        /// de infra), o fallback automático de IA é disparado em background (loop gerar→validar→
        /// corrigir via Ollama), sujeito a cooldown de 4h por LayoutGuid; ver
        /// <see cref="GetAiCandidateStatus"/> para acompanhar o resultado.
        ///
        /// <para>Issue LayoutParserReact #86 (diagnóstico estruturado): a resposta traz, de forma
        /// ADITIVA (não quebra clientes existentes), <see cref="TransformationExecutionCandidatesResponse.PathwayDiagnostics"/>
        /// — um <see cref="Models.Transformation.PathwayDiagnostic"/> por pathway avaliado (sysmiddle/tcl-xsl/
        /// ai-fallback), com <c>status</c>/<c>code</c>/<c>message</c> — e
        /// <see cref="TransformationExecutionCandidatesResponse.CorrelationId"/>, que permite ao suporte
        /// cruzar a resposta HTTP com o log estruturado completo (não sanitizado) desta chamada. Toda
        /// <c>Message</c> nesse array já passou por <see cref="Services.Transformation.LowCode.LowCodeErrorSanitizer"/>
        /// — nunca contém caminho físico de disco ou detalhe interno cru.</para>
        /// </summary>
        /// <param name="request"><c>InputContent</c> e <c>LayoutName</c> obrigatórios; <c>LayoutGuid</c> opcional (tem precedência sobre o do catálogo).</param>
        /// <returns>
        /// <see cref="Models.Transformation.TransformationExecutionCandidatesResponse"/>: array <c>Candidates</c>
        /// (um por pathway/mapper que produziu XML), <c>RecommendedCandidateId</c> (melhor <c>Score</c>),
        /// <c>PathwayDiagnostics</c> (status por pathway avaliado) e <c>CorrelationId</c> para suporte.
        /// </returns>
        /// <response code="200">Ao menos um pathway foi avaliado (mesmo com <c>Candidates</c> vazio — ver <c>PathwayDiagnostics</c>).</response>
        /// <response code="400"><c>InputContent</c>/<c>LayoutName</c> ausente, ou layout não encontrado no catálogo.</response>
        /// <response code="500">Falha de infraestrutura ao consultar o catálogo de layouts.</response>
        /// <response code="504">Timeout do conjunto de candidatos (teto calculado por <c>LowCodeCandidatesBudget</c>).</response>
        // Issue #32: dispara processos externos (runner x86) e é operação privilegiada — era
        // restrita ao papel "admin". Issue #93: reabre para qualquer usuário autenticado (o
        // isolamento por dono via CurrentUserId/AiCandidateStore, já feito na issue #92, é quem
        // impede um usuário ler/afetar o ticket de outro — não mais o papel).
        [Authorize]
        [HttpPost("execute-candidates")]
        public async Task<IActionResult> ExecuteTransformationCandidates([FromBody] TransformationRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.InputContent))
                return BadRequest(new { success = false, errors = new[] { "InputContent é obrigatório" }, warnings = Array.Empty<string>() });

            if (string.IsNullOrEmpty(request.LayoutName))
                return BadRequest(new { success = false, errors = new[] { "LayoutName é obrigatório" }, warnings = Array.Empty<string>() });

            _logger.LogInformation("Executando transformação multi-candidato para layout: {LayoutName}", request.LayoutName);

            // Resolver o layout no banco: serve tanto para validar existência (400 se não encontrado)
            // e oferecer fallback de LayoutGuid ao pathway sysmiddle. O LayoutGuid enviado no request,
            // quando válido, tem precedência porque o catálogo legado pode retornar Guid.Empty.
            // Exceção aqui = falha de infra que impede sequer listar candidatos → 500 (linha
            // "Falha total de infraestrutura"
            // da tabela de decisão do contrato).
            LayoutRecord? layoutRecord;
            try
            {
                var searchResponse = await _layoutDb.SearchLayoutsAsync(new LayoutSearchRequest { SearchTerm = request.LayoutName });
                if (!searchResponse.Success)
                    throw new InvalidOperationException(searchResponse.ErrorMessage);

                layoutRecord = searchResponse.Layouts
                    .FirstOrDefault(l => string.Equals(l.Name, request.LayoutName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha de infraestrutura ao resolver layout {LayoutName} para multi-candidato", request.LayoutName);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao consultar o catálogo de layouts" });
            }

            if (layoutRecord == null)
                return BadRequest(new { success = false, errors = new[] { $"Layout '{request.LayoutName}' não encontrado" }, warnings = Array.Empty<string>() });

            var warnings = new List<string>();
            var isXmlInput = request.InputContent.TrimStart().StartsWith("<");

            // Timeout do CONJUNTO (decisão de design, não 100% especificada no contrato). São dois
            // limites diferentes e o código antes confundia os dois num número só:
            //
            //   (a) quanto o TRABALHO pode plausivelmente demorar. Os candidatos sysmiddle competem
            //       pelo mesmo semáforo do runner, então rodam em ondas de MaxConcurrentRunners; o
            //       pior caso é ceil(N / MaxConcurrentRunners) ondas de RunnerTimeoutSeconds. N é
            //       capado por MultiCandidateTopN, que é o teto real de candidatos disparados.
            //   (b) quanto o CLIENTE HTTP pode esperar — CandidatesRequestTimeoutSeconds.
            //
            // A fórmula anterior (RunnerTimeoutSeconds * MaxConcurrentRunners) errava as duas: com o
            // timeout do runner corrigido para 180s ela dava 360s de espera, e CRESCIA ao se aumentar
            // MaxConcurrentRunners — mais slots deveriam reduzir a fila, não aumentar o teto.
            var budget = LowCodeCandidatesBudget.Calculate(
                _lowCodeOpt.MultiCandidateTopN,
                _lowCodeOpt.MaxConcurrentRunners,
                _lowCodeOpt.RunnerTimeoutSeconds,
                _lowCodeOpt.CandidatesRequestTimeoutSeconds);
            var overallTimeoutSeconds = budget.EffectiveSeconds;

            // ✅ O teto CANCELA o trabalho, não só a espera. Sem isso, o 504 abaixo devolvia a resposta
            // e deixava até MaxConcurrentRunners processos x86 vivos segurando os slots do semáforo —
            // que é do PROCESSO INTEIRO da API (singleton), então travaria também os uploads de outros
            // usuários por até RunnerTimeoutSeconds. Mesmo defeito já corrigido no ParseController
            // (spec §1.1); este endpoint tinha ficado para trás, e o timeout de 180s o tornou caro.
            // Cancelar não perde trabalho: o pathway sysmiddle persiste em disco dentro da própria
            // chamada e o resultado fica consultável pelo ticket.
            using var candidatesCts = new CancellationTokenSource(TimeSpan.FromSeconds(overallTimeoutSeconds));

            // failureKinds: classificação interna (§2 do design-fallback-ia-automatico) coletada na
            // ORIGEM de cada pathway — nunca inferida depois por regex sobre warning já sanitizado.
            var failureKinds = new ConcurrentBag<FailureKind>();
            var pathwayDiagnostics = new ConcurrentBag<Models.Transformation.PathwayDiagnostic>();
            var sysmiddleTask = ExecuteSysmiddleCandidatesAsync(request, layoutRecord, isXmlInput, warnings, failureKinds, pathwayDiagnostics, candidatesCts.Token);
            var tclXslTask = ExecuteTclXslCandidatesAsync(request, isXmlInput, warnings, failureKinds, pathwayDiagnostics);

            var allTask = Task.WhenAll(sysmiddleTask, tclXslTask);
            var winner = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(overallTimeoutSeconds)));

            // ⚠️ Vencer a corrida não basta: o cancelamento é cooperativo e faz a task terminar QUASE
            // no mesmo instante do Task.Delay, devolvendo os candidatos já marcados como falha. Sem
            // checar o token, o resultado sairia às vezes como 200 com tudo falhando — que mente pior
            // que o 504, porque diz "terminou e não deu certo" quando a verdade é "não deu tempo".
            if (winner != allTask || candidatesCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Timeout do conjunto de candidatos (>{TimeoutSeconds}s, budget de trabalho {BudgetTrabalho}s em {Ondas} onda(s), teto de request {TetoRequest}s) para layout {LayoutName}",
                    overallTimeoutSeconds, budget.BudgetTrabalhoSeconds, budget.Ondas, budget.TetoRequestSeconds, request.LayoutName);
                return StatusCode(504, new { success = false, error = "Tempo limite excedido ao gerar candidatos de transformação" });
            }

            var candidates = new List<TransformationCandidate>();
            candidates.AddRange(await sysmiddleTask);
            candidates.AddRange(await tclXslTask);

            if (candidates.Count == 0)
                warnings.Add($"Nenhum candidato de transformação encontrado para o layout {request.LayoutName}");

            // Pathway IA (Issue #40): dispara só depois de ter gabarito sysmiddle disponível — nunca
            // como terceiro Task síncrono (ver docs/architecture/pathway-ia-execute-candidates.md §3).
            // Fire-and-forget: NUNCA atrasa nem derruba a resposta síncrona já calculada acima.
            TryEnqueueAiCandidate(request, layoutRecord, candidates, isXmlInput, CurrentUserId);

            // Fallback automático de IA (design-fallback-ia-automatico-2026-08-16.md §1/§2): só
            // quando NENHUM candidato foi produzido pelos dois pathways síncronos E nenhum deles
            // falhou por infra (Estado B) — aí a correção é operacional, não de transformação, e a
            // IA nunca deveria tentar "recriar" um mapper que já existe e está correto.
            if (candidates.Count == 0)
                TryEnqueueAiFallback(request, layoutRecord, isXmlInput, failureKinds, warnings, pathwayDiagnostics, CurrentUserId);

            string? recommendedId = null;
            if (candidates.Count > 0)
            {
                var bestScored = candidates.Where(c => c.Score.HasValue).OrderByDescending(c => c.Score!.Value).FirstOrDefault();
                recommendedId = bestScored?.CandidateId ?? candidates[0].CandidateId;
            }

            // DocumentId (ADR docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md §4,
            // Gap 1): identificador estável derivado de hash, mesma resolução de LayoutGuid já usada
            // pelo pathway sysmiddle (request.LayoutGuid tem precedência sobre o catálogo, que pode
            // vir Guid.Empty). Quando nenhum dos dois resolve, cai no LayoutGuid cru do catálogo —
            // ainda determinístico, nunca lança.
            var resolvedLayoutGuidForDocumentId =
                LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid)
                ?? layoutRecord.LayoutGuid.ToString();
            var documentId = DocumentIdCalculator.Calculate(request.InputContent, resolvedLayoutGuidForDocumentId);

            // tbFieldCorrectionContext (issue #345, ADR §4/§7): gravação best-effort, fire-and-forget —
            // nunca atrasa nem derruba esta resposta. Habilita o futuro POST field-correction a
            // resolver documentId -> contexto do documento.
            TryPersistFieldCorrectionContext(request, layoutRecord, candidates, documentId, resolvedLayoutGuidForDocumentId);

            return Ok(new TransformationExecutionCandidatesResponse
            {
                Success = true,
                Candidates = candidates,
                RecommendedCandidateId = recommendedId,
                Warnings = warnings,
                // pathwayDiagnostics (Issue #86): populado na origem por cada pathway (sysmiddle,
                // tcl-xsl, ai-fallback) — ver docs/architecture/diagnostico-issue-86-*.md §4.
                PathwayDiagnostics = pathwayDiagnostics.ToList(),
                CorrelationId = Services.Logging.CorrelationContext.CurrentId,
                DocumentId = documentId
            });
        }

        /// <summary>
        /// Best-effort (issue #345, ADR §4/§7): grava <c>tbFieldCorrectionContext</c> com o
        /// documentId já calculado, o gabarito sysmiddle quando existir (primeiro candidato do
        /// pathway sysmiddle) e o LayoutGuid/Name resolvidos. Mapper/GroundTruth ficam <c>null</c>
        /// quando a request não produziu candidato sysmiddle (ex.: entrada XML) — reporte de
        /// correção continua possível, só sem o gabarito ao lado. Fire-and-forget: qualquer falha
        /// (IdentityDatabase indisponível etc.) vira log, nunca afeta a resposta síncrona já calculada.
        /// </summary>
        private void TryPersistFieldCorrectionContext(
            TransformationRequest request, LayoutRecord layoutRecord, List<TransformationCandidate> candidates,
            string documentId, string resolvedLayoutGuid)
        {
            var sysmiddleCandidate = candidates.FirstOrDefault(c => c.Pathway == "sysmiddle" && !string.IsNullOrEmpty(c.TransformedXml));
            var mapperGuid = sysmiddleCandidate != null && sysmiddleCandidate.CandidateId.StartsWith("sysmiddle-", StringComparison.Ordinal)
                ? sysmiddleCandidate.CandidateId["sysmiddle-".Length..]
                : null;
            var groundTruthXml = sysmiddleCandidate?.TransformedXml;
            var layoutName = request.LayoutName;
            var inputContent = request.InputContent;
            var safeLayoutNameForLog = Services.Logging.LogMessageSanitizer.Sanitize(layoutName);

            // ✅ Mesmo padrão de TryEnqueueAiCandidate: Task.Run + CancellationToken.None, sobrevive
            // ao fim da request HTTP; IFieldCorrectionStore é Scoped, então precisa de scope próprio.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scopedStore = scope.ServiceProvider.GetRequiredService<IFieldCorrectionStore>();
                    await scopedStore.SaveContextAsync(
                        new FieldCorrectionContext(
                            documentId,
                            mapperGuid,
                            MapperName: null, // não disponível neste ponto sem consulta SQL adicional — best-effort, campo aditivo.
                            resolvedLayoutGuid,
                            layoutName,
                            inputContent,
                            groundTruthXml,
                            DateTimeOffset.UtcNow),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Falha ao persistir tbFieldCorrectionContext (best-effort) para documentId={DocumentId} layout={LayoutName}",
                        documentId, safeLayoutNameForLog);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Issue #345 (ADR docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md §3/§5/§7):
        /// reporte de correção humana de um campo divergente. Assíncrono por design — não
        /// re-executa nenhum pathway nem chama Ollama no request; só valida, resolve o contexto
        /// via <c>documentId</c> e persiste com <c>Status = pending</c> (curadoria humana é a Issue 2,
        /// fora do escopo aqui). Fail-closed via <c>_currentUser.UserId</c>, mesmo padrão de
        /// <see cref="MappingGovernanceController"/> — sem identidade resolvida, <c>404</c> (não
        /// <c>401</c>), para não distinguir "recurso inexistente" de "sem permissão".
        /// </summary>
        /// <param name="request">
        /// <c>documentId</c>/<c>candidateId</c>/<c>fieldPath</c>/<c>observedValue</c>/<c>expectedValue</c>
        /// obrigatórios; <c>justification</c> e <c>documentType</c> (só telemetria) opcionais.
        /// </param>
        /// <response code="202">Reporte registrado — <c>{ reportId, status: "queued", message }</c>.</response>
        /// <response code="400">Campo obrigatório ausente.</response>
        /// <response code="403">Usuário só tem papel de Leitor (viewer) em seus workspaces.</response>
        /// <response code="404">
        /// Sem identidade resolvida, OU <c>documentId</c> não resolve contexto persistido (mensagem
        /// pede para reenviar o parse — contexto pode ter expirado, gravação best-effort pode ter
        /// falhado, ou o id nunca existiu).
        /// </response>
        [Authorize]
        [HttpPost("field-correction")]
        public async Task<IActionResult> ReportFieldCorrection([FromBody] FieldCorrectionRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound(); // fail-closed, mesmo padrão de MappingGovernanceController.

            // RBAC 4 papéis (D3, 2026-10-01): field-correction grava na fila de curadoria => Leitor NÃO usa.
            // Exige nível Operador+ em ao menos um workspace do usuário.
            if (_identityWorkspaces != null)
            {
                try
                {
                    var meus = await _identityWorkspaces.GetOrCreateMyWorkspacesAsync(userId, cancellationToken);
                    if (!meus.Workspaces.Any(w => WorkspaceRole.AtLeast(w.Role, WorkspaceRoleLevel.Operator)))
                        return StatusCode(StatusCodes.Status403Forbidden, new { success = false, error = "Papel insuficiente para esta operação." });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Falha ao verificar papel de workspace para field-correction");
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, error = "Não foi possível verificar permissões no momento." });
                }
            }

            if (request == null || string.IsNullOrWhiteSpace(request.DocumentId))
                return BadRequest(new { success = false, error = "documentId é obrigatório" });
            if (string.IsNullOrWhiteSpace(request.CandidateId))
                return BadRequest(new { success = false, error = "candidateId é obrigatório" });
            if (string.IsNullOrWhiteSpace(request.FieldPath))
                return BadRequest(new { success = false, error = "fieldPath é obrigatório" });
            if (string.IsNullOrWhiteSpace(request.ObservedValue))
                return BadRequest(new { success = false, error = "observedValue é obrigatório" });
            if (string.IsNullOrWhiteSpace(request.ExpectedValue))
                return BadRequest(new { success = false, error = "expectedValue é obrigatório" });

            FieldCorrectionContext? context;
            try
            {
                context = await _fieldCorrectionStore.GetContextAsync(request.DocumentId, cancellationToken);
            }
            catch (Exception ex)
            {
                // Diferente da gravação (best-effort, nunca falha), a LEITURA aqui é o caminho crítico
                // do endpoint — se o IdentityDatabase estiver fora do ar, não há como resolver o
                // documentId, então degrada para 404 (mesma mensagem do caso "nunca existiu"), nunca
                // deixa a exceção subir.
                _logger.LogWarning(ex, "Falha ao consultar tbFieldCorrectionContext para documentId={DocumentId}", request.DocumentId);
                context = null;
            }

            if (context == null)
                return NotFound(new { success = false, error = "contexto do documento expirado — reenvie o parse para reportar uma correção" });

            Guid reportId;
            try
            {
                reportId = await _fieldCorrectionStore.CreateReportAsync(
                    new FieldCorrectionReportInput(
                        request.DocumentId,
                        request.CandidateId,
                        request.FieldPath,
                        request.ObservedValue,
                        request.ExpectedValue,
                        request.Justification),
                    userId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao persistir tbFieldCorrectionReport para documentId={DocumentId}", request.DocumentId);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao registrar a correção" });
            }

            _logger.LogInformation(
                "Correção humana registrada: reportId={ReportId} documentId={DocumentId} candidateId={CandidateId} usuario={UserId}",
                reportId, request.DocumentId, Services.Logging.LogMessageSanitizer.Sanitize(request.CandidateId), userId);

            return Accepted(new
            {
                reportId,
                status = "queued",
                message = "Correção registrada. Será usada para refinar o modelo em treinos futuros."
            });
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Curadoria de correção humana (issue #346, ADR §6/§8)
        //
        // NOTA DE AUTORIZAÇÃO: não existe hoje um papel dedicado de revisor fiscal no projeto —
        // os papéis em uso são "admin" (DataGenerationController, LogsController) e "operador"
        // (MapperDatabaseController). Curar o que vira dado de treino do modelo é uma operação
        // privilegiada, então cai em "admin". TODO(#346): trocar por um papel "fiscal"/"revisor"
        // quando o produto definir a matriz de papéis (ver docs/architecture/rollout-p2-autenticacao.md).
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fila de curadoria: reportes de correção humana ainda <c>pending</c> (issue #346), mais
        /// antigos primeiro. Somente <c>admin</c>.
        /// </summary>
        /// <param name="limit">Teto de itens retornados (default 100, máx. 500).</param>
        /// <response code="200"><c>{ success, count, reports[] }</c>.</response>
        /// <response code="404">Sem identidade resolvida (fail-closed).</response>
        /// <response code="500">Falha de infraestrutura ao consultar o <c>IdentityDatabase</c>.</response>
        [Authorize(Roles = "admin")]
        [HttpGet("field-correction/pending")]
        public async Task<IActionResult> ListPendingFieldCorrections([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
        {
            if (_currentUser.UserId is not Guid)
                return NotFound(); // fail-closed, mesmo padrão de ReportFieldCorrection.

            if (limit <= 0) limit = 100;
            if (limit > 500) limit = 500;

            try
            {
                var pending = await _fieldCorrectionStore.ListPendingReportsAsync(limit, cancellationToken);
                return Ok(new { success = true, count = pending.Count, reports = pending });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao listar reportes de correção pendentes");
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao listar pendências de curadoria" });
            }
        }

        /// <summary>
        /// Transição de curadoria de um reporte: <c>pending → reviewed_accepted | reviewed_rejected</c>
        /// (issue #346). Grava <c>ReviewedByUserId</c> (via <see cref="ICurrentUser"/>) e
        /// <c>ReviewedAtUtc</c>. Idempotente: um segundo review do mesmo reporte devolve <c>409</c>.
        /// Só <c>reviewed_accepted</c> gera uma linha no dataset de treino incremental
        /// (<c>source = "human-correction-reviewed"</c>); <c>reviewed_rejected</c> apenas encerra o
        /// ciclo de vida. O <c>GroundTruthXml</c> do contexto NUNCA é tocado — a revisão só decide
        /// o que vira treino. Somente <c>admin</c>.
        /// </summary>
        /// <response code="200"><c>{ reportId, status }</c> — transição efetuada.</response>
        /// <response code="400"><c>decision</c> ausente ou diferente de <c>accepted</c>/<c>rejected</c>.</response>
        /// <response code="404">Sem identidade resolvida (fail-closed).</response>
        /// <response code="409">Reporte inexistente ou já revisado (só <c>pending</c> transiciona).</response>
        /// <response code="500">Falha de infraestrutura ao gravar a transição.</response>
        [Authorize(Roles = "admin")]
        [HttpPost("field-correction/{reportId:guid}/review")]
        public async Task<IActionResult> ReviewFieldCorrection(Guid reportId, [FromBody] FieldCorrectionReviewRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid reviewerId)
                return NotFound(); // fail-closed.

            var decision = request?.Decision?.Trim().ToLowerInvariant();
            var newStatus = decision switch
            {
                "accepted" => FieldCorrectionReportStatus.ReviewedAccepted,
                "rejected" => FieldCorrectionReportStatus.ReviewedRejected,
                _ => null
            };
            if (newStatus is null)
                return BadRequest(new { success = false, error = "decision é obrigatório e deve ser 'accepted' ou 'rejected'" });

            bool transitioned;
            try
            {
                transitioned = await _fieldCorrectionStore.TransitionStatusAsync(reportId, newStatus, reviewerId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao transicionar reporte de correção reportId={ReportId}", reportId);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao registrar a revisão" });
            }

            if (!transitioned)
                return Conflict(new { success = false, error = "reporte inexistente ou já revisado — apenas reportes 'pending' podem ser curados" });

            _logger.LogInformation(
                "Curadoria de correção humana: reportId={ReportId} status={Status} revisor={ReviewerId}",
                reportId, newStatus, reviewerId);

            // Só o aceite alimenta o dataset incremental. Best-effort: a transição já está gravada,
            // uma falha aqui (contexto expirado, disco cheio) vira warning, nunca reverte a revisão
            // nem derruba a resposta.
            if (newStatus == FieldCorrectionReportStatus.ReviewedAccepted)
                await TryCaptureAcceptedCorrectionAsync(reportId, cancellationToken);

            return Ok(new { reportId, status = newStatus });
        }

        /// <summary>
        /// Monta o exemplo de treino incremental de um reporte recém-aceito: contexto original
        /// (<c>InputXml</c>/<c>GroundTruthXml</c>) + <c>ExpectedValue</c> do reporte como saída
        /// esperada. Delega a escrita ao <see cref="TrainingDataCaptureService"/> (mesmo arquivo
        /// diário e formato do runtime capture). Best-effort — nunca lança.
        /// </summary>
        private async Task TryCaptureAcceptedCorrectionAsync(Guid reportId, CancellationToken cancellationToken)
        {
            try
            {
                var report = await _fieldCorrectionStore.GetReportAsync(reportId, cancellationToken);
                if (report is null)
                {
                    _logger.LogWarning("Reporte aceito não encontrado ao montar o exemplo de treino (reportId={ReportId})", reportId);
                    return;
                }

                var context = await _fieldCorrectionStore.GetContextAsync(report.DocumentId, cancellationToken);
                if (context is null)
                {
                    _logger.LogWarning(
                        "Contexto do documento ausente/expirado ao montar o exemplo de treino do reporte aceito (reportId={ReportId} documentId={DocumentId})",
                        reportId, report.DocumentId);
                    return;
                }

                _trainingDataCapture.TryCaptureHumanCorrection(context, report);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha ao capturar o exemplo de treino do reporte aceito — best-effort, a revisão permanece gravada (reportId={ReportId})",
                    reportId);
            }
        }

        /// <summary>
        /// Pathway sysmiddle (low-code multi-candidato): reaproveita <see cref="LowCodeAutoTransformationService"/>
        /// (mesma infraestrutura usada por <c>ParseController.Upload</c>). Isolamento total: qualquer falha
        /// aqui (estrutural ou por candidato individual) vira warning, nunca deriuba o pathway tcl-xsl.
        ///
        /// <para><paramref name="cancellationToken"/> é o teto da request. Este é o único dos dois
        /// pathways que precisa dele: aqui cada candidato ocupa um slot de <c>MaxConcurrentRunners</c>
        /// e um processo x86 externo, ambos compartilhados por toda a API. Desistir sem cancelar
        /// deixaria esses recursos presos depois de a resposta já ter ido embora.</para>
        /// </summary>
        private async Task<List<TransformationCandidate>> ExecuteSysmiddleCandidatesAsync(
            TransformationRequest request, LayoutRecord layoutRecord, bool isXmlInput, List<string> warnings,
            ConcurrentBag<FailureKind> failureKinds, ConcurrentBag<Models.Transformation.PathwayDiagnostic> pathwayDiagnostics,
            CancellationToken cancellationToken)
        {
            var result = new List<TransformationCandidate>();
            // ✅ CodeQL cs/log-forging: LayoutName vem do request (usuário) e pode conter \r/\n —
            // saneia UMA VEZ aqui e reusa nos logs deste método (o valor cru continua sendo usado
            // fora dos logs — SearchTerm, ticket, EnqueueAsync — onde forjar log não se aplica).
            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(request.LayoutName);

            // Sysmiddle/low-code espera texto posicional (TXT), não XML — não é uma falha do
            // pathway, é entrada fora de escopo (a IA não deveria disparar por causa disso).
            if (isXmlInput)
            {
                _logger.LogInformation(
                    "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} motivo=entrada XML fora do escopo do pathway sysmiddle",
                    Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "not_applicable", "not_applicable", safeLayoutName);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "sysmiddle",
                    Status = "not_applicable",
                    Code = "not_applicable",
                    Message = "Entrada XML — pathway sysmiddle espera texto posicional (TXT)"
                });
                return result;
            }

            try
            {
                var resolvedLayoutGuid = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
                var safeResolvedLayoutGuid = Services.Logging.LogMessageSanitizer.Sanitize(resolvedLayoutGuid);
                if (resolvedLayoutGuid == null)
                {
                    var msg = $"Layout {request.LayoutName} sem LayoutGuid válido no request ou no catálogo — pathway sysmiddle não aplicável";
                    warnings.Add(msg);
                    failureKinds.Add(FailureKind.NotApplicable);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} fonte=request.LayoutGuid/catalogo (nenhum resolvível)",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "not_applicable", "not_applicable", safeLayoutName);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "not_applicable",
                        Code = "not_applicable",
                        Message = msg
                    });
                    return result;
                }

                // Este endpoint só possui o registro resumido do catálogo, não o LayoutVO completo.
                // Portanto não inventa MQSeries: persiste unknown/default e põe a amostra em quarentena.
                var positionalMetadata = LowCodePositionalMetadata.CreateDefault();
                var autoResult = await _lowCodeAuto.RunAsync(
                    resolvedLayoutGuid,
                    request.LayoutName,
                    request.InputContent,
                    detectedType: "unknown",
                    originalFileName: "execute-candidates",
                    positionalMetadata: positionalMetadata,
                    cancellationToken: cancellationToken);

                if (!autoResult.Applicable)
                {
                    var msgNoMapper = $"Nenhum mapeador low-code encontrado para o layout {request.LayoutName} (pathway sysmiddle)";
                    warnings.Add(msgNoMapper);
                    // Estado A (§2 do design-fallback-ia-automatico): não existe mapper cadastrado
                    // para este layout — gap real de cobertura, elegível ao fallback de IA.
                    failureKinds.Add(FailureKind.NotApplicable);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=catalogo (consulta a mapeadores low-code sem resultado)",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "not_applicable", "no_mapper", safeLayoutName, safeResolvedLayoutGuid);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "not_applicable",
                        Code = "no_mapper",
                        Message = msgNoMapper
                    });
                    return result;
                }

                // ✅ Issue #141 (design §2, opção B): parse posicional do documento é feito UMA VEZ por
                // request (compartilhado entre todos os candidatos sysmiddle — mesmo documento de
                // entrada) em vez de recalculado por candidato. O mapper de cada candidato já veio
                // decifrado de volta em LowCodeCandidateResult.DecryptedMapperContent (sem 2ª consulta
                // SQL) — só falta parsear o TXT contra o Layout de origem, que RunAsync não expõe (o
                // runner .exe parseia por dentro do processo externo, não via ILayoutParserService).
                ParsingResult? sharedParsingResult = null;
                if (!string.IsNullOrWhiteSpace(layoutRecord.DecryptedContent))
                {
                    try
                    {
                        using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutRecord.DecryptedContent));
                        using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.InputContent));
                        sharedParsingResult = await _layoutParser.ParseAsync(layoutStream, txtStream);
                        if (!sharedParsingResult.Success || sharedParsingResult.Layout == null)
                        {
                            _logger.LogWarning(
                                "fieldMappings (issue #141): parse posicional compartilhado falhou para layout {LayoutName} — candidatos sysmiddle seguem sem fieldMappings. Erro={ErrorMessage}",
                                safeLayoutName, Services.Logging.LogMessageSanitizer.Sanitize(sharedParsingResult.ErrorMessage));
                            sharedParsingResult = null;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        // Nunca deixa a composição de fieldMappings afetar o XML já produzido pelo runner.
                        _logger.LogWarning(parseEx,
                            "fieldMappings (issue #141): exceção no parse posicional compartilhado para layout {LayoutName} — candidatos sysmiddle seguem sem fieldMappings",
                            safeLayoutName);
                        sharedParsingResult = null;
                    }
                }

                var anyCandidateFailed = false;
                string lastCandidateFailureMessage = null;
                foreach (var c in autoResult.Candidates)
                {
                    if (c.Success && !string.IsNullOrEmpty(c.OutputXml))
                    {
                        // Issue #138 (Fase 0): resolução estrutural de SectionMappings a partir do
                        // MapeadorVO já decifrado deste candidato — nunca lança (degrada para [] com
                        // xmlNamespaces=null; ver SysmiddleSectionMappingResolver).
                        var (sectionMappings, xmlNamespaces) = SysmiddleSectionMappingResolver.Resolve(
                            c.DecryptedMapperContent, c.OutputXml,
                            msg => _logger.LogDebug("{Message} (mapper={MapperGuid})", msg, c.MapperGuid));

                        result.Add(new TransformationCandidate
                        {
                            CandidateId = $"sysmiddle-{c.MapperGuid}",
                            Pathway = "sysmiddle",
                            TransformedXml = c.OutputXml,
                            FieldMappings = TryComposeFieldMappings(sharedParsingResult, c, request.LayoutName, warnings),
                            SectionMappings = sectionMappings,
                            XmlNamespaces = xmlNamespaces
                        });
                    }
                    else
                    {
                        // Falha isolada de UM candidato — não entra no array (nunca item com XML nulo),
                        // vira warning (ver tabela de decisão do contrato). Estado B (§2 do design):
                        // o mapper EXISTE (Applicable==true) mas a execução falhou — é infra/config
                        // (runner, timeout, .exe ausente), não gap de cobertura. Nunca dispara IA.
                        var sanitizedCandidateError = LowCodeErrorSanitizer.ForWire(c.ErrorMessage ?? "erro desconhecido");
                        anyCandidateFailed = true;
                        lastCandidateFailureMessage = sanitizedCandidateError;
                        warnings.Add($"Candidato {c.MapperGuid} (pathway sysmiddle) falhou: {sanitizedCandidateError}");
                        failureKinds.Add(FailureKind.ExecutionInfraError);
                    }
                }

                if (result.Count > 0)
                {
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} layout={LayoutName} layoutGuid={LayoutGuid} candidatos={CandidateCount} fonte=mapeadores low-code do catalogo",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "candidate_generated", safeLayoutName, safeResolvedLayoutGuid, result.Count);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "candidate_generated",
                        Code = null,
                        Message = $"{result.Count} candidato(s) sysmiddle gerado(s)"
                    });
                }
                else if (anyCandidateFailed)
                {
                    // autoResult.Applicable == true (mapper existe) mas TODOS os candidatos
                    // falharam na execução — infra/runner, não gap de cobertura (§4.3 "runner_unavailable").
                    _logger.LogWarning(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=execução do runner (mapper existe, execução falhou)",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "failed", "runner_unavailable", safeLayoutName, safeResolvedLayoutGuid);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "failed",
                        Code = "runner_unavailable",
                        Message = lastCandidateFailureMessage ?? "Todos os candidatos sysmiddle falharam na execução"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha estrutural no pathway sysmiddle ao gerar candidatos para layout {LayoutName}. PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code}",
                    safeLayoutName, Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "failed", "execution_error");
                // Saneado: exceção de I/O deste pathway carrega caminho de disco do servidor e este
                // warning sai no payload 200 (mesmo defeito do §3.1 da spec, outro ponto de saída).
                var sanitizedEx = LowCodeErrorSanitizer.ForWire(ex);
                warnings.Add($"Pathway sysmiddle falhou: {sanitizedEx}");
                // Falha estrutural (exceção) é sempre infra, não "não modelado" — nunca dispara IA.
                failureKinds.Add(FailureKind.ExecutionInfraError);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "sysmiddle",
                    Status = "failed",
                    Code = "execution_error",
                    Message = sanitizedEx
                });
            }

            return result;
        }

        /// <summary>
        /// Compõe <c>fieldMappings</c> (issue #141) para UM candidato sysmiddle bem-sucedido, sobre o
        /// <paramref name="sharedParsingResult"/> já calculado uma vez por request e o mapper decifrado
        /// que o próprio candidato já carrega (<see cref="LowCodeCandidateResult.DecryptedMapperContent"/>
        /// — nenhuma consulta SQL nova). Nunca lança: qualquer falha (parse indisponível, mapper
        /// ilegível, exceção do motor de composição) vira <c>null</c> + warning, e o candidato mantém
        /// o <c>TransformedXml</c> já produzido pelo runner (design §2, "isolamento total").
        /// </summary>
        private IReadOnlyList<XslSynth.Model.FieldToXmlMapping>? TryComposeFieldMappings(
            ParsingResult? sharedParsingResult, LowCodeCandidateResult candidate, string layoutName, List<string> warnings)
        {
            if (sharedParsingResult == null || sharedParsingResult.Layout == null)
                return null; // já logado como warning no ponto em que o parse compartilhado falhou.

            if (string.IsNullOrWhiteSpace(candidate.DecryptedMapperContent))
                return null; // candidato sem mapper decifrado disponível (ex.: falha antes da resolução do mapper).

            try
            {
                var mapperVo = new RealMapperParser().Parse(XDocument.Parse(candidate.DecryptedMapperContent));
                return _fieldMappingComposition.Compose(
                    sharedParsingResult.Layout, sharedParsingResult.ParsedFields, mapperVo, sharedParsingResult.LineInfos);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fieldMappings (issue #141): falha ao compor mapeamentos estruturais para candidato mapper={MapperGuid} do layout {LayoutName}",
                    candidate.MapperGuid, Services.Logging.LogMessageSanitizer.Sanitize(layoutName));
                warnings.Add($"Candidato {candidate.MapperGuid} (pathway sysmiddle): falha ao compor fieldMappings — ver log do servidor");
                return null;
            }
        }

        /// <summary>
        /// Dispara o job assíncrono do pathway IA (Issue #40) quando há gabarito sysmiddle
        /// disponível — o dono do projeto fechou que a IA "sempre trabalha" nessa condição,
        /// não é um fallback condicionado ao tcl-xsl. Nunca lança: qualquer falha aqui vira
        /// warning e não afeta o array <c>candidates[]</c> já calculado.
        /// </summary>
        private void TryEnqueueAiCandidate(
            TransformationRequest request, LayoutRecord layoutRecord, List<TransformationCandidate> candidates, bool isXmlInput,
            string userId)
        {
            var plan = AiCandidateDispatchPlan.TryBuild(
                request.LayoutGuid, layoutRecord.LayoutGuid, request.InputContent, isXmlInput, candidates);
            if (plan == null)
                return; // sem gabarito sysmiddle bem-sucedido ou sem LayoutGuid resolvível: IA não aplicável (§2.1/§3.2 do desenho).

            // ✅ Correção pós-review da Quinn (2026-08-29, docs/architecture/pathway-ia-execute-
            // candidates.md §3): o ParseAsync que monta os ParsedField pro RepairOrchestrator
            // (Issue #140) NÃO pode rodar no caminho síncrono da request — atrasa toda chamada de
            // execute-candidates que tenha gabarito+LayoutGuid resolvível, contrariando o comentário
            // "Fire-and-forget: NUNCA atrasa..." acima do call site. O parse posicional entra DENTRO
            // do job em background, junto com o resto do trabalho de EnqueueAsync/RunLoopAsync —
            // capturamos só o conteúdo bruto aqui (string), nada de IO/CPU síncrono.
            var layoutName = request.LayoutName;
            // ✅ CodeQL cs/log-forging: valor saneado só para logging deste job em background.
            var safeLayoutNameForLog = Services.Logging.LogMessageSanitizer.Sanitize(layoutName);
            var decryptedLayoutContent = layoutRecord.DecryptedContent;
            var inputContent = request.InputContent;

            // ✅ Não usa a request.HttpContext.RequestAborted — o job sobrevive ao fim da request
            // (dotnet-standards.md §Background work). CancellationToken.None + teto de sanidade
            // interno do serviço (AiTransformationCandidateOptions.SanityTimeoutMinutes).
            _ = Task.Run(async () =>
            {
                // ✅ Issue #140/decisão 2026-08-29 (docs/architecture/decisao-pendente-input-xml-
                // repairorchestrator-2026-08-29.md): o RepairOrchestrator (motor novo de
                // AiTransformationCandidateService) exige o resultado do parse posicional REAL
                // (ParsedField) para montar o XML de entrada via ParsedFieldRootTreeBuilder — TXT cru
                // não é XML e nunca vai ser aceito por XDocument.Parse. Parse próprio (não reaproveita
                // o sharedParsingResult de ExecuteSysmiddleCandidatesAsync — escopo local ao método,
                // reestruturar o retorno dele para isso não vale o acoplamento). Nunca lança: falha
                // aqui apenas degrada o motor novo para o loop legado XML-direto (parsedFields=null).
                IReadOnlyList<Models.Entities.ParsedField>? parsedFields = null;
                if (!string.IsNullOrWhiteSpace(decryptedLayoutContent) && !isXmlInput)
                {
                    try
                    {
                        // ✅ ILayoutParserService é Scoped (dotnet-standards.md) — o job sobrevive ao
                        // fim do scope da request HTTP, então não pode capturar o _layoutParser do
                        // controller (seria usar um serviço Scoped fora do seu ciclo de vida, mesmo
                        // padrão já documentado em AiTransformationCandidateService). Abrimos um scope
                        // próprio aqui dentro do job.
                        using var scope = _scopeFactory.CreateScope();
                        var scopedLayoutParser = scope.ServiceProvider.GetRequiredService<ILayoutParserService>();
                        using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(decryptedLayoutContent));
                        using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(inputContent));
                        var parseResult = await scopedLayoutParser.ParseAsync(layoutStream, txtStream);
                        if (parseResult.Success && parseResult.ParsedFields is { Count: > 0 })
                            parsedFields = parseResult.ParsedFields;
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogDebug(parseEx,
                            "Pathway IA: parse posicional para ParsedFieldRootTreeBuilder falhou — motor novo degrada para o loop legado (layout={LayoutName})",
                            safeLayoutNameForLog);
                    }
                }

                try
                {
                    // userId (issue #92): particiona o ticket na AiCandidateStore — só quem disparou o
                    // job consegue consultá-lo depois em ia-status.
                    await _aiCandidateService.EnqueueAsync(
                        userId,
                        plan.Ticket,
                        layoutName,
                        plan.LayoutGuid,
                        plan.MapperGuid,
                        inputContent,
                        plan.GroundTruthXml,
                        CancellationToken.None,
                        parsedFields);
                }
                catch (Exception ex)
                {
                    // EnqueueAsync não deveria lançar (contrato do serviço), mas isolamento total aqui
                    // também — nunca derrubar o job em background por causa da IA.
                    _logger.LogWarning(ex, "Falha ao disparar o pathway IA para layout {LayoutName}", safeLayoutNameForLog);
                }
            }, CancellationToken.None); // O próprio Task.Run não deve morrer com a request HTTP.
        }

        /// <summary>
        /// Fallback automático de IA — Estado A (docs/architecture/design-fallback-ia-automatico-2026-08-16.md
        /// §1/§2/§5). Só chega aqui quando <c>candidates.Count == 0</c>. Dispara <see cref="IAiTransformationCandidateService.EnqueueAsync"/>
        /// no modo SEM gabarito (<c>groundTruthXml: null</c>) se, e somente se, nenhum dos pathways
        /// síncronos reportou <see cref="FailureKind.ExecutionInfraError"/> (Estado B — correção é
        /// operacional, a IA não deveria tentar recriar um mapper que já existe). Consulta o
        /// <see cref="IAiFallbackSuppressionGate"/> antes de disparar para não repetir uma chamada
        /// cara ao Ollama para um layout que já falhou recentemente. Nunca lança: qualquer falha aqui
        /// vira warning e não afeta a resposta síncrona já calculada.
        /// </summary>
        private void TryEnqueueAiFallback(
            TransformationRequest request, LayoutRecord layoutRecord, bool isXmlInput,
            ConcurrentBag<FailureKind> failureKinds, List<string> warnings,
            ConcurrentBag<Models.Transformation.PathwayDiagnostic> pathwayDiagnostics, string userId)
        {
            // ✅ CodeQL cs/log-forging: saneado uma vez, reusado só nos logs deste método.
            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(request.LayoutName);
            try
            {
                if (failureKinds.Any(k => k == FailureKind.ExecutionInfraError))
                {
                    // Estado B: já existe o warning de infra específico emitido pelo pathway que
                    // falhou (e já virou pathwayDiagnostics próprio de sysmiddle/tcl-xsl) — nada a
                    // acrescentar aqui, só não disparar a IA (§2 do desenho). Não emite um 3º
                    // diagnóstico "ai-fallback: not_applicable" para não duplicar sinal — o front já
                    // tem os itens failed de quem realmente quebrou.
                    return;
                }

                var resolvedLayoutGuidText = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
                if (resolvedLayoutGuidText == null || !Guid.TryParse(resolvedLayoutGuidText, out var resolvedLayoutGuid))
                {
                    var msg = $"Layout {request.LayoutName} sem LayoutGuid válido — fallback de IA não aplicável";
                    warnings.Add(msg);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} fonte=request.LayoutGuid/catalogo (nenhum resolvível)",
                        Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "not_applicable", "not_applicable", safeLayoutName);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "ai-fallback",
                        Status = "not_applicable",
                        Code = "not_applicable",
                        Message = msg
                    });
                    return;
                }

                if (_aiFallbackGate.IsInCooldown(resolvedLayoutGuid, out var retryAt))
                {
                    var msg = $"Pathway IA fallback suprimido para este layout até {retryAt:HH:mm} (já tentado sem sucesso)";
                    warnings.Add(msg);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=IAiFallbackSuppressionGate (cooldown ativo até {RetryAt})",
                        Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "not_applicable", "not_applicable", safeLayoutName, resolvedLayoutGuid, retryAt);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "ai-fallback",
                        Status = "not_applicable",
                        Code = "not_applicable",
                        Message = msg
                    });
                    return;
                }

                var ticket = LowCodeTransformationStore.BuildTicketFromContent(request.InputContent, resolvedLayoutGuidText);
                if (ticket == null)
                {
                    var msg = $"Layout {request.LayoutName}: não foi possível compor o ticket do fallback de IA";
                    warnings.Add(msg);
                    _logger.LogWarning(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=LowCodeTransformationStore.BuildTicketFromContent (retornou null)",
                        Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "failed", "configuration_error", safeLayoutName, resolvedLayoutGuid);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "ai-fallback",
                        Status = "failed",
                        Code = "configuration_error",
                        Message = msg
                    });
                    return;
                }

                // mapperGuid: não há candidato sysmiddle bem-sucedido no Estado A (por definição), então
                // não existe um MapperGuid real a associar — usa o próprio LayoutGuid como identificador
                // estável do job (mesma convenção de particionamento por layout do gate de supressão).
                _ = _aiCandidateService.EnqueueAsync(
                    userId,
                    ticket,
                    request.LayoutName,
                    resolvedLayoutGuid,
                    mapperGuid: resolvedLayoutGuidText,
                    request.InputContent,
                    groundTruthXml: null,
                    CancellationToken.None);

                var enqueuedMsg = $"Nenhum candidato de transformação encontrado — fallback automático de IA enfileirado (ticket {ticket}), consulte GET execute-candidates/{ticket}/ia-status";
                warnings.Add(enqueuedMsg);
                // "candidate_generated" no sentido de que o pathway produziu um item consultável
                // (ticket assíncrono) — não um XML pronto, mas o front tem o que fazer com ele
                // (§4.2 do desenho: "inclui o ticket assíncrono do fallback de IA, que 'gera' no
                // sentido de estar em processamento").
                _logger.LogInformation(
                    "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} layout={LayoutName} layoutGuid={LayoutGuid} ticket={Ticket} fonte=IAiTransformationCandidateService.EnqueueAsync (sem gabarito)",
                    Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "candidate_generated", safeLayoutName, resolvedLayoutGuid, Services.Logging.LogMessageSanitizer.Sanitize(ticket));
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "ai-fallback",
                    Status = "candidate_generated",
                    Code = null,
                    Message = enqueuedMsg
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha ao disparar o fallback automático de IA para layout {LayoutName}. PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code}",
                    safeLayoutName, Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "failed", "execution_error");
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "ai-fallback",
                    Status = "failed",
                    Code = "execution_error",
                    Message = LowCodeErrorSanitizer.ForWire(ex)
                });
            }
        }

        /// <summary>
        /// Consulta o status do job assíncrono do pathway IA (Issue #40). Mesma política de
        /// autorização de <see cref="ExecuteTransformationCandidates"/> (Issue #32) — endpoint
        /// novo, mesmo custo/sensibilidade de disparar processos/objetos caros.
        /// </summary>
        /// <remarks>
        /// Issue #92: a consulta é isolada por usuário — <c>ticket</c> de outro usuário devolve 404,
        /// nunca 403. 403 confirmaria "o ticket existe, mas não é seu" (enumeração); 404 se comporta
        /// exatamente como um ticket inexistente/expirado, que é o mesmo caso hoje. Único gate de
        /// papel era o <c>[Authorize(Roles = "admin")]</c> — a issue #93 abriu o endpoint além de
        /// admin (<c>[Authorize]</c> simples) porque o isolamento por dono já estava pronto.
        /// Candidatos originados do fallback automático de IA (Estado A) trazem
        /// <c>HasGroundTruth=false</c>: não há gabarito/histórico de validação para o layout, então
        /// o resultado é uma sugestão que exige revisão humana antes de ir para produção.
        /// </remarks>
        /// <param name="ticket">Ticket devolvido em <c>execute-candidates</c> (correlato ao fallback/pathway IA).</param>
        /// <response code="200">Status do job (ver <c>AiCandidateStatus</c>: pending/running/completed/failed).</response>
        /// <response code="404">Ticket inexistente ou pertencente a outro usuário (não distinguível de propósito — evita enumeração).</response>
        [Authorize]
        [HttpGet("execute-candidates/{ticket}/ia-status")]
        public async Task<IActionResult> GetAiCandidateStatus(string ticket, CancellationToken cancellationToken)
        {
            var status = await _aiCandidateService.GetStatusAsync(CurrentUserId, ticket, cancellationToken);
            if (status.Status == AiCandidateStatus.StatusNotFound)
                return NotFound();

            return Ok(status);
        }

        /// <summary>
        /// Issue #98: define/atualiza a instrução customizada que o usuário quer anexar ao prompt
        /// padrão do pathway IA (<c>execute-candidates</c> e o fallback automático). Endpoint
        /// dedicado, separado do payload de execução — fallback mínimo por partição de usuário
        /// (mesma partição da issue #92), enquanto a sessão de IA completa (issue #6) não existe.
        /// </summary>
        /// <param name="request">Instrução em texto livre; vazio/nulo remove a instrução salva.</param>
        /// <response code="200">Instrução salva (ou removida, se vazia).</response>
        [Authorize]
        [HttpPut("ai-prompt-adicional")]
        public IActionResult SetAiPromptAdicional([FromBody] SetAiPromptAdicionalRequest request)
        {
            _aiUserInstructionStore.Set(CurrentUserId, request?.Instruction);
            return Ok(new { saved = !string.IsNullOrWhiteSpace(request?.Instruction) });
        }

        /// <summary>Consulta a instrução customizada atualmente salva para o usuário (issue #98).</summary>
        [Authorize]
        [HttpGet("ai-prompt-adicional")]
        public IActionResult GetAiPromptAdicional()
        {
            var instruction = _aiUserInstructionStore.Get(CurrentUserId);
            return Ok(new { instruction });
        }

        /// <summary>
        /// Issue #322: define/atualiza as 3 preferências de usuário além do prompt customizado
        /// (idioma de exibição, nível de detalhe da explicação de mapeamento, engine padrão quando o
        /// sistema precisa escolher entre TCL/XSLT sem sinal explícito no request). Rota dedicada,
        /// paralela a <c>ai-prompt-adicional</c> (issue #98) em vez de consolidada nela — granularidade
        /// mantida porque as duas evoluem em ritmos diferentes (o prompt já está em produção e não deve
        /// ganhar um contrato novo por acidente de "juntar tudo"; preferências estruturadas têm
        /// validação própria, `422` por valor inválido, que um campo de texto livre não tem).
        /// Persistido em <c>tbLpAiUserSession</c> (<see cref="Services.Database.SqlAiUserSessionStore"/>,
        /// schema da issue #102) — diferente do <see cref="AiUserInstructionStore"/> usado pelo
        /// <c>ai-prompt-adicional</c> atual, que é só em memória (não sobrevive a restart). Campos
        /// omitidos/nulos no corpo preservam o valor já salvo (upsert parcial); envie o valor atual de
        /// volta para não alterá-lo.
        /// </summary>
        /// <param name="request">Qualquer campo pode vir nulo (preserva o valor já salvo).</param>
        /// <response code="200"><c>{ saved: true }</c>.</response>
        /// <response code="404">Sem identidade resolvida (fail-closed, mesmo padrão de <see cref="ReportFieldCorrection"/>).</response>
        /// <response code="422"><c>preferredExplanationDetailLevel</c> ou <c>defaultTransformationEngine</c> fora dos valores aceitos.</response>
        [Authorize]
        [HttpPut("ai-preferences")]
        public async Task<IActionResult> SetAiPreferences([FromBody] SetAiPreferencesRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                return NotFound(); // fail-closed, mesmo padrão de ReportFieldCorrection.

            if (!string.IsNullOrWhiteSpace(request?.PreferredExplanationDetailLevel)
                && !Services.Database.AiUserPreferenceDefaults.ValidExplanationDetailLevels.Contains(request.PreferredExplanationDetailLevel))
            {
                return UnprocessableEntity(new
                {
                    success = false,
                    error = $"preferredExplanationDetailLevel deve ser um de: {string.Join(", ", Services.Database.AiUserPreferenceDefaults.ValidExplanationDetailLevels)}"
                });
            }

            if (!string.IsNullOrWhiteSpace(request?.DefaultTransformationEngine)
                && !Services.Database.AiUserPreferenceDefaults.ValidTransformationEngines.Contains(request.DefaultTransformationEngine))
            {
                return UnprocessableEntity(new
                {
                    success = false,
                    error = $"defaultTransformationEngine deve ser um de: {string.Join(", ", Services.Database.AiUserPreferenceDefaults.ValidTransformationEngines)}"
                });
            }

            await _aiUserSessionStore.SetPreferencesAsync(
                CurrentUserId,
                request?.PreferredLanguage,
                request?.PreferredExplanationDetailLevel,
                request?.DefaultTransformationEngine,
                cancellationToken);

            return Ok(new { saved = true });
        }

        /// <summary>
        /// Issue #322: consulta as 4 preferências do usuário atual (prompt customizado + idioma/nível
        /// de detalhe/engine padrão), com defaults aplicados quando a preferência nunca foi setada (ver
        /// <see cref="Services.Database.AiUserPreferenceDefaults"/>). <c>customPromptInstruction</c>
        /// continua vindo do <see cref="AiUserInstructionStore"/> em memória (fonte de verdade atual do
        /// <c>ai-prompt-adicional</c>, issue #98) — não migrado para o SQL nesta issue.
        /// </summary>
        /// <response code="200">Preferências com defaults já aplicados.</response>
        /// <response code="404">Sem identidade resolvida (fail-closed).</response>
        [Authorize]
        [HttpGet("ai-preferences")]
        public async Task<IActionResult> GetAiPreferences(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                return NotFound(); // fail-closed, mesmo padrão de ReportFieldCorrection.

            var saved = await _aiUserSessionStore.GetPreferencesAsync(CurrentUserId, cancellationToken);
            var customPromptInstruction = _aiUserInstructionStore.Get(CurrentUserId);

            return Ok(new
            {
                customPromptInstruction,
                preferredLanguage = saved?.PreferredLanguage ?? Services.Database.AiUserPreferenceDefaults.DefaultLanguage,
                preferredExplanationDetailLevel = saved?.PreferredExplanationDetailLevel ?? Services.Database.AiUserPreferenceDefaults.DefaultExplanationDetailLevel,
                defaultTransformationEngine = saved?.DefaultTransformationEngine ?? Services.Database.AiUserPreferenceDefaults.DefaultTransformationEngine
            });
        }

        /// <summary>
        /// Issue #97 (fase 2, Passo 3): histórico persistente do usuário atual — <c>Ticket</c>/
        /// <c>Status</c>/<c>CreatedAt</c> gravados por <see cref="Services.Database.SqlAiUserSessionStore"/>
        /// (schema já criado pela issue #102) toda vez que um job do pathway IA chega a um status
        /// terminal. Não é "conversa" nem memória de chamada Ollama entre tickets — é só a lista que
        /// alimenta <see cref="ResumeAiCandidateTicket"/> para o usuário escolher qual ticket falho
        /// reabrir.
        /// </summary>
        /// <param name="maxEntries">Teto de entradas retornadas (mais recente primeiro); default 50.</param>
        /// <response code="200">Lista (pode ser vazia) do histórico do usuário autenticado.</response>
        [Authorize]
        [HttpGet("ai-session/history")]
        public async Task<IActionResult> GetAiSessionHistory([FromQuery] int maxEntries, CancellationToken cancellationToken)
        {
            var history = await _aiUserSessionStore.GetHistoryAsync(CurrentUserId, maxEntries, cancellationToken);
            return Ok(history);
        }

        /// <summary>
        /// Issue #97 (fase 2, Passo 3): retomada pontual de um ticket falho do pathway IA. Reabre
        /// SÓ o ticket indicado (single-shot) — não carrega memória de tentativas anteriores para o
        /// prompt, o loop gerar→validar→corrigir do <see cref="IAiTransformationCandidateService"/>
        /// recomeça do zero com o conteúdo enviado agora no corpo da requisição. Isso é necessário
        /// porque o histórico persistente (issue #102) guarda só <c>Ticket</c>/<c>Status</c> — o
        /// TXT/XML de entrada em si é conteúdo pesado e continua vivendo só no
        /// <see cref="Services.Transformation.Ai.AiCandidateStore"/> (cache quente, TTL curto), não
        /// duplicado no SQL (mesmo critério de aceite documentado em
        /// <see cref="Services.Database.SqlAiUserSessionStore"/>).
        /// </summary>
        /// <remarks>
        /// Isolamento por dono (issue #92): só reabre um ticket que apareça no histórico do PRÓPRIO
        /// usuário autenticado — <c>404</c> tanto para ticket inexistente quanto para ticket de outro
        /// usuário (não distinguível de propósito, mesma defesa contra enumeração de
        /// <see cref="GetAiCandidateStatus"/>).
        /// </remarks>
        /// <param name="ticket">Ticket que já apareceu em <c>GET ai-session/history</c> para este usuário.</param>
        /// <param name="request"><c>InputContent</c>/<c>LayoutName</c> obrigatórios (mesmo conteúdo que originou o ticket, ou uma correção pontual); <c>ExpectedOutput</c> opcional vira gabarito.</param>
        /// <response code="202">Retomada enfileirada — consulte <c>GET execute-candidates/{ticket}/ia-status</c>.</response>
        /// <response code="400"><c>InputContent</c>/<c>LayoutName</c> ausente, ou layout não encontrado no catálogo.</response>
        /// <response code="404">Ticket não encontrado no histórico do usuário autenticado.</response>
        [Authorize]
        [HttpPost("ai-session/history/{ticket}/resume")]
        public async Task<IActionResult> ResumeAiCandidateTicket(string ticket, [FromBody] TransformationRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ticket))
                return NotFound();

            if (request == null || string.IsNullOrEmpty(request.InputContent))
                return BadRequest(new { success = false, errors = new[] { "InputContent é obrigatório" } });

            if (string.IsNullOrEmpty(request.LayoutName))
                return BadRequest(new { success = false, errors = new[] { "LayoutName é obrigatório" } });

            // Só reabre ticket que já apareceu no histórico do PRÓPRIO usuário — não confia em ticket
            // "adivinhado" no path, mesma defesa contra enumeração de GetAiCandidateStatus.
            var history = await _aiUserSessionStore.GetHistoryAsync(CurrentUserId, maxEntries: 200, cancellationToken);
            var owned = history.Any(entry => string.Equals(entry.Ticket, ticket, StringComparison.Ordinal));
            if (!owned)
                return NotFound();

            LayoutRecord? layoutRecord;
            try
            {
                var searchResponse = await _layoutDb.SearchLayoutsAsync(new LayoutSearchRequest { SearchTerm = request.LayoutName });
                if (!searchResponse.Success)
                    throw new InvalidOperationException(searchResponse.ErrorMessage);

                layoutRecord = searchResponse.Layouts
                    .FirstOrDefault(l => string.Equals(l.Name, request.LayoutName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha de infraestrutura ao resolver layout {LayoutName} para retomada de ticket IA", request.LayoutName);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao consultar o catálogo de layouts" });
            }

            if (layoutRecord == null)
                return BadRequest(new { success = false, errors = new[] { $"Layout '{request.LayoutName}' não encontrado" } });

            var resolvedLayoutGuidText = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
            if (resolvedLayoutGuidText == null || !Guid.TryParse(resolvedLayoutGuidText, out var resolvedLayoutGuid))
                return BadRequest(new { success = false, errors = new[] { $"Layout '{request.LayoutName}' sem LayoutGuid válido para retomada" } });

            var userId = CurrentUserId;
            var groundTruthXml = string.IsNullOrWhiteSpace(request.ExpectedOutput) ? null : request.ExpectedOutput;

            // Fire-and-forget (mesmo padrão de TryEnqueueAiCandidate/TryEnqueueAiFallback): nunca
            // atrasa nem derruba a resposta HTTP; EnqueueAsync já não lança para o chamador.
            _ = _aiCandidateService.EnqueueAsync(
                userId,
                ticket,
                request.LayoutName,
                resolvedLayoutGuid,
                mapperGuid: resolvedLayoutGuidText,
                request.InputContent,
                groundTruthXml,
                CancellationToken.None);

            return Accepted(new { ticket, resumed = true });
        }

        /// <summary>
        /// Pathway tcl-xsl (canônico): reaproveita <see cref="TransformationPipelineService"/>, mesma lógica
        /// já usada pelo endpoint <c>execute</c>. Hoje produz no máximo 1 candidato (o pipeline não tem
        /// noção de múltiplos TCL/XSL candidatos para o mesmo layout).
        /// </summary>
        private async Task<List<TransformationCandidate>> ExecuteTclXslCandidatesAsync(
            TransformationRequest request, bool isXmlInput, List<string> warnings, ConcurrentBag<FailureKind> failureKinds,
            ConcurrentBag<Models.Transformation.PathwayDiagnostic> pathwayDiagnostics)
        {
            var result = new List<TransformationCandidate>();
            // ✅ CodeQL cs/log-forging: saneado uma vez, reusado só nos logs deste método.
            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(request.LayoutName);

            try
            {
                var pipelineResult = isXmlInput
                    ? await _pipelineService.TransformXmlToXmlAsync(
                        request.InputContent,
                        request.SourceDocumentType ?? "NFe",
                        request.TargetDocumentType ?? "NFe",
                        request.LayoutName)
                    : await _pipelineService.TransformTxtToXmlAsync(
                        request.InputContent,
                        request.LayoutName,
                        request.TargetDocumentType ?? "NFe");

                if (!pipelineResult.Success || string.IsNullOrEmpty(pipelineResult.TransformedXml))
                {
                    // Saneado (§5 do diagnóstico-issue-86): pipelineResult.Errors pode carregar
                    // caminho de disco cru (IOException/XmlException internos do pipeline).
                    var sanitizedTclXslError = LowCodeErrorSanitizer.ForWire(string.Join("; ", pipelineResult.Errors));
                    warnings.Add($"Candidato tcl-xsl falhou: {sanitizedTclXslError}");
                    // "Sem heurística aplicável" para este layout — Estado A (§2 do design).
                    failureKinds.Add(FailureKind.NotApplicable);

                    // Issue #86 §2.4: distingue "arquivo MAP não encontrado" de "arquivo XSL não
                    // encontrado" pelo ErrorCode populado na origem (TransformationPipelineService),
                    // não por regex sobre a mensagem já sanitizada.
                    var code = pipelineResult.ErrorCode switch
                    {
                        "map_not_found" => "map_not_found",
                        "xsl_not_found" => "xsl_not_found",
                        _ => "map_not_found" // fallback conservador: maioria dos casos "não aplicável" hoje é ausência de MAP
                    };
                    // Issue #642: mapeador ausente → dispara a criação automática em background
                    // (fire-and-forget, com cooldown por layout; nunca altera o código de erro nem a resposta).
                    _missingMapperTrigger?.TriggerForLayout(request.LayoutName, code);
                    _logger.LogWarning(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} fonte=TransformationPipelineService.ErrorCode={ErrorCode}",
                        Services.Logging.CorrelationContext.CurrentId, "tcl-xsl", "failed", code, safeLayoutName, pipelineResult.ErrorCode);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "tcl-xsl",
                        Status = "failed",
                        Code = code,
                        Message = sanitizedTclXslError
                    });
                    return result;
                }

                object? validation = null;
                if (request.Validate)
                {
                    try
                    {
                        validation = await _validatorService.ValidateTransformationAsync(
                            isXmlInput ? null : request.InputContent,
                            request.LayoutName,
                            pipelineResult.TclPath,
                            pipelineResult.XslPath,
                            request.ExpectedOutput);
                    }
                    catch (Exception ex)
                    {
                        // Falha de validação não invalida o candidato em si (o XML transformado existe) —
                        // só fica sem o campo Validation preenchido.
                        _logger.LogWarning(ex, "Falha ao validar candidato tcl-xsl para layout {LayoutName}", safeLayoutName);
                        // Saneado (§5 do diagnóstico-issue-86): mesmo padrão do sysmiddle (linha ~385).
                        warnings.Add($"Validação do candidato tcl-xsl falhou: {LowCodeErrorSanitizer.ForWire(ex)}");
                    }
                }

                result.Add(new TransformationCandidate
                {
                    CandidateId = "tclxsl-1",
                    Pathway = "tcl-xsl",
                    TransformedXml = pipelineResult.TransformedXml,
                    SegmentMappings = pipelineResult.SegmentMappings?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    Validation = validation,
                    // Issue #138 (Fase 0): pathway tcl-xsl NÃO suporta rastreabilidade de linha/seção
                    // ainda — SectionMappings=null por definição (semântica obrigatória do contrato).
                    // O SegmentMappings existente acima é um artefato PRÉVIO e DIFERENTE: só existe
                    // para entrada MQSeries, é indexado por número de linha (não GUID/estrutura) e
                    // carrega um XmlElementPath fixo ("NFe/infNFe") hardcoded em MqSeriesToXmlTransformer
                    // — não é XPath resolvido estruturalmente, não atende ao contrato de #138. Virar
                    // SectionMappings real para tcl-xsl exigiria expor a mesma resolução estrutural que
                    // o pathway sysmiddle já tem (GUID→XPath via RealMapperParser) dentro do
                    // TransformationPipelineService — fora do escopo desta fase.
                    SectionMappings = null
                });

                _logger.LogInformation(
                    "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} layout={LayoutName} tclPath={TclPath} xslPath={XslPath} fonte=TransformationPipelineService",
                    Services.Logging.CorrelationContext.CurrentId, "tcl-xsl", "candidate_generated", safeLayoutName,
                    System.IO.Path.GetFileName(pipelineResult.TclPath), System.IO.Path.GetFileName(pipelineResult.XslPath));
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "tcl-xsl",
                    Status = "candidate_generated",
                    Code = null,
                    Message = "Candidato tcl-xsl gerado com sucesso"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha estrutural no pathway tcl-xsl ao gerar candidato para layout {LayoutName}. PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code}",
                    safeLayoutName, Services.Logging.CorrelationContext.CurrentId, "tcl-xsl", "failed", "execution_error");
                // Saneado (§5 do diagnóstico-issue-86): mesmo padrão do sysmiddle (linha ~385).
                var sanitizedTclXslEx = LowCodeErrorSanitizer.ForWire(ex);
                warnings.Add($"Pathway tcl-xsl falhou: {sanitizedTclXslEx}");
                // Exceção estrutural é infra, não "não modelado" — nunca dispara IA.
                failureKinds.Add(FailureKind.ExecutionInfraError);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "tcl-xsl",
                    Status = "failed",
                    Code = "execution_error",
                    Message = sanitizedTclXslEx
                });
            }

            return result;
        }

        /// <summary>
        /// Valida um resultado de transformação já produzido (TCL/XSL em disco) contra o TXT de
        /// entrada e/ou o XML esperado — usado para checagem manual/QA de um par TCL/XSL específico.
        /// </summary>
        /// <response code="200">Resultado da validação (diffs/erros de schema, se houver).</response>
        /// <response code="500">Falha ao rodar a validação.</response>
        [HttpPost("validate")]
        public async Task<IActionResult> ValidateTransformation([FromBody] ValidationRequest request)
        {
            try
            {
                _logger.LogInformation("Validando transformação para layout: {LayoutName}", request.LayoutName);

                var validationResult = await _validatorService.ValidateTransformationAsync(
                    request.InputTxt,
                    request.LayoutName,
                    request.TclPath,
                    request.XslPath,
                    request.ExpectedOutputXml);

                return Ok(validationResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Alimenta o motor de aprendizado com exemplos de TCL/XSL para um layout — usado para
        /// treinar padrões reaproveitáveis na geração automática (<see cref="AutoTransformationController"/>).
        /// </summary>
        /// <response code="200">Padrões aprendidos (ver <c>patterns</c>).</response>
        /// <response code="500">Falha ao processar os exemplos.</response>
        [HttpPost("learn-from-examples")]
        public async Task<IActionResult> LearnFromExamples([FromBody] LearnFromExamplesRequest request)
        {
            try
            {
                _logger.LogInformation("Iniciando aprendizado a partir de exemplos para layout: {LayoutName}", request.LayoutName);

                object learningResult = new { success = false };

                if (request.TclExamples != null && request.TclExamples.Any())
                {
                    var tclResult = await _learningService.LearnTclPatternsAsync(
                        request.LayoutName,
                        request.TclExamples);

                    learningResult = new { success = tclResult.Success, patterns = tclResult.PatternsLearned };
                }

                if (request.XslExamples != null && request.XslExamples.Any())
                {
                    var xslResult = await _learningService.LearnXslPatternsAsync(
                        request.LayoutName,
                        request.XslExamples);

                    learningResult = new { success = xslResult.Success, patterns = xslResult.PatternsLearned };
                }

                return Ok(learningResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar aprendizado");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Roda uma transformação (pathway tcl-xsl) e imediatamente valida o resultado contra o
        /// XML esperado — atalho para QA/regressão em um único request.
        /// </summary>
        /// <response code="200">Transformação executada; ver <c>testPassed</c> para o veredito.</response>
        /// <response code="400">Transformação falhou (ver <c>errors</c>) — teste não chegou a validar.</response>
        /// <response code="500">Falha não catalogada.</response>
        [HttpPost("run-test")]
        public async Task<IActionResult> RunTransformationTest([FromBody] TransformationTestRequest request)
        {
            try
            {
                _logger.LogInformation("Executando teste de transformação para layout: {LayoutName}", request.LayoutName);

                // Executar transformação
                var transformationResult = await _pipelineService.TransformTxtToXmlAsync(
                    request.InputTxt,
                    request.LayoutName,
                    request.TargetDocumentType ?? "NFe");

                if (!transformationResult.Success)
                {
                    return BadRequest(new
                    {
                        success = false,
                        testPassed = false,
                        errors = transformationResult.Errors
                    });
                }

                // Validar resultado
                var validationResult = await _validatorService.ValidateTransformationAsync(
                    request.InputTxt,
                    request.LayoutName,
                    transformationResult.TclPath,
                    transformationResult.XslPath,
                    request.ExpectedOutputXml);

                var testPassed = validationResult.Success &&
                                validationResult.ValidationSteps.All(s => s.Success);

                return Ok(new
                {
                    success = true,
                    testPassed = testPassed,
                    transformedXml = transformationResult.TransformedXml,
                    validation = validationResult,
                    segmentMappings = transformationResult.SegmentMappings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar teste de transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Executa transformação usando o motor low-code (SysMiddle) via runner x86, direto por
        /// mapper (sem a busca multi-candidato de <see cref="ExecuteTransformationCandidates"/>).
        /// </summary>
        /// <param name="request"><c>InputContent</c> obrigatório; <c>MapperId</c> ou <c>MapperName</c> obrigatório.</param>
        /// <response code="200">Transformação concluída (<c>transformedXml</c>).</response>
        /// <response code="400"><c>InputContent</c> ou identificador do mapper ausente.</response>
        /// <response code="500">Falha do runner x86/processo externo.</response>
        // Issue #32: idem execute-candidates — era restrito ao papel "admin". Issue #93: mesma
        // reabertura para qualquer usuário autenticado.
        [Authorize]
        [HttpPost("execute-lowcode")]
        public async Task<IActionResult> ExecuteLowCode([FromBody] LowCodeTransformationRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.InputContent))
                    return BadRequest(new { error = "InputContent é obrigatório" });

                if (string.IsNullOrWhiteSpace(request.MapperId) && string.IsNullOrWhiteSpace(request.MapperName))
                    return BadRequest(new { error = "MapperId ou MapperName é obrigatório" });

                var transformed = await _lowCode.TransformAsync(
                    request.InputContent,
                    mapperId: request.MapperId,
                    mapperName: request.MapperName,
                    fileName: request.FileName,
                    package: request.Package,
                    globalFolder: request.GlobalFolder,
                    sysmiddleDir: request.SysmiddleDir);

                return Ok(new { success = true, transformedXml = transformed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar transformação low-code");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Issue #140 (itens 2/6-9 da divisão de trabalho, design em
        /// docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md §8): endpoint
        /// dedicado que conecta o motor de resolução estrutural TXT↔XML já implementado (item 1/3/4/5,
        /// <c>ai/XslSynth.Contracts/Core/StructuralResolution/</c>) ao pipeline real — parse posicional
        /// real (<see cref="ILayoutParserService"/>, fonte de <c>ParsedField.Occurrence</c> real) +
        /// mapper real decifrado (<see cref="MapperDatabaseService"/> + <c>RealMapperParser</c>, Parser
        /// B canônico da #139) + catálogo XML de destino cacheado (NF-e via XSD).
        ///
        /// <para>Deliberadamente SEPARADO do contrato de <c>execute-candidates</c>
        /// (<see cref="TransformationExecutionCandidatesResponse"/>): a decisão de expor
        /// <c>FieldToXmlMapping[]</c> como recurso de primeira classe dentro daquele contrato é da
        /// issue #141, não desta — aqui só a infraestrutura de composição é ligada ponta a ponta.</para>
        ///
        /// <para>Resiliência: qualquer falha (layout/mapper não encontrado, XSD indisponível, parse
        /// malformado) vira 200 com <c>fieldMappings: []</c> + warning, nunca deriuba com 500 — o
        /// motor de resolução estrutural é best-effort por natureza (design §5).</para>
        /// </summary>
        /// <response code="200">Sempre — inclusive quando não há mapeamentos (<c>fieldMappings: []</c> + <c>warnings</c>, nunca 500 por falha do motor best-effort).</response>
        /// <response code="400"><c>LayoutName</c>/<c>InputContent</c> ausente, layout não encontrado, ou sem <c>LayoutGuid</c> válido.</response>
        /// <response code="500">Falha de infraestrutura ao consultar o catálogo de layouts.</response>
        [Authorize]
        [HttpPost("field-mappings")]
        public async Task<IActionResult> GetFieldMappings([FromBody] FieldMappingsRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.LayoutName) || string.IsNullOrWhiteSpace(request.InputContent))
                return BadRequest(new { success = false, error = "LayoutName e InputContent são obrigatórios" });

            var warnings = new List<string>();
            // ✅ CodeQL cs/log-forging: saneado uma vez, reusado nos logs deste endpoint.
            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(request.LayoutName);

            LayoutRecord? layoutRecord;
            try
            {
                var searchResponse = await _layoutDb.SearchLayoutsAsync(new LayoutSearchRequest { SearchTerm = request.LayoutName });
                if (!searchResponse.Success)
                    throw new InvalidOperationException(searchResponse.ErrorMessage);

                layoutRecord = searchResponse.Layouts
                    .FirstOrDefault(l => string.Equals(l.Name, request.LayoutName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha de infraestrutura ao resolver layout {LayoutName} para field-mappings", safeLayoutName);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao consultar o catálogo de layouts" });
            }

            if (layoutRecord == null || string.IsNullOrWhiteSpace(layoutRecord.DecryptedContent))
                return BadRequest(new { success = false, error = $"Layout '{request.LayoutName}' não encontrado ou sem conteúdo decifrado" });

            var resolvedLayoutGuid = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
            if (resolvedLayoutGuid == null)
                return BadRequest(new { success = false, error = $"Layout {request.LayoutName} sem LayoutGuid válido no request ou no catálogo" });

            try
            {
                // 1) Parse posicional real do documento de entrada — fonte de Layout (crosswalk
                //    GUID/nome de origem) e ParsedField (Occurrence físico real, nunca sintético).
                using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutRecord.DecryptedContent));
                using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.InputContent));
                var parsingResult = await _layoutParser.ParseAsync(layoutStream, txtStream);

                if (!parsingResult.Success || parsingResult.Layout == null)
                {
                    warnings.Add($"Parse posicional falhou para layout {request.LayoutName}: {parsingResult.ErrorMessage}");
                    return Ok(new { success = true, fieldMappings = Array.Empty<object>(), warnings });
                }

                // 2) Mapper real (Parser B canônico #139) — mesma seleção/priorização já usada pelo
                //    pathway sysmiddle de execute-candidates.
                var ranked = await _mapperDb.GetRankedMapperCandidatesForLayoutGuidAsync(
                    resolvedLayoutGuid, _lowCodeOpt.ProjectId, _lowCodeOpt.AllowedPackageGuids);
                var mapperRecord = ranked.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.DecryptedContent));

                if (mapperRecord == null)
                {
                    warnings.Add($"Nenhum mapeador decifrável encontrado para o layout {request.LayoutName}");
                    return Ok(new { success = true, fieldMappings = Array.Empty<object>(), warnings });
                }

                var mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapperRecord.DecryptedContent));

                // 3) Composição: motor de resolução estrutural (itens 1/3/4/5, já implementado) sobre
                //    dados 100% reais — nenhuma coordenada sintética.
                var fieldMappings = _fieldMappingComposition.Compose(parsingResult.Layout, parsingResult.ParsedFields, mapperVo, parsingResult.LineInfos);

                return Ok(new
                {
                    success = true,
                    mapperGuid = mapperRecord.MapperGuid,
                    fieldMappings,
                    warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao compor field mappings para layout {LayoutName}", safeLayoutName);
                warnings.Add("Falha ao compor mapeamentos estruturais — ver log do servidor");
                return Ok(new { success = true, fieldMappings = Array.Empty<object>(), warnings });
            }
        }
    }

    /// <summary>Requisição de <c>PUT ai-prompt-adicional</c> (issue #98).</summary>
    public class SetAiPromptAdicionalRequest
    {
        public string? Instruction { get; set; }
    }

    /// <summary>Requisição de <c>PUT ai-preferences</c> (issue #322). Campos nulos preservam o valor já salvo.</summary>
    public class SetAiPreferencesRequest
    {
        public string? PreferredLanguage { get; set; }
        public string? PreferredExplanationDetailLevel { get; set; }
        public string? DefaultTransformationEngine { get; set; }
    }

    /// <summary>Request do endpoint /field-mappings (issue #140). Mesma convenção de LayoutGuid
    /// opcional já usada por <see cref="TransformationRequest"/> (precedência sobre o catálogo).</summary>
    public class FieldMappingsRequest
    {
        public string LayoutName { get; set; } = "";
        public string InputContent { get; set; } = "";
        public string? LayoutGuid { get; set; }
    }

    /// <summary>Requisição do pathway low-code direto (<c>execute-lowcode</c>).</summary>
    public class LowCodeTransformationRequest
    {
        public string InputContent { get; set; } = "";
        /// <summary>GUID do mapper Sysmiddle. Um de <see cref="MapperId"/>/<see cref="MapperName"/> é obrigatório.</summary>
        public string? MapperId { get; set; }
        /// <summary>Nome do mapper — usado para busca quando <see cref="MapperId"/> não é informado.</summary>
        public string? MapperName { get; set; }
        public string? FileName { get; set; }

        // Overrides opcionais (caso não queira depender do appsettings)
        public string? Package { get; set; }
        public string? GlobalFolder { get; set; }
        public string? SysmiddleDir { get; set; }
    }

    /// <summary>
    /// Request de <c>POST field-correction</c> (issue #345, ADR §7). <c>documentId</c>/<c>candidateId</c>
    /// vêm da resposta de <c>execute-candidates</c>; <c>documentType</c> é opcional e usado só para
    /// telemetria — nunca prevalece sobre o <c>layoutGuid</c> resolvido no contexto persistido.
    /// </summary>
    public class FieldCorrectionRequest
    {
        public string DocumentId { get; set; } = "";
        public string CandidateId { get; set; } = "";
        public string FieldPath { get; set; } = "";
        public string ObservedValue { get; set; } = "";
        public string ExpectedValue { get; set; } = "";
        public string? Justification { get; set; }
        public string? DocumentType { get; set; }
    }

    /// <summary>
    /// Body de <c>POST field-correction/{reportId}/review</c> (issue #346). <c>decision</c>
    /// obrigatório: <c>"accepted"</c> ou <c>"rejected"</c> (case-insensitive).
    /// </summary>
    public class FieldCorrectionReviewRequest
    {
        public string? Decision { get; set; }
    }
}
