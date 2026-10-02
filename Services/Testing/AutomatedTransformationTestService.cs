using LayoutParserApi.Services.Storage;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Testing.Models;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis;

using System.Xml.Linq;

namespace LayoutParserApi.Services.Testing
{
    /// <summary>
    /// Serviço para executar testes automatizados de transformação usando exemplos MQSeries
    /// </summary>
    public class AutomatedTransformationTestService
    {
        private readonly ILogger<AutomatedTransformationTestService> _logger;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly MapperDatabaseService _mapperDatabaseService;
        private readonly TransformationPipelineService _pipelineService;
        private readonly TransformationValidatorService _validatorService;
        private readonly XmlDocumentTypeDetector _documentTypeDetector;
        private readonly string _examplesBasePath;
        private readonly string _expectedOutputsPath;

        public AutomatedTransformationTestService(
            ILogger<AutomatedTransformationTestService> logger,
            ICachedLayoutService cachedLayoutService,
            MapperDatabaseService mapperDatabaseService,
            TransformationPipelineService pipelineService,
            TransformationValidatorService validatorService,
            XmlDocumentTypeDetector documentTypeDetector,
            IConfiguration configuration,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _cachedLayoutService = cachedLayoutService;
            _mapperDatabaseService = mapperDatabaseService;
            _pipelineService = pipelineService;
            _validatorService = validatorService;
            _documentTypeDetector = documentTypeDetector;
            _examplesBasePath = (storagePaths ?? new StoragePaths(configuration)).TestExamples;
            _expectedOutputsPath = (storagePaths ?? new StoragePaths(configuration)).ExpectedOutputs;

            Directory.CreateDirectory(_expectedOutputsPath);
        }

