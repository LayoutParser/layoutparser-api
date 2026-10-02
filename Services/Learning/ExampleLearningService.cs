using LayoutParserApi.Services.Storage;
using LayoutParserApi.Services.Learning.Models;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Serviço para aprender a partir de exemplos TCL e XSL existentes
    /// </summary>
    public class ExampleLearningService
    {
        private readonly ILogger<ExampleLearningService> _logger;
        private readonly TransformationLearningService _learningService;
        private readonly string _examplesBasePath;

        public ExampleLearningService(ILogger<ExampleLearningService> logger,TransformationLearningService learningService,IConfiguration configuration, StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _learningService = learningService;
            _examplesBasePath = (storagePaths ?? new StoragePaths(configuration)).LearningExamples;

            if (!Directory.Exists(_examplesBasePath))
                _logger.LogWarning("Diretório de exemplos de aprendizado não encontrado: {Path}", _examplesBasePath);
        }

        /// <summary>
        /// Aprende a partir de todos os exemplos TCL e XSL disponíveis
        /// </summary>
        public async Task<LearningBatchResult> LearnFromAllExamplesAsync()
        {
            var result = new LearningBatchResult
            {
                Success = true,
                LearnedLayouts = new List<string>(),
                Errors = new List<string>(),
                StartTime = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation("Iniciando aprendizado a partir de exemplos TCL e XSL");

                if (!Directory.Exists(_examplesBasePath))
                {
                    result.Errors.Add($"Diretório não encontrado: {_examplesBasePath}");
                    result.Success = false;
                    return result;
                }

                // Buscar todos os arquivos TCL e XSL
                var tclFiles = Directory.GetFiles(_examplesBasePath, "*.tcl", SearchOption.AllDirectories).ToList();
                var xslFiles = Directory.GetFiles(_examplesBasePath, "*.xsl", SearchOption.AllDirectories).ToList();

                _logger.LogInformation("Encontrados {TclCount} arquivos TCL e {XslCount} arquivos XSL",tclFiles.Count, xslFiles.Count);

                // Agrupar TCL e XSL por layout (baseado no nome do arquivo ou diretório)
                var tclGroups = GroupFilesByLayout(tclFiles);
                var xslGroups = GroupFilesByLayout(xslFiles);

                // Aprender padrões TCL
                foreach (var group in tclGroups)
                {
                    try
                    {
                        var layoutName = group.Key;
                        var tclExamples = new List<TclExample>();

                        foreach (var filePath in group.Value)
                        {
                            var tclContent = await File.ReadAllTextAsync(filePath);

                            // Tentar encontrar arquivos relacionados (input TXT e output XML)
                            var inputTxt = await FindRelatedFileAsync(filePath, "*.txt");
                            var outputXml = await FindRelatedFileAsync(filePath, "*.xml");

                            tclExamples.Add(new TclExample
                            {
                                LayoutName = layoutName,
                                Content = tclContent,
                                InputTxt = inputTxt,
                                OutputXml = outputXml
                            });
                        }

                        if (tclExamples.Any())
                        {
                            var learningResult = await _learningService.LearnTclPatternsAsync(layoutName, tclExamples);
                            if (learningResult.Success)
                            {
                                result.LearnedLayouts.Add($"TCL: {layoutName}");
                                _logger.LogInformation("Aprendizado TCL concluído para: {LayoutName}. Padrões: {Count}",layoutName, learningResult.PatternsLearned.Count);
                            }
                            else
                                result.Errors.AddRange(learningResult.Errors);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao aprender padrões TCL para layout: {LayoutName}", group.Key);
                        result.Errors.Add($"Erro ao aprender TCL para {group.Key}: {ex.Message}");
                    }
                }

                // Aprender padrões XSL
                foreach (var group in xslGroups)
                {
                    try
                    {
                        var layoutName = group.Key;
                        var xslExamples = new List<XslExample>();

                        foreach (var filePath in group.Value)
                        {
                            var xslContent = await File.ReadAllTextAsync(filePath);

                            // Tentar encontrar arquivos relacionados (input XML e output XML)
                            var inputXml = await FindRelatedFileAsync(filePath, "input*.xml");
                            var outputXml = await FindRelatedFileAsync(filePath, "output*.xml");

                            xslExamples.Add(new XslExample
                            {
                                LayoutName = layoutName,
                                Content = xslContent,
                                InputXml = inputXml,
                                OutputXml = outputXml
                            });
                        }

                        if (xslExamples.Any())
                        {
                            var learningResult = await _learningService.LearnXslPatternsAsync(layoutName, xslExamples);
                            if (learningResult.Success)
                            {
                                result.LearnedLayouts.Add($"XSL: {layoutName}");
                                _logger.LogInformation("Aprendizado XSL concluído para: {LayoutName}. Padrões: {Count}",layoutName, learningResult.PatternsLearned.Count);
                            }
                            else
                                result.Errors.AddRange(learningResult.Errors);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao aprender padrões XSL para layout: {LayoutName}", group.Key);
                        result.Errors.Add($"Erro ao aprender XSL para {group.Key}: {ex.Message}");
                    }
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Success = result.Errors.Count == 0;

                _logger.LogInformation("Aprendizado concluído. Layouts aprendidos: {Count}", result.LearnedLayouts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar aprendizado a partir de exemplos");
                result.Success = false;
                result.Errors.Add($"Erro geral: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Agrupa arquivos por layout (baseado no nome do arquivo ou diretório)
        /// </summary>
        private Dictionary<string, List<string>> GroupFilesByLayout(List<string> filePaths)
        {
            var groups = new Dictionary<string, List<string>>();

            foreach (var filePath in filePaths)
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var directoryName = Path.GetDirectoryName(filePath);
                var layoutName = ExtractLayoutName(fileName, directoryName);

                if (!groups.ContainsKey(layoutName))
                    groups[layoutName] = new List<string>();

                groups[layoutName].Add(filePath);
            }

            return groups;
        }

        /// <summary>
        /// Extrai nome do layout do nome do arquivo ou diretório
        /// </summary>
        private string ExtractLayoutName(string fileName, string directoryName)
        {
            // Tentar extrair do nome do arquivo (ex: "LAY_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl")
            if (fileName.StartsWith("LAY_"))
                return fileName;

            // Tentar extrair do diretório (ex: "C:\...\LAY_TXT_MQSERIES_ENVNFE_4.00_NFe\...")
            if (!string.IsNullOrEmpty(directoryName))
            {
                var dirName = Path.GetFileName(directoryName);
                if (dirName.StartsWith("LAY_"))
                    return dirName;
            }

            // Fallback: usar nome do arquivo
            return fileName;
        }

        /// <summary>
        /// Encontra arquivo relacionado no mesmo diretório
        /// </summary>
        private async Task<string> FindRelatedFileAsync(string filePath, string pattern)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);

                if (files.Any())
                    return await File.ReadAllTextAsync(files.First());
            }
            catch { }

            return null;
        }
    }
}