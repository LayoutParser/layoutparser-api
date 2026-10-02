using LayoutParserApi.Services.Storage;
using System.Xml.Linq;

using LayoutParserApi.Services.Logging;
using LayoutParserApi.Services.Security;
using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Serviço de aprendizado de máquina para melhorar geração de TCL e XSL
    /// Usa exemplos existentes para aprender padrões e melhorar a precisão
    /// </summary>
    public class TransformationLearningService
    {
        private readonly ILogger<TransformationLearningService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _examplesBasePath;
        private readonly string _examplesTclPath;
        private readonly string _examplesXslPath;
        private readonly string _learningModelsPath;

        public TransformationLearningService(
            ILogger<TransformationLearningService> logger,
            IConfiguration configuration,
            StoragePaths? storagePaths = null)
        {
            var paths = storagePaths ?? new StoragePaths(configuration);
            _logger = logger;
            _configuration = configuration;
            _examplesBasePath = paths.Examples;
            _examplesTclPath = paths.ExamplesTcl;
            _examplesXslPath = paths.ExamplesXsl;
            _learningModelsPath = paths.LearningModels;

            Directory.CreateDirectory(_examplesBasePath);
            Directory.CreateDirectory(_examplesTclPath);
            Directory.CreateDirectory(_examplesXslPath);
            Directory.CreateDirectory(_learningModelsPath);
        }

        /// <summary>
        /// Aprende padrões de TCL a partir de exemplos existentes
        /// </summary>
        public async Task<LearningResult> LearnTclPatternsAsync(string layoutName, List<TclExample> examples)
        {
            var result = new LearningResult
            {
                Success = true,
                PatternsLearned = new List<LearnedPattern>(),
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Iniciando aprendizado de padrões TCL para layout: {LayoutName}", layoutName);

                if (examples == null || !examples.Any())
                {
                    result.Warnings.Add("Nenhum exemplo fornecido para aprendizado");
                    return result;
                }

                // Analisar padrões comuns nos exemplos TCL
                var patterns = await AnalyzeTclPatternsAsync(examples);

                // Extrair regras de mapeamento
                var mappingRules = await ExtractMappingRulesAsync(examples);

                // Salvar modelo aprendido
                var model = new LearnedTclModel
                {
                    LayoutName = layoutName,
                    Patterns = patterns,
                    MappingRules = mappingRules,
                    ExamplesCount = examples.Count,
                    LearnedAt = DateTime.UtcNow
                };

                await SaveLearnedModelAsync($"tcl_{layoutName}", model);

                result.PatternsLearned = patterns;
                result.Success = true;

                _logger.LogInformation("Aprendizado TCL concluído. {Count} padrões aprendidos", patterns.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante aprendizado de padrões TCL");
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Aprende padrões de XSL a partir de exemplos existentes
        /// </summary>
        public async Task<LearningResult> LearnXslPatternsAsync(string layoutName, List<XslExample> examples)
        {
            var result = new LearningResult
            {
                Success = true,
                PatternsLearned = new List<LearnedPattern>(),
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Iniciando aprendizado de padrões XSL para layout: {LayoutName}", layoutName);

                if (examples == null || !examples.Any())
                {
                    result.Warnings.Add("Nenhum exemplo fornecido para aprendizado");
                    return result;
                }

                // Analisar padrões comuns nos exemplos XSL
                var patterns = await AnalyzeXslPatternsAsync(examples);

                // Extrair regras de transformação
                var transformationRules = await ExtractTransformationRulesAsync(examples);

                // Salvar modelo aprendido
                var model = new LearnedXslModel
                {
                    LayoutName = layoutName,
                    Patterns = patterns,
                    TransformationRules = transformationRules,
                    ExamplesCount = examples.Count,
                    LearnedAt = DateTime.UtcNow
                };

                await SaveLearnedModelAsync($"xsl_{layoutName}", model);

                result.PatternsLearned = patterns;
                result.Success = true;

                _logger.LogInformation("Aprendizado XSL concluído. {Count} padrões aprendidos", patterns.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante aprendizado de padrões XSL");
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
            }

            return result;
        }


        /// <summary>
        /// Analisa padrões comuns em exemplos XSL com aprendizado avançado
        /// </summary>
        private async Task<List<LearnedPattern>> AnalyzeXslPatternsAsync(List<XslExample> examples)
        {
            var patterns = new List<LearnedPattern>();

            try
            {
                // Analisar templates XSL com análise de estrutura
                var allTemplates = examples.SelectMany(e => ParseXslTemplates(e.Content)).ToList();

                var templateGroups = allTemplates.GroupBy(t => t.TemplateName).ToList();

                foreach (var group in templateGroups)
                {
                    var representative = FindMostRepresentativeTemplate(group.ToList());

                    patterns.Add(new LearnedPattern
                    {
                        Type = "XslTemplate",
                        Name = group.Key,
                        Pattern = representative.TemplateStructure,
                        Frequency = group.Count(),
                        Confidence = CalculateConfidence(group.Count(), examples.Count),
                        Metadata = new Dictionary<string, object>
                        {
                            ["MatchPattern"] = representative.MatchPattern,
                            ["HasApplyTemplates"] = representative.HasApplyTemplates,
                            ["HasForEach"] = representative.HasForEach,
                            ["HasChoose"] = representative.HasChoose,
                            ["ElementCount"] = representative.ElementCount
                        }
                    });
                }

                // Analisar transformações XSL com análise de XPath
                var allTransforms = examples.SelectMany(e => ParseXslTransforms(e.Content)).ToList();

                // Agrupar por tipo de transformação e XPath
                var transformGroups = allTransforms.GroupBy(t => new { t.TransformType, t.SourceXPath }).ToList();

                foreach (var group in transformGroups)
                {
                    patterns.Add(new LearnedPattern
                    {
                        Type = "XslTransform",
                        Name = $"{group.Key.TransformType}_{group.Key.SourceXPath}",
                        Pattern = group.First().TransformPattern,
                        Frequency = group.Count(),
                        Confidence = CalculateConfidence(group.Count(), allTransforms.Count),
                        Metadata = new Dictionary<string, object>
                        {
                            ["TransformType"] = group.Key.TransformType,
                            ["SourceXPath"] = group.Key.SourceXPath,
                            ["ParentElement"] = group.First().ParentElement
                        }
                    });
                }

                // Analisar correspondências entre input XML e output XML
                if (examples.Any(e => !string.IsNullOrEmpty(e.InputXml) && !string.IsNullOrEmpty(e.OutputXml)))
                {
                    var mappingPatterns = await AnalyzeXmlToXmlMappingAsync(examples);
                    patterns.AddRange(mappingPatterns);
                }

                // Analisar padrões de transformação hierárquica
                var hierarchyPatterns = AnalyzeXmlHierarchyPatterns(allTemplates, allTransforms);
                patterns.AddRange(hierarchyPatterns);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar padrões XSL");
            }

            return patterns;
        }

        /// <summary>
        /// Encontra o template mais representativo
        /// </summary>
        private XslTemplateInfo FindMostRepresentativeTemplate(List<XslTemplateInfo> templates)
        {
            if (!templates.Any())
                return new XslTemplateInfo();

            // Retornar template com mais elementos (mais completo)
            return templates.OrderByDescending(t => t.ElementCount).First();
        }

        /// <summary>
        /// Analisa mapeamento entre input XML e output XML
        /// </summary>
        private async Task<List<LearnedPattern>> AnalyzeXmlToXmlMappingAsync(List<XslExample> examples)
        {
            var patterns = new List<LearnedPattern>();

            try
            {
                foreach (var example in examples)
                {
                    if (string.IsNullOrEmpty(example.InputXml) || string.IsNullOrEmpty(example.OutputXml))
                        continue;

                    var inputDoc = XDocument.Parse(example.InputXml);
                    var outputDoc = XDocument.Parse(example.OutputXml);
                    var transforms = ParseXslTransforms(example.Content);

                    // Analisar como elementos do input mapeiam para output
                    foreach (var transform in transforms)
                    {
                        if (string.IsNullOrEmpty(transform.SourceXPath))
                            continue;

                        // Tentar encontrar elemento no input
                        var inputElement = FindElementByXPath(inputDoc, transform.SourceXPath);
                        if (inputElement != null)
                        {
                            // Tentar encontrar elemento correspondente no output
                            var outputElement = FindCorrespondingOutputElement(outputDoc, transform);
                            if (outputElement != null)
                            {
                                patterns.Add(new LearnedPattern
                                {
                                    Type = "XmlToXmlMapping",
                                    Name = transform.TransformType,
                                    Pattern = $"{GetXPath(inputElement)} -> {GetXPath(outputElement)}",
                                    Frequency = 1,
                                    Confidence = 0.5,
                                    Metadata = new Dictionary<string, object>
                                    {
                                        ["InputXPath"] = GetXPath(inputElement),
                                        ["OutputXPath"] = GetXPath(outputElement),
                                        ["TransformType"] = transform.TransformType
                                    }
                                });
                            }
                        }
                    }
                }

                // Agrupar padrões similares
                return patterns
                    .GroupBy(p => p.Pattern)
                    .Select(g => new LearnedPattern
                    {
                        Type = g.First().Type,
                        Name = g.First().Name,
                        Pattern = g.Key,
                        Frequency = g.Count(),
                        Confidence = CalculateConfidence(g.Count(), examples.Count),
                        Metadata = g.First().Metadata
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar mapeamento XML→XML");
            }

            return patterns;
        }

        /// <summary>
        /// Encontra elemento XML por XPath simplificado
        /// </summary>
        private XElement FindElementByXPath(XDocument doc, string xpath)
        {
            try
            {
                // XPath simplificado - buscar por nome do elemento
                var elementName = xpath.Split('/').Last();
                return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Encontra elemento correspondente no output
        /// </summary>
        private XElement FindCorrespondingOutputElement(XDocument outputDoc, XslTransformInfo transform)
        {
            // Lógica simplificada - em produção, usar análise mais sofisticada
            // baseada no contexto da transformação XSL
            return outputDoc.Descendants().FirstOrDefault();
        }

        /// <summary>
        /// Analisa padrões hierárquicos de transformação XML
        /// </summary>
        private List<LearnedPattern> AnalyzeXmlHierarchyPatterns(List<XslTemplateInfo> templates, List<XslTransformInfo> transforms)
        {
            var patterns = new List<LearnedPattern>();

            try
            {
                // Analisar hierarquia de templates (templates que chamam outros templates)
                var templateHierarchy = templates.Where(t => t.HasApplyTemplates).Select(t => new LearnedPattern
                {
                    Type = "TemplateHierarchy",
                    Name = t.TemplateName,
                    Pattern = $"Template '{t.TemplateName}' uses apply-templates",
                    Frequency = 1,
                    Confidence = 1.0,
                    Metadata = new Dictionary<string, object>
                    {
                        ["MatchPattern"] = t.MatchPattern,
                        ["HasForEach"] = t.HasForEach
                    }
                }).ToList();

                patterns.AddRange(templateHierarchy);

                // Analisar padrões de aninhamento (for-each dentro de templates)
                var nestingPatterns = templates.Where(t => t.HasForEach).Select(t => new LearnedPattern
                {
                    Type = "NestingPattern",
                    Name = t.TemplateName,
                    Pattern = $"Template '{t.TemplateName}' contains for-each",
                    Frequency = 1,
                    Confidence = 1.0
                }).ToList();

                patterns.AddRange(nestingPatterns);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar padrões hierárquicos");
            }

            return patterns;
        }

        /// <summary>
        /// Extrai regras de mapeamento dos exemplos TCL com análise avançada
        /// </summary>
        private async Task<List<MappingRule>> ExtractMappingRulesAsync(List<TclExample> examples)
        {
            var rules = new List<MappingRule>();

            try
            {
                foreach (var example in examples)
                {
                    if (string.IsNullOrEmpty(example.InputTxt) || string.IsNullOrEmpty(example.OutputXml))
                        continue;

                    // Parsear campos do TCL
                    var fields = ParseTclFields(example.Content);
                    var txtLines = example.InputTxt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    var outputDoc = XDocument.Parse(example.OutputXml);

                    // Para cada campo, tentar encontrar correspondência no XML
                    foreach (var field in fields)
                    {
                        // Encontrar linha do TXT que contém este campo
                        var txtLine = FindTxtLineContainingField(txtLines, field);
                        if (string.IsNullOrEmpty(txtLine))
                            continue;

                        // Extrair valor do campo do TXT
                        var fieldValue = ExtractFieldValueFromTxt(txtLine, field);
                        if (string.IsNullOrEmpty(fieldValue))
                            continue;

                        // Procurar valor correspondente no XML
                        var xmlElement = FindElementByValue(outputDoc, fieldValue, field);
                        if (xmlElement != null)
                        {
                            var rule = new MappingRule
                            {
                                SourceField = $"{field.LineName}.{field.FieldName}",
                                TargetElement = xmlElement.Name.LocalName,
                                TransformType = DetermineTransformType(field, xmlElement),
                                SourcePosition = field.StartPosition,
                                SourceLength = field.Length,
                                SourceType = field.FieldType,
                                TargetXPath = GetXPath(xmlElement),
                                Confidence = CalculateMappingConfidence(field, xmlElement, fieldValue)
                            };

                            rules.Add(rule);
                        }
                    }
                }

                // Agrupar regras similares e calcular confiança
                var groupedRules = rules.GroupBy(r => new { r.SourceField, r.TargetElement }).Select(g => new MappingRule
                {
                    SourceField = g.Key.SourceField,
                    TargetElement = g.Key.TargetElement,
                    TransformType = g.GroupBy(r => r.TransformType).OrderByDescending(gr => gr.Count()).First().Key,
                    SourcePosition = (int)g.Average(r => r.SourcePosition),
                    SourceLength = (int)g.Average(r => r.SourceLength),
                    SourceType = g.First().SourceType,
                    TargetXPath = g.First().TargetXPath,
                    Confidence = g.Average(r => r.Confidence)
                }).OrderByDescending(r => r.Confidence).ToList();

                return groupedRules;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair regras de mapeamento");
            }

            return rules;
        }

        /// <summary>
        /// Encontra linha TXT que contém um campo específico
        /// </summary>
        private string FindTxtLineContainingField(string[] txtLines, TclFieldInfo field)
        {
            // Buscar linha que corresponde ao tipo de linha do campo
            foreach (var line in txtLines)
            {
                var lineIdentifier = DetectLineIdentifierForField(line, field);
                if (lineIdentifier == field.LineName)
                    return line;
            }

            return null;
        }

        /// <summary>
        /// Detecta identificador de linha para um campo
        /// </summary>
        private string DetectLineIdentifierForField(string txtLine, TclFieldInfo field)
        {
            // Lógica similar à do TransformationPipelineService
            if (txtLine.StartsWith("HEADER")) return "HEADER";
            if (txtLine.StartsWith("TRAILER")) return "TRAILER";
            if (txtLine.Length > 0 && char.IsLetter(txtLine[0]))
                return txtLine[0].ToString();

            return field.LineName;
        }

        /// <summary>
        /// Extrai valor do campo do TXT
        /// </summary>
        private string ExtractFieldValueFromTxt(string txtLine, TclFieldInfo field)
        {
            try
            {
                if (field.StartPosition + field.Length > txtLine.Length)
                    return "";

                return txtLine.Substring(field.StartPosition, field.Length).Trim();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Encontra elemento XML por valor
        /// </summary>
        private XElement FindElementByValue(XDocument xmlDoc, string value, TclFieldInfo field)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            // Buscar elemento que contém o valor
            var elements = xmlDoc.Descendants().Where(e => e.Value.Contains(value) || e.Attributes().Any(a => a.Value.Contains(value))).ToList();

            if (!elements.Any())
                return null;

            // Priorizar elementos com nome similar ao campo
            var fieldNameLower = field.FieldName.ToLower();
            var matchingName = elements.FirstOrDefault(e => e.Name.LocalName.ToLower().Contains(fieldNameLower) || fieldNameLower.Contains(e.Name.LocalName.ToLower()));

            return matchingName ?? elements.First();
        }

        /// <summary>
        /// Determina tipo de transformação baseado no campo e elemento
        /// </summary>
        private string DetermineTransformType(TclFieldInfo field, XElement xmlElement)
        {
            // Verificar se precisa de transformação de tipo
            if (field.FieldType == "DATE" && xmlElement.Value.Length == 8)
                return "DATE_FORMAT";
            if (field.FieldType == "DECIMAL" && xmlElement.Value.Contains("."))
                return "DECIMAL_FORMAT";
            if (field.FieldType == "CNPJ_CPF")
                return "CNPJ_CPF_FORMAT";

            return "DIRECT";
        }

        /// <summary>
        /// Calcula confiança do mapeamento
        /// </summary>
        private double CalculateMappingConfidence(TclFieldInfo field, XElement xmlElement, string value)
        {
            var confidence = 0.5; // Base

            // Aumentar confiança se nomes são similares
            var fieldNameLower = field.FieldName.ToLower();
            var elementNameLower = xmlElement.Name.LocalName.ToLower();
            if (fieldNameLower.Contains(elementNameLower) || elementNameLower.Contains(fieldNameLower))
                confidence += 0.3;

            // Aumentar confiança se o valor corresponde
            if (xmlElement.Value.Contains(value) || value.Contains(xmlElement.Value))
                confidence += 0.2;

            return Math.Min(1.0, confidence);
        }

        /// <summary>
        /// Extrai regras de transformação dos exemplos XSL com análise avançada
        /// </summary>
        private async Task<List<TransformationRule>> ExtractTransformationRulesAsync(List<XslExample> examples)
        {
            var rules = new List<TransformationRule>();

            try
            {
                foreach (var example in examples)
                {
                    if (string.IsNullOrEmpty(example.InputXml) || string.IsNullOrEmpty(example.OutputXml))
                        continue;

                    var inputDoc = XDocument.Parse(example.InputXml);
                    var outputDoc = XDocument.Parse(example.OutputXml);
                    var transforms = ParseXslTransforms(example.Content);
                    var templates = ParseXslTemplates(example.Content);

                    // Analisar cada transformação XSL
                    foreach (var transform in transforms)
                    {
                        if (string.IsNullOrEmpty(transform.SourceXPath))
                            continue;

                        // Encontrar elemento no input
                        var inputElement = FindElementByXPath(inputDoc, transform.SourceXPath);
                        if (inputElement == null)
                            continue;

                        // Tentar encontrar elemento correspondente no output
                        var outputElement = FindOutputElementByTransform(outputDoc, transform, inputElement);
                        if (outputElement != null)
                        {
                            var rule = new TransformationRule
                            {
                                SourceXPath = GetXPath(inputElement),
                                TargetXPath = GetXPath(outputElement),
                                TransformType = transform.TransformType,
                                SourceElement = inputElement.Name.LocalName,
                                TargetElement = outputElement.Name.LocalName,
                                TransformPattern = transform.TransformPattern,
                                Confidence = CalculateTransformConfidence(transform, inputElement, outputElement)
                            };

                            rules.Add(rule);
                        }
                    }
                }

                // Agrupar regras similares
                var groupedRules = rules
                    .GroupBy(r => new { r.SourceXPath, r.TargetXPath, r.TransformType })
                    .Select(g => new TransformationRule
                    {
                        SourceXPath = g.Key.SourceXPath,
                        TargetXPath = g.Key.TargetXPath,
                        TransformType = g.Key.TransformType,
                        SourceElement = g.First().SourceElement,
                        TargetElement = g.First().TargetElement,
                        TransformPattern = g.First().TransformPattern,
                        Confidence = g.Average(r => r.Confidence)
                    }).OrderByDescending(r => r.Confidence).ToList();

                return groupedRules;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair regras de transformação");
            }

            return rules;
        }

        /// <summary>
        /// Encontra elemento no output baseado na transformação
        /// </summary>
        private XElement FindOutputElementByTransform(XDocument outputDoc, XslTransformInfo transform, XElement inputElement)
        {
            try
            {
                // Tentar encontrar elemento com nome similar
                var inputName = inputElement.Name.LocalName;
                var outputElement = outputDoc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == inputName);

                if (outputElement != null)
                    return outputElement;

                // Tentar encontrar no mesmo nível hierárquico
                var parentXPath = GetXPath(inputElement.Parent);
                var outputParent = FindElementByXPath(outputDoc, parentXPath);
                if (outputParent != null)
                    return outputParent.Elements().FirstOrDefault(e => e.Value == inputElement.Value);

                // Fallback: buscar qualquer elemento com valor similar
                return outputDoc.Descendants().FirstOrDefault(e => e.Value == inputElement.Value);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calcula confiança da transformação
        /// </summary>
        private double CalculateTransformConfidence(XslTransformInfo transform, XElement inputElement, XElement outputElement)
        {
            var confidence = 0.5; // Base

            // Aumentar confiança se elementos têm nomes similares
            if (inputElement.Name.LocalName == outputElement.Name.LocalName)
                confidence += 0.3;

            // Aumentar confiança se valores correspondem
            if (inputElement.Value == outputElement.Value)
                confidence += 0.2;

            return Math.Min(1.0, confidence);
        }

        /// <summary>
        /// Calcula confiança baseada em frequência
        /// </summary>
        private double CalculateConfidence(int frequency, int total)
        {
            if (total == 0) return 0.0;
            var baseConfidence = (double)frequency / total;
            // Normalizar para 0.0-1.0 com um mínimo de 0.1
            return Math.Max(0.1, Math.Min(1.0, baseConfidence));
        }

        /// <summary>
        /// Analisa padrões TCL a partir de exemplos
        /// </summary>
        private async Task<List<LearnedPattern>> AnalyzeTclPatternsAsync(List<TclExample> examples)
        {
            var patterns = new List<LearnedPattern>();

            try
            {
                if (examples == null || !examples.Any())
                    return patterns;

                // Parsear todas as linhas TCL
                var allLines = new List<TclLineInfo>();
                var allFields = new List<TclFieldInfo>();

                foreach (var example in examples)
                {
                    var lines = ParseTclLines(example.Content);
                    allLines.AddRange(lines);

                    foreach (var line in lines)
                    {
                        var fields = ParseTclFields(example.Content);
                        allFields.AddRange(fields);
                    }
                }

                // Agrupar padrões por tipo de linha
                var linePatterns = allLines.GroupBy(l => l.LineType).Select(g => new LearnedPattern
                {
                    Type = "TclLine",
                    Name = g.Key,
                    Pattern = g.First().Structure,
                    Frequency = g.Count(),
                    Confidence = CalculateConfidence(g.Count(), allLines.Count),
                    Metadata = new Dictionary<string, object>
                    {
                        ["LineType"] = g.Key,
                        ["FieldCount"] = g.Average(l => l.FieldCount)
                    }
                }).ToList();

                patterns.AddRange(linePatterns);

                // Agrupar padrões por tipo de campo
                var fieldPatterns = allFields.GroupBy(f => f.FieldType).Select(g => new LearnedPattern
                {
                    Type = "TclField",
                    Name = g.Key,
                    Pattern = g.First().Mapping,
                    Frequency = g.Count(),
                    Confidence = CalculateConfidence(g.Count(), allFields.Count),
                    Metadata = new Dictionary<string, object>
                    {
                        ["FieldType"] = g.Key,
                        ["AverageLength"] = g.Average(f => f.Length),
                        ["AverageStartPosition"] = g.Average(f => f.StartPosition)
                    }
                }).ToList();

                patterns.AddRange(fieldPatterns);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar padrões TCL");
            }

            return patterns;
        }

        /// <summary>
        /// Carrega modelo aprendido
        /// </summary>
        public async Task<LearnedTclModel> LoadTclModelAsync(string layoutName)
        {
            try
            {
                // ✅ SCS0018 (issue #88, achado real): layoutName chega cru de
                // MetricsController.GetLearningMetrics ([HttpGet("learning/{layoutName}")], sem
                // validação nenhuma antes deste método) e de ImprovedTclGeneratorService. Diferente
                // do que o baseline documentava para SaveLearnedModelAsync (modelName supostamente
                // só interno), aqui o nome do arquivo é montado diretamente com o parâmetro de rota —
                // "..\..\..\Windows\win.ini" (prefixado por "tcl_/") chegava direto ao
                // File.ReadAllTextAsync. Fechado com SafePathResolver, mesmo padrão do projeto.
                var modelPath = SafePathResolver.Resolve(_learningModelsPath, $"tcl_{layoutName}.json");
                if (modelPath == null)
                {
                    _logger.LogWarning("Layout rejeitado para leitura de modelo TCL aprendido: {LayoutName}", LogMessageSanitizer.Sanitize(layoutName));
                    return null;
                }

#pragma warning disable SCS0018
                if (File.Exists(modelPath))
                {
                    var json = await File.ReadAllTextAsync(modelPath);
                    return System.Text.Json.JsonSerializer.Deserialize<LearnedTclModel>(json);
                }
#pragma warning restore SCS0018
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao carregar modelo TCL aprendido");
            }

            return null;
        }

        /// <summary>
        /// Carrega modelo XSL aprendido
        /// </summary>
        public async Task<LearnedXslModel> LoadXslModelAsync(string layoutName)
        {
            try
            {
                // ✅ SCS0018 (issue #88, achado real) — mesma justificativa de LoadTclModelAsync acima.
                var modelPath = SafePathResolver.Resolve(_learningModelsPath, $"xsl_{layoutName}.json");
                if (modelPath == null)
                {
                    _logger.LogWarning("Layout rejeitado para leitura de modelo XSL aprendido: {LayoutName}", LogMessageSanitizer.Sanitize(layoutName));
                    return null;
                }

#pragma warning disable SCS0018
                if (File.Exists(modelPath))
                {
                    var json = await File.ReadAllTextAsync(modelPath);
                    return System.Text.Json.JsonSerializer.Deserialize<LearnedXslModel>(json);
                }
#pragma warning restore SCS0018
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao carregar modelo XSL aprendido");
            }

            return null;
        }

        /// <summary>
        /// Agrega, entre TODOS os layouts já treinados, os modelos TCL/XSL persistidos em
        /// <see cref="_learningModelsPath"/> — usado pelo dashboard de métricas (issue #174).
        /// Falha de leitura de um arquivo individual não derruba o agregado (loga e pula).
        /// </summary>
        public async Task<LearningSummary> GetLearningSummaryAsync()
        {
            var summary = new LearningSummary();
            var confidences = new List<double>();

            try
            {
                if (!Directory.Exists(_learningModelsPath))
                {
                    _logger.LogWarning("Diretório de modelos de aprendizado não encontrado: {Path}", _learningModelsPath);
                    return summary;
                }

                var tclFiles = Directory.GetFiles(_learningModelsPath, "tcl_*.json", SearchOption.TopDirectoryOnly);
                var xslFiles = Directory.GetFiles(_learningModelsPath, "xsl_*.json", SearchOption.TopDirectoryOnly);

                foreach (var file in tclFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var model = System.Text.Json.JsonSerializer.Deserialize<LearnedTclModel>(json);
                        if (model == null) continue;

                        summary.TotalModels++;
                        summary.TotalPatterns += model.Patterns.Count;
                        summary.TotalExamples += model.ExamplesCount;
                        confidences.AddRange(model.Patterns.Select(p => p.Confidence));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao carregar modelo TCL para o resumo agregado: {File}", file);
                    }
                }

                foreach (var file in xslFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var model = System.Text.Json.JsonSerializer.Deserialize<LearnedXslModel>(json);
                        if (model == null) continue;

                        summary.TotalModels++;
                        summary.TotalPatterns += model.Patterns.Count;
                        summary.TotalExamples += model.ExamplesCount;
                        confidences.AddRange(model.Patterns.Select(p => p.Confidence));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao carregar modelo XSL para o resumo agregado: {File}", file);
                    }
                }

                summary.AverageConfidence = confidences.Count > 0 ? confidences.Average() : 0.0;
            }
            catch (Exception ex)
            {
                // Degrade gracioso: resumo zerado em vez de derrubar o endpoint.
                _logger.LogError(ex, "Erro ao agregar resumo de métricas de aprendizado");
            }

            return summary;
        }

        /// <summary>
        /// Carrega exemplos TCL constantemente da pasta ExamplesTclPath
        /// </summary>
        public async Task<List<TclExample>> LoadTclExamplesAsync(string layoutName = null)
        {
            var examples = new List<TclExample>();

            try
            {
                _logger.LogInformation("Carregando exemplos TCL da pasta: {Path}", _examplesTclPath);

                if (!Directory.Exists(_examplesTclPath))
                {
                    _logger.LogWarning("Diretório de exemplos TCL não encontrado: {Path}", _examplesTclPath);
                    return examples;
                }

                // Buscar todos os arquivos TCL
                var tclFiles = Directory.GetFiles(_examplesTclPath, "*.tcl", SearchOption.AllDirectories).ToList();

                _logger.LogInformation("Encontrados {Count} arquivos TCL", tclFiles.Count);

                // Filtrar por layout se fornecido
                if (!string.IsNullOrEmpty(layoutName))
                {
                    var normalizedLayoutName = NormalizeLayoutName(layoutName);
                    tclFiles = tclFiles.Where(f =>
                    {
                        var fileName = Path.GetFileNameWithoutExtension(f);
                        var normalizedFileName = NormalizeLayoutName(fileName);
                        return normalizedFileName.Contains(normalizedLayoutName) || normalizedLayoutName.Contains(normalizedFileName);
                    }).ToList();
                }

                foreach (var tclFile in tclFiles)
                {
                    try
                    {
                        var tclContent = await File.ReadAllTextAsync(tclFile);

                        // Tentar encontrar arquivos relacionados (input TXT e output XML)
                        var directory = Path.GetDirectoryName(tclFile);
                        var inputTxt = Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        var outputXml = Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();

                        var example = new TclExample
                        {
                            LayoutName = layoutName ?? Path.GetFileNameWithoutExtension(tclFile),
                            Content = tclContent,
                            InputTxt = inputTxt != null ? await File.ReadAllTextAsync(inputTxt) : null,
                            OutputXml = outputXml != null ? await File.ReadAllTextAsync(outputXml) : null
                        };

                        examples.Add(example);
                        _logger.LogInformation("Exemplo TCL carregado: {File} ({Size} chars)", tclFile, tclContent.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao carregar exemplo TCL: {File}", tclFile);
                    }
                }

                _logger.LogInformation("Total de exemplos TCL carregados: {Count}", examples.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar exemplos TCL da pasta: {Path}", _examplesTclPath);
            }

            return examples;
        }

        /// <summary>
        /// Carrega exemplos XSL constantemente da pasta ExamplesXslPath
        /// </summary>
        public async Task<List<XslExample>> LoadXslExamplesAsync(string layoutName = null)
        {
            var examples = new List<XslExample>();

            try
            {
                _logger.LogInformation("Carregando exemplos XSL da pasta: {Path}", _examplesXslPath);

                if (!Directory.Exists(_examplesXslPath))
                {
                    _logger.LogWarning("Diretório de exemplos XSL não encontrado: {Path}", _examplesXslPath);
                    return examples;
                }

                // Buscar todos os arquivos XSL
                var xslFiles = Directory.GetFiles(_examplesXslPath, "*.xsl", SearchOption.AllDirectories)
                    .ToList();

                _logger.LogInformation("Encontrados {Count} arquivos XSL", xslFiles.Count);

                // Filtrar por layout se fornecido
                if (!string.IsNullOrEmpty(layoutName))
                {
                    var normalizedLayoutName = NormalizeLayoutName(layoutName);
                    xslFiles = xslFiles.Where(f =>
                    {
                        var fileName = Path.GetFileNameWithoutExtension(f);
                        var normalizedFileName = NormalizeLayoutName(fileName);
                        return normalizedFileName.Contains(normalizedLayoutName) || normalizedLayoutName.Contains(normalizedFileName);
                    }).ToList();
                }

                foreach (var xslFile in xslFiles)
                {
                    try
                    {
                        var xslContent = await File.ReadAllTextAsync(xslFile);

                        // Tentar encontrar arquivos relacionados (input XML e output XML)
                        var directory = Path.GetDirectoryName(xslFile);
                        var inputXml = Directory.GetFiles(directory, "input*.xml", SearchOption.TopDirectoryOnly)
                            .Concat(Directory.GetFiles(directory, "*input*.xml", SearchOption.TopDirectoryOnly)).FirstOrDefault();
                        var outputXml = Directory.GetFiles(directory, "output*.xml", SearchOption.TopDirectoryOnly)
                            .Concat(Directory.GetFiles(directory, "*output*.xml", SearchOption.TopDirectoryOnly)).FirstOrDefault();

                        var example = new XslExample
                        {
                            LayoutName = layoutName ?? Path.GetFileNameWithoutExtension(xslFile),
                            Content = xslContent,
                            InputXml = inputXml != null ? await File.ReadAllTextAsync(inputXml) : null,
                            OutputXml = outputXml != null ? await File.ReadAllTextAsync(outputXml) : null
                        };

                        examples.Add(example);
                        _logger.LogInformation("Exemplo XSL carregado: {File} ({Size} chars)", xslFile, xslContent.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao carregar exemplo XSL: {File}", xslFile);
                    }
                }

                _logger.LogInformation("Total de exemplos XSL carregados: {Count}", examples.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar exemplos XSL da pasta: {Path}", _examplesXslPath);
            }

            return examples;
        }

        /// <summary>
        /// Carrega exemplos XML esperados da pasta ExamplesPath baseado no nome do layout
        /// </summary>
        public async Task<List<XslExample>> LoadXmlExamplesAsync(string layoutName, string targetLayoutGuid = null)
        {
            var examples = new List<XslExample>();

            try
            {
                _logger.LogInformation("Carregando exemplos XML para layout: {LayoutName}", layoutName);

                if (!Directory.Exists(_examplesBasePath))
                {
                    _logger.LogWarning("Diretório de exemplos não encontrado: {Path}", _examplesBasePath);
                    return examples;
                }

                // Buscar diretórios que correspondem ao layout (por nome ou GUID)
                // Normalizar o nome do layout para comparação (remover prefixos, espaços, etc.)
                var normalizedLayoutName = NormalizeLayoutName(layoutName);

                var layoutDirs = Directory.GetDirectories(_examplesBasePath, "*", SearchOption.TopDirectoryOnly).Where(d =>
                    {
                        var dirName = Path.GetFileName(d);
                        var normalizedDirName = NormalizeLayoutName(dirName);

                        // Verificar se o nome do diretório contém o nome do layout ou GUID
                        // Comparação mais flexível: verificar se partes do nome correspondem
                        var layoutNameParts = normalizedLayoutName.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length > 3).ToList();

                        var dirNameParts = normalizedDirName.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length > 3).ToList();

                        // Verificar se há correspondência entre partes do nome
                        var matchingParts = layoutNameParts.Count(part => dirNameParts.Any(dirPart => dirPart.Contains(part, StringComparison.OrdinalIgnoreCase) || part.Contains(dirPart, StringComparison.OrdinalIgnoreCase)));

                        // Se pelo menos 2 partes correspondem, considerar como match
                        var isMatch = matchingParts >= 2 || dirName.Contains(layoutName, StringComparison.OrdinalIgnoreCase) || normalizedDirName.Contains(normalizedLayoutName, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(targetLayoutGuid) && dirName.Contains(targetLayoutGuid, StringComparison.OrdinalIgnoreCase));

                        if (isMatch)
                            _logger.LogInformation("Diretório de exemplo encontrado: {DirName} (match com layout: {LayoutName})", dirName, layoutName);

                        return isMatch;
                    }).ToList();

                _logger.LogInformation("Encontrados {Count} diretórios de exemplo para layout {LayoutName}", layoutDirs.Count, layoutName);

                // Se não encontrou diretórios específicos, buscar em todos os subdiretórios
                if (!layoutDirs.Any())
                {
                    _logger.LogInformation("Nenhum diretório específico encontrado. Buscando em todos os subdiretórios...");
                    layoutDirs = Directory.GetDirectories(_examplesBasePath, "*", SearchOption.AllDirectories).Take(20).ToList();
                }

                // Buscar arquivos XML em cada diretório
                foreach (var layoutDir in layoutDirs)
                {
                    var xmlFiles = Directory.GetFiles(layoutDir, "*.xml", SearchOption.AllDirectories).ToList();

                    _logger.LogInformation("Encontrados {Count} arquivos XML no diretório: {Dir}", xmlFiles.Count, layoutDir);

                    foreach (var xmlFile in xmlFiles)
                    {
                        try
                        {
                            var xmlContent = await File.ReadAllTextAsync(xmlFile);

                            // Verificar se é um XML válido
                            var xmlDoc = XDocument.Parse(xmlContent);

                            // Criar exemplo (sem InputXml e Content por enquanto, apenas OutputXml)
                            var example = new XslExample
                            {
                                LayoutName = layoutName,
                                OutputXml = xmlContent
                            };

                            examples.Add(example);
                            _logger.LogInformation("Exemplo XML carregado: {File} ({Size} chars)", xmlFile, xmlContent.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erro ao carregar exemplo XML: {File}", xmlFile);
                        }
                    }
                }

                // Se não encontrou exemplos específicos, buscar em todos os subdiretórios
                if (!examples.Any())
                {
                    _logger.LogInformation("Nenhum exemplo específico encontrado. Buscando em todos os subdiretórios...");

                    var allXmlFiles = Directory.GetFiles(_examplesBasePath, "*.xml", SearchOption.AllDirectories).Take(10).ToList();

                    foreach (var xmlFile in allXmlFiles)
                    {
                        try
                        {
                            var xmlContent = await File.ReadAllTextAsync(xmlFile);
                            var xmlDoc = XDocument.Parse(xmlContent);

                            var example = new XslExample
                            {
                                LayoutName = layoutName,
                                OutputXml = xmlContent
                            };

                            examples.Add(example);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erro ao carregar exemplo XML: {File}", xmlFile);
                        }
                    }
                }

                _logger.LogInformation("Total de exemplos XML carregados: {Count}", examples.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar exemplos XML para layout: {LayoutName}", layoutName);
            }

            return examples;
        }

        /// <summary>
        /// Carrega um exemplo XML específico baseado no nome do layout e arquivo
        /// </summary>
        public async Task<string> LoadExpectedOutputXmlAsync(string layoutName, string fileName = null)
        {
            try
            {
                if (!Directory.Exists(_examplesBasePath))
                {
                    _logger.LogWarning("Diretório de exemplos não encontrado: {Path}", _examplesBasePath);
                    return null;
                }

                // Buscar diretórios que correspondem ao layout
                var layoutDirs = Directory.GetDirectories(_examplesBasePath, "*", SearchOption.TopDirectoryOnly).Where(d => Path.GetFileName(d).Contains(layoutName, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var layoutDir in layoutDirs)
                {
                    // Se fileName foi especificado, buscar esse arquivo
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        var filePath = Path.Combine(layoutDir, fileName);
                        if (File.Exists(filePath))
                        {
                            var xmlContent = await File.ReadAllTextAsync(filePath);
                            _logger.LogInformation("Exemplo XML carregado: {File}", filePath);
                            return xmlContent;
                        }
                    }
                    else
                    {
                        // Buscar primeiro arquivo XML encontrado
                        var xmlFiles = Directory.GetFiles(layoutDir, "*.xml", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();

                        if (xmlFiles != null && File.Exists(xmlFiles))
                        {
                            var xmlContent = await File.ReadAllTextAsync(xmlFiles);
                            _logger.LogInformation("Exemplo XML carregado: {File}", xmlFiles);
                            return xmlContent;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar exemplo XML esperado para layout: {LayoutName}", layoutName);
            }

            return null;
        }

        /// <summary>
        /// Normaliza nome de layout para comparação (remove prefixos, espaços, etc.)
        /// </summary>
        private string NormalizeLayoutName(string layoutName)
        {
            if (string.IsNullOrEmpty(layoutName))
                return "";

            var normalized = layoutName.Trim();

            // Remover prefixos comuns
            if (normalized.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            if (normalized.StartsWith("MAP_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);

            // Converter para minúsculas e remover espaços extras
            normalized = normalized.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

            return normalized;
        }

        /// <summary>
        /// Salva modelo aprendido
        /// </summary>
        private async Task SaveLearnedModelAsync(string modelName, object model)
        {
            try
            {
                // ✅ SCS0018 (issue #88, achado real): apesar do baseline anterior descrever
                // modelName como "definido internamente", os dois chamadores reais (LearnTclPatternsAsync/
                // LearnXslPatternsAsync) montam modelName como $"tcl_{layoutName}"/$"xsl_{layoutName}",
                // e layoutName chega cru da requisição (TransformationExecutionController). Path traversal
                // aqui seria pior que leitura — é File.WriteAllTextAsync (escrita arbitrária). Fechado
                // com SafePathResolver, mesmo padrão do projeto.
                var modelPath = SafePathResolver.Resolve(_learningModelsPath, $"{modelName}.json");
                if (modelPath == null)
                {
                    _logger.LogWarning("Nome de modelo rejeitado para gravação: {ModelName}", LogMessageSanitizer.Sanitize(modelName));
                    return;
                }

                var json = System.Text.Json.JsonSerializer.Serialize(model, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
#pragma warning disable SCS0018
                await File.WriteAllTextAsync(modelPath, json);
#pragma warning restore SCS0018
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar modelo aprendido");
            }
        }

        /// <summary>
        /// Parseia linhas do TCL e extrai informações estruturadas
        /// </summary>
        private List<TclLineInfo> ParseTclLines(string tclContent)
        {
            var lines = new List<TclLineInfo>();

            try
            {
                var doc = XDocument.Parse(tclContent);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();
                if (mapElement == null)
                    return lines;

                var lineElements = mapElement.Elements("LINE").ToList();

                foreach (var lineElement in lineElements)
                {
                    var lineInfo = new TclLineInfo
                    {
                        LineType = lineElement.Attribute("identifier")?.Value ?? lineElement.Attribute("name")?.Value ?? "UNKNOWN",
                        Structure = ExtractLineStructure(lineElement),
                        FieldCount = lineElement.Elements("FIELD").Count(),
                        Attributes = lineElement.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value)
                    };

                    lines.Add(lineInfo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao parsear linhas do TCL");
            }

            return lines;
        }

        /// <summary>
        /// Extrai estrutura de uma linha TCL
        /// </summary>
        private string ExtractLineStructure(XElement lineElement)
        {
            var fields = lineElement.Elements("FIELD").Select(f => $"{f.Attribute("name")?.Value}:{f.Attribute("length")?.Value}").ToList();

            return string.Join("|", fields);
        }

        /// <summary>
        /// Parseia campos do TCL e extrai mapeamentos
        /// </summary>
        private List<TclFieldInfo> ParseTclFields(string tclContent)
        {
            var fields = new List<TclFieldInfo>();

            try
            {
                var doc = XDocument.Parse(tclContent);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();
                if (mapElement == null)
                    return fields;

                var lineElements = mapElement.Elements("LINE").ToList();

                foreach (var lineElement in lineElements)
                {
                    var lineName = lineElement.Attribute("identifier")?.Value ?? lineElement.Attribute("name")?.Value;

                    var fieldElements = lineElement.Elements("FIELD").ToList();
                    var currentPosition = 0;

                    foreach (var fieldElement in fieldElements)
                    {
                        var fieldName = fieldElement.Attribute("name")?.Value;
                        var lengthAttr = fieldElement.Attribute("length")?.Value;
                        var startPos = fieldElement.Attribute("startPosition")?.Value ?? currentPosition.ToString();

                        if (!string.IsNullOrEmpty(fieldName) && !string.IsNullOrEmpty(lengthAttr))
                        {
                            var fieldInfo = new TclFieldInfo
                            {
                                FieldType = DetermineFieldType(fieldName, lengthAttr),
                                Mapping = $"{lineName}.{fieldName}",
                                StartPosition = int.TryParse(startPos, out var pos) ? pos : currentPosition,
                                Length = ParseLength(lengthAttr),
                                FieldName = fieldName,
                                LineName = lineName
                            };

                            fields.Add(fieldInfo);
                            currentPosition = fieldInfo.StartPosition + fieldInfo.Length;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao parsear campos do TCL");
            }

            return fields;
        }

        /// <summary>
        /// Determina o tipo de campo baseado no nome e comprimento
        /// </summary>
        private string DetermineFieldType(string fieldName, string length)
        {
            var nameLower = fieldName.ToLower();

            // Detectar tipos comuns
            if (nameLower.Contains("data") || nameLower.Contains("date"))
                return "DATE";
            if (nameLower.Contains("hora") || nameLower.Contains("time"))
                return "TIME";
            if (nameLower.Contains("valor") || nameLower.Contains("value") || nameLower.Contains("preco"))
                return "DECIMAL";
            if (nameLower.Contains("quantidade") || nameLower.Contains("qtd"))
                return "DECIMAL";
            if (nameLower.Contains("cnpj") || nameLower.Contains("cpf"))
                return "CNPJ_CPF";
            if (nameLower.Contains("codigo") || nameLower.Contains("id"))
                return "STRING";
            if (length.Contains(","))
                return "DECIMAL"; // Decimal tem formato "15,2,0"

            return "STRING";
        }

        /// <summary>
        /// Parseia comprimento do campo (pode ser "60" ou "15,2,0")
        /// </summary>
        private int ParseLength(string lengthAttr)
        {
            if (string.IsNullOrEmpty(lengthAttr))
                return 0;

            var parts = lengthAttr.Split(',');
            if (int.TryParse(parts[0], out var length))
                return length;

            return 0;
        }

        /// <summary>
        /// Parseia templates XSL e extrai estruturas
        /// </summary>
        private List<XslTemplateInfo> ParseXslTemplates(string xslContent)
        {
            var templates = new List<XslTemplateInfo>();

            try
            {
                var doc = XDocument.Parse(xslContent);
                var ns = XNamespace.Get("http://www.w3.org/1999/XSL/Transform");

                var templateElements = doc.Descendants(ns + "template").ToList();

                foreach (var templateElement in templateElements)
                {
                    var match = templateElement.Attribute("match")?.Value;
                    var name = templateElement.Attribute("name")?.Value;

                    var templateInfo = new XslTemplateInfo
                    {
                        TemplateName = name ?? match ?? "UNNAMED",
                        TemplateStructure = ExtractTemplateStructure(templateElement, ns),
                        MatchPattern = match,
                        HasApplyTemplates = templateElement.Descendants(ns + "apply-templates").Any(),
                        HasForEach = templateElement.Descendants(ns + "for-each").Any(),
                        HasChoose = templateElement.Descendants(ns + "choose").Any(),
                        ElementCount = templateElement.Descendants().Count()
                    };

                    templates.Add(templateInfo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao parsear templates XSL");
            }

            return templates;
        }

        /// <summary>
        /// Extrai estrutura de um template XSL
        /// </summary>
        private string ExtractTemplateStructure(XElement templateElement, XNamespace ns)
        {
            var structure = new List<string>();

            // Extrair elementos principais
            var valueOfs = templateElement.Descendants(ns + "value-of").Select(e => $"value-of:{e.Attribute("select")?.Value}").ToList();
            structure.AddRange(valueOfs);

            var elements = templateElement.Descendants().Where(e => e.Name.Namespace != ns && e.Parent == templateElement).Select(e => $"element:{e.Name.LocalName}").ToList();
            structure.AddRange(elements);

            var attributes = templateElement.Descendants(ns + "attribute").Select(e => $"attr:{e.Attribute("name")?.Value}").ToList();
            structure.AddRange(attributes);

            return string.Join("|", structure);
        }

        /// <summary>
        /// Parseia transformações XSL e extrai padrões
        /// </summary>
        private List<XslTransformInfo> ParseXslTransforms(string xslContent)
        {
            var transforms = new List<XslTransformInfo>();

            try
            {
                var doc = XDocument.Parse(xslContent);
                var ns = XNamespace.Get("http://www.w3.org/1999/XSL/Transform");

                // Extrair value-of transforms
                var valueOfs = doc.Descendants(ns + "value-of").ToList();
                foreach (var valueOf in valueOfs)
                {
                    var select = valueOf.Attribute("select")?.Value;
                    if (!string.IsNullOrEmpty(select))
                    {
                        transforms.Add(new XslTransformInfo
                        {
                            TransformType = "value-of",
                            TransformPattern = select,
                            SourceXPath = ExtractXPathFromSelect(select),
                            ParentElement = valueOf.Parent?.Name.LocalName ?? "UNKNOWN"
                        });
                    }
                }

                // Extrair for-each transforms
                var forEachs = doc.Descendants(ns + "for-each").ToList();
                foreach (var forEach in forEachs)
                {
                    var select = forEach.Attribute("select")?.Value;
                    if (!string.IsNullOrEmpty(select))
                    {
                        transforms.Add(new XslTransformInfo
                        {
                            TransformType = "for-each",
                            TransformPattern = select,
                            SourceXPath = ExtractXPathFromSelect(select),
                            ParentElement = forEach.Parent?.Name.LocalName ?? "UNKNOWN"
                        });
                    }
                }

                // Extrair if conditions
                var ifs = doc.Descendants(ns + "if").ToList();
                foreach (var ifElement in ifs)
                {
                    var test = ifElement.Attribute("test")?.Value;
                    if (!string.IsNullOrEmpty(test))
                    {
                        transforms.Add(new XslTransformInfo
                        {
                            TransformType = "if",
                            TransformPattern = test,
                            SourceXPath = ExtractXPathFromSelect(test),
                            ParentElement = ifElement.Parent?.Name.LocalName ?? "UNKNOWN"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao parsear transformações XSL");
            }

            return transforms;
        }

        /// <summary>
        /// Extrai XPath de uma expressão select/test
        /// </summary>
        private string ExtractXPathFromSelect(string select)
        {
            if (string.IsNullOrEmpty(select))
                return "";

            // Remover funções e manter apenas o caminho
            var xpath = select;

            // Remover funções comuns
            xpath = System.Text.RegularExpressions.Regex.Replace(xpath, @"\w+\(([^)]+)\)", "$1");

            // Normalizar
            xpath = xpath.Trim().TrimStart('/');

            return xpath;
        }

        /// <summary>
        /// Obtém XPath de um elemento XML
        /// </summary>
        private string GetXPath(XElement element)
        {
            if (element == null)
                return "";

            try
            {
                var ancestors = element.Ancestors().Reverse();
                var path = string.Join("/", ancestors.Select(e => e.Name.LocalName));
                if (!string.IsNullOrEmpty(path))
                    return $"/{path}/{element.Name.LocalName}";

                return $"/{element.Name.LocalName}";
            }
            catch
            {
                return element?.Name.LocalName ?? "";
            }
        }
    }
}