        /// <summary>
        /// Executa testes automatizados para todos os layouts que têm exemplos
        /// </summary>
        public async Task<TestSuiteResult> RunAllTestsAsync()
        {
            var result = new TestSuiteResult
            {
                Success = true,
                TestResults = new List<TestResult>(),
                StartTime = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation("Iniciando execução de testes automatizados");

                // Buscar todos os diretórios de exemplos
                var exampleDirectories = Directory.GetDirectories(_examplesBasePath, "LAY_*", SearchOption.TopDirectoryOnly);

                foreach (var exampleDir in exampleDirectories)
                {
                    var layoutName = Path.GetFileName(exampleDir);
                    _logger.LogInformation("Processando testes para layout: {LayoutName}", layoutName);

                    // Executar testes para este layout
                    var testResult = await RunTestsForLayoutAsync(layoutName, exampleDir);
                    result.TestResults.Add(testResult);

                    if (!testResult.Success)
                        result.Success = false;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.TotalTests = result.TestResults.Count;
                result.PassedTests = result.TestResults.Count(t => t.Success);
                result.FailedTests = result.TestResults.Count(t => !t.Success);

                _logger.LogInformation("Testes concluídos. Total: {Total}, Passou: {Passed}, Falhou: {Failed}", result.TotalTests, result.PassedTests, result.FailedTests);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar testes automatizados");
                result.Success = false;
                result.Errors.Add($"Erro geral: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Executa testes para um layout específico
        /// </summary>
        public async Task<TestResult> RunTestsForLayoutAsync(string layoutName, string examplesDirectory = null)
        {
            var result = new TestResult
            {
                LayoutName = layoutName,
                Success = true,
                TestCases = new List<TestCaseResult>(),
                StartTime = DateTime.UtcNow
            };

            try
            {
                // ✅ SCS0018: layoutName e examplesDirectory chegam do corpo da requisição (TestingController)
                // sem sanitização. Validamos layoutName como identificador simples e confinamos
                // examplesDirectory dentro de _examplesBasePath para impedir path traversal
                // (ex.: layoutName="..\..\Windows" ou examplesDirectory="C:\dados-sensiveis").
                if (!IsValidLayoutName(layoutName))
                {
                    result.Success = false;
                    result.Errors.Add($"Nome de layout inválido: {layoutName}");
                    return result;
                }

                if (string.IsNullOrEmpty(examplesDirectory))
                    examplesDirectory = Path.Combine(_examplesBasePath, layoutName);

                if (!IsWithinBasePath(examplesDirectory, _examplesBasePath))
                {
                    _logger.LogWarning(
                        "Tentativa de acessar diretório de exemplos fora da base permitida: {ExamplesDirectory}",
                        Services.Logging.LogMessageSanitizer.Sanitize(examplesDirectory));
                    result.Success = false;
                    result.Errors.Add("Diretório de exemplos fora da área permitida.");
                    return result;
                }

                if (!Directory.Exists(examplesDirectory))
                {
                    result.Success = false;
                    result.Errors.Add($"Diretório de exemplos não encontrado: {examplesDirectory}");
                    return result;
                }

                // Buscar layout no Redis
                var layout = await FindLayoutByNameAsync(layoutName);
                if (layout == null)
                {
                    result.Success = false;
                    result.Errors.Add($"Layout não encontrado no Redis: {layoutName}");
                    return result;
                }

                result.LayoutGuid = layout.LayoutGuid != Guid.Empty ? layout.LayoutGuid.ToString() : layout.Id.ToString();

                // Buscar mapeador para este layout (InputLayoutGuid)
                var mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(result.LayoutGuid);
                if (mapper == null)
                    result.Warnings.Add($"Nenhum mapeador encontrado para o layout {layoutName}");

                // Buscar arquivos de exemplo TXT
                // ✅ SCS0018 (issue #88): examplesDirectory já passou por IsWithinBasePath (linha 126,
                // contra _examplesBasePath) antes de qualquer ponto de leitura — o SCS não reconhece a
                // validação custom como sanitizador, mas o caminho nunca escapa da base permitida.
#pragma warning disable SCS0018
                var exampleFiles = Directory.GetFiles(examplesDirectory, "*.txt", SearchOption.AllDirectories).Concat(Directory.GetFiles(examplesDirectory, "*.mqseries", SearchOption.AllDirectories)).ToList();
#pragma warning restore SCS0018

                if (!exampleFiles.Any())
                {
                    result.Warnings.Add($"Nenhum arquivo de exemplo encontrado em {examplesDirectory}");
                    return result;
                }

                // Buscar arquivo XML esperado (se existir) — mesma justificativa acima.
                var expectedXmlPath = Path.Combine(examplesDirectory, "expected_output.xml");
#pragma warning disable SCS0018
                var expectedXml = File.Exists(expectedXmlPath) ? await File.ReadAllTextAsync(expectedXmlPath) : null;
#pragma warning restore SCS0018

                // Executar teste para cada arquivo de exemplo
                foreach (var exampleFile in exampleFiles)
                {
                    var testCase = await RunTestCaseAsync(layoutName, exampleFile, expectedXml);
                    result.TestCases.Add(testCase);

                    if (!testCase.Success)
                        result.Success = false;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar testes para layout: {LayoutName}", layoutName);
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Valida que o nome do layout é um identificador simples (sem separadores de caminho
        /// ou sequências de path traversal). Usado como barreira anti-SCS0018 antes de qualquer
        /// combinação com caminhos de arquivo.
        /// </summary>
        private static bool IsValidLayoutName(string layoutName)
        {
            return !string.IsNullOrWhiteSpace(layoutName)
                && layoutName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !layoutName.Contains("..")
                && !Path.IsPathRooted(layoutName);
        }

        /// <summary>
        /// Confirma que o caminho resolvido permanece dentro do diretório base permitido,
        /// impedindo que um caminho controlado externamente escape via "..".
        /// </summary>
        private static bool IsWithinBasePath(string candidatePath, string basePath)
        {
            var fullCandidate = Path.GetFullPath(candidatePath);
            var fullBase = Path.GetFullPath(basePath);
            return fullCandidate.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Executa um caso de teste individual
        /// </summary>
        private async Task<TestCaseResult> RunTestCaseAsync(string layoutName, string exampleFilePath, string expectedXml = null)
        {
            var result = new TestCaseResult
            {
                TestCaseName = Path.GetFileName(exampleFilePath),
                Success = true,
                StartTime = DateTime.UtcNow
            };

            try
            {
                // Ler conteúdo do arquivo de exemplo — exampleFilePath vem de Directory.GetFiles sobre
                // examplesDirectory, já confinado por IsWithinBasePath em RunTestsForLayoutAsync.
#pragma warning disable SCS0018
                var inputTxt = await File.ReadAllTextAsync(exampleFilePath);
#pragma warning restore SCS0018

                // Detectar tipo de documento (NFe/CTe/NFCom/MDFe) a partir do nome do layout.
                // Sem indicador mais forte no pipeline hoje (namespace só existe DEPOIS da
                // transformação); fallback para "NFe" com warning quando não reconhecido,
                // preservando o comportamento anterior mas sem assumir silenciosamente.
                var documentTypeInfo = _documentTypeDetector.DetectFromLayoutName(layoutName);
                var documentType = documentTypeInfo.Type;
                if (string.IsNullOrEmpty(documentType) || documentType == "UNKNOWN")
                {
                    documentType = "NFe";
                    _logger.LogWarning(
                        "Não foi possível detectar o tipo de documento a partir do layout {LayoutName}; usando fallback {FallbackType}",
                        Services.Logging.LogMessageSanitizer.Sanitize(layoutName), documentType);
                }

                // Executar transformação
                var transformationResult = await _pipelineService.TransformTxtToXmlAsync(inputTxt, layoutName, documentType);

                if (!transformationResult.Success)
                {
                    result.Success = false;
                    result.Errors.AddRange(transformationResult.Errors);
                    result.EndTime = DateTime.UtcNow;
                    result.Duration = result.EndTime - result.StartTime;
                    return result;
                }

                result.TransformedXml = transformationResult.TransformedXml;
                result.TclPath = transformationResult.TclPath;
                result.XslPath = transformationResult.XslPath;

                // Validar transformação
                var validationResult = await _validatorService.ValidateTransformationAsync(inputTxt,layoutName,transformationResult.TclPath,transformationResult.XslPath,expectedXml);

                result.ValidationResult = validationResult;
                result.Success = validationResult.Success && validationResult.ValidationSteps.All(s => s.Success);

                // Comparar com saída esperada se disponível
                if (!string.IsNullOrEmpty(expectedXml))
                {
                    var comparisonResult = await CompareWithExpectedAsync(transformationResult.TransformedXml, expectedXml);
                    result.ComparisonResult = comparisonResult;

                    if (!comparisonResult.Match)
                        result.Warnings.AddRange(comparisonResult.Differences);
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar caso de teste: {TestCaseName}", result.TestCaseName);
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        /// <summary>
        /// Compara XML gerado com XML esperado
        /// </summary>
        private async Task<Models.ComparisonResult> CompareWithExpectedAsync(string actualXml, string expectedXml)
        {
            var result = new Models.ComparisonResult
            {
                Match = true,
                Differences = new List<string>()
            };

            try
            {
                var actualDoc = XDocument.Parse(actualXml);
                var expectedDoc = XDocument.Parse(expectedXml);

                // Comparar elementos principais
                var actualElements = actualDoc.Descendants().ToList();
                var expectedElements = expectedDoc.Descendants().ToList();

                if (actualElements.Count != expectedElements.Count)
                {
                    result.Match = false;
                    result.Differences.Add($"Número de elementos diferente: esperado {expectedElements.Count}, encontrado {actualElements.Count}");
                }

                // Comparar estrutura básica
                if (actualDoc.Root?.Name != expectedDoc.Root?.Name)
                {
                    result.Match = false;
                    result.Differences.Add($"Elemento raiz diferente: esperado {expectedDoc.Root?.Name}, encontrado {actualDoc.Root?.Name}");
                }

                // Comparar elementos críticos
                var criticalElements = new[] { "infNFe", "ide", "emit", "dest" };
                foreach (var elementName in criticalElements)
                {
                    var actualElement = actualDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
                    var expectedElement = expectedDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);

                    if (expectedElement != null && actualElement == null)
                    {
                        result.Match = false;
                        result.Differences.Add($"Elemento crítico ausente: {elementName}");
                    }
                }

                result.Message = result.Match ? "XML gerado corresponde ao esperado": $"Encontradas {result.Differences.Count} diferenças";
            }
            catch (Exception ex)
            {
                result.Match = false;
                result.Differences.Add($"Erro ao comparar: {ex.Message}");
                result.Message = "Erro na comparação";
            }

            return result;
        }

        /// <summary>
        /// Busca layout no Redis pelo nome
        /// </summary>
        private async Task<LayoutRecord> FindLayoutByNameAsync(string layoutName)
        {
            try
            {
                var searchRequest = new LayoutSearchRequest
                {
                    SearchTerm = layoutName,
                    MaxResults = 100
                };

                var searchResponse = await _cachedLayoutService.SearchLayoutsAsync(searchRequest);
                return searchResponse?.Layouts?.FirstOrDefault(l => l.Name == layoutName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao buscar layout: {LayoutName}", layoutName);
                return null;
            }
        }
    }
}