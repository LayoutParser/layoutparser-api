using LayoutParserApi.Services.Storage;
using LayoutParserApi.Services.XmlAnalysis.Models;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Transforma documentos MQSeries/IDOC em XML NFe para validação XSD
    /// Agora usa o pipeline de transformação: TXT → MAP → XML Intermediário → XSL → XML Final
    /// </summary>
    public class MqSeriesToXmlTransformer
    {
        private readonly ILogger<MqSeriesToXmlTransformer> _logger;
        private readonly string _transformationRulesPath;
        private readonly TransformationPipelineService _pipelineService;

        public MqSeriesToXmlTransformer(
            ILogger<MqSeriesToXmlTransformer> logger,
            IConfiguration configuration,
            TransformationPipelineService pipelineService,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _transformationRulesPath = (storagePaths ?? new StoragePaths(configuration)).TransformationRules;
            _pipelineService = pipelineService;
        }

        /// <summary>
        /// Transforma conteúdo MQSeries/IDOC em XML NFe usando pipeline: TXT → MAP → XSL → XML
        /// </summary>
        public async Task<TransformationResult> TransformToXmlAsync(string mqseriesContent, string layoutName, string transformationRulesXml = null)
        {
            var result = new TransformationResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                SegmentMappings = new Dictionary<int, SegmentMapping>()
            };

            try
            {
                _logger.LogInformation("Iniciando transformação MQSeries/IDOC para XML usando pipeline. Layout: {LayoutName}", layoutName);

                // Usar pipeline de transformação: TXT → MAP → XML Intermediário → XSL → XML Final
                var pipelineResult = await _pipelineService.TransformTxtToXmlAsync(mqseriesContent, layoutName, "NFe");

                if (!pipelineResult.Success)
                {
                    result.Success = false;
                    result.Errors.AddRange(pipelineResult.Errors);
                    result.Warnings.AddRange(pipelineResult.Warnings);
                    return result;
                }

                result.TransformedXml = pipelineResult.TransformedXml;
                result.Success = true;
                result.Warnings.AddRange(pipelineResult.Warnings);

                // Mapear segmentos se disponível
                foreach (var mapping in pipelineResult.SegmentMappings)
                {
                    result.SegmentMappings[mapping.Key] = new SegmentMapping
                    {
                        MqSeriesLineNumber = mapping.Key,
                        MqSeriesSegment = mapping.Value,
                        XmlElementPath = "NFe/infNFe",
                        XmlElement = null
                    };
                }

                _logger.LogInformation("Transformação pipeline concluída. Sucesso: {Success}", result.Success);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação MQSeries para XML");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }
    }
}