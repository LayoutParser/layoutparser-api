using LayoutParserApi.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Models.Database;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Text;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Security;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Métricas de aprendizado (padrões TCL/XSL aprendidos) e de qualidade (cobertura de campos
    /// do layout pelos artefatos TCL/XSL já gerados em disco) — dashboard/diagnóstico, não
    /// afeta o pathway de transformação em runtime.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class MetricsController : ControllerBase
    {
        private readonly ILogger<MetricsController> _logger;
        private readonly TransformationLearningService _learningService;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly TransformationValidatorService _validatorService;
        private readonly IConfiguration _configuration;
        private readonly string _tclBasePath;
        private readonly string _xslBasePath;

        public MetricsController(
            ILogger<MetricsController> logger,
            TransformationLearningService learningService,
            ICachedLayoutService cachedLayoutService,
            TransformationValidatorService validatorService,
            IConfiguration configuration,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _learningService = learningService;
            _cachedLayoutService = cachedLayoutService;
            _validatorService = validatorService;
            _configuration = configuration;

            _tclBasePath = (storagePaths ?? new StoragePaths(configuration)).Tcl;
            _xslBasePath = (storagePaths ?? new StoragePaths(configuration)).Xsl;
        }

        /// <summary>Métricas do modelo de aprendizado (padrões/exemplos/confiança) de TCL e XSL para um layout.</summary>
        /// <response code="200">Métricas (campos <c>tclMetrics</c>/<c>xslMetrics</c> nulos se o layout nunca foi treinado).</response>
        /// <response code="500">Falha não catalogada.</response>
        [HttpGet("learning/{layoutName}")]
        public async Task<IActionResult> GetLearningMetrics(string layoutName)
        {
            try
            {
                _logger.LogInformation("Buscando métricas de aprendizado para layout: {LayoutName}", layoutName);

                // Carregar modelos aprendidos
                var tclModel = await _learningService.LoadTclModelAsync(layoutName);
                var xslModel = await _learningService.LoadXslModelAsync(layoutName);

                var metrics = new
                {
                    layoutName = layoutName,
                    tclMetrics = tclModel != null ? new
                    {
                        examplesCount = tclModel.ExamplesCount,
                        patternsCount = tclModel.Patterns.Count,
                        mappingRulesCount = tclModel.MappingRules.Count,
                        learnedAt = tclModel.LearnedAt,
                        lastUpdatedAt = tclModel.LastUpdatedAt,
                        averageConfidence = tclModel.Patterns.Any() 
                            ? tclModel.Patterns.Average(p => p.Confidence) 
                            : 0.0,
                        patternsByType = tclModel.Patterns
                            .GroupBy(p => p.Type)
                            .Select(g => new
                            {
                                type = g.Key,
                                count = g.Count(),
                                averageConfidence = g.Average(p => p.Confidence)
                            })
                            .ToList()
                    } : null,
                    xslMetrics = xslModel != null ? new
                    {
                        examplesCount = xslModel.ExamplesCount,
                        patternsCount = xslModel.Patterns.Count,
                        transformationRulesCount = xslModel.TransformationRules.Count,
                        learnedAt = xslModel.LearnedAt,
                        lastUpdatedAt = xslModel.LastUpdatedAt,
                        averageConfidence = xslModel.Patterns.Any() 
                            ? xslModel.Patterns.Average(p => p.Confidence) 
                            : 0.0,
                        patternsByType = xslModel.Patterns
                            .GroupBy(p => p.Type)
                            .Select(g => new
                            {
                                type = g.Key,
                                count = g.Count(),
                                averageConfidence = g.Average(p => p.Confidence)
                            })
                            .ToList()
                    } : null
                };

                return Ok(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar métricas de aprendizado");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Resumo agregado de aprendizado entre todos os layouts — soma os modelos TCL/XSL já
        /// persistidos em disco (<c>TransformationPipeline:LearningModelsPath</c>).
        /// </summary>
        /// <response code="200">Resumo agregado (zerado se nenhum layout foi treinado ainda, ou se a leitura falhar — degrade gracioso).</response>
        [HttpGet("learning/summary")]
        public async Task<IActionResult> GetLearningSummary()
        {
            try
            {
                _logger.LogInformation("Buscando resumo de métricas de aprendizado");

                // Fonte de dado: arquivos tcl_*.json/xsl_*.json em LearningModelsPath — mesmo
                // local usado por GetLearningMetrics (LoadTclModelAsync/LoadXslModelAsync), não
                // há SQL/cache para modelos aprendidos.
                var aggregate = await _learningService.GetLearningSummaryAsync();

                var summary = new
                {
                    totalModels = aggregate.TotalModels,
                    totalPatterns = aggregate.TotalPatterns,
                    totalExamples = aggregate.TotalExamples,
                    // Média simples entre TODOS os padrões de TODOS os modelos — não há uma métrica
                    // de "confiança real" única no pipeline hoje, apenas a confiança por padrão já
                    // calculada no treino individual (ver GetLearningMetrics).
                    averageConfidence = aggregate.AverageConfidence
                };

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar resumo de métricas");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Compara os artefatos TCL/XSL já gerados em disco contra os campos declarados no layout
        /// (Redis) e calcula cobertura + score de qualidade geral.
        /// </summary>
        /// <response code="200">Métricas de qualidade calculadas.</response>
        /// <response code="404">Layout, XML do layout, TCL ou XSL não encontrado.</response>
        /// <response code="500">Falha não catalogada.</response>
        [HttpGet("quality/{layoutName}")]
        public async Task<IActionResult> GetQualityMetrics(string layoutName)
        {
            try
            {
                _logger.LogInformation("Buscando métricas de qualidade para layout: {LayoutName}", layoutName);

                // 1. Buscar layout do Redis
                var layoutRequest = new LayoutSearchRequest
                {
                    SearchTerm = layoutName,
                    MaxResults = 1
                };
                var layoutResponse = await _cachedLayoutService.SearchLayoutsAsync(layoutRequest);

                if (!layoutResponse.Success || !layoutResponse.Layouts.Any())
                {
                    return NotFound(new { error = $"Layout '{layoutName}' não encontrado" });
                }

                var layout = layoutResponse.Layouts.First();
                
                // 2. Carregar layout XML (usar DecryptedContent ou ValueContent)
                var layoutXml = layout.DecryptedContent ?? layout.ValueContent;
                
                if (string.IsNullOrEmpty(layoutXml))
                {
                    // Tentar obter do banco de dados se não estiver no cache
                    var layoutDatabaseService = _cachedLayoutService.GetLayoutDatabaseService();
                    var layoutFromDb = await layoutDatabaseService.GetLayoutByIdAsync(layout.Id);
                    if (layoutFromDb != null)
                    {
                        layoutXml = layoutFromDb.DecryptedContent ?? layoutFromDb.ValueContent;
                    }
                }
                
                if (string.IsNullOrEmpty(layoutXml))
                {
                    return NotFound(new { error = $"Layout XML não encontrado para '{layoutName}'" });
                }

                // 3. Parse do layout XML (similar ao LayoutParserService)
                var layoutDoc = XDocument.Parse(layoutXml);
                var layoutElements = layoutDoc.Descendants("Element")
                    .Where(e => e.Element("FieldElements") != null)
                    .SelectMany(e => e.Element("FieldElements").Elements("FieldElement"))
                    .ToList();

                var totalFieldsInLayout = layoutElements.Count;
                var requiredFieldsInLayout = layoutElements
                    .Where(f => f.Element("IsRequired")?.Value == "true")
                    .Count();

                // 4. Verificar se TCL existe e fazer parse
                // ✅ P0 — path traversal: layoutName vem da rota (cliente). Mesmo helper único do
                // DocumentController — SanitizeFileName (saneamento) trocado por validação estrita.
                // null = nome recusado → 404 (não revela existência de arquivo fora da base).
                var tclPath = SafePathResolver.Resolve(_tclBasePath, $"{layoutName}.tcl");
                if (tclPath is null)
                    return NotFound(new { error = $"Layout '{layoutName}' não encontrado" });
                var tclMetrics = await AnalyzeTclQualityAsync(tclPath, layoutElements, layoutName);

                // 5. Verificar se XSL existe e fazer parse
                var xslPath = SafePathResolver.Resolve(_xslBasePath, $"{layoutName}.xsl");
                if (xslPath is null)
                    return NotFound(new { error = $"Layout '{layoutName}' não encontrado" });
                var xslMetrics = await AnalyzeXslQualityAsync(xslPath, layoutName);

                // 6. Calcular métricas gerais
                var overallQuality = CalculateOverallQuality(tclMetrics, xslMetrics, totalFieldsInLayout, requiredFieldsInLayout);

                var metrics = new
                {
                    layoutName = layoutName,
                    layoutGuid = layout.LayoutGuid,
                    totalFieldsInLayout = totalFieldsInLayout,
                    requiredFieldsInLayout = requiredFieldsInLayout,
                    tclMetrics = tclMetrics,
                    xslMetrics = xslMetrics,
                    overallQuality = overallQuality,
                    generatedAt = DateTime.UtcNow
                };

                return Ok(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar métricas de qualidade");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Analisa qualidade do TCL gerado
        /// </summary>
        private async Task<object> AnalyzeTclQualityAsync(string tclPath, List<XElement> layoutFields, string layoutName)
        {
            var metrics = new
            {
                exists = false,
                isValid = false,
                fieldsCoverage = 0.0,
                requiredFieldsCoverage = 0.0,
                totalLines = 0,
                totalFields = 0,
                mappedFields = 0,
                unmappedFields = new List<string>(),
                errors = new List<string>()
            };

            if (!System.IO.File.Exists(tclPath))
            {
                return metrics;
            }

            try
            {
                // SCS0018 (issue #88): falso positivo — tclPath já saiu sanitizado de
                // SafePathResolver.Resolve(_tclBasePath, ...) no chamador; SCS não reconhece o helper.
#pragma warning disable SCS0018
                var tclContent = await System.IO.File.ReadAllTextAsync(tclPath, Encoding.UTF8);
#pragma warning restore SCS0018
                
                // Parse do TCL (XML)
                var tclDoc = XDocument.Parse(tclContent);
                metrics = new
                {
                    exists = true,
                    isValid = true,
                    fieldsCoverage = CalculateTclFieldsCoverage(tclDoc, layoutFields),
                    requiredFieldsCoverage = CalculateTclRequiredFieldsCoverage(tclDoc, layoutFields),
                    totalLines = tclDoc.Descendants("LINE").Count(),
                    totalFields = tclDoc.Descendants("FIELD").Count(),
                    mappedFields = tclDoc.Descendants("FIELD").Count(),
                    unmappedFields = FindUnmappedFields(tclDoc, layoutFields),
                    errors = new List<string>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar TCL: {Path}", tclPath);
                return new
                {
                    exists = true,
                    isValid = false,
                    fieldsCoverage = 0.0,
                    requiredFieldsCoverage = 0.0,
                    totalLines = 0,
                    totalFields = 0,
                    mappedFields = 0,
                    unmappedFields = new List<string>(),
                    errors = new List<string> { ex.Message }
                };
            }

            return metrics;
        }

        /// <summary>
        /// Analisa qualidade do XSL gerado
        /// </summary>
        private async Task<object> AnalyzeXslQualityAsync(string xslPath, string layoutName)
        {
            var metrics = new
            {
                exists = false,
                isValid = false,
                totalTemplates = 0,
                totalTransforms = 0,
                errors = new List<string>()
            };

            if (!System.IO.File.Exists(xslPath))
            {
                return metrics;
            }

            try
            {
                // SCS0018 (issue #88): mesmo falso positivo — xslPath já saiu sanitizado de
                // SafePathResolver.Resolve(_xslBasePath, ...) no chamador.
#pragma warning disable SCS0018
                var xslContent = await System.IO.File.ReadAllTextAsync(xslPath, Encoding.UTF8);
#pragma warning restore SCS0018
                
                // Parse do XSL
                var xslDoc = XDocument.Parse(xslContent);
                XNamespace xsl = "http://www.w3.org/1999/XSL/Transform";
                
                metrics = new
                {
                    exists = true,
                    isValid = true,
                    totalTemplates = xslDoc.Descendants(xsl + "template").Count(),
                    totalTransforms = xslDoc.Descendants(xsl + "value-of").Count() + 
                                     xslDoc.Descendants(xsl + "apply-templates").Count(),
                    errors = new List<string>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar XSL: {Path}", xslPath);
                return new
                {
                    exists = true,
                    isValid = false,
                    totalTemplates = 0,
                    totalTransforms = 0,
                    errors = new List<string> { ex.Message }
                };
            }

            return metrics;
        }

        /// <summary>
        /// Calcula cobertura de campos no TCL
        /// </summary>
        private double CalculateTclFieldsCoverage(XDocument tclDoc, List<XElement> layoutFields)
        {
            if (layoutFields.Count == 0) return 0.0;

            var tclFieldNames = tclDoc.Descendants("FIELD")
                .Select(f => f.Element("NAME")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            var layoutFieldNames = layoutFields
                .Select(f => f.Element("Name")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            var mappedCount = layoutFieldNames.Count(fn => tclFieldNames.Any(tfn => 
                tfn.Equals(fn, StringComparison.OrdinalIgnoreCase) || 
                tfn.Contains(fn, StringComparison.OrdinalIgnoreCase)));

            return layoutFieldNames.Count > 0 ? (double)mappedCount / layoutFieldNames.Count : 0.0;
        }

        /// <summary>
        /// Calcula cobertura de campos obrigatórios no TCL
        /// </summary>
        private double CalculateTclRequiredFieldsCoverage(XDocument tclDoc, List<XElement> layoutFields)
        {
            var requiredFields = layoutFields
                .Where(f => f.Element("IsRequired")?.Value == "true")
                .Select(f => f.Element("Name")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            if (requiredFields.Count == 0) return 1.0;

            var tclFieldNames = tclDoc.Descendants("FIELD")
                .Select(f => f.Element("NAME")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            var mappedCount = requiredFields.Count(fn => tclFieldNames.Any(tfn => 
                tfn.Equals(fn, StringComparison.OrdinalIgnoreCase) || 
                tfn.Contains(fn, StringComparison.OrdinalIgnoreCase)));

            return (double)mappedCount / requiredFields.Count;
        }

        /// <summary>
        /// Encontra campos não mapeados no TCL
        /// </summary>
        private List<string> FindUnmappedFields(XDocument tclDoc, List<XElement> layoutFields)
        {
            var tclFieldNames = tclDoc.Descendants("FIELD")
                .Select(f => f.Element("NAME")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            var layoutFieldNames = layoutFields
                .Select(f => f.Element("Name")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            var unmapped = layoutFieldNames
                .Where(fn => !tclFieldNames.Any(tfn => 
                    tfn.Equals(fn, StringComparison.OrdinalIgnoreCase) || 
                    tfn.Contains(fn, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return unmapped;
        }

        /// <summary>
        /// Calcula qualidade geral
        /// </summary>
        private object CalculateOverallQuality(object tclMetrics, object xslMetrics, int totalFields, int requiredFields)
        {
            dynamic tcl = tclMetrics;
            dynamic xsl = xslMetrics;

            var tclScore = tcl.exists && tcl.isValid ? (tcl.fieldsCoverage * 0.5 + tcl.requiredFieldsCoverage * 0.5) : 0.0;
            var xslScore = xsl.exists && xsl.isValid ? 1.0 : 0.0; // XSL é binário: existe e é válido ou não

            var overallScore = (tclScore * 0.7 + xslScore * 0.3); // TCL tem peso maior
            var qualityLevel = overallScore >= 0.9 ? "Excellent" :
                              overallScore >= 0.7 ? "Good" :
                              overallScore >= 0.5 ? "Fair" :
                              "Poor";

            return new
            {
                score = overallScore,
                level = qualityLevel,
                tclContribution = tclScore,
                xslContribution = xslScore
            };
        }

    }
}

