using LayoutParserApi.Services.Storage;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Transformation.Models;

using System.Xml.Linq;

using XslSynth.Core;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Serviço para validar transformações TCL e XSL
    /// Verifica se a transformação está gerando o resultado esperado
    /// </summary>
    public class TransformationValidatorService
    {
        private readonly ILogger<TransformationValidatorService> _logger;
        private readonly IConfiguration _configuration;
        private readonly TransformationPipelineService _pipelineService;
        private readonly XmlDocumentTypeDetector _documentTypeDetector;
        private readonly XsdValidationService _xsdValidationService;
        private readonly string _expectedOutputsPath;
        private readonly string _tclBasePath;

        public TransformationValidatorService(
            ILogger<TransformationValidatorService> logger,
            IConfiguration configuration,
            TransformationPipelineService pipelineService,
            XmlDocumentTypeDetector documentTypeDetector,
            XsdValidationService xsdValidationService,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _configuration = configuration;
            _pipelineService = pipelineService;
            _documentTypeDetector = documentTypeDetector;
            _xsdValidationService = xsdValidationService;
            _expectedOutputsPath = (storagePaths ?? new StoragePaths(configuration)).ExpectedOutputs;
            _tclBasePath = (storagePaths ?? new StoragePaths(configuration)).Tcl;

            Directory.CreateDirectory(_expectedOutputsPath);
        }

        /// <summary>
        /// Valida transformação completa (TXT -> TCL -> XSL -> XML)
        /// </summary>
        public async Task<TransformationValidationResult> ValidateTransformationAsync(
            string inputTxt,
            string layoutName,
            string tclPath,
            string xslPath,
            string expectedOutputXml = null)
        {
            var result = new TransformationValidationResult
            {
                Success = true,
                ValidationSteps = new List<ValidationStep>(),
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Iniciando validação de transformação para layout: {LayoutName}", layoutName);

                // Passo 1: Validar TCL (se fornecido)
                // ✅ SCS0018: tclPath é gerado internamente pelo pipeline (TransformationPipelineService,
                // a partir de TclPath base + layoutName), não é digitado livremente pelo chamador — mas
                // como layoutName chega da requisição, confinamos o caminho ao diretório de TCL configurado
                // antes de ler o arquivo.
                if (!string.IsNullOrEmpty(tclPath) && IsWithinBasePath(tclPath, _tclBasePath) && File.Exists(tclPath))
                {
                    var tclValidation = await ValidateTclAsync(tclPath, inputTxt);
                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "TCL Validation",
                        Success = tclValidation.Success,
                        Message = tclValidation.Message,
                        Details = tclValidation.Details
                    });

                    if (!tclValidation.Success)
                    {
                        result.Errors.AddRange(tclValidation.Errors);
                        result.Success = false;
                    }
                }

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

                // Passo 2: Executar transformação completa
                var transformationResult = await _pipelineService.TransformTxtToXmlAsync(
                    inputTxt,
                    layoutName,
                    documentType);

                if (transformationResult.Success)
                {
                    result.TransformedXml = transformationResult.TransformedXml;
                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "Transformation Execution",
                        Success = true,
                        Message = "Transformação executada com sucesso"
                    });
                }
                else
                {
                    result.Errors.AddRange(transformationResult.Errors);
                    result.Success = false;
                    return result;
                }

                // Passo 3: Validar estrutura XML resultante
                var xmlValidation = await ValidateXmlStructureAsync(transformationResult.TransformedXml);
                result.ValidationSteps.Add(new ValidationStep
                {
                    Step = "XML Structure Validation",
                    Success = xmlValidation.Success,
                    Message = xmlValidation.Message,
                    Details = xmlValidation.Details
                });

                if (!xmlValidation.Success)
                {
                    result.Errors.AddRange(xmlValidation.Errors);
                    result.Success = false;
                }

                // Passo 3.5: Validação de schema XSD (issue #173) — separada da comparação de
                // conteúdo (Passo 4), pois são preocupações diferentes: "é um NFe/CTe/NFCom/MDFe
                // válido perante o schema oficial?" vs. "bate com o XML esperado deste teste?".
                // Reaproveita XsdValidationService (já registrado no DI, mesmo grupo de validação),
                // sem duplicar lógica de resolução de XSD por documentType.
                try
                {
                    var xsdResult = await _xsdValidationService.ValidateXmlAgainstXsdAsync(
                        transformationResult.TransformedXml, layoutName: layoutName);

                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "XSD Schema Validation",
                        Success = xsdResult.IsValid,
                        Message = xsdResult.IsValid
                            ? $"XML válido contra XSD {xsdResult.XsdVersion ?? xsdResult.DocumentType}"
                            : $"{xsdResult.Errors.Count} erro(s) de schema encontrados",
                        Details = xsdResult.Errors.Count > 0
                            ? string.Join("; ", xsdResult.Errors.Select(e => e.Message))
                            : ""
                    });

                    if (!xsdResult.IsValid)
                    {
                        // Falha de schema não derruba o Success geral aqui — mantém o comportamento
                        // anterior (comparação de conteúdo é o gate principal); XSD é reportado como
                        // Warning para não quebrar consumidores que hoje só olham Errors/Success do
                        // pipeline de transformação.
                        result.Warnings.AddRange(xsdResult.Errors.Select(e => $"XSD: {e.Message}"));
                    }
                }
                catch (Exception ex)
                {
                    // Degrada graciosamente: XSD ausente/config errada não pode derrubar a validação
                    // de transformação inteira (princípio de resiliência do projeto).
                    _logger.LogWarning(ex, "Falha ao validar XML contra XSD para layout {LayoutName} — etapa ignorada.", layoutName);
                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "XSD Schema Validation",
                        Success = false,
                        Message = "Validação XSD não pôde ser executada",
                        Details = ex.Message
                    });
                }

                // Passo 4: Comparar com saída esperada (se fornecida)
                if (!string.IsNullOrEmpty(expectedOutputXml))
                {
                    var comparisonResult = await CompareWithExpectedAsync(transformationResult.TransformedXml, expectedOutputXml);

                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "Expected Output Comparison",
                        Success = comparisonResult.Match,
                        Message = comparisonResult.Message,
                        Details = comparisonResult.Differences != null && comparisonResult.Differences.Any()
                            ? string.Join("; ", comparisonResult.Differences)
                            : ""
                    });

                    if (!comparisonResult.Match)
                        result.Warnings.AddRange(comparisonResult.Differences);

                    result.FieldDiffs.AddRange(comparisonResult.FieldDiffs);
                }
                else if (IsValidLayoutName(layoutName))
                {
                    // Tentar carregar saída esperada do diretório
                    // ✅ SCS0018: layoutName vem da requisição (TransformationExecutionController) sem
                    // sanitização; IsValidLayoutName barra separadores/".." antes do Path.Combine e o
                    // IsWithinBasePath confirma que o caminho final não escapou de _expectedOutputsPath.
                    var expectedPath = Path.Combine(_expectedOutputsPath, $"{layoutName}_expected.xml");
#pragma warning disable SCS0018
                    if (IsWithinBasePath(expectedPath, _expectedOutputsPath) && File.Exists(expectedPath))
                    {
                        var expectedXml = await File.ReadAllTextAsync(expectedPath);
#pragma warning restore SCS0018
                        var comparisonResult = await CompareWithExpectedAsync(
                            transformationResult.TransformedXml,
                            expectedXml);

                        result.ValidationSteps.Add(new ValidationStep
                        {
                            Step = "Expected Output Comparison (from file)",
                            Success = comparisonResult.Match,
                            Message = comparisonResult.Message,
                            Details = comparisonResult.Differences != null && comparisonResult.Differences.Any()
                                ? string.Join("; ", comparisonResult.Differences)
                                : ""
                        });

                        if (!comparisonResult.Match)
                        {
                            result.Warnings.AddRange(comparisonResult.Differences);
                        }

                        result.FieldDiffs.AddRange(comparisonResult.FieldDiffs);
                    }
                }

                _logger.LogInformation("Validação concluída. Sucesso: {Success}", result.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante validação de transformação");
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
        /// Valida estrutura do TCL
        /// </summary>
        private async Task<TransformationCheckResult> ValidateTclAsync(string tclPath, string inputTxt)
        {
            var result = new TransformationCheckResult
            {
                Success = true,
                Errors = new List<string>()
            };

            try
            {
                // ✅ SCS0018 (issue #88): ValidateTclAsync só é chamada (linha 69) depois que
                // ValidateTransformationAsync confirma IsWithinBasePath(tclPath, TclPath) —
                // tclPath nunca chega aqui sem passar pela barreira.
#pragma warning disable SCS0018
                var tclContent = await File.ReadAllTextAsync(tclPath);
#pragma warning restore SCS0018

                // Verificar se o TCL é XML válido
                try
                {
                    var tclDoc = XDocument.Parse(tclContent);
                    result.Message = "TCL é um XML válido";
                    result.Details = $"Root element: {tclDoc.Root?.Name.LocalName}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Errors.Add($"TCL não é um XML válido: {ex.Message}");
                    result.Message = "TCL inválido";
                }

                // Verificar estrutura básica do TCL (MAP, LINE, FIELD) — issue #173: antes só
                // checava bem-formação XML, sem checar se o TCL de fato tem os elementos/atributos
                // que o parser posicional exige (o TODO original). Reaproveita
                // TclStructureValidator (mesma lógica já usada por ImprovedTclGeneratorService),
                // sem duplicar a checagem. Só roda se a etapa anterior (bem-formação) já passou —
                // se o TCL nem é XML válido, o erro de parse já foi reportado acima e repetir a
                // checagem de estrutura só duplicaria a mensagem.
                if (result.Success)
                {
                    var structureValidation = TclStructureValidator.Validate(tclContent);
                    if (!structureValidation.Success)
                    {
                        result.Success = false;
                        result.Errors.AddRange(structureValidation.Errors);
                        result.Message = "TCL com estrutura inválida";
                        result.Details = string.IsNullOrEmpty(result.Details)
                            ? structureValidation.Details
                            : $"{result.Details}; {structureValidation.Details}";
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Erro ao validar TCL: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Valida estrutura do XML resultante
        /// </summary>
        private async Task<TransformationCheckResult> ValidateXmlStructureAsync(string xmlContent)
        {
            var result = new TransformationCheckResult
            {
                Success = true,
                Errors = new List<string>()
            };

            try
            {
                var xmlDoc = XDocument.Parse(xmlContent);

                result.Message = "XML válido estruturalmente";
                result.Details = $"Root element: {xmlDoc.Root?.Name.LocalName}, Elements: {xmlDoc.Descendants().Count()}";

                // Validar elementos obrigatórios (ex: NFe)
                if (xmlDoc.Root?.Name.LocalName != "NFe")
                    result.Warnings.Add($"Elemento raiz esperado: NFe, encontrado: {xmlDoc.Root?.Name.LocalName}");
                
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"XML inválido: {ex.Message}");
                result.Message = "XML inválido";
            }

            return result;
        }

        /// <summary>
        /// Compara XML gerado com XML esperado, campo a campo (issue #173).
        ///
        /// Reaproveita <see cref="CanonicalDiffer"/> (mesmo comparador determinístico do loop de
        /// IA em <c>Services/Transformation/Ai</c>) em vez do diff raso anterior (só contagem de
        /// elementos + checagem de 4 nomes fixos) — um único juiz determinístico para
        /// gerar→validar→corrigir E validação manual, em vez de dois caminhos de comparação
        /// paralelos.
        /// </summary>
        private Task<ComparisonResult> CompareWithExpectedAsync(string actualXml, string expectedXml)
        {
            var result = new ComparisonResult
            {
                Match = true,
                Differences = new List<string>(),
                FieldDiffs = new List<FieldValidationDiff>()
            };

            try
            {
                var nodeDiffs = new CanonicalDiffer().Diff(expectedXml, actualXml);

                result.FieldDiffs = nodeDiffs.Select(ToFieldValidationDiff).ToList();
                result.Match = result.FieldDiffs.Count == 0;
                // Details rico vai por campo (FieldDiffs); Differences/Message seguem como resumo
                // textual para não quebrar consumidores existentes do contrato.
                result.Differences = nodeDiffs.Select(d => d.ToString()).ToList();
                result.Message = result.Match
                    ? "XML gerado corresponde ao esperado"
                    : $"Encontradas {result.FieldDiffs.Count} diferença(s)";

                // LogDebug com o diff completo; LogInformation só com o resumo (evita log verboso
                // de payload fiscal por campo, ver dotnet-standards §Logging).
                _logger.LogDebug("Diff canônico completo: {@FieldDiffs}", result.FieldDiffs);
                if (!result.Match)
                {
                    var countsByType = result.FieldDiffs
                        .GroupBy(d => d.DiffType)
                        .ToDictionary(g => g.Key.ToString(), g => g.Count());
                    _logger.LogInformation("Comparação com saída esperada encontrou {DiffCount} divergência(s): {@CountsByType}",
                        result.FieldDiffs.Count, countsByType);
                }
            }
            catch (Exception ex)
            {
                result.Match = false;
                result.Differences.Add($"Erro ao comparar: {ex.Message}");
                result.Message = "Erro na comparação";
            }

            return Task.FromResult(result);
        }

        /// <summary>Mapeia o <c>Kind</c> do diff canônico (texto livre) para o enum fechado <see cref="FieldDiffType"/> exposto no contrato.</summary>
        private static FieldValidationDiff ToFieldValidationDiff(NodeDiff diff)
        {
            var diffType = diff.Kind switch
            {
                "missing" => FieldDiffType.MissingInOutput,
                "extra" => FieldDiffType.UnexpectedInOutput,
                "name" => FieldDiffType.TypeMismatch,
                "attr" when diff.Actual is null => FieldDiffType.MissingInOutput,
                "attr" when diff.Expected is null => FieldDiffType.UnexpectedInOutput,
                _ => FieldDiffType.ValueMismatch, // "attr" com os dois valores presentes, ou "text".
            };

            return new FieldValidationDiff
            {
                XPath = diff.XPath,
                Expected = diff.Expected,
                Actual = diff.Actual,
                DiffType = diffType
            };
        }
    }
}