using LayoutParserApi.Services.Storage;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning.Models;
using LayoutParserApi.Services.Validation;

using System.Globalization;
using System.Text.Json;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Detector de anomalia por z-score sobre os padrões históricos de
    /// MLData/DocumentPatterns (P2 do roadmap — meta 1.1: documentos incorretos do cliente).
    ///
    /// Funcionamento:
    /// 1. Carrega os <c>DocumentPattern</c> do LayoutGuid (mesmos arquivos <c>pattern_*.json</c>
    ///    gravados pelo <c>DocumentMLValidationService</c>).
    /// 2. Extrai do documento novo as MESMAS features numéricas
    ///    (via <see cref="DocumentFeatureExtractor"/> — helper compartilhado, sem duplicação).
    /// 3. Para cada feature, calcula z = |x - média| / desvio contra a distribuição histórica.
    /// 4. Normaliza cada z em [0..1] por saturação na regra 3-sigma (z >= 3 → 1.0) e combina:
    ///    <c>score = 0.5 * max + 0.5 * média</c> das features pontuadas — o máximo captura
    ///    anomalia concentrada numa feature; a média evita que uma única flutuação domine.
    ///
    /// Guarda de amostra mínima: com menos de <see cref="MinimumSamples"/> padrões históricos
    /// do layout, retorna resultado explícito de dados insuficientes (score null) — nunca
    /// inventa score. Qualquer falha interna degrada para o mesmo resultado (resiliência).
    /// </summary>
    public class DocumentAnomalyDetectorService : IDocumentAnomalyDetector
    {
        /// <summary>Mínimo de padrões históricos do layout para pontuar com honestidade.</summary>
        private const int MinimumSamples = 5;

        /// <summary>|z| a partir do qual a feature entra na lista de suspeitas.</summary>
        private const double SuspiciousZThreshold = 2.0;

        /// <summary>Saturação da normalização (regra 3-sigma): |z| >= 3 → score 1.0 na feature.</summary>
        private const double ZSaturation = 3.0;

        /// <summary>Score combinado a partir do qual o documento é marcado como anômalo.</summary>
        private const double AnomalyScoreThreshold = 0.5;

        /// <summary>Nº de amostras em que a confiança satura em 1.0.</summary>
        private const int ConfidenceSaturationSamples = 20;

        private readonly ILogger<DocumentAnomalyDetectorService> _logger;
        private readonly string _learningDataPath;

        public DocumentAnomalyDetectorService(
            ILogger<DocumentAnomalyDetectorService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            // Mesmo path (e mesmo default) usado pelo DocumentMLValidationService ao gravar os padrões
            _learningDataPath = new StoragePaths(configuration).LearningData;
        }

        /// <inheritdoc />
        public async Task<DocumentAnomalyResult> DetectAsync(string documentContent, string layoutGuid)
        {
            var result = new DocumentAnomalyResult { LayoutGuid = layoutGuid ?? "" };

            try
            {
                var patterns = await LoadPatternsForLayoutAsync(layoutGuid);
                result.HistoricalSampleCount = patterns.Count;

                // ✅ Guarda de amostra mínima: sem histórico suficiente, NÃO inventa score
                if (patterns.Count < MinimumSamples)
                {
                    result.HasSufficientData = false;
                    result.AnomalyScore = null;
                    result.IsAnomalous = false;
                    result.Confidence = 0.0;
                    result.Explanation =
                        $"Dados insuficientes para o layout '{result.LayoutGuid}': {patterns.Count} padrão(ões) " +
                        $"histórico(s) encontrado(s), mínimo exigido = {MinimumSamples}. Nenhum score foi calculado.";
                    return result;
                }

                // ✅ Tamanho de linha via LineLengthResolver (nunca reintroduzir o literal 600)
                var expectedLineLength = LineLengthResolver.Resolve(0, layoutGuid) ?? LineLengthResolver.LegacyDefaultLineLength;

                // Mesmas features do histórico (helper compartilhado com o DocumentMLValidationService)
                var currentFeatures = DocumentFeatureExtractor.Extract(documentContent ?? "", expectedLineLength);

                var featureScores = new List<double>();

                foreach (var (featureName, rawValue) in currentFeatures)
                {
                    if (!DocumentFeatureExtractor.TryToDouble(rawValue, out var value))
                        continue;

                    // Distribuição histórica da feature para este LayoutGuid
                    var historicalValues = new List<double>();
                    foreach (var pattern in patterns)
                    {
                        if (pattern.Features != null
                            && pattern.Features.TryGetValue(featureName, out var historical)
                            && DocumentFeatureExtractor.TryToDouble(historical, out var h))
                        {
                            historicalValues.Add(h);
                        }
                    }

                    // Feature precisa da mesma guarda mínima para entrar no score
                    if (historicalValues.Count < MinimumSamples)
                        continue;

                    var mean = historicalValues.Average();
                    var stdDev = Math.Sqrt(historicalValues.Sum(v => (v - mean) * (v - mean)) / historicalValues.Count);

                    double zScore;
                    if (stdDev < 1e-9)
                    {
                        // Histórico constante: qualquer desvio real é fortemente anômalo
                        var tolerance = Math.Max(1e-9, Math.Abs(mean) * 1e-6);
                        zScore = Math.Abs(value - mean) <= tolerance ? 0.0 : ZSaturation;
                    }
                    else
                    {
                        zScore = Math.Abs(value - mean) / stdDev;
                    }

                    // Normalização por saturação: [0..1], com z >= 3 (3-sigma) → 1.0
                    featureScores.Add(Math.Min(zScore / ZSaturation, 1.0));

                    if (zScore >= SuspiciousZThreshold)
                    {
                        result.SuspiciousFeatures.Add(new SuspiciousFeature
                        {
                            FeatureName = featureName,
                            Value = value,
                            ZScore = Math.Round(zScore, 2),
                            ExpectedMin = Math.Round(mean - 2 * stdDev, 2),
                            ExpectedMax = Math.Round(mean + 2 * stdDev, 2)
                        });
                    }
                }

                if (featureScores.Count == 0)
                {
                    // Padrões existem mas nenhuma feature comparável — honestidade: sem score
                    result.HasSufficientData = false;
                    result.AnomalyScore = null;
                    result.IsAnomalous = false;
                    result.Confidence = 0.0;
                    result.Explanation =
                        "Padrões históricos encontrados, porém sem features numéricas comparáveis " +
                        "com o documento atual. Nenhum score foi calculado.";
                    return result;
                }

                // Score combinado: 0.5 * máximo + 0.5 * média das features pontuadas
                var score = 0.5 * featureScores.Max() + 0.5 * featureScores.Average();
                score = Math.Clamp(score, 0.0, 1.0);

                result.HasSufficientData = true;
                result.AnomalyScore = Math.Round(score, 4);
                result.IsAnomalous = score >= AnomalyScoreThreshold;
                result.Confidence = Math.Round(Math.Min(1.0, patterns.Count / (double)ConfidenceSaturationSamples), 4);
                result.Explanation = BuildExplanation(result);

                _logger.LogInformation(
                    "Anomalia calculada para layout {LayoutGuid}: score={Score}, suspeitas={SuspiciousCount}, amostras={SampleCount}",
                    result.LayoutGuid, result.AnomalyScore, result.SuspiciousFeatures.Count, patterns.Count);

                return result;
            }
            catch (Exception ex)
            {
                // Resiliência: falha interna NUNCA propaga — degrada para "não avaliado"
                _logger.LogError(ex, "Erro ao calcular score de anomalia para layout {LayoutGuid}", layoutGuid);
                result.HasSufficientData = false;
                result.AnomalyScore = null;
                result.IsAnomalous = false;
                result.Confidence = 0.0;
                result.Explanation = "Falha interna ao calcular o score de anomalia; documento não avaliado.";
                return result;
            }
        }

        /// <summary>
        /// Carrega os padrões históricos (pattern_*.json) do LayoutGuid informado.
        /// Arquivos corrompidos são ignorados individualmente (Warning), sem derrubar a análise.
        /// </summary>
        private async Task<List<DocumentPattern>> LoadPatternsForLayoutAsync(string? layoutGuid)
        {
            var patterns = new List<DocumentPattern>();

            if (string.IsNullOrWhiteSpace(layoutGuid) || !Directory.Exists(_learningDataPath))
                return patterns;

            foreach (var file in Directory.GetFiles(_learningDataPath, "pattern_*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var pattern = JsonSerializer.Deserialize<DocumentPattern>(json);

                    if (pattern != null && string.Equals(pattern.LayoutGuid, layoutGuid, StringComparison.OrdinalIgnoreCase))
                        patterns.Add(pattern);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao carregar padrão do arquivo {File} (ignorado)", file);
                }
            }

            return patterns;
        }

        /// <summary>
        /// Monta a explicação legível (PT-BR) do resultado.
        /// </summary>
        private static string BuildExplanation(DocumentAnomalyResult result)
        {
            var score = (result.AnomalyScore ?? 0).ToString("0.00", CultureInfo.InvariantCulture);

            if (!result.IsAnomalous)
            {
                return $"Documento dentro da faixa histórica do layout (score {score}, " +
                       $"{result.HistoricalSampleCount} amostras históricas).";
            }

            var details = result.SuspiciousFeatures
                .OrderByDescending(f => f.ZScore)
                .Select(f =>
                    $"{f.FeatureName}={f.Value.ToString("0.##", CultureInfo.InvariantCulture)} fora da faixa esperada " +
                    $"[{f.ExpectedMin.ToString("0.##", CultureInfo.InvariantCulture)}; {f.ExpectedMax.ToString("0.##", CultureInfo.InvariantCulture)}] " +
                    $"(z={f.ZScore.ToString("0.0", CultureInfo.InvariantCulture)})");

            var detailText = result.SuspiciousFeatures.Count > 0
                ? string.Join("; ", details)
                : "múltiplas features com desvio moderado em relação ao histórico";

            return $"Documento provavelmente incorreto (score {score}, " +
                   $"{result.HistoricalSampleCount} amostras históricas): {detailText}.";
        }
    }
}
