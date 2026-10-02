using LayoutParserApi.Services.Storage;
﻿using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Security;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ParseController : ControllerBase
    {
        private readonly ILayoutParserService _parserService;
        private readonly ILogger<ParseController> _logger;
        private readonly ILayoutDetector _layoutDetector;
        private readonly FileStorageService _fileStorage;
        private readonly LayoutLearningService _learningService;
        private readonly IConfiguration _configuration;
        private readonly LowCodeAutoTransformationService _lowCodeAuto;
        private readonly LowCodeRunnerOptions _lowCodeOpt;
        private readonly LowCodeTransformationStore _transformationStore;
        private readonly IFiscalAnalysisService? _analysisService;
        private readonly ICurrentUser? _currentUser;

        /// <summary>Teto do registro de histórico (issue #366): estourou, a análise segue sem AnalysisId.</summary>
        private static readonly TimeSpan AnalysisHistoryTimeout = TimeSpan.FromSeconds(5);

        // Contexto de histórico repassado de Upload/Auto ao núcleo do upload (issue #366).
        private sealed record AnalysisHistoryContext(string? WorkspaceIdRaw, string Source, string? LayoutGuid, string? OriginalDocumentName);

        public ParseController(
            ILayoutParserService parserService,
            ILogger<ParseController> logger,
            ILayoutDetector layoutDetector,
            FileStorageService fileStorage,
            LayoutLearningService learningService,
            IConfiguration configuration,
            LowCodeAutoTransformationService lowCodeAuto,
            IOptions<LowCodeRunnerOptions> lowCodeOptions,
            LowCodeTransformationStore transformationStore,
            // Issue #366 (histórico de análises): opcionais — sem eles o parse segue sem histórico.
            IFiscalAnalysisService? analysisService = null,
            ICurrentUser? currentUser = null)
        {
            _analysisService = analysisService;
            _currentUser = currentUser;
            _parserService = parserService;
            _logger = logger;
            _layoutDetector = layoutDetector;
            _fileStorage = fileStorage;
            _learningService = learningService;
            _configuration = configuration;
            _lowCodeAuto = lowCodeAuto;
            _lowCodeOpt = lowCodeOptions.Value;
            _transformationStore = transformationStore;
        }

        /// <summary>
        /// Endpoint principal de parse: recebe um layout XML (low-code Sysmiddle) + um documento
        /// posicional (TXT/MQSeries/IDOC) e devolve a estrutura parseada, validações de linha e —
        /// quando aplicável — transformação(ões) XSLT candidata(s), entregue de forma síncrona
        /// (dentro do teto <c>LowCode:SyncDeliveryTimeoutSeconds</c>) ou assíncrona via
        /// <c>transformationsTicket</c>.
        /// </summary>
        /// <param name="layoutFile">Layout XML (Sysmiddle) que descreve os campos/posições esperados.</param>
        /// <param name="txtFile">Documento a ser parseado (TXT posicional, MQSeries ou IDOC).</param>
        /// <param name="layoutName">
        /// Nome do layout selecionado no front — usado para salvar o documento na pasta de
        /// aprendizado (<c>TransformationPipeline:ExamplesPath</c>) e para dar override no tipo
        /// detectado quando contém "MQ".
        /// </param>
        /// <param name="workspaceId">
        /// Opcional (issue #366). GUID do workspace; quando presente, válido, com usuário identificado
        /// e membro do workspace, a análise (documento + layout) é registrada no histórico
        /// (<c>GET api/workspaces/{ws}/analyses</c>). Sem ele, ou se o registro falhar/estourar 5 s, o
        /// parse segue normalmente e <c>historyRegistered</c> vem <c>false</c>.
        /// </param>
        /// <returns>
        /// Documento parseado (<c>layout</c>, <c>fields</c>, <c>documentStructure</c>,
        /// <c>lineValidations</c>) + estado do pathway de transformação low-code
        /// (<c>transformations</c>, <c>transformationsStatus</c>, <c>transformationsTicket</c>).
        /// Se o arquivo enviado for XML, retorna instrução para processar no front-end em vez de
        /// tentar parsear no servidor. Campos aditivos do histórico (#366): <c>analysisId</c> (omitido
        /// quando a análise não foi registrada) e <c>historyRegistered</c> (bool).
        /// </returns>
        /// <response code="200">Parse concluído (mesmo com <c>validationErrors</c> — o parse degrada, não falha, quando o defeito é localizável).</response>
        /// <response code="400">Layout XML ou documento ausente, ou layout não é <c>.xml</c>.</response>
        /// <response code="422">Entrada inválida/irrecuperável (documento vazio, malformado) — culpa do arquivo enviado, não da API.</response>
        /// <response code="500">Falha não catalogada (defeito nosso) — mensagem segura no corpo, causa real no log via <c>correlationId</c>.</response>
        // SCS0016 (issue #88): sem [ValidateAntiForgeryToken] por design — a API não usa autenticação
        // por cookie de sessão (o vetor clássico de CSRF); a identidade vem do BFF via
        // TrustedIdentityMiddleware, que só confia nos headers x-iis-user/x-iis-roles quando a origem
        // é loopback (ver .claude/rules/security.md). Isso fecha a forja de identidade cross-site que
        // o SCS0016 pressupõe.
#pragma warning disable SCS0016
        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpPost("upload")]
        public Task<IActionResult> Upload(IFormFile layoutFile, IFormFile txtFile, [FromForm] string layoutName = null, [FromForm] string? workspaceId = null)
