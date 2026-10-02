using LayoutParserApi.Services.Storage;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis.Models;

using System.Text;
using System.Xml.Linq;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Serviço para gerar automaticamente TCL e XSL baseado em layouts do Redis e mapeadores do banco
    /// </summary>
    public class AutoTransformationGeneratorService
    {
        private readonly ILogger<AutoTransformationGeneratorService> _logger;
        private readonly TclGeneratorService _tclGenerator;
        private readonly XslGeneratorService _xslGenerator;
        private readonly ImprovedTclGeneratorService _improvedTclGenerator;
        private readonly ImprovedXslGeneratorService _improvedXslGenerator;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly MapperDatabaseService _mapperDatabaseService;
        private readonly TransformationLearningService _learningService;
        private readonly string _tclBasePath;
        private readonly string _xslBasePath;

        /// <summary>
        /// Expõe LayoutDatabaseService para uso externo
        /// </summary>
        public ILayoutDatabaseService GetLayoutDatabaseService() => _cachedLayoutService.GetLayoutDatabaseService();

        public AutoTransformationGeneratorService(
            ILogger<AutoTransformationGeneratorService> logger,
            TclGeneratorService tclGenerator,
            XslGeneratorService xslGenerator,
            ImprovedTclGeneratorService improvedTclGenerator,
            ImprovedXslGeneratorService improvedXslGenerator,
            ICachedLayoutService cachedLayoutService,
            MapperDatabaseService mapperDatabaseService,
            TransformationLearningService learningService,
            IConfiguration configuration,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _tclGenerator = tclGenerator;
            _xslGenerator = xslGenerator;
            _improvedTclGenerator = improvedTclGenerator;
            _improvedXslGenerator = improvedXslGenerator;
            _cachedLayoutService = cachedLayoutService;
            _mapperDatabaseService = mapperDatabaseService;
            _learningService = learningService;

            _tclBasePath = (storagePaths ?? new StoragePaths(configuration)).Tcl;
            _xslBasePath = (storagePaths ?? new StoragePaths(configuration)).Xsl;

            // Garantir que os diretórios existam
            Directory.CreateDirectory(_tclBasePath);
            Directory.CreateDirectory(_xslBasePath);
        }

        /// <summary>
        /// Gera automaticamente TCL e XSL para todos os layouts
        /// </summary>
        public async Task<AutoGenerationResult> GenerateAllTransformationsAsync()
        {
            var result = new AutoGenerationResult
            {
                Success = true,
                ProcessedLayouts = new List<ProcessedLayout>(),
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Iniciando geração automática de TCL e XSL para todos os layouts");

                // Buscar todos os layouts do Redis
                var layoutsRequest = new LayoutSearchRequest
                {
                    SearchTerm = "", // String vazia = buscar todos
                    MaxResults = 1000 // Buscar muitos layouts
                };

                var layoutsResponse = await _cachedLayoutService.SearchLayoutsAsync(layoutsRequest);

                if (layoutsResponse == null || !layoutsResponse.Layouts.Any())
                {
                    result.Warnings.Add("Nenhum layout encontrado no Redis");
                    return result;
                }

                _logger.LogInformation("Encontrados {Count} layouts para processar", layoutsResponse.Layouts.Count);

                // Processar cada layout
                foreach (var layout in layoutsResponse.Layouts)
                {
                    try
                    {
                        var processed = await ProcessLayoutAsync(layout);
                        result.ProcessedLayouts.Add(processed);

                        if (!processed.Success)
                            result.Errors.AddRange(processed.Errors);
                        if (processed.Warnings.Any())
                            result.Warnings.AddRange(processed.Warnings);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao processar layout: {LayoutName}", layout.Name);
                        result.Errors.Add($"Erro ao processar layout {layout.Name}: {ex.Message}");
                    }
                }

                result.Success = !result.Errors.Any();
                _logger.LogInformation("Geração automática concluída. Processados: {Count}, Sucessos: {SuccessCount}, Erros: {ErrorCount}", result.ProcessedLayouts.Count, result.ProcessedLayouts.Count(p => p.Success), result.Errors.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante geração automática");
                result.Success = false;
                result.Errors.Add($"Erro geral: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Processa um layout específico
        /// </summary>
        public async Task<ProcessedLayout> ProcessLayoutAsync(LayoutRecord layout)
        {
            var processed = new ProcessedLayout
            {
                LayoutGuid = layout.LayoutGuid != Guid.Empty
                    ? layout.LayoutGuid.ToString()
                    : layout.Id.ToString(),
                LayoutName = layout.Name,
                LayoutType = layout.LayoutType,
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                GeneratedFiles = new List<string>()
            };

            try
            {
                _logger.LogInformation("Processando layout: {Name} (Type: {Type}, Guid: {Guid})", layout.Name, layout.LayoutType, layout.LayoutGuid);

                // ✅ Issue #219: o `LayoutType` cru vindo da coluna SQL [LayoutType] pode divergir do
                // `<LayoutType>` embutido no XML descriptografado do layout (ex.: cadastro legado com
                // código numérico Sysmiddle "2" em vez do texto "TextPositional"). Normalizamos aqui
                // antes de decidir o caminho de geração — ver ResolveEffectiveLayoutType.
                var effectiveLayoutType = ResolveEffectiveLayoutType(layout);
                processed.LayoutType = effectiveLayoutType;

                // Verificar tipo do layout
                if (effectiveLayoutType == "TextPositional")
                    // TextPositional: gerar TCL e XSL
                    await ProcessTextPositionalLayoutAsync(layout, processed);
                else if (effectiveLayoutType == "XML")
                    // XML: gerar apenas XSL
                    await ProcessXmlLayoutAsync(layout, processed);
                else
                {
                    processed.Warnings.Add($"Tipo de layout não suportado: {effectiveLayoutType}");
                    processed.Success = false;
                    return processed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar layout: {LayoutName}", layout.Name);
                processed.Success = false;
                processed.Errors.Add($"Erro: {ex.Message}");
            }

            return processed;
        }

        /// <summary>
        /// Resolve o tipo efetivo do layout ("TextPositional"/"XML"), lidando com a divergência entre
        /// a coluna SQL [LayoutType] e o XML descriptografado do layout.
        /// ✅ Issue #219 — ver <see cref="LayoutTypeNormalizer"/> para a lógica completa e o racional.
        /// </summary>
        private string ResolveEffectiveLayoutType(LayoutRecord layout)
            => LayoutTypeNormalizer.ResolveEffectiveLayoutType(layout, message => _logger.LogWarning("{Message}", message));

        /// <summary>
        /// Processa layout TextPositional (gera TCL e XSL)
        /// </summary>
        private async Task ProcessTextPositionalLayoutAsync(LayoutRecord layout, ProcessedLayout processed)
        {
            try
            {
                // 1. Gerar TCL a partir do layout XML
                // Usar DecryptedContent se disponível, senão ValueContent
                var layoutXml = !string.IsNullOrEmpty(layout.DecryptedContent) ? layout.DecryptedContent : layout.ValueContent;

                if (string.IsNullOrEmpty(layoutXml))
                {
                    processed.Errors.Add("Layout XML vazio no Redis");
                    processed.Success = false;
                    return;
                }

                // Salvar layout XML temporariamente
                var layoutGuidStr = layout.LayoutGuid != Guid.Empty ? layout.LayoutGuid.ToString() : layout.Id.ToString();
                var tempLayoutPath = Path.Combine(Path.GetTempPath(), $"layout_{layoutGuidStr}.xml");
                await File.WriteAllTextAsync(tempLayoutPath, layoutXml, Encoding.UTF8);

                try
                {
                    // Gerar TCL usando aprendizado de máquina se disponível
                    var tclFileName = SanitizeFileName($"{layout.Name}.tcl");
                    var tclPath = Path.Combine(_tclBasePath, tclFileName);

                    // Tentar usar gerador melhorado com ML
                    var improvedResult = await _improvedTclGenerator.GenerateTclWithMLAsync(tempLayoutPath, layout.Name, tclPath);

                    if (improvedResult.Success && !string.IsNullOrEmpty(improvedResult.SuggestedTcl))
                    {
                        // Usar TCL melhorado pela ML
                        await File.WriteAllTextAsync(tclPath, improvedResult.SuggestedTcl, Encoding.UTF8);
                        processed.GeneratedFiles.Add(tclPath);

                        if (improvedResult.Suggestions.Any())
                            processed.Warnings.AddRange(improvedResult.Suggestions.Take(5));

                        _logger.LogInformation("TCL gerado com ML: {Path}. Sugestões: {Count}", tclPath, improvedResult.Suggestions.Count);
                    }
                    else
                    {
                        // Fallback para gerador base
                        var tclContent = await _tclGenerator.GenerateTclFromLayoutAsync(tempLayoutPath, tclPath);
                        processed.GeneratedFiles.Add(tclPath);
                        _logger.LogInformation("TCL gerado (base): {Path}", tclPath);

                        if (improvedResult.Warnings.Any())
                            processed.Warnings.AddRange(improvedResult.Warnings.Take(3));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao gerar TCL para layout: {LayoutName}", layout.Name);
                    processed.Errors.Add($"Erro ao gerar TCL: {ex.Message}");
                    processed.Success = false;
                }

                // 2. Buscar mapeador e gerar XSL
                await GenerateXslForLayoutAsync(layout, processed);
            }
            finally
            {
                // Limpar arquivo temporário se existir
                var layoutGuidStr = layout.LayoutGuid != Guid.Empty ? layout.LayoutGuid.ToString() : layout.Id.ToString();
                var tempLayoutPath = Path.Combine(Path.GetTempPath(), $"layout_{layoutGuidStr}.xml");
                if (File.Exists(tempLayoutPath))
                    try { File.Delete(tempLayoutPath); } catch { }

            }
        }

        /// <summary>
        /// Processa layout XML (gera apenas XSL)
        /// </summary>
        private async Task ProcessXmlLayoutAsync(LayoutRecord layout, ProcessedLayout processed)
        {
            // Para XML, apenas gerar XSL
            await GenerateXslForLayoutAsync(layout, processed);
        }

        /// <summary>
        /// Gera XSL para um layout buscando o mapeador correspondente
        /// </summary>
        private async Task GenerateXslForLayoutAsync(LayoutRecord layout, ProcessedLayout processed)
        {
            try
            {
                // Buscar mapeador pelo InputLayoutGuid ou TargetLayoutGuid
                var layoutGuidStr = layout.LayoutGuid != Guid.Empty ? layout.LayoutGuid.ToString() : layout.Id.ToString();

                // Para layouts TextPositional, buscar mapeador onde este layout é a entrada (InputLayoutGuid)
                // Para layouts XML, buscar mapeador onde este layout pode ser entrada ou saída
                Mapper mapper = null;

                if (layout.LayoutType == "TextPositional")
                    // TextPositional geralmente é entrada (transforma TXT -> XML)
                    mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(layoutGuidStr);
                else if (layout.LayoutType == "XML")
                {
                    // XML pode ser entrada ou saída
                    // Tentar primeiro como entrada, depois como saída
                    mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(layoutGuidStr);
                    if (mapper == null)
                        mapper = await _mapperDatabaseService.GetMapperByTargetLayoutGuidAsync(layoutGuidStr);
                }

                // Fallback: buscar qualquer mapeador relacionado
                if (mapper == null)
                {
                    var mappers = await _mapperDatabaseService.GetMappersByLayoutGuidAsync(layoutGuidStr);
                    if (mappers.Any())
                    {
                        mapper = mappers.OrderByDescending(m => m.LastUpdateDate).First();
                        _logger.LogInformation("Usando mapeador encontrado via fallback para layout {LayoutName}", layout.Name);
                    }
                }

                if (mapper == null)
                {
                    processed.Warnings.Add($"Nenhum mapeador encontrado para o layout {layout.Name} (Guid: {layoutGuidStr})");
                    return;
                }

                // Usar DecryptedContent (já descriptografado) se disponível, senão ValueContent
                string mapperXml = !string.IsNullOrEmpty(mapper.DecryptedContent) ? mapper.DecryptedContent : mapper.ValueContent;

                if (string.IsNullOrEmpty(mapperXml))
                {
                    processed.Errors.Add($"Mapeador {mapper.Name} não possui conteúdo (ValueContent vazio)");
                    processed.Success = false;
                    return;
                }

                // Verificar se o XML do mapeador está completo e válido
                try
                {
                    var testDoc = XDocument.Parse(mapperXml);
                    // Se já for um XML válido com estrutura MapperVO, usar como está
                    if (testDoc.Root?.Name.LocalName == "MapperVO")
                        // XML válido e completo, usar diretamente
                        _logger.LogInformation("XML do mapeador válido e completo com estrutura MapperVO");
                    else
                    {
                        // XML válido mas sem estrutura MapperVO, envolver
                        _logger.LogWarning("XML do mapeador não tem estrutura MapperVO, envolvendo...");
                        mapperXml = CreateMapperXmlFromValueContent(mapperXml);
                    }
                }
                catch (Exception parseEx)
                {
                    // Se não for XML válido, tentar criar estrutura
                    _logger.LogWarning(parseEx, "Conteúdo do mapeador não é XML válido, tentando criar estrutura");
                    mapperXml = CreateMapperXmlFromValueContent(mapperXml);
                }

                // Salvar mapeador XML temporariamente
                var tempMapperPath = Path.Combine(Path.GetTempPath(), $"mapper_{mapper.MapperGuid}.xml");
                await File.WriteAllTextAsync(tempMapperPath, mapperXml, Encoding.UTF8);

                try
                {
                    // Gerar XSL usando aprendizado de máquina se disponível
                    var xslFileName = SanitizeFileName($"{layout.Name}.xsl");
                    var xslPath = Path.Combine(_xslBasePath, xslFileName);

                    _logger.LogInformation("Gerando XSL para layout: {LayoutName} -> {XslPath}", layout.Name, xslPath);

                    // Tentar usar gerador melhorado com ML
                    var improvedResult = await _improvedXslGenerator.GenerateXslWithMLAsync(
                        tempMapperPath,
                        layout.Name,
                        xslPath);

                    if (improvedResult.Success && !string.IsNullOrEmpty(improvedResult.SuggestedXsl))
                    {
                        // Usar XSL melhorado pela ML
                        await File.WriteAllTextAsync(xslPath, improvedResult.SuggestedXsl, Encoding.UTF8);
                        processed.GeneratedFiles.Add(xslPath);

                        if (improvedResult.Suggestions.Any())
                            processed.Warnings.AddRange(improvedResult.Suggestions.Take(5));

                        _logger.LogInformation("XSL gerado com ML: {Path}. Sugestões: {Count}", xslPath, improvedResult.Suggestions.Count);
                    }
                    else
                    {
                        // Fallback para gerador base
                        var xslContent = await _xslGenerator.GenerateXslFromMapAsync(tempMapperPath, xslPath);

                        if (!string.IsNullOrEmpty(xslContent))
                        {
                            processed.GeneratedFiles.Add(xslPath);
                            _logger.LogInformation("XSL gerado (base): {Path}", xslPath);
                        }
                        else
                        {
                            processed.Errors.Add("XSL gerado está vazio");
                            processed.Success = false;
                        }

                        if (improvedResult.Warnings.Any())
                            processed.Warnings.AddRange(improvedResult.Warnings.Take(3));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao gerar XSL para layout: {LayoutName}", layout.Name);
                    processed.Errors.Add($"Erro ao gerar XSL: {ex.Message}");
                    processed.Success = false;
                }
                finally
                {
                    // Limpar arquivo temporário
                    if (File.Exists(tempMapperPath))
                        try { File.Delete(tempMapperPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XSL para layout: {LayoutName}", layout.Name);
                processed.Errors.Add($"Erro ao buscar mapeador: {ex.Message}");
                processed.Success = false;
            }
        }

        /// <summary>
        /// Cria estrutura XML do mapeador a partir do ValueContent
        /// </summary>
        private string CreateMapperXmlFromValueContent(string valueContent)
        {
            try
            {
                // Se já for XML válido, retornar como está
                try
                {
                    XDocument.Parse(valueContent);
                    return valueContent;
                }
                catch
                {
                    // Se não for XML, criar estrutura básica
                    // O ValueContent pode conter as regras de transformação
                    // que precisam ser envolvidas em uma estrutura XML de mapeador
                    var xml = new XDocument(
                        new XElement("MapperVO",
                            new XElement("Rules",
                                new XElement("Rule",
                                    new XElement("ContentValue", valueContent)
                                )
                            )
                        )
                    );

                    return xml.ToString();
                }
            }
            catch
            {
                // Fallback: criar estrutura mínima
                return $@"<?xml version=""1.0"" encoding=""utf-8""?>
                            <MapperVO>
                                <Rules>
                                    <Rule>
                                        <ContentValue>{System.Security.SecurityElement.Escape(valueContent ?? "")}</ContentValue>
                                    </Rule>
                                </Rules>
                            </MapperVO>";
            }
        }

        /// <summary>
        /// Sanitiza nome de arquivo
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = fileName;
            foreach (var c in invalidChars)
                sanitized = sanitized.Replace(c, '_');

            return sanitized;
        }
    }
}