using LayoutParserApi.Services.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Models.Fiscal;
using LayoutParserApi.Services.Transformation.Ai.Retraining;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Captura, em produção, cada convergência real do <see cref="RepairOrchestratorXslSynthesizerService"/>
    /// como um exemplo incremental do dataset de treino (F3 do ADR
    /// docs/architecture/adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md, issue #338).
    ///
    /// <para><b>Schema (compatível com <c>ai/XslSynth/training-data/train_lora.py</c>):</b> o
    /// script já lê JSONL com <c>obj.get("instruction"/"input"/"output")</c> — só essas 3 chaves
    /// importam pro treino hoje. Este serviço grava essas 3 chaves MAIS metadados extras
    /// (<c>groundTruthXml</c>, <c>mapperGuid</c>, <c>mapperName</c>, <c>layoutName</c>,
    /// <c>iterationsUsed</c>, <c>source</c>, <c>capturedAtUtc</c>) — chaves adicionais são
    /// ignoradas pelo loader atual (<c>obj.get(...)</c> não falha com chave desconhecida), então
    /// o arquivo continua carregável sem mudar <c>train_lora.py</c>, e os metadados ficam
    /// preservados para uso futuro (ex.: filtrar por mapper, auditar o gabarito exato usado).
    /// </para>
    ///
    /// <para><b>Diferença de conteúdo vs. o dataset batch existente</b>
    /// (<c>sysmiddle-dsl-dataset-2026-09-02.jsonl</c>): aquele dataset tem 1 exemplo por REGRA DSL
    /// individual; este captura o XSLT COMPLETO convergido por mapeador/layout. Mesma forma
    /// (instruction/input/output), granularidade diferente — decisão registrada aqui porque o ADR
    /// delega o schema exato a este agente (issue #338, "Escopo").</para>
    ///
    /// <para><b>Best-effort:</b> qualquer falha (I/O, disco cheio, path inválido) é logada como
    /// warning e NUNCA propaga — mesmo padrão de
    /// <see cref="RepairOrchestratorXslSynthesizerService"/>.TryPersistXslt.</para>
    /// </summary>
    public class TrainingDataCaptureService
    {
        private static readonly object WriteLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // Mesma convenção do resto do projeto (dotnet-standards.md) — preserva XML/DSL intacto
            // no payload sem escaping agressivo de '<', '>' etc.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly ILogger<TrainingDataCaptureService> _logger;
        private readonly string _trainingDataPath;

        // F4.2 (issue #351): a cada exemplo efetivamente gravado no JSONL, o contador de retraining
        // avança. Opcional (nullable) pra não quebrar quem constrói o serviço só com logger+config
        // (testes de F3) — sem coordenador, a captura funciona igual, só não alimenta o gatilho.
        private readonly IRetrainingCoordinator? _retrainingCoordinator;

        public TrainingDataCaptureService(
            ILogger<TrainingDataCaptureService> logger,
            IConfiguration configuration,
            IRetrainingCoordinator? retrainingCoordinator = null)
        {
            _logger = logger;
            _retrainingCoordinator = retrainingCoordinator;
            // Sem convenção de prod existente pra esta pasta (diferente de XSD/XSL — ver
            // XsdValidation:BasePath / TransformationPipeline:XslPath); fica configurável e cai,
            // por padrão, na mesma árvore do dataset batch já versionado no repo.
            _trainingDataPath = new StoragePaths(configuration).AiTrainingData;
        }

        /// <summary>
        /// Grava (best-effort) o exemplo de convergência no dataset incremental. Nunca lança —
        /// falha vira warning de log. Chame só quando <c>report.Converged == true</c>.
        /// </summary>
        public void TryCapture(
            string mapperGuid,
            string? mapperName,
            string? layoutName,
            string inputXml,
            string groundTruthXml,
            string finalXslt,
            int iterationsUsed)
        {
            var safeMapperGuid = Services.Logging.LogMessageSanitizer.Sanitize(mapperGuid);
            try
            {
                var line = BuildJsonlLine(
                    mapperGuid, mapperName, layoutName, inputXml, groundTruthXml, finalXslt,
                    iterationsUsed, DateTime.UtcNow);

                AppendJsonlLine(line, $"mapperGuid={safeMapperGuid}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Falha ao capturar exemplo de convergência pro dataset de treino incremental — best-effort, não afeta a síntese (mapperGuid={MapperGuid})",
                    safeMapperGuid);
            }
        }

        /// <summary>
        /// Curadoria humana aceita (issue #346): grava (best-effort) um exemplo incremental a partir
        /// do contexto original do documento + o valor esperado revisado por um humano. Mesma forma
        /// (<c>instruction</c>/<c>input</c>/<c>output</c>) e mesmo arquivo diário do
        /// <see cref="TryCapture"/>, só com <c>source = "human-correction-reviewed"</c>. Nunca lança.
        /// Chame SÓ depois que a transição para <c>reviewed_accepted</c> foi confirmada.
        /// </summary>
        public void TryCaptureHumanCorrection(FieldCorrectionContext context, FieldCorrectionReportSummary report)
        {
            try
            {
                var line = BuildHumanCorrectionJsonlLine(context, report, DateTime.UtcNow);
                AppendJsonlLine(line, $"human-correction reportId={report.ReportId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Falha ao capturar correção humana revisada pro dataset de treino incremental — best-effort (reportId={ReportId})",
                    report.ReportId);
            }
        }

        /// <summary>
        /// Append + rotação diária + log + contador de retraining — compartilhado por
        /// <see cref="TryCapture"/> e <see cref="TryCaptureHumanCorrection"/> (um único
        /// <c>File.AppendAllText</c> e uma única resolução de path/nome de arquivo).
        /// </summary>
        private void AppendJsonlLine(string line, string logContext)
        {
            Directory.CreateDirectory(_trainingDataPath);
            // Rotação diária — mesmo racional do dataset batch (nome com data), evita um
            // único arquivo monolítico crescendo pra sempre e facilita auditar por dia.
            var fileName = $"runtime-capture-{DateTime.UtcNow:yyyy-MM-dd}.jsonl";
            var path = Path.Combine(_trainingDataPath, fileName);

            // Lock só de processo — várias capturas ~simultâneas no mesmo worker não podem
            // intercalar linhas parciais no arquivo (fire-and-forget, sem fila a montante).
            lock (WriteLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }

            _logger.LogInformation(
                "Exemplo capturado pro dataset de treino incremental em {Path} ({Context})",
                Services.Logging.LogMessageSanitizer.Sanitize(path), logContext);

            // F4.2 (issue #351): só conta depois que a linha foi de fato escrita.
            _retrainingCoordinator?.RegisterCapturedExample();
        }

        /// <summary>Monta a linha JSONL de uma correção humana revisada. <c>internal</c> pra teste sem I/O.</summary>
        internal static string BuildHumanCorrectionJsonlLine(
            FieldCorrectionContext context,
            FieldCorrectionReportSummary report,
            DateTime capturedAtUtc)
        {
            var mapperLabel = context.MapperName ?? context.MapperGuid ?? "mapeador não identificado";
            var record = new TrainingExampleRecord
            {
                Instruction =
                    $"No documento do layout '{context.LayoutName}' (mapeador '{mapperLabel}'), o campo " +
                    $"'{report.FieldPath}' foi gerado como '{report.ObservedValue}', mas o valor correto — " +
                    $"revisado e aprovado por um humano — é '{report.ExpectedValue}'. Ajuste a transformação " +
                    "para produzir o valor correto nesse campo a partir do XML de entrada.",
                Input = context.InputXml,
                Output = report.ExpectedValue ?? string.Empty,
                GroundTruthXml = context.GroundTruthXml ?? string.Empty,
                MapperGuid = context.MapperGuid ?? string.Empty,
                MapperName = context.MapperName,
                LayoutName = context.LayoutName,
                IterationsUsed = 0,
                Source = "human-correction-reviewed",
                CapturedAtUtc = capturedAtUtc.ToString("o"),
            };

            return JsonSerializer.Serialize(record, JsonOptions);
        }

        /// <summary>Monta a linha JSONL. <c>internal</c> pra ser testável isoladamente sem I/O.</summary>
        internal static string BuildJsonlLine(
            string mapperGuid,
            string? mapperName,
            string? layoutName,
            string inputXml,
            string groundTruthXml,
            string finalXslt,
            int iterationsUsed,
            DateTime capturedAtUtc)
        {
            var record = new TrainingExampleRecord
            {
                Instruction =
                    $"Gere o XSLT completo Sysmiddle para o mapeador '{mapperName ?? mapperGuid}' " +
                    $"(layout '{layoutName}') que transforma o XML de entrada (parse posicional real) " +
                    "no XML de saída validado contra o gabarito Sysmiddle (diff canônico + XSD).",
                Input = inputXml,
                Output = finalXslt,
                GroundTruthXml = groundTruthXml,
                MapperGuid = mapperGuid,
                MapperName = mapperName,
                LayoutName = layoutName,
                IterationsUsed = iterationsUsed,
                Source = "repair-orchestrator-runtime",
                CapturedAtUtc = capturedAtUtc.ToString("o"),
            };

            return JsonSerializer.Serialize(record, JsonOptions);
        }

        /// <summary>
        /// Nomes de propriedade em camelCase minúsculo (<c>instruction</c>/<c>input</c>/<c>output</c>)
        /// pra bater exatamente com as chaves que <c>train_lora.py</c> já lê via
        /// <c>obj.get("instruction"/"input"/"output")</c>.
        /// </summary>
        private sealed class TrainingExampleRecord
        {
            [JsonPropertyName("instruction")]
            public string Instruction { get; init; } = string.Empty;

            [JsonPropertyName("input")]
            public string Input { get; init; } = string.Empty;

            [JsonPropertyName("output")]
            public string Output { get; init; } = string.Empty;

            [JsonPropertyName("groundTruthXml")]
            public string GroundTruthXml { get; init; } = string.Empty;

            [JsonPropertyName("mapperGuid")]
            public string MapperGuid { get; init; } = string.Empty;

            [JsonPropertyName("mapperName")]
            public string? MapperName { get; init; }

            [JsonPropertyName("layoutName")]
            public string? LayoutName { get; init; }

            [JsonPropertyName("iterationsUsed")]
            public int IterationsUsed { get; init; }

            [JsonPropertyName("source")]
            public string Source { get; init; } = string.Empty;

            [JsonPropertyName("capturedAtUtc")]
            public string CapturedAtUtc { get; init; } = string.Empty;
        }
    }
}
