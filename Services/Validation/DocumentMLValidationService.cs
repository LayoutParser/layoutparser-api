using LayoutParserApi.Services.Storage;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.ML;
using LayoutParserApi.Models.Validation;
using LayoutParserApi.Services.Interfaces;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Serviço ML para análise de padrões de erros em documentos
    /// Aprende com documentos históricos e sugere correções
    /// </summary>
    public class DocumentMLValidationService
    {
        private readonly ITechLogger _techLogger;
        private readonly ILogger<DocumentMLValidationService> _logger;
        private readonly string _learningDataPath;
        private readonly string _trainingSamplesPath;
        private readonly IDocumentAnomalyDetector? _anomalyDetector;
        private Dictionary<string, DocumentPattern> _learnedPatterns = new();

        public DocumentMLValidationService(
            ITechLogger techLogger,
            ILogger<DocumentMLValidationService> logger,
            IConfiguration configuration,
            IDocumentAnomalyDetector? anomalyDetector = null)
        {
            _techLogger = techLogger;
            _logger = logger;
            // ✅ Dependência opcional: sem o detector registrado, o serviço segue funcionando normalmente
            _anomalyDetector = anomalyDetector;
            _learningDataPath = new StoragePaths(configuration).LearningData;
            _trainingSamplesPath = new StoragePaths(configuration).TrainingSamples;

            // ✅ Resiliência: sem permissão de escrita (ex.: serviço Linux sem acesso ao diretório da app),
            // o serviço degrada (sem persistir aprendizado) em vez de derrubar o request principal.
            try
            {
                Directory.CreateDirectory(_learningDataPath);
                Directory.CreateDirectory(_trainingSamplesPath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex, "Não foi possível criar diretórios de ML ({LearningPath}, {SamplesPath}); aprendizado em disco desativado", _learningDataPath, _trainingSamplesPath);
            }

            // Carregar padrões aprendidos
            LoadLearnedPatterns();
        }

        /// <summary>
        /// Analisa um erro de linha e gera sugestões baseadas em padrões aprendidos
        /// </summary>
        public async Task<List<ErrorSuggestion>> AnalyzeErrorAndSuggestAsync(
            DocumentLineError lineError,
            string documentContent,
            string layoutGuid)
        {
            var suggestions = new List<ErrorSuggestion>();

            try
            {
                // Extrair contexto da linha
                var context = ExtractLineContext(lineError, documentContent);

                // Buscar padrões similares
                var similarPatterns = FindSimilarPatterns(context, layoutGuid);

                // Gerar sugestões baseadas nos padrões
                foreach (var pattern in similarPatterns.OrderByDescending(p => p.Confidence))
                {
                    var suggestion = GenerateSuggestionFromPattern(pattern, context, lineError);
                    if (suggestion != null)
                        suggestions.Add(suggestion);
                }

                // Se não houver padrões, gerar sugestões baseadas em regras
                if (!suggestions.Any())
                    suggestions.AddRange(GenerateRuleBasedSuggestions(lineError, context));

                // ✅ Enriquecimento opcional com o score de anomalia (P2) — resiliente:
                // qualquer falha aqui é engolida e NUNCA quebra a resposta principal
                try
                {
                    if (_anomalyDetector != null)
                    {
                        var anomaly = await _anomalyDetector.DetectAsync(documentContent, layoutGuid);
                        if (anomaly.AnomalyScore.HasValue && anomaly.IsAnomalous)
                        {
                            suggestions.Add(new ErrorSuggestion
                            {
                                FieldName = "Documento completo",
                                CurrentLength = documentContent?.Length ?? 0,
                                SuggestedLength = 0,
                                Action = "review",
                                Reason = $"[Anomalia] {anomaly.Explanation}",
                                Confidence = anomaly.AnomalyScore.Value * anomaly.Confidence
                            });
                        }
                    }
                }
                catch (Exception anomalyEx)
                {
                    _logger.LogWarning(anomalyEx, "Falha ao enriquecer sugestões com score de anomalia (ignorada)");
                }

                // Ordenar por confiança
                suggestions = suggestions.OrderByDescending(s => s.Confidence).ToList();

                _logger.LogInformation("Geradas {Count} sugestões para erro na linha {LineIndex}",
                    suggestions.Count, lineError.LineIndex);

                return suggestions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar erro e gerar sugestões");
                return suggestions;
            }
        }

        /// <summary>
        /// Registra um documento processado para aprendizado
        /// </summary>
        public async Task LearnFromDocumentAsync(
            string documentContent,
            string layoutGuid,
            List<DocumentLineError>? errors = null,
            DocumentTrainingSample? metadata = null)
        {
            try
            {
                await SaveTrainingSampleAsync(documentContent, layoutGuid, errors, metadata);

                // ✅ Resolver tamanho de linha pelo GUID (allowlist); sem dado melhor, cai no default legado
                var expectedLineLength = LineLengthResolver.Resolve(0, layoutGuid) ?? LineLengthResolver.LegacyDefaultLineLength;

                var pattern = new DocumentPattern
                {
                    LayoutGuid = layoutGuid,
                    DocumentLength = documentContent.Length,
                    LineCount = documentContent.Length / expectedLineLength,
                    ErrorsFound = errors?.Count ?? 0,
                    CreatedAt = DateTime.UtcNow
                };

                // Extrair features do documento
                pattern.Features = ExtractDocumentFeatures(documentContent, expectedLineLength);

                // Salvar padrão
                await SavePatternAsync(pattern);

                _logger.LogInformation("Padrão aprendido e salvo para layout {LayoutGuid}", layoutGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aprender de documento");
            }
        }

        private async Task SaveTrainingSampleAsync(
            string documentContent,
            string layoutGuid,
            List<DocumentLineError>? errors,
            DocumentTrainingSample? metadata)
        {
            try
            {
                var isValid = errors == null || errors.Count == 0;
                var firstErrorLineIndex = !isValid ? errors!.Min(e => e.LineIndex) : (int?)null;

                var sha256 = ComputeSha256(documentContent);
                var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
                var folder = Path.Combine(_trainingSamplesPath, dateFolder);
                Directory.CreateDirectory(folder);

                var dedupeMarker = Path.Combine(folder, $"sha256_{sha256}.marker");

                // IMPORTANT: concorrência (multi-thread/process). Criar marker de forma atômica.
                // Se já existir (ou estiver sendo criado), consideramos duplicata.
                // ✅ SCS0018 (issue #88): dedupeMarker é montado só com sha256 (hash hexadecimal
                // calculado aqui mesmo, não digitado pelo cliente) sobre "folder", que por sua vez
                // vem de _trainingSamplesPath + dateFolder (DateTime.UtcNow) — nenhum segmento do
                // caminho é controlável pelo request.
                try
                {
#pragma warning disable SCS0018
                    using var _ = new FileStream(dedupeMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
#pragma warning restore SCS0018
                }
                catch (IOException)
                {
                    _logger.LogInformation("Amostra já registrada ou em registro (sha256={Sha256}) - ignorando duplicata", sha256);
                    return;
                }

                var sample = metadata ?? new DocumentTrainingSample();
                sample.LayoutGuid = layoutGuid ?? "";
                sample.IsValid = isValid;
                sample.FirstErrorLineIndex = firstErrorLineIndex;
                sample.Sha256 = sha256;
                sample.DocumentLength = documentContent?.Length ?? 0;
                sample.Errors = errors;

                // Salvar documento completo em .txt
                var docPath = Path.Combine(folder, $"doc_{sample.SampleId}.txt");
                await File.WriteAllTextAsync(docPath, documentContent ?? "", Encoding.UTF8);

                // Salvar metadados em .json
                sample.SavedDocumentPath = docPath;
                var metaPath = Path.Combine(folder, $"sample_{sample.SampleId}.json");
                var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(metaPath, json, Encoding.UTF8);
                sample.SavedMetadataPath = metaPath;

                // Escrever o SampleId dentro do marker (best-effort; mantendo o arquivo já criado acima)
                // ✅ SCS0018 (issue #88): mesma justificativa acima — dedupeMarker não carrega
                // segmento controlável pelo request.
#pragma warning disable SCS0018
                try { await File.WriteAllTextAsync(dedupeMarker, sample.SampleId, Encoding.UTF8); } catch { }
#pragma warning restore SCS0018

                _logger.LogInformation(
                    "Amostra de treino salva: valid={IsValid}, layout={LayoutGuid}, sha256={Sha256}, meta={MetaPath}",
                    sample.IsValid, sample.LayoutGuid, sha256, metaPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar amostra de treino");
            }
        }

        private static string ComputeSha256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input ?? "");
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Registra feedback sobre sugestão (aprendizado supervisionado)
        /// </summary>
        public async Task RegisterFeedbackAsync(
            string suggestionId,
            bool wasAccepted,
            string? actualCorrection = null)
        {
            try
            {
                var feedback = new SuggestionFeedback
                {
                    SuggestionId = suggestionId,
                    WasAccepted = wasAccepted,
                    ActualCorrection = actualCorrection,
                    Timestamp = DateTime.UtcNow
                };

                await SaveFeedbackAsync(feedback);

                // Se foi aceito, aumentar confiança do padrão
                // Se foi rejeitado, diminuir confiança
                // Se houve correção manual diferente, aprender novo padrão

                _logger.LogInformation("Feedback registrado para sugestão {SuggestionId}: {Accepted}",
                    suggestionId, wasAccepted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar feedback");
            }
        }

        /// <summary>
        /// Extrai contexto da linha com erro
        /// </summary>
        private LineContext ExtractLineContext(DocumentLineError lineError, string documentContent)
        {
            var context = new LineContext
            {
                Sequence = lineError.Sequence,
                ActualLength = lineError.ActualLength,
                ExpectedLength = lineError.ExpectedLength,
                StartPosition = lineError.StartPosition,
                ExcessChars = lineError.ActualLength - lineError.ExpectedLength
            };

            // Extrair conteúdo ao redor da linha (antes e depois)
            int contextStart = Math.Max(0, lineError.StartPosition - 100);
            int contextEnd = Math.Min(documentContent.Length, lineError.EndPosition + 100);
            context.SurroundingContent = documentContent.Substring(contextStart, contextEnd - contextStart);

            // Tentar identificar tipo de linha (HEADER, LINHA000, etc.) pela posição
            if (lineError.StartPosition == 0)
                context.LineType = "HEADER";
            else
            {
                // Estimar tipo de linha baseado na posição (usando o tamanho esperado do próprio erro,
                // com fallback no default legado quando não informado)
                int lineLength = lineError.ExpectedLength > 0 ? lineError.ExpectedLength : LineLengthResolver.LegacyDefaultLineLength;
                int estimatedLineNumber = lineError.StartPosition / lineLength;
                context.LineType = $"LINHA{estimatedLineNumber:D3}";
            }

            return context;
        }

        /// <summary>
        /// Extrai features de um documento para aprendizado
        /// </summary>
        private Dictionary<string, object> ExtractDocumentFeatures(string documentContent, int expectedLineLength = LineLengthResolver.LegacyDefaultLineLength)
        {
            // ✅ Lógica centralizada no DocumentFeatureExtractor — helper compartilhado com o
            // DocumentAnomalyDetectorService para garantir features idênticas às do histórico
            return DocumentFeatureExtractor.Extract(documentContent, expectedLineLength);
        }

        /// <summary>
        /// Encontra padrões similares ao contexto atual
        /// </summary>
        private List<DocumentPattern> FindSimilarPatterns(LineContext context, string layoutGuid)
        {
            var similarPatterns = new List<DocumentPattern>();

            foreach (var pattern in _learnedPatterns.Values)
            {
                // Filtrar por layout se fornecido
                if (!string.IsNullOrEmpty(layoutGuid) && pattern.LayoutGuid != layoutGuid)
                    continue;

                // Calcular similaridade simples (pode ser melhorado com ML.NET)
                double similarity = CalculateSimilarity(context, pattern);

                if (similarity > 0.5) // Threshold de similaridade
                {
                    pattern.Confidence = similarity;
                    similarPatterns.Add(pattern);
                }
            }

            return similarPatterns;
        }

        /// <summary>
        /// Calcula similaridade entre contexto e padrão (simplificado)
        /// </summary>
        private double CalculateSimilarity(LineContext context, DocumentPattern pattern)
        {
            double similarity = 0.0;
            int factors = 0;

            // Comparar tipo de linha
            if (pattern.Features.ContainsKey("lineType") && pattern.Features["lineType"].ToString() == context.LineType)
                similarity += 0.3;

            factors++;

            // Comparar comprimento
            if (pattern.Features.ContainsKey("averageLineLength"))
            {
                double patternLength = Convert.ToDouble(pattern.Features["averageLineLength"]);
                double lengthDiff = Math.Abs(patternLength - context.ActualLength) / context.ActualLength;
                similarity += Math.Max(0, 0.3 * (1 - lengthDiff));
            }
            factors++;

            // Aplicar taxa de sucesso do padrão
            if (pattern.SuccessRate > 0)
                similarity += 0.4 * pattern.SuccessRate;

            factors++;

            return factors > 0 ? similarity / factors : 0.0;
        }

        /// <summary>
        /// Gera sugestão baseada em padrão aprendido
        /// </summary>
        private ErrorSuggestion? GenerateSuggestionFromPattern(
            DocumentPattern pattern,
            LineContext context,
            DocumentLineError error)
        {
            if (pattern.Suggestions == null || !pattern.Suggestions.Any())
                return null;

            // Usar a sugestão mais bem-sucedida do padrão
            var bestSuggestion = pattern.Suggestions.OrderByDescending(s => s.SuccessRate).FirstOrDefault();

            if (bestSuggestion == null)
                return null;

            return new ErrorSuggestion
            {
                FieldName = bestSuggestion.FieldName,
                CurrentLength = context.ActualLength,
                SuggestedLength = bestSuggestion.SuggestedLength,
                Action = bestSuggestion.Action,
                Reason = $"Baseado em padrão aprendido (taxa de sucesso: {bestSuggestion.SuccessRate:P0})",
                Confidence = pattern.Confidence * bestSuggestion.SuccessRate
            };
        }

        /// <summary>
        /// Gera sugestões baseadas em regras (fallback quando não há padrões)
        /// </summary>
        private List<ErrorSuggestion> GenerateRuleBasedSuggestions(
            DocumentLineError error,
            LineContext context)
        {
            var suggestions = new List<ErrorSuggestion>();

            int excessChars = error.ActualLength - error.ExpectedLength;

            if (excessChars > 0)
            {
                suggestions.Add(new ErrorSuggestion
                {
                    FieldName = "Linha completa",
                    CurrentLength = error.ActualLength,
                    SuggestedLength = error.ExpectedLength,
                    Action = "truncate",
                    Reason = $"Linha excede em {excessChars} caracteres. Recomendado truncar os últimos {excessChars} caracteres.",
                    Confidence = 0.7
                });
            }

            return suggestions;
        }

        /// <summary>
        /// Carrega padrões aprendidos do disco
        /// </summary>
        private void LoadLearnedPatterns()
        {
            try
            {
                var patternFiles = Directory.GetFiles(_learningDataPath, "pattern_*.json");

                foreach (var file in patternFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var pattern = JsonSerializer.Deserialize<DocumentPattern>(json);
                        if (pattern != null)
                        {
                            var key = $"{pattern.LayoutGuid}_{pattern.PatternId}";
                            _learnedPatterns[key] = pattern;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao carregar padrão do arquivo {File}", file);
                    }
                }

                _logger.LogInformation("Carregados {Count} padrões aprendidos", _learnedPatterns.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar padrões aprendidos");
            }
        }

        /// <summary>
        /// Salva um padrão aprendido
        /// </summary>
        private async Task SavePatternAsync(DocumentPattern pattern)
        {
            try
            {
                pattern.PatternId = Guid.NewGuid().ToString();
                var fileName = Path.Combine(_learningDataPath, $"pattern_{pattern.PatternId}.json");
                var json = JsonSerializer.Serialize(pattern, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(fileName, json);

                // Atualizar cache
                var key = $"{pattern.LayoutGuid}_{pattern.PatternId}";
                _learnedPatterns[key] = pattern;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar padrão");
            }
        }

        /// <summary>
        /// Salva feedback sobre sugestão
        /// </summary>
        private async Task SaveFeedbackAsync(SuggestionFeedback feedback)
        {
            try
            {
                var fileName = Path.Combine(_learningDataPath, "feedback", $"feedback_{Guid.NewGuid()}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
                var json = JsonSerializer.Serialize(feedback, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(fileName, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar feedback");
            }
        }
    }
}