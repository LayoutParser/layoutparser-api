using LayoutParserApi.Services.Storage;
using System.Diagnostics;
using System.Xml.Linq;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai.Retraining;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Options;

using Serilog.Context;

using XslSynth.Core;
using XslSynth.Synthesis;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação real de <see cref="IXslSynthesizerService"/>: instancia o
    /// <see cref="RepairOrchestrator"/> de <c>ai/XslSynth.Core</c> (in-process — ver design doc
    /// no header de <see cref="IXslSynthesizerService"/>) e delega o loop
    /// gerar → validar (diff canônico + XSD) → corrigir, usando o Ollama local.
    ///
    /// <para><b>Limitação conhecida (documentada, não corrigida nesta iteração):</b> a resolução
    /// do caminho do XSD reaproveita a MESMA convenção de <c>XsdValidationService.FindXsdFile</c>
    /// (pasta <c>XsdValidation:BasePath/{versão}</c>, maior <c>*.xsd</c>), mas duplicada aqui —
    /// aquele método é privado e não há um método público equivalente. Se a convenção mudar lá,
    /// precisa mudar aqui também.</para>
    /// </summary>
    public class RepairOrchestratorXslSynthesizerService : IXslSynthesizerService
    {
        private readonly ILogger<RepairOrchestratorXslSynthesizerService> _logger;
        private readonly ICachedMapperService _mapperService;
        private readonly XmlDocumentTypeDetector _documentTypeDetector;
        private readonly OllamaOptions _ollamaOptions;
        private readonly TrainingDataCaptureService _trainingDataCapture;
        // F4.3 (issue #351): exclusão mútua com o treino LoRA na mesma VM. Opcional (nullable) —
        // sem o serviço registrado, a checagem é ignorada e o comportamento é o de hoje.
        private readonly IRetrainingLock? _retrainingLock;
        private readonly string _xsdBasePath;
        private readonly string _xslBasePath;
        private readonly RepairOrchestrator _orchestrator = new();
        private readonly MapperExtractor _sampleExtractor = new();
        private readonly RealMapperParser _realParser = new();

        public RepairOrchestratorXslSynthesizerService(
            ILogger<RepairOrchestratorXslSynthesizerService> logger,
            ICachedMapperService mapperService,
            XmlDocumentTypeDetector documentTypeDetector,
            IOptions<OllamaOptions> ollamaOptions,
            IConfiguration configuration,
            TrainingDataCaptureService trainingDataCapture,
            IRetrainingLock? retrainingLock = null,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _mapperService = mapperService;
            _documentTypeDetector = documentTypeDetector;
            _ollamaOptions = ollamaOptions.Value;
            _trainingDataCapture = trainingDataCapture;
            _retrainingLock = retrainingLock;
            _xsdBasePath = (storagePaths ?? new StoragePaths(configuration)).Xsd;
            // Mesma convenção de TransformationPipelineService/AutoTransformationGeneratorService —
            // {mapperName}_{layoutName}.xsl (issue #55) — pra persistir o XSLT sintetizado no lugar
            // que o pathway tcl-xsl já sabe ler.
            _xslBasePath = (storagePaths ?? new StoragePaths(configuration)).Xsl;
        }

        public async Task<XslSynthesisResult> SynthesizeAsync(
            string mapperGuid,
            string inputXml,
            string groundTruthXml,
            int maxIterations,
            string? layoutName,
            CancellationToken cancellationToken,
            IReadOnlyList<ParsedField>? parsedFields = null)
        {
            // ✅ CodeQL cs/log-forging: mapperGuid/layoutName vêm do request (ticket assíncrono de
            // IA) e podem conter \r/\n — saneia uma vez, reusa só nos logs deste serviço.
            var safeMapperGuid = Services.Logging.LogMessageSanitizer.Sanitize(mapperGuid);
            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(layoutName);
            try
            {
                // F4.3 (issue #351, ADR §6.2): se o treino LoRA está rodando na VM (retraining.lock
                // presente), a síntese via Ollama fica suspensa — treino + inferência juntos na
                // mesma CPU degradam a convergência pelas ~40h inteiras. Degrada graciosamente
                // (não lança): o ticket de IA volta como não-convergido e pode ser retentado depois.
                if (_retrainingLock?.IsHeld() == true)
                {
                    _logger.LogWarning(
                        "Síntese de XSLT suspensa: retraining LoRA em andamento na VM ({LockPath}) — " +
                        "Ollama de inferência não é acionado enquanto o treino roda (mapperGuid={MapperGuid})",
                        Services.Logging.LogMessageSanitizer.Sanitize(_retrainingLock.LockFilePath), safeMapperGuid);
                    return Failed(
                        "Retraining do modelo em andamento na VM — síntese via Ollama suspensa até o treino " +
                        "terminar (retraining.lock presente). Retente mais tarde.");
                }

                var mapper = await ResolveMapperAsync(mapperGuid, cancellationToken);
                if (mapper is null)
                {
                    return Failed($"Mapeador '{mapperGuid}' não encontrado (cache/banco) ou sem conteúdo descriptografado.");
                }

                var mapperVo = ParseMapperVo(mapper.DecryptedContent, mapperGuid);
                if (mapperVo is null)
                {
                    return Failed($"MapeadorVO do mapper '{mapperGuid}' não é XML bem-formado.");
                }

                XDocument input;
                if (parsedFields is { Count: > 0 })
                {
                    // Caminho real (docs/architecture/decisao-pendente-input-xml-repairorchestrator-
                    // 2026-08-29.md): constrói o XDocument input a partir do parse posicional REAL
                    // (ParsedField), no mesmo dialeto ROOT/Linha/Campo que RootTreeBuilder/TclRootBuilder
                    // já produzem — não tenta mais parsear TXT cru como XML (sempre falhava).
                    var built = ParsedFieldRootTreeBuilder.Build(parsedFields);
                    if (built.Root is null)
                    {
                        _logger.LogWarning(
                            "ParsedFieldRootTreeBuilder recusou montar o ROOT (gate de qualidade) — {Motivo} (mapperGuid={MapperGuid})",
                            built.Motivo, safeMapperGuid);
                        return Failed($"Não foi possível montar o XML de entrada a partir do parse posicional: {built.Motivo}");
                    }

                    _logger.LogInformation(
                        "ROOT construído via ParsedFieldRootTreeBuilder: {LinhasDistintas} tipo(s) de linha, {ComValor}/{Total} campo(s) com valor (mapperGuid={MapperGuid})",
                        built.LinhasDistintas, built.CamposComValor, built.CamposFisicos, safeMapperGuid);
                    input = built.Root;
                }
                else
                {
                    try
                    {
                        input = XDocument.Parse(inputXml);
                    }
                    catch (Exception ex)
                    {
                        // Sem ParsedFields e a entrada também não é XML bem-formado (TXT posicional
                        // cru) — nada com que construir o input do RepairOrchestrator.
                        _logger.LogWarning(ex, "Entrada não é XML bem-formado e nenhum ParsedField foi informado — RepairOrchestrator não tem como montar o documento de entrada (mapperGuid={MapperGuid})", safeMapperGuid);
                        return Failed("Entrada não é XML válido e nenhum ParsedField foi informado — RepairOrchestrator exige o documento já convertido (low-code) ou o resultado do parse posicional.");
                    }
                }

                var xsdPath = ResolveXsdPath(groundTruthXml);
                if (xsdPath is null)
                {
                    _logger.LogWarning("XSD não resolvido para o gabarito (mapperGuid={MapperGuid}) — validação XSD do loop sempre reportará inválido", safeMapperGuid);
                }

                var synthesizer = CreateOllamaSynthesizer();

                // F1 (ADR issue #151, seção 3.2): antes de sempre partir do transpilador
                // determinístico, tenta reaproveitar um XSLT já convergido numa execução
                // anterior para o mesmo mapper/layout — mesma convenção de nome que
                // TryPersistXslt já usa para gravar (issue #55).
                var seedXslt = TryLoadSeedXslt(mapper.Name, layoutName);

                var stopwatch = Stopwatch.StartNew();
                var report = await _orchestrator.RunAsync(
                    mapperVo,
                    input,
                    groundTruthXml,
                    xsdPath ?? string.Empty,
                    synthesizer,
                    log => _logger.LogInformation("[RepairOrchestrator] {Message}", log),
                    maxIterations,
                    cancellationToken,
                    seedXslt);
                stopwatch.Stop();

                // F4.1 (issue #351, ADR §8/§10): telemetria de duração/iterações do RepairOrchestrator
                // no caminho de PRODUÇÃO — até agora só o CLI offline (MetricsBatchRunner) media isso.
                // Mesmo canal de F3 (Source=AiMetrics), pré-requisito pra calibrar o N do gatilho de F4.2.
                LogRuntimeMetrics(safeMapperGuid, safeLayoutName, stopwatch.Elapsed.TotalSeconds, report.Iterations,
                    report.Converged, report.FinalXsd.IsValid);

                if (report.Converged && !string.IsNullOrWhiteSpace(layoutName))
                    TryPersistXslt(mapper.Name, layoutName!, report.FinalXslt);

                // ✅ Issue #338 (F3 do ADR de convergência TCL/XSLT): toda convergência real vira
                // exemplo do dataset de treino incremental — best-effort, nunca falha a síntese.
                if (report.Converged)
                {
                    _trainingDataCapture.TryCapture(
                        mapperGuid,
                        mapper.Name,
                        layoutName,
                        input.ToString(),
                        groundTruthXml,
                        report.FinalXslt,
                        report.Iterations);
                }

                return new XslSynthesisResult
                {
                    Success = true,
                    Converged = report.Converged,
                    GeneratedXslt = report.FinalXslt,
                    FinalOutputXml = report.FinalOutput,
                    IterationsUsed = report.Iterations,
                    XsdValid = report.FinalXsd.IsValid,
                    ValidationErrors = report.FinalDiffs.Select(d => d.ToString())
                        .Concat(report.FinalXsd.Errors)
                        .ToList()
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Resiliência (dotnet-standards.md): Ollama/RepairOrchestrator podem falhar —
                // nunca propaga exceção não tratada pro chamador (fire-and-forget).
                _logger.LogError(ex, "Falha não tratada na síntese de XSLT via RepairOrchestrator (mapperGuid={MapperGuid})", safeMapperGuid);
                return Failed($"Falha interna na síntese de XSLT: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve o mapper real via <see cref="ICachedMapperService"/> — não há método de busca
        /// direta por MapperGuid, filtra sobre <c>GetAllMappersAsync</c> (mesmo padrão já usado
        /// noutros pontos do domínio).
        /// </summary>
        private async Task<LayoutParserApi.Models.Entities.Mapper?> ResolveMapperAsync(string mapperGuid, CancellationToken cancellationToken)
        {
            var allMappers = await _mapperService.GetAllMappersAsync();
            var mapper = allMappers.FirstOrDefault(m =>
                string.Equals(m.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));

            return mapper is null || string.IsNullOrWhiteSpace(mapper.DecryptedContent) ? null : mapper;
        }

        /// <summary>Parseia o conteúdo descriptografado com <see cref="RealMapperParser"/> (formato
        /// real Sysmiddle); cai para <see cref="MapperExtractor"/> (formato MVP/sample) se o
        /// parser real não reconhecer a estrutura.</summary>
        private XslSynth.Model.MapperVo? ParseMapperVo(string decryptedContent, string mapperGuid)
        {
            try
            {
                var doc = XDocument.Parse(decryptedContent);
                try
                {
                    return _realParser.Parse(doc);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "RealMapperParser não reconheceu o MapeadorVO — tentando MapperExtractor (formato sample)");
                    return _sampleExtractor.Extract(doc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MapeadorVO do mapper {MapperGuid} não é XML bem-formado", Services.Logging.LogMessageSanitizer.Sanitize(mapperGuid));
                return null;
            }
        }

        /// <summary>
        /// F1 (ADR issue #151, seção 3.2): tenta ler <c>{XslPath}/{mapperName}_{layoutName}.xsl</c>
        /// de uma convergência anterior (mesma convenção de <see cref="TryPersistXslt"/>) para
        /// reaproveitar como ponto de partida do <see cref="RepairOrchestrator"/>, em vez de
        /// recomeçar sempre do transpilador determinístico.
        /// <para>Best-effort (5.4 do ADR): arquivo ausente ou XML inválido apenas perde a
        /// otimização — nunca impede a síntese, que cai para o comportamento atual (baseline).</para>
        /// </summary>
        private XDocument? TryLoadSeedXslt(string? mapperName, string? layoutName)
        {
            if (string.IsNullOrWhiteSpace(mapperName) || string.IsNullOrWhiteSpace(layoutName))
                return null;

            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(layoutName);
            try
            {
                var path = Path.Combine(_xslBasePath, $"{mapperName}_{layoutName}.xsl");
                if (!File.Exists(path))
                    return null;

                var seed = XDocument.Load(path);
                _logger.LogInformation(
                    "Seed de XSLT convergido reaproveitado de {Path} (layout={LayoutName})",
                    Services.Logging.LogMessageSanitizer.Sanitize(path), safeLayoutName);
                return seed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler seed de XSLT convergido — síntese seguirá sem reaproveitamento (layout={LayoutName})", safeLayoutName);
                return null;
            }
        }

        /// <summary>Best-effort: falha na gravação nunca derruba a síntese (o candidato já foi
        /// devolvido convergido ao chamador via <see cref="XslSynthesisResult"/>).</summary>
        private void TryPersistXslt(string? mapperName, string layoutName, string xslt)
        {
            var safeLayoutName = Services.Logging.LogMessageSanitizer.Sanitize(layoutName);
            try
            {
                if (string.IsNullOrWhiteSpace(mapperName))
                {
                    _logger.LogWarning("Não foi possível persistir o XSLT sintetizado — mapper sem Name (layout={LayoutName})", safeLayoutName);
                    return;
                }

                Directory.CreateDirectory(_xslBasePath);
                var path = Path.Combine(_xslBasePath, $"{mapperName}_{layoutName}.xsl");
                File.WriteAllText(path, xslt);
                _logger.LogInformation("XSLT sintetizado via RepairOrchestrator persistido em {Path}", Services.Logging.LogMessageSanitizer.Sanitize(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao persistir o XSLT sintetizado (layout={LayoutName})", safeLayoutName);
            }
        }

        /// <summary>Mesma convenção de <c>XsdValidationService.FindXsdFile</c> — ver limitação no
        /// header da classe.</summary>
        private string? ResolveXsdPath(string groundTruthXml)
        {
            try
            {
                var docType = _documentTypeDetector.DetectDocumentType(groundTruthXml);
                if (string.IsNullOrEmpty(docType?.XsdVersion))
                    return null;

                var versionPath = Path.Combine(_xsdBasePath, docType.XsdVersion);
                if (!Directory.Exists(versionPath))
                    return null;

                return Directory.GetFiles(versionPath, "*.xsd", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao resolver caminho do XSD para o gabarito");
                return null;
            }
        }

        /// <summary>
        /// <see cref="OllamaXslSynthesizer"/> lê configuração via variável de ambiente
        /// (<c>OLLAMA_URL</c>/<c>OLLAMA_MODEL</c>), não via <c>IOptions</c> — é o mesmo componente
        /// usado pelo CLI standalone, sem DI. Propagamos a config já validada da API (seção
        /// <c>Ollama</c> de <c>appsettings.json</c>) só se a env var ainda não tiver sido setada
        /// externamente, pra não sobrescrever uma configuração de processo intencional.
        /// </summary>
        private OllamaXslSynthesizer CreateOllamaSynthesizer()
        {
            if (Environment.GetEnvironmentVariable("OLLAMA_URL") is null && !string.IsNullOrWhiteSpace(_ollamaOptions.Url))
                Environment.SetEnvironmentVariable("OLLAMA_URL", _ollamaOptions.Url);

            if (Environment.GetEnvironmentVariable("OLLAMA_MODEL") is null && !string.IsNullOrWhiteSpace(_ollamaOptions.Model))
                Environment.SetEnvironmentVariable("OLLAMA_MODEL", _ollamaOptions.Model);

            return new OllamaXslSynthesizer(message => _logger.LogDebug("[Ollama] {Message}", message));
        }

        /// <summary>
        /// F4.1 (issue #351): grava a linha estruturada de duração/iterações do RepairOrchestrator
        /// em runtime, com <c>Source=AiMetrics</c> (mesmo canal que o job <c>--mode=metrics-batch</c>
        /// e a ingestão externa já usam). Pares Chave=Valor, cultura invariante no double — o leitor
        /// de métricas tokeniza por espaço e ignora chave desconhecida, então é aditivo.
        /// </summary>
        private void LogRuntimeMetrics(
            string safeMapperGuid, string? safeLayoutName, double durationSeconds, int iterations,
            bool converged, bool xsdValid)
        {
            using (LogContext.PushProperty("Source", "AiMetrics"))
            {
                _logger.LogInformation(
                    "RepairOrchestrator runtime concluido. MapperGuid={MapperGuid} Layout={Layout} " +
                    "DuracaoSegundos={DurationSeconds} Iteracoes={Iterations} Convergiu={Converged} " +
                    "XsdValido={XsdValid} Origem={Origem}",
                    safeMapperGuid,
                    safeLayoutName ?? string.Empty,
                    durationSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    iterations,
                    converged,
                    xsdValid,
                    "repair-orchestrator-runtime");
            }
        }

        private static XslSynthesisResult Failed(string error) => new()
        {
            Success = false,
            Converged = false,
            Error = error
        };
    }
}