#pragma warning restore SCS0016
            => UploadCoreAsync(layoutFile, txtFile, layoutName, new AnalysisHistoryContext(workspaceId, FiscalAnalysisSource.Upload, null, null));

        // Núcleo do /upload — /auto reusa este método (com contexto de histórico próprio) em vez de
        // chamar o endpoint público, para não gravar o XML descriptografado do catálogo no histórico.
        private async Task<IActionResult> UploadCoreAsync(IFormFile layoutFile, IFormFile txtFile, string layoutName, AnalysisHistoryContext? history)
        {
            if (layoutFile == null || txtFile == null)
                return BadRequest("Layout XML e arquivo são obrigatórios.");

            if (Path.GetExtension(layoutFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo de layout deve ser XML.");

            try
            {
                // Detecção de tipo extraída para método privado reutilizável (issue #216) —
                // o novo endpoint /api/parse/detect chama o mesmo método, evitando duplicar os
                // "casos especiais" hardcoded (linha com 601 chars → mqseries, extensão .idoc, etc.).
                var (detectedType, sample, fileExtension, isXmlFile) = await DetectDocumentTypeAsync(txtFile, layoutName);

                var isXmlInput = isXmlFile || detectedType == "xml";

                // Se for arquivo XML, retornar indicando que deve ser processado no front-end
                if (isXmlInput)
                {
                    _logger.LogInformation("Arquivo XML detectado, deve ser processado no front-end");
                    return Ok(new
                    {
                        success = true,
                        fileType = "xml",
                        detectedType = "xml",
                        message = "Arquivo XML detectado. Processe no front-end com xmltools.js",
                        content = sample // Retornar conteúdo para processamento no front-end
                    });
                }

                // ✅ Documento sem conteúdo: irrecuperável, não há o que renderizar (spec §2.2).
                // Sem este gate o parse "sucede" com zero campos e o payload sairia com
                // documentHealth="clean" — uma mentira: documento vazio não é documento limpo.
                // Fica ANTES do aprendizado de máquina de propósito: arquivo vazio não é amostra.
                if (string.IsNullOrEmpty(sample))
                {
                    return ParseFailureResult(
                        ParseFailureCause.DocumentMalformed,
                        ParseFailure.EmptyDocumentMessage,
                        detectedType,
                        layoutFile.FileName,
                        txtFile.FileName);
                }

                // Salvar arquivo para aprendizado de máquina ANTES de processar
                if (!string.IsNullOrEmpty(layoutName))
                    await SaveFileForLearningAsync(layoutName, txtFile, detectedType);


                // Processar arquivo
                using var layoutStream = layoutFile.OpenReadStream();
                using var txtStream = txtFile.OpenReadStream();

                // ✅ Instrumentação de duração do parse (issue #99): resposta formal à proposta do
                // front-end sobre a barra de progresso travando em "100%" — antes desta medição não
                // havia número de referência para decidir se o parse (à parte da transformação, que
                // já tem o transformationsTicket abaixo) também precisaria de um mecanismo próprio no
                // futuro. Mede só ParseAsync, não o upload/detecção/gravação de aprendizado que vêm
                // antes — é o trecho apontado como suspeito na issue.
                var parseStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await _parserService.ParseAsync(layoutStream, txtStream);
                parseStopwatch.Stop();
                _logger.LogInformation(
                    "Parse concluído em {ParseDurationMs}ms (Layout={LayoutFile}, Txt={TxtFile}, Tipo={DetectedType})",
                    parseStopwatch.ElapsedMilliseconds, layoutFile.FileName, txtFile.FileName, detectedType);

                // ✅ Gate de falha de parse: ParseAsync captura a exceção internamente e devolve
                // Success=false / Layout=null, com a causa real em ErrorMessage. Sem este gate,
                // ReestruturarLayout(null) devolve null em silêncio e o NullReference só estoura
                // adiante (ao ler LayoutGuid), virando um 500 "Object reference not set..." que
                // apaga a mensagem que diria a causa real.
                //
                // O gate não sumiu — foi RECLASSIFICADO (spec-taxonomia-de-falha-do-parse.md §3).
                // Antes toda falha virava 422, o que culpa o arquivo do usuário até quando a culpa
                // é nossa. Agora a causa sai do tipo da exceção: entrada ruim → 422; qualquer outra
                // → 500, porque exceção não catalogada é defeito nosso até prova em contrário.
                if (!result.Success || result.Layout == null)
                {
                    // FailureCause nulo aqui = falhou sem exceção catalogada (ex.: Layout nulo com
                    // Success=true). O default culpa a NÓS, não o usuário.
                    var causa = result.FailureCause ?? ParseFailureCause.ParserDefect;

                    return ParseFailureResult(
                        causa,
                        result.ErrorMessage,
                        detectedType,
                        layoutFile.FileName,
                        txtFile.FileName);
                }

                var layoutReestruturado = _parserService.ReestruturarLayout(result.Layout);
                var layoutReordenado = _parserService.ReordenarSequences(layoutReestruturado);

                var flattenedLayout = new Layout
                {
                    LayoutGuid = layoutReordenado.LayoutGuid,
                    LayoutType = layoutReordenado.LayoutType,
                    Name = layoutReordenado.Name,
                    Description = layoutReordenado.Description,
                    LimitOfCaracters = layoutReordenado.LimitOfCaracters,
                    // ✅ Discriminador canônico de formato físico (ADR-001). Achatar o layout não pode
                    // apagar o campo: quem consumir este objeto adiante precisa saber se é IDOC
                    // (registro por linha) ou MQSeries (stream contínuo) — LayoutType não distingue.
                    WithBreakLines = layoutReordenado.WithBreakLines,
                    Elements = layoutReordenado.Elements
                };

                // ✅ SEMPRE processar documento mesmo com erros de validação
                // (result.Success sempre será true agora, mas pode ter ValidationErrors)
                
                var documentStructure = _parserService.BuildDocumentStructure(result);

                // Calcular validações e posições das linhas para o front-end
                // ✅ Resolução mesclada: LimitOfCaracters > 0 → allowlist manual → null (sem validação)
                List<LineValidationInfo>? lineValidations = null;
                var expectedLineLength = LineLengthResolver.Resolve(flattenedLayout);
                var positionalMetadata = LowCodePositionalMetadata.Resolve(
                    result.Layout,
                    result.RawText,
                    expectedLineLength ?? LineLengthResolver.LegacyDefaultLineLength);
                
                if (expectedLineLength.HasValue)
                    lineValidations = _parserService.CalculateLineValidations(flattenedLayout, expectedLineLength.Value);

                // ✅ Transformação low-code: entrega SÍNCRONA no response quando possível, com teto de
                // tempo (LowCode:SyncDeliveryTimeoutSeconds); se estourar, cai para processamento em
                // background (o trabalho já em andamento NÃO é perdido — a persistência em disco
                // acontece dentro da mesma chamada, independente de o controller continuar esperando).
                // Decisão de arquitetura (Aria, 2026-07-28): o parse do documento é a resposta
                // principal e NUNCA pode ser bloqueado além do teto nem falhar por causa deste pathway.
                object? transformations = null;
                var transformationsStatus = "not_applicable";
                var eligibility = LowCodeTransformationEligibility.Evaluate(
                    result.Success,
                    flattenedLayout.LayoutGuid,
                    result.RawText,
                    detectedType,
                    isXmlInput);
                string? transformationsReason = eligibility.Reason;

                // ✅ Ticket de consulta das transformações (spec §2.6): emitido sempre que o pathway
                // é elegível — INCLUSIVE quando a entrega síncrona não deu tempo ("processing"). É o
                // que mata o rótulo "(processando...)" eterno: antes o front não tinha a quem
                // perguntar se terminou, porque o store era escrito e nunca lido.
                string? transformationsTicket = eligibility.IsEligible
                    ? LowCodeTransformationStore.BuildTicketFromContent(result.RawText, flattenedLayout.LayoutGuid)
                    : null;

                try
                {
                    if (eligibility.IsEligible)
                    {
                        var syncTimeoutSeconds = _lowCodeOpt.SyncDeliveryTimeoutSeconds > 0 ? _lowCodeOpt.SyncDeliveryTimeoutSeconds : 6;

                        // ✅ O teto agora CANCELA o trabalho, não só a espera (spec §1.1). Antes, o
                        // trabalho abandonado seguia vivo segurando um dos MaxConcurrentRunners e
                        // atrasando o próximo upload — e não chegava a lugar nenhum, porque ninguém
                        // conseguia ler o store. Com o índice consultável, cancelar deixa de ser
                        // perda: o que ficou pronto é gravado e responde pelo ticket.
                        var syncCts = new CancellationTokenSource(TimeSpan.FromSeconds(syncTimeoutSeconds));

                        var transformTask = _lowCodeAuto.RunAsync(
                            flattenedLayout.LayoutGuid,
                            flattenedLayout.Name,
                            result.RawText,
                            detectedType,
                            txtFile.FileName,
                            positionalMetadata,
                            syncCts.Token);

                        // A corrida contra o Task.Delay continua: o cancelamento é cooperativo (o
                        // kill do processo tem sua própria janela), e o parse não pode esperar nem
                        // isso — a resposta principal é o documento parseado.
                        var winner = await Task.WhenAny(transformTask, Task.Delay(TimeSpan.FromSeconds(syncTimeoutSeconds)));

                        // ⚠️ Vencer a corrida não basta: o cancelamento faz a task terminar QUASE no
                        // mesmo instante do Task.Delay (ela devolve os candidatos já marcados como
                        // falha por cancelamento). Sem checar o token, essa corrida sairia às vezes
                        // como "completed" com tudo falhando — que é pior que "processing": diria ao
                        // usuário "terminou e deu erro" quando a verdade é "não deu tempo".
                        var concluiuDentroDoTeto = winner == transformTask && !syncCts.IsCancellationRequested;

                        if (concluiuDentroDoTeto)
                        {
                            // Já concluiu dentro do teto — observamos o resultado (RunAsync já trata
                            // falha de candidato individual internamente, não deve lançar por isso).
                            var autoResult = await transformTask;
                            syncCts.Dispose();

                            // ✅ "failed" (contrato aditivo 2026-08-27, spec §2): existe candidato mas
                            // NENHUM teve sucesso — distinto de "completed" (ao menos um candidato OK),
                            // sem exigir que o front varra o array pra descobrir que deu tudo errado.
                            var todosFalharam = autoResult.Applicable
                                && autoResult.Candidates.Count > 0
                                && autoResult.Candidates.All(c => !c.Success);

                            transformationsStatus = !autoResult.Applicable
                                ? "not_applicable"
                                : todosFalharam ? "failed" : "completed";

                            if (autoResult.Applicable)
                            {
                                transformations = AplicarTetoDeXmlInline(autoResult.Candidates);
                                transformationsReason = null;
                            }
                            else
                            {
                                transformationsReason = LowCodeTransformationEligibility.NoMapperReason;
                            }
                        }
                        else
                        {
                            // Estourou o teto síncrono: a resposta segue sem esperar mais e o trabalho
                            // é interrompido (o slot do runner volta para a fila). O que já tiver
                            // ficado pronto fica no índice, consultável por transformationsTicket.
                            transformationsStatus = "processing";
                            transformationsReason = LowCodeTransformationEligibility.TimeoutSyncReason;
                            _ = transformTask.ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                    _logger.LogError(t.Exception, "Falha no processamento low-code em background (após estouro do teto síncrono de {SyncTimeoutSeconds}s)", syncTimeoutSeconds);
                                syncCts.Dispose();
                            }, TaskScheduler.Default);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ✅ Falha estrutural do pathway de transformação (ex.: banco fora do ar ao buscar
                    // mapeadores) NUNCA pode derrubar o parse principal — o parse TXT->estrutura já
                    // sucedeu e é a resposta que importa.
                    _logger.LogWarning(ex, "Falha ao processar transformações low-code (parse principal não afetado)");
                    transformationsStatus = "error";
                    transformationsReason = LowCodeTransformationEligibility.StructuralErrorReason;
                }

                // ✅ Issue #366: histórico opt-in (workspaceId) — falha/timeout NUNCA derruba o parse.
                var analysisId = await TryRegisterAnalysisAsync(history, layoutFile, txtFile, layoutName, detectedType);

                return Ok(new
                {
                    success = true,
                    detectedType,
                    analysisId, // null (omitido no JSON) quando não registrado; ver historyRegistered
                    historyRegistered = analysisId.HasValue,
                    // ✅ Defeito localizável NÃO é 422: o documento parseou e é renderizável, só
                    // vai anotado. A UI decide o modo de exibição por este campo (spec §2.1).
                    documentHealth = DocumentHealth.Resolve(result.ValidationErrors),
                    layout = flattenedLayout,
                    fields = result.ParsedFields,
                    text = result.RawText,
                    summary = result.Summary,
                    documentStructure = documentStructure,
                    lineValidations = lineValidations, // Validações e posições calculadas (apenas para layouts configurados)
                    lineInfos = result.LineInfos, // ✅ Contrato aditivo 2026-08-27: sinais por linha (IsDeclaredEmpty, PositionalAlignmentFailed)
                    validationErrors = result.ValidationErrors, // ✅ Erros de validação de tamanho de linha
                    validationWarning = !string.IsNullOrEmpty(result.ErrorMessage) ? result.ErrorMessage : null, // ✅ Aviso se houver erros
                    transformations, // array de candidatos low-code (mapper/target/xml/sucesso-ou-erro) quando concluído a tempo
                    transformationsStatus, // "not_applicable" | "completed" | "failed" | "processing" | "error" (contrato aditivo 2026-08-27: "failed" é novo — ver GetTransformations)
                    transformationsReason, // opcional: no_mapper | type_not_positional | empty_input | timeout_sync | structural_error
                    transformationsTicket // consulta do resultado: GET /api/parse/transformations/{ticket}
                });
            }
            catch (Exception ex)
            {
                // Exceção fora do ParseAsync (montagem da resposta, detecção, I/O do upload).
                // Mesma taxonomia: exceção não catalogada é defeito NOSSO → 500 com mensagem
                // segura. Antes daqui saía `"Erro interno: {ex.Message}"` — uma string crua, sem
                // failureCause nem correlationId, vazando texto de exceção pro cliente.
                _logger.LogError(ex, "Erro durante o parsing do XML. TipoExcecao={ExceptionType}", ex.GetType().FullName);

                return ParseFailureResult(
                    ParseFailure.Classify(ex),
                    ex.Message,
                    "unknown",
                    layoutFile.FileName,
                    txtFile.FileName);
            }
        }

        /// <summary>
        /// Manifesto das transformações de um documento já parseado — status + descritores dos
        /// candidatos, <b>sem o XML</b> (é o lado "consultado sempre" do split da spec §2.4).
        ///
        /// <para>Vocabulário deliberadamente compatível com o <c>execute-candidates</c>
        /// (<c>candidateId</c>/<c>pathway</c>/<c>failureReason</c> de <c>TransformationCandidate</c>)
        /// somado aos descritores de domínio do candidato low-code (<c>mapperGuid</c>,
        /// <c>success</c>, <c>outputLength</c>): é um superconjunto dos dois shapes que o front já
        /// consome, para não criar um terceiro dialeto (spec §3.3).</para>
        /// </summary>
        /// <param name="ticket">"{sha256}.{layoutGuid}" — devolvido pelo upload em <c>transformationsTicket</c>.</param>
        /// <response code="200">Manifesto encontrado (status "processing" | "completed" | "failed").</response>
        /// <response code="400">Ticket fora do formato.</response>
        /// <response code="404">Nenhuma execução registrada para este ticket.</response>
        /// <remarks>
        /// Contrato aditivo (2026-08-27, ver
        /// <c>docs/architecture/contrato-linha-vazia-progresso-e-degradacao-posicional-2026-08-27.md</c>
        /// §2): o vocabulário completo de fases é <c>"uploaded"</c> → <c>"layout_selected"</c> →
        /// <c>"parsing"</c> → <c>"transforming"</c> → <c>"completed"</c>/<c>"failed"</c>, mas as 3
        /// primeiras são <b>client-side only</b> — este endpoint só existe (índice só é gravado) a
        /// partir de depois que o documento já foi parseado, então a API nunca as emite. O que
        /// este endpoint efetivamente retorna em <c>status</c> é <c>"processing"</c> (valor de fio
        /// inalterado — equivale à fase "transforming"), <c>"completed"</c> (≥1 candidato com
        /// sucesso) ou <c>"failed"</c> (novo: existe candidato, mas nenhum teve sucesso — antes
        /// isso vinha como "completed" com <c>success=false</c> em todos os itens de
        /// <c>candidates</c>, obrigando o front a inferir o fracasso varrendo o array).
        /// </remarks>
        [HttpGet("transformations/{ticket}")]
        public async Task<IActionResult> GetTransformations(string ticket)
        {
            // ✅ VALIDAÇÃO por charset fixo, nunca sanitização por remoção de caracteres: o ticket
            // vem do cliente e vira nome de arquivo. Sanitizar aceitaria entrada hostil e tentaria
            // consertá-la; validar recusa. Path traversal ("..", separador) morre aqui (spec §2.5).
            if (!LowCodeTransformationStore.TryParseTicket(ticket, out var sha256, out var layoutGuid))
                return BadRequest(new { success = false, error = "Ticket de transformação inválido." });

            var entrada = await _transformationStore.ReadEntryAsync(sha256, layoutGuid);
            if (entrada == null)
                return NotFound(new { success = false, error = "Nenhuma transformação registrada para este ticket." });

            return Ok(new
            {
                success = true,
                ticket,
                status = entrada.Status, // "processing" | "completed" | "failed" (contrato aditivo 2026-08-27)
                partial = entrada.Partial, // true = execução interrompida no teto síncrono; pode faltar candidato
                candidates = entrada.Candidates.Select(c => new
                {
                    candidateId = $"sysmiddle-{c.MapperGuid}",
                    pathway = "sysmiddle",
                    mapperGuid = c.MapperGuid,
                    mapperName = c.MapperName,
                    targetLayoutGuid = c.TargetLayoutGuid,
                    success = c.Success,
                    outputLength = c.OutputLength,
                    // Já saneado na escrita; saneado de novo na leitura porque o índice é um arquivo
                    // em disco — nada que venha de arquivo entra no wire sem passar pelo filtro.
                    errorMessage = LowCodeErrorSanitizer.ForWire(c.ErrorMessage),
                    failureReason = LowCodeErrorSanitizer.ForWire(c.ErrorMessage)
                }).ToList()
            });
        }

        /// <summary>
        /// Corpo (XML) de UM candidato — o lado "consultado às vezes" do split (spec §2.4). É o que
        /// o front busca quando o <c>outputXml</c> foi omitido do payload do parse por exceder
        /// <c>LowCode:InlineXmlMaxChars</c>.
        /// </summary>
        /// <response code="200">XML do candidato.</response>
        /// <response code="400">Ticket ou mapperGuid fora do formato.</response>
        /// <response code="404">Ticket, candidato ou artefato inexistente.</response>
        [HttpGet("transformations/{ticket}/candidates/{mapperGuid}")]
        public async Task<IActionResult> GetTransformationCandidate(string ticket, string mapperGuid)
        {
            if (!LowCodeTransformationStore.TryParseTicket(ticket, out var sha256, out var layoutGuid))
                return BadRequest(new { success = false, error = "Ticket de transformação inválido." });

            if (string.IsNullOrWhiteSpace(mapperGuid))
                return BadRequest(new { success = false, error = "mapperGuid é obrigatório." });

            var entrada = await _transformationStore.ReadEntryAsync(sha256, layoutGuid);
            if (entrada == null)
                return NotFound(new { success = false, error = "Nenhuma transformação registrada para este ticket." });

            // O caminho do artefato vem do índice (nosso), não do cliente — o mapperGuid só é usado
            // para CASAR com um candidato registrado. Não casou, não existe.
            var candidato = entrada.Candidates
                .FirstOrDefault(c => string.Equals(c.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));

            if (candidato == null || !candidato.Success)
                return NotFound(new { success = false, error = "Candidato não encontrado ou sem XML de saída." });

            var xml = await _transformationStore.ReadCandidateXmlAsync(entrada, sha256, layoutGuid, candidato);
            if (xml == null)
                return NotFound(new { success = false, error = "Artefato do candidato indisponível." });

            return Ok(new
            {
                success = true,
                candidateId = $"sysmiddle-{candidato.MapperGuid}",
                mapperGuid = candidato.MapperGuid,
                outputLength = xml.Length,
                outputXml = xml // mesmo nome do campo no payload do parse: o front faz uma ramificação só
            });
        }

        /// <summary>
        /// Aplica o teto de entrega inline (spec §2.4): acima de <c>LowCode:InlineXmlMaxChars</c> o
        /// <c>outputXml</c> é omitido do payload (o serializador ignora nulos) e o front busca o
        /// corpo pelo endpoint dedicado. <c>outputLength</c> vai sempre — sem ele, "campo ausente"
        /// seria indistinguível de "candidato sem saída".
        /// </summary>
        private List<LowCodeCandidateResult> AplicarTetoDeXmlInline(List<LowCodeCandidateResult> candidatos)
        {
            var teto = _lowCodeOpt.InlineXmlMaxChars > 0 ? _lowCodeOpt.InlineXmlMaxChars : 262144;

            foreach (var candidato in candidatos)
            {
                if (candidato.OutputXml == null)
                    continue;

                candidato.OutputLength = candidato.OutputXml.Length;
                if (candidato.OutputLength > teto)
                    candidato.OutputXml = null;
            }

            return candidatos;
        }

        /// <summary>
        /// Monta a resposta de falha do parse conforme a taxonomia (spec §2.2 e §2.3): 422 quando
        /// a entrada é ruim, 500 quando o defeito é nosso.
        ///
        /// <para><b>Nunca vaze detalhe interno no 500.</b> O <c>message</c> do <c>parser_defect</c>
        /// é um literal fixo; o motivo real (que carrega texto de exceção) fica só no log
        /// estruturado, alcançável pelo <c>correlationId</c> que devolvemos ao cliente.</para>
        /// </summary>
        private IActionResult ParseFailureResult(
            ParseFailureCause causa,
            string? motivoInterno,
            string detectedType,
            string layoutFileName,
            string documentFileName)
        {
            var statusCode = ParseFailure.ToHttpStatusCode(causa);
            var failureCause = ParseFailure.ToWireCode(causa);
            var correlationId = CorrelationContext.CurrentId ?? HttpContext.TraceIdentifier;

            _logger.LogError(
                "Falha no parse do documento. Causa={FailureCause}, Status={StatusCode}, Layout={LayoutFile}, Arquivo={DocumentFile}, Tipo={DetectedType}, Motivo={ErrorMessage}",
                failureCause, statusCode, layoutFileName, documentFileName, detectedType,
                string.IsNullOrWhiteSpace(motivoInterno) ? "(sem motivo registrado)" : motivoInterno);

            return StatusCode(statusCode, new
            {
                success = false,
                failureCause,
                detectedType = string.IsNullOrWhiteSpace(detectedType) ? "unknown" : detectedType,
                message = ParseFailure.ResolveClientMessage(causa, motivoInterno),
                correlationId
            });
        }

        /// <summary>
        /// Detecta o tipo do documento (txt/mqseries/idoc/xml) lendo o conteúdo e aplicando os
        /// overrides por contexto (extensão / layout selecionado). Extraído do fluxo de
        /// <see cref="Upload"/> (issue #216) para ser reutilizado também por <see cref="Detect"/>,
        /// sem duplicar os "casos especiais" hardcoded (ex.: linha com 601 chars → mqseries).
        /// </summary>
        /// <returns>
        /// Tupla com o tipo detectado, o conteúdo lido (amostra), a extensão do arquivo e se o
        /// arquivo é XML puro.
        /// </returns>
        private async Task<(string DetectedType, string Sample, string FileExtension, bool IsXmlFile)> DetectDocumentTypeAsync(
            IFormFile txtFile, string layoutName)
        {
            var fileExtension = Path.GetExtension(txtFile.FileName).ToLower();
            var isXmlFile = fileExtension == ".xml";

            // Ler conteúdo do arquivo para detecção de tipo
            using var txtStreamForDetection = txtFile.OpenReadStream();
            using var reader = new StreamReader(txtStreamForDetection, leaveOpen: true);
            var sample = await reader.ReadToEndAsync();
            var detectedType = _layoutDetector.DetectType(sample);

            // ✅ Overrides por contexto (extensão / layout selecionado)
            // Quando o documento MQSeries tem linha com 601 chars, o detector por conteúdo pode falhar.
            // Nesses casos, a extensão e/ou o layout selecionado são a fonte de verdade.
            if (fileExtension == ".mq_series" ||
                (!string.IsNullOrWhiteSpace(layoutName) && layoutName.Contains("MQ", StringComparison.OrdinalIgnoreCase)))
            {
                detectedType = "mqseries";
            }
            else if (fileExtension == ".idoc")
            {
                detectedType = "idoc";
            }

            return (detectedType, sample, fileExtension, isXmlFile);
        }

        /// <summary>
        /// Endpoint de detecção isolada (issue #216): recebe um documento e retorna só o tipo
        /// detectado (txt/mqseries/idoc/xml) + confiança, sem disparar parse completo nem gravar
        /// amostra de aprendizado. Útil para um agente/consumidor decidir o que fazer antes de
        /// chamar <see cref="Upload"/>.
        /// </summary>
        /// <param name="documentFile">Documento a analisar.</param>
        /// <param name="layoutName">
        /// Nome do layout (opcional) — mesmo override de detecção usado no upload (ex.: contém "MQ").
        /// </param>
        /// <remarks>
        /// <c>suggestedLayouts</c> é um MVP honesto: hoje não existe, isolado do fluxo de parse,
        /// um mecanismo de "matching" que pontue quais layouts do catálogo combinam com o conteúdo
        /// do documento (o que existe é o aprendizado de máquina acoplado ao parse completo). Em vez
        /// de inventar um score, o endpoint retorna a lista vazia — sugestão de layout com score real
        /// fica para uma issue de acompanhamento, conforme já registrado no plano técnico (#216).
        /// </remarks>
        /// <response code="200">Detecção concluída (mesmo quando o tipo não pôde ser identificado — retorna confidence "low").</response>
        /// <response code="400">Documento ausente.</response>
        // SCS0016 (issue #88): mesmo padrão de Upload — sem cookie de sessão, identidade via BFF/
        // TrustedIdentityMiddleware com guarda de loopback.
#pragma warning disable SCS0016
        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpPost("detect")]
        public async Task<IActionResult> Detect(IFormFile documentFile, [FromForm] string layoutName = null)
#pragma warning restore SCS0016
        {
            if (documentFile == null)
                return BadRequest("Documento é obrigatório.");

            // SCS0018 (issue #88): a linha reportada aqui pelo SCS é resíduo de taint-tracking do
            // parâmetro documentFile/layoutName propagado até o sink real de escrita em
            // SaveFileForLearningAsync (FileStream abaixo) — DetectDocumentTypeAsync, chamada logo
            // adiante, não grava nenhum arquivo, só lê o conteúdo em memória. Sink real já sanitizado
            // por SafePathResolver.Resolve + IsInsideBase.
#pragma warning disable SCS0018
            try
            {
                var (detectedType, sample, fileExtension, isXmlFile) = await DetectDocumentTypeAsync(documentFile, layoutName);
                var isXmlInput = isXmlFile || detectedType == "xml";

                // O LayoutDetector retorna "unknown" quando nenhum padrão (xml/mqseries/idoc) bate —
                // na prática, o fluxo de upload trata isso como TXT posicional genérico (ver
                // GetLearningExtension/linha 602 acima: idoc/mqseries/_ → "txt").
                var normalizedType = isXmlInput ? "xml" : detectedType == "unknown" ? "txt" : detectedType;
                var confidence = detectedType == "unknown" ? "low" : "high";

                _logger.LogInformation("Detect: {FileName} -> {DetectedType} (confidence={Confidence})",
                    documentFile.FileName, normalizedType, confidence);

                return Ok(new
                {
                    detectedType = normalizedType,
                    confidence,
                    suggestedLayouts = Array.Empty<object>()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao detectar tipo do documento {FileName}", documentFile.FileName);
                return StatusCode(500, new { success = false, message = "Falha ao detectar o tipo do documento." });
            }
#pragma warning restore SCS0018
        }

        /// <summary>
        /// Salva arquivo na pasta do layout para aprendizado de máquina
        /// </summary>
        private async Task SaveFileForLearningAsync(string layoutName, IFormFile txtFile, string detectedType)
        {
            try
            {
                _logger.LogInformation("Salvando arquivo para aprendizado: Layout={LayoutName}, Tipo={Type}", layoutName, detectedType);

                // Criar diretório baseado no nome do layout
                var basePath = new StoragePaths(_configuration).Examples;

                // ✅ P0 — path traversal (WRITE): layoutName vem [FromForm] do cliente e vira nome de
                // DIRETÓRIO. Sem blindagem, "..\..\algo" escreveria fora da base. Mesmo helper único
                // dos endpoints de leitura. Recusado → pula o aprendizado (best-effort, não derruba o
                // parse) em vez de gravar em lugar arbitrário.
                var layoutDirectory = SafePathResolver.Resolve(basePath, layoutName);
                if (layoutDirectory is null)
                {
                    _logger.LogWarning("Aprendizado ignorado: layoutName invalido para nome de diretorio: {LayoutName}", layoutName);
                    return;
                }

                if (!Directory.Exists(layoutDirectory))
                {
                    Directory.CreateDirectory(layoutDirectory);
                    _logger.LogInformation("Diretório criado: {Path}", Services.Logging.LogMessageSanitizer.Sanitize(layoutDirectory));
                }

                // Salvar com nome totalmente gerado pelo servidor.
                // O nome enviado pelo cliente nunca participa do caminho físico.
                // A extensão também é derivada somente do tipo já detectado.
                // Isso evita traversal, colisões e vazamento de nomes externos.
                // O identificador aleatório mantém cada amostra independente.
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var learningExtension = GetLearningExtension(detectedType);
                var fileName = $"{timestamp}_{Guid.NewGuid():N}{learningExtension}";
                var filePath = Path.Combine(layoutDirectory, fileName);

                if (!SafePathResolver.IsInsideBase(layoutDirectory, filePath))
                {
                    _logger.LogWarning("Aprendizado ignorado: caminho interno fora da base permitida.");
                    return;
                }

                // SCS0018 (issue #88): sink real do finding acima — filePath já validado por
                // SafePathResolver.Resolve + IsInsideBase logo acima; SCS não reconhece o guard custom.
#pragma warning disable SCS0018
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await txtFile.CopyToAsync(stream);
                }
#pragma warning restore SCS0018

                _logger.LogInformation("Arquivo salvo para aprendizado: {Path}", filePath);

                // Executar aprendizado de máquina em background (não bloquear resposta)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Determinar tipo de arquivo para aprendizado
                        var fileType = detectedType?.ToLower() switch
                        {
                            "xml" => "xml",
                            "idoc" => "txt",
                            // ✅ fix/mqseries-line-detection-601: MQSeries é stream contínuo de
                            // largura fixa (sem \n/\r entre "linhas" lógicas) — precisa de split
                            // fixo (ver LayoutLearningService.LearnTextPositionalStructureAsync).
                            // Antes caía em "txt" e o split por quebra de linha via o arquivo
                            // inteiro como 1 única "linha" (TotalFields=0).
                            "mqseries" => "mqseries",
                            _ => "txt"
                        };

                        // Aprender estrutura do arquivo
                        var learningResult = await _learningService.LearnFromFileAsync(filePath, fileType);
                        
                        if (learningResult.Success && learningResult.LearnedModel != null)
                        {
                            // Salvar modelo aprendido
                            learningResult.LearnedModel.FilePath = filePath;
                            await _fileStorage.SaveLearnedModelAsync(layoutDirectory, learningResult.LearnedModel);
                            
                            _logger.LogInformation("Aprendizado concluído para {LayoutName}: {Fields} campos detectados", layoutName, learningResult.LearnedModel.TotalFields);
                        }
                        else
                            _logger.LogWarning("Aprendizado falhou para {LayoutName}: {Message}", layoutName, learningResult.Message);                        
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro durante aprendizado de máquina para {LayoutName}", layoutName);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar arquivo para aprendizado");
                // Não falhar o processamento principal se houver erro no aprendizado
            }
        }

        /// <summary>
        /// Detecta o layout de um documento MQSeries/IDoc usando somente o catálogo interno.
        /// Unicidade exige exatamente um candidato compatível após os gates estruturais; score
        /// serve apenas para ordenar alternativas e nunca cria certeza.
        /// </summary>
        /// <param name="documentFile">Documento posicional a analisar.</param>
        /// <param name="layoutGuidOverride">GUID opcional escolhido entre os candidatos ranked da detecção atual.</param>
        /// <param name="automaticLayoutDetection">Serviço determinístico de detecção.</param>
        /// <param name="cancellationToken">Cancelamento da requisição.</param>
        /// <param name="workspaceId">
        /// Opcional (issue #366). Mesmo comportamento de <c>POST /api/parse/upload</c>: registra a
        /// análise no histórico do workspace. Em <c>/auto</c> o <c>analysisId</c>/<c>historyRegistered</c>
        /// ficam dentro de <c>parseResult</c>, e o layout do catálogo é guardado só como GUID (o XML
        /// descriptografado nunca vai para o histórico).
        /// </param>
        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpPost("auto")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(AutomaticParseResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(AutomaticParseResponse), StatusCodes.Status422UnprocessableEntity)]
#pragma warning disable SCS0016 // A API não usa autenticação por cookie: aceita identidade apenas do BFF em loopback.
        // ✅ FIX (2026-09-03): documentFile NÃO leva [FromForm] explícito — o model binding do
        // ASP.NET Core já trata IFormFile como multipart/form-data automaticamente, e o
        // Swashbuckle não sabe gerar schema para [FromForm] + IFormFile combinados (quebrava
        // /api/swagger/v1/swagger.json com 500 em produção). Mesmo padrão já usado em
        // Upload/Detect deste controller — não reintroduza o atributo aqui.
        public async Task<IActionResult> Auto(
            IFormFile? documentFile,
            [FromForm] string? layoutGuidOverride,
            [FromServices] IAutomaticLayoutDetectionService automaticLayoutDetection,
            CancellationToken cancellationToken,
            [FromForm] string? workspaceId = null)
#pragma warning restore SCS0016
        {
            var correlationId = EnsureCorrelationId();

            if (documentFile is null || documentFile.Length == 0)
            {
                return BadRequest(new
                {
                    success = false,
                    correlationId,
                    message = "O arquivo do documento é obrigatório e não pode estar vazio."
                });
            }

            try
            {
                byte[] documentBytes;
                await using (var sourceStream = documentFile.OpenReadStream())
                await using (var buffer = new MemoryStream())
                {
                    await sourceStream.CopyToAsync(buffer, cancellationToken);
                    documentBytes = buffer.ToArray();
                }

                string documentContent;
                await using (var detectionStream = new MemoryStream(documentBytes, writable: false))
                using (var reader = new StreamReader(detectionStream, System.Text.Encoding.UTF8, true, leaveOpen: false))
                    documentContent = await reader.ReadToEndAsync(cancellationToken);

                var result = await automaticLayoutDetection.DetectAsync(documentContent, cancellationToken);
                var detection = result.Detection;

                LayoutParserApi.Models.Database.LayoutRecord? selectedRecord = null;
                AutomaticLayoutCandidate? selectedCandidate = null;
                var selectionSource = "none";

                if (!string.IsNullOrWhiteSpace(layoutGuidOverride))
                {
                    if (!result.TryGetRankedLayout(layoutGuidOverride, out selectedRecord) || selectedRecord is null)
                    {
                        return UnprocessableEntity(new AutomaticParseResponse
                        {
                            Success = false,
                            CorrelationId = correlationId,
                            Detection = detection,
                            Message = "O layout informado não pertence aos candidatos compatíveis da detecção atual. Execute uma nova detecção e escolha um dos candidatos retornados."
                        });
                    }

                    selectedCandidate = detection.Candidates.First(candidate =>
                        AutomaticLayoutDetectionResult.TryNormalizeGuid(candidate.LayoutGuid, out var candidateGuid)
                        && AutomaticLayoutDetectionResult.TryNormalizeGuid(layoutGuidOverride, out var overrideGuid)
                        && string.Equals(candidateGuid, overrideGuid, StringComparison.OrdinalIgnoreCase));
                    selectionSource = detection.Status == AutomaticLayoutDetectionStatus.Unique
                        ? "auto_unique_confirmed"
                        : "ranked_override";
                    detection.SelectedLayout = selectedCandidate;
                }
                else if (detection.Status == AutomaticLayoutDetectionStatus.Unique
                    && detection.SelectedLayout is not null
                    && result.TryGetRankedLayout(detection.SelectedLayout.LayoutGuid, out selectedRecord))
                {
                    selectedCandidate = detection.SelectedLayout;
                    selectionSource = "auto_unique";
                }

                if (selectedRecord is null || selectedCandidate is null)
                {
                    return Ok(new AutomaticParseResponse
                    {
                        Success = true,
                        CorrelationId = correlationId,
                        Detection = detection
                    });
                }

                if (string.IsNullOrWhiteSpace(selectedRecord.DecryptedContent))
                    throw new InvalidOperationException("O layout selecionado não possui conteúdo interno disponível.");

                _logger.LogInformation(
                    "Seleção de layout da detecção automática. CorrelationId={CorrelationId} Status={DetectionStatus} SelectionSource={SelectionSource} LayoutGuid={LayoutGuid} Rank={Rank} AlgorithmVersion={AlgorithmVersion} CatalogVersion={CatalogVersion}",
                    correlationId,
                    detection.Status,
                    selectionSource,
                    selectedCandidate.LayoutGuid,
                    selectedCandidate.Rank,
                    detection.AlgorithmVersion,
                    detection.CatalogVersion);

                // Reusa integralmente o pipeline protegido de /upload com nomes internos fixos.
                // O XML descriptografado e o nome original do documento nunca voltam ao navegador.
                var layoutBytes = System.Text.Encoding.UTF8.GetBytes(selectedRecord.DecryptedContent);
                await using var layoutStream = new MemoryStream(layoutBytes, writable: false);
                await using var documentStream = new MemoryStream(documentBytes, writable: false);
                var internalLayoutFile = new FormFile(
                    layoutStream,
                    0,
                    layoutBytes.Length,
                    "layoutFile",
                    $"{selectedCandidate.LayoutGuid}.xml")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/xml"
                };
                var internalDocumentFile = new FormFile(
                    documentStream,
                    0,
                    documentBytes.Length,
                    "txtFile",
                    detection.DetectedType == "idoc" ? "document.idoc" : "document.mq_series")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };

                // Histórico (#366): layout do catálogo guarda só o GUID; documento guarda o nome ORIGINAL.
                var uploadResult = await UploadCoreAsync(
                    internalLayoutFile, internalDocumentFile, selectedRecord.Name,
                    new AnalysisHistoryContext(workspaceId, FiscalAnalysisSource.Auto, selectedCandidate.LayoutGuid, documentFile.FileName));
                return WrapUploadResult(uploadResult, detection, correlationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Detecção automática indisponível. CorrelationId={CorrelationId}", correlationId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    success = false,
                    correlationId,
                    message = "A detecção automática está temporariamente indisponível."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada na detecção automática. CorrelationId={CorrelationId}", correlationId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    success = false,
                    correlationId,
                    message = "Não foi possível concluir a detecção automática."
                });
            }
        }

        /// <summary>
        /// Registra a análise no histórico (issue #366) quando há <c>workspaceId</c> válido e usuário
        /// identificado. AGUARDADO com timeout de 5s (o cliente precisa do <c>analysisId</c> na resposta);
        /// qualquer falha vira <c>null</c> — o parse principal nunca é afetado.
        /// </summary>
        private async Task<Guid?> TryRegisterAnalysisAsync(
            AnalysisHistoryContext? history, IFormFile layoutFile, IFormFile txtFile, string? layoutName, string? detectedType)
        {
            if (history == null || _analysisService == null || _currentUser?.UserId is not Guid userId
                || !Guid.TryParse(history.WorkspaceIdRaw, out var workspaceId))
                return null;

            try
            {
                var files = new List<FiscalAnalysisFileInput>
                {
                    new(FiscalAnalysisFileRole.Document, history.OriginalDocumentName ?? txtFile.FileName, await ReadAllBytesAsync(txtFile))
                };

                var isCatalog = history.Source == FiscalAnalysisSource.Auto;
                if (!isCatalog)
                    files.Add(new(FiscalAnalysisFileRole.Layout, layoutFile.FileName, await ReadAllBytesAsync(layoutFile)));

                return await _analysisService.RegisterAsync(
                    new FiscalAnalysisRegistration(
                        workspaceId, userId, history.Source,
                        isCatalog ? FiscalAnalysisLayoutMode.Catalog : FiscalAnalysisLayoutMode.File,
                        history.LayoutGuid,
                        !string.IsNullOrWhiteSpace(layoutName) ? layoutName : layoutFile.FileName,
                        detectedType, files),
                    AnalysisHistoryTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao preparar registro de histórico da análise (parse não afetado)");
                return null;
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(IFormFile file)
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        private IActionResult WrapUploadResult(
            IActionResult uploadResult,
            AutomaticLayoutDetection detection,
            string correlationId)
        {
            if (uploadResult is ObjectResult objectResult)
            {
                var statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
                return StatusCode(statusCode, new AutomaticParseResponse
                {
                    Success = statusCode is >= 200 and < 300,
                    CorrelationId = correlationId,
                    Detection = detection,
                    ParseResult = objectResult.Value
                });
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new AutomaticParseResponse
            {
                Success = false,
                CorrelationId = correlationId,
                Detection = detection,
                Message = "O pipeline de parse devolveu uma resposta não reconhecida."
            });
        }

        private string EnsureCorrelationId()
        {
            var correlationId = CorrelationContext.CurrentId;
            if (string.IsNullOrWhiteSpace(correlationId))
                correlationId = HttpContext.TraceIdentifier;

            Response.Headers["X-Correlation-ID"] = correlationId;
            return correlationId;
        }

        private static string GetLearningExtension(string? detectedType) =>
            detectedType?.ToLowerInvariant() switch
            {
                "xml" => ".xml",
                "idoc" => ".idoc",
                "mqseries" => ".mq_series",
                _ => ".txt"
            };
    }
}
