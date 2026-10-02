using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;

using LayoutParserApi.Services.Logging;
using LayoutParserApi.Services.Security;
using LayoutParserApi.Services.XmlAnalysis.Models;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Serviço de pipeline de transformação: TXT → MAP/TCL → XML Intermediário → XSL → XML Final.
    /// <b>Pathway 2 - canônico</b> (item 2.1 do dispatch de IA, docs/architecture/ai-roadmap-dispatch.md,
    /// 2026-07-21): é o serviço por trás do <see cref="LayoutParserApi.Controllers.TransformationExecutionController"/>,
    /// que o front-end de fato chama hoje. Comparar com <see cref="LayoutParserApi.Services.Transformation.MapperTransformationService"/>
    /// (Pathway 1 - legado, sem novo investimento).
    /// </summary>
    public class TransformationPipelineService
    {
        private readonly ILogger<TransformationPipelineService> _logger;
        private readonly string _tclBasePath;
        private readonly string _xslBasePath;
        private readonly IGeneratedXslResolver? _generatedXslResolver;

        public TransformationPipelineService(
            ILogger<TransformationPipelineService> logger,
            IConfiguration configuration,
            IGeneratedXslResolver? generatedXslResolver = null)
        {
            _logger = logger;
            _generatedXslResolver = generatedXslResolver;
            _tclBasePath = configuration["TransformationPipeline:TclPath"] ?? @"C:\inetpub\wwwroot\layoutparser\TCL";
            _xslBasePath = configuration["TransformationPipeline:XslPath"] ?? @"C:\inetpub\wwwroot\layoutparser\XSL";
        }

        /// <summary>
        /// Transforma TXT MQSeries/IDOC → MAP → XML Intermediário → XSL → XML NFe
        /// </summary>
        public async Task<TransformationPipelineResult> TransformTxtToXmlAsync(string txtContent, string layoutName, string targetDocumentType = "NFe")
        {
            var result = new TransformationPipelineResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                StepResults = new Dictionary<string, string>()
            };

            try
            {
                _logger.LogInformation("Iniciando pipeline de transformação TXT → XML. Layout: {LayoutName}, Target: {TargetType}",
                    layoutName, targetDocumentType);

                // Etapa 1: TXT → XML Intermediário (usando MAP/TCL)
                var intermediateXml = await TransformTxtToIntermediateXmlAsync(txtContent, layoutName, result);
                if (intermediateXml == null)
                {
                    result.Success = false;
                    return result;
                }

                result.StepResults["IntermediateXml"] = intermediateXml;
                _logger.LogInformation("XML intermediário gerado com sucesso");

                // Etapa 2: XML Intermediário → XML Final (usando XSL)
                var finalXml = await TransformIntermediateToFinalXmlAsync(intermediateXml, layoutName, targetDocumentType, result);
                if (finalXml == null)
                {
                    result.Success = false;
                    return result;
                }

                // Preencher caminhos TCL e XSL no resultado
                var tclFile = Path.Combine(_tclBasePath, $"{layoutName}.tcl");
                if (File.Exists(tclFile))
                    result.TclPath = tclFile;

                result.StepResults["FinalXml"] = finalXml;
                result.TransformedXml = finalXml;
                result.Success = true;

                _logger.LogInformation("Pipeline de transformação concluído com sucesso");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante pipeline de transformação");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Transforma XML → XSL → XML Final (para transformações XML→XML)
        /// </summary>
        public async Task<TransformationPipelineResult> TransformXmlToXmlAsync(string xmlContent, string sourceDocumentType, string targetDocumentType, string layoutName = null)
        {
            var result = new TransformationPipelineResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                StepResults = new Dictionary<string, string>()
            };

            try
            {
                _logger.LogInformation("Iniciando transformação XML → XML. Source: {SourceType}, Target: {TargetType}",
                    sourceDocumentType, targetDocumentType);

                // Carregar XSL apropriado
                // Disco primeiro (compatibilidade); depois artefato gerado Ready (issue #642, opção b).
                var xslPath = FindXslFile(layoutName);
                string? generatedXsl = null;
                if (string.IsNullOrEmpty(xslPath) || !File.Exists(xslPath))
                {
                    xslPath = null;
                    generatedXsl = await TryGetGeneratedXslAsync(layoutName, result);
                    if (generatedXsl == null)
                    {
                        result.Success = false;
                        result.ErrorCode = "xsl_not_found";
                        result.Errors.Add($"Arquivo XSL não encontrado para transformação {sourceDocumentType} → {targetDocumentType}");
                        return result;
                    }
                }

                if (xslPath != null) result.XslPath = xslPath;

                // Aplicar transformação XSL
                var finalXml = generatedXsl != null
                    ? await ApplyGeneratedXsltTransformAsync(xmlContent, generatedXsl, result)
                    : await ApplyXsltTransformAsync(xmlContent, xslPath!, result);
                if (finalXml == null)
                {
                    result.Success = false;
                    return result;
                }

                result.StepResults["FinalXml"] = finalXml;
                result.TransformedXml = finalXml;
                result.Success = true;

                _logger.LogInformation("Transformação XML → XML concluída com sucesso");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação XML → XML");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Etapa 1: Transforma TXT MQSeries/IDOC em XML Intermediário usando MAP
        /// </summary>
        private async Task<string> TransformTxtToIntermediateXmlAsync(string txtContent, string layoutName, TransformationPipelineResult result)
        {
            try
            {
                // Carregar arquivo MAP
                var mapContent = await LoadMappingFileAsync(layoutName);
                if (mapContent == null)
                {
                    result.ErrorCode = "map_not_found";
                    result.Errors.Add($"Arquivo MAP não encontrado para layout: {layoutName}");
                    return null;
                }

                // Parsear MAP para entender estrutura
                var mapDocument = XDocument.Parse(mapContent);

                // Parsear TXT em linhas
                var txtLines = txtContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

                // Gerar XML intermediário baseado no MAP
                var intermediateXml = GenerateIntermediateXmlFromMap(txtLines, mapDocument, result);

                return intermediateXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar TXT para XML intermediário");
                result.Errors.Add($"Erro na transformação TXT → XML Intermediário: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gera XML intermediário a partir do TXT e do MAP
        /// </summary>
        private string GenerateIntermediateXmlFromMap(List<string> txtLines, XDocument mapDocument, TransformationPipelineResult result)
        {
            try
            {
                var root = new XElement("ROOT");
                var currentLineIndex = 0;

                // Processar cada linha do TXT
                foreach (var txtLine in txtLines)
                {
                    if (string.IsNullOrWhiteSpace(txtLine))
                        continue;

                    // Detectar tipo de linha baseado no identificador (primeiros caracteres ou padrão)
                    var lineIdentifier = DetectLineIdentifier(txtLine);

                    // Encontrar definição correspondente no MAP
                    var lineDefinition = mapDocument.Descendants("LINE").FirstOrDefault(l => l.Attribute("identifier")?.Value == lineIdentifier || l.Attribute("name")?.Value == lineIdentifier);

                    if (lineDefinition != null)
                    {
                        // Extrair campos da linha baseado na definição do MAP
                        var lineElement = ExtractLineFromTxt(txtLine, lineDefinition);
                        if (lineElement != null)
                            root.Add(lineElement);
                    }
                    else
                        result.Warnings.Add($"Linha {currentLineIndex + 1}: Identificador '{lineIdentifier}' não encontrado no MAP");

                    currentLineIndex++;
                }

                // Adicionar elemento chave se necessário (geralmente vem da primeira linha)
                if (root.Elements().Any(e => e.Name.LocalName == "chave"))
                {

                }
                else if (txtLines.Count > 0)
                {
                    // Tentar extrair chave da primeira linha
                    var chaveElement = new XElement("chave");
                    // Lógica para extrair chave da primeira linha
                    root.AddFirst(chaveElement);
                }

                var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
                return doc.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XML intermediário");
                throw;
            }
        }

        /// <summary>
        /// Detecta identificador da linha (primeiro caractere ou padrão específico)
        /// </summary>
        private string DetectLineIdentifier(string txtLine)
        {
            if (string.IsNullOrWhiteSpace(txtLine))
                return "UNKNOWN";

            // Para MQSeries: pode ter identificador no início (ex: "A", "B", "H")
            // Ou pode ser baseado no conteúdo (ex: "HEADER", "TRAILER")
            if (txtLine.Length >= 1)
            {
                var firstChar = txtLine[0];
                if (char.IsLetter(firstChar))
                    // Verificar se é um identificador simples (A, B, C, etc.)
                    if (txtLine.Length > 1 && (char.IsDigit(txtLine[1]) || char.IsWhiteSpace(txtLine[1])))
                        return firstChar.ToString();
            }

            // Verificar padrões conhecidos
            if (txtLine.Contains("HEADER")) return "HEADER";
            if (txtLine.Contains("TRAILER")) return "TRAILER";
            if (txtLine.Contains("LINHA000")) return "LINHA000";
            if (txtLine.Contains("LINHA001")) return "LINHA001";

            // Padrão IDOC
            if (txtLine.StartsWith("EDI_DC40")) return "EDI_DC40";
            if (txtLine.StartsWith("ZRSDM_NFE")) return "ZRSDM_NFE";

            return "UNKNOWN";
        }

        /// <summary>
        /// Extrai campos de uma linha TXT baseado na definição do MAP
        /// </summary>
        private XElement ExtractLineFromTxt(string txtLine, XElement lineDefinition)
        {
            var lineName = lineDefinition.Attribute("name")?.Value ?? "Line";
            var lineElement = new XElement(lineName);

            var fields = lineDefinition.Descendants("FIELD").ToList();
            var currentPosition = 0;

            foreach (var fieldDef in fields)
            {
                var fieldName = fieldDef.Attribute("name")?.Value;
                var lengthAttr = fieldDef.Attribute("length")?.Value;

                if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(lengthAttr))
                    continue;

                // Parsear length (pode ser "60" ou "15,2,0" para decimais)
                var lengthParts = lengthAttr.Split(',');
                var fieldLength = int.Parse(lengthParts[0]);

                if (currentPosition + fieldLength <= txtLine.Length)
                {
                    var fieldValue = txtLine.Substring(currentPosition, fieldLength).Trim();
                    lineElement.Add(new XElement(fieldName, fieldValue));
                    currentPosition += fieldLength;
                }
            }

            return lineElement;
        }

        /// <summary>
        /// Etapa 2: Transforma XML Intermediário em XML Final usando XSL
        /// </summary>
        private async Task<string> TransformIntermediateToFinalXmlAsync(string intermediateXml, string layoutName, string targetDocumentType, TransformationPipelineResult result)
        {
            try
            {
                // Encontrar arquivo XSL apropriado
                var xslPath = FindXslFile(layoutName);
                if (string.IsNullOrEmpty(xslPath) || !File.Exists(xslPath))
                {
                    // Issue #642 (b): sem arquivo em disco, tenta o XSL do artefato gerado Ready.
                    // Só chega aqui com TCL já resolvido (etapa 1) — o TCL nunca é inventado.
                    var generatedXsl = await TryGetGeneratedXslAsync(layoutName, result);
                    if (generatedXsl != null)
                        return await ApplyGeneratedXsltTransformAsync(intermediateXml, generatedXsl, result);

                    result.ErrorCode = "xsl_not_found";
                    result.Errors.Add($"Arquivo XSL não encontrado para transformação Intermediate → {targetDocumentType}");
                    return null;
                }

                // Armazenar caminho XSL no resultado
                result.XslPath = xslPath;

                // Aplicar transformação XSL
                var finalXml = await ApplyXsltTransformAsync(intermediateXml, xslPath, result);
                return finalXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML intermediário para XML final");
                result.Errors.Add($"Erro na transformação XML Intermediário → XML Final: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Busca o XSL do artefato gerado Ready (nunca lança; falha => null => erro exato do chamador).
        /// O TCL gerado/da referência virá pelo catálogo (#636) numa fase seguinte: por ora entrada TXT
        /// sem TCL em disco segue em <c>map_not_found</c>.
        /// </summary>
        private async Task<string?> TryGetGeneratedXslAsync(string? layoutName, TransformationPipelineResult result)
        {
            if (_generatedXslResolver == null || string.IsNullOrWhiteSpace(layoutName)) return null;
            try
            {
                var xsl = await _generatedXslResolver.ResolveReadyXslAsync(layoutName);
                if (xsl != null)
                {
                    _logger.LogInformation("XSL ausente em disco; usando artefato gerado Ready. Layout: {LayoutName}",
                        LogMessageSanitizer.Sanitize(layoutName));
                    result.Warnings.Add("XSL obtido do artefato gerado (não havia arquivo em disco).");
                }
                return xsl;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao resolver XSL gerado para {LayoutName}", LogMessageSanitizer.Sanitize(layoutName));
                return null;
            }
        }

        /// <summary>XSLT a partir de conteúdo em memória: sem document() e sem resolver externo.</summary>
        private async Task<string?> ApplyGeneratedXsltTransformAsync(string xmlContent, string xslContent, TransformationPipelineResult result)
        {
            try
            {
                var xslt = new XslCompiledTransform();
                var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var xslReader = XmlReader.Create(new StringReader(xslContent), readerSettings))
                    xslt.Load(xslReader, new XsltSettings(), null);
                return await Task.FromResult(RunTransform(xslt, xmlContent, readerSettings));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar XSLT gerado");
                result.Errors.Add($"Erro na transformação XSLT: {ex.Message}");
                return null;
            }
        }

        private static string RunTransform(XslCompiledTransform xslt, string xmlContent, XmlReaderSettings readerSettings)
        {
            using var xmlReader = XmlReader.Create(new StringReader(xmlContent), readerSettings);
            using var stringWriter = new StringWriter();
            using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = false,
                Encoding = Encoding.UTF8
            });
            xslt.Transform(xmlReader, xmlWriter);
            return stringWriter.ToString();
        }

        /// <summary>
        /// Aplica transformação XSLT
        /// </summary>
        private async Task<string> ApplyXsltTransformAsync(string xmlContent, string xslPath, TransformationPipelineResult result)
        {
            try
            {
                // Configurar XslCompiledTransform
                var xslt = new XslCompiledTransform();
                xslt.Load(xslPath, new XsltSettings { EnableDocumentFunction = true }, new XmlUrlResolver());

                // Carregar XML de entrada — DTD proibido e sem resolver externo para evitar XXE (CodeQL #4)
                var xmlReaderSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using (var xmlReader = XmlReader.Create(new StringReader(xmlContent), xmlReaderSettings))
                using (var stringWriter = new StringWriter())
                using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8
                }))
                {
                    // Aplicar transformação
                    xslt.Transform(xmlReader, xmlWriter);
                    return stringWriter.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar transformação XSLT");
                result.Errors.Add($"Erro na transformação XSLT: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Carrega arquivo MAP (estrutura &lt;MAP&gt;&lt;LINE&gt;&lt;FIELD/&gt;&lt;/LINE&gt;&lt;/MAP&gt;).
        /// <b>Convenção confirmada por evidência de dump de produção (issue #39, 2026-08-12):</b> apesar da
        /// hipótese original ser <c>MAP_{layoutName}.xml</c> dentro de <c>MappingPath</c> (Mapeamentro) — pasta
        /// que sequer existe em produção — e da hipótese seguinte ser uma pasta nomeada pelo mapper dentro de
        /// <c>Examples/</c> — que na prática só guarda amostras brutas de aprendizado (.mq_series/layout_learned.json,
        /// sem estrutura MAP) —, o dump real mostra que o arquivo com a definição de LINE/FIELD fica em
        /// <c>TclPath/{layoutName}.tcl</c>: mesmo com extensão .tcl, o conteúdo é XML puro (confirmado
        /// byte-a-byte contra <c>tcl/LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl</c>). O nome usado é o do
        /// LAYOUT (já disponível como parâmetro), não o do mapper.
        /// </summary>
        private async Task<string> LoadMappingFileAsync(string layoutName)
        {
            try
            {
                // ✅ SCS0018 (issue #88, achado real): layoutName chega sem validação a partir de
                // TransformTxtToXmlAsync, cujos chamadores incluem
                // TransformationExecutionController.RunTransformationTest ([FromBody] request.LayoutName
                // cru). Diferente dos outros sites do projeto (que já usam
                // IsValidLayoutName+IsWithinBasePath ou SafePathResolver), este nunca teve barreira —
                // "..\..\..\Windows\win.ini" (sem a extensão .tcl importar, já que Windows aceita
                // caminho com pontos extras) chegava direto ao Path.Combine/File.ReadAllTextAsync.
                // Fechado com o mesmo SafePathResolver.Resolve já padronizado no projeto.
                var mapPath = SafePathResolver.Resolve(_tclBasePath, $"{layoutName}.tcl");
                if (mapPath == null)
                {
                    _logger.LogWarning("Layout rejeitado para leitura de MAP/TCL: {LayoutName}", LogMessageSanitizer.Sanitize(layoutName));
                    return null;
                }

#pragma warning disable SCS0018
                if (File.Exists(mapPath))
                    return await File.ReadAllTextAsync(mapPath, Encoding.UTF8);
#pragma warning restore SCS0018

                _logger.LogWarning("Arquivo MAP (TCL) não encontrado: {Path}", mapPath);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar arquivo MAP");
                return null;
            }
        }

        /// <summary>
        /// Encontra arquivo XSL apropriado.
        /// <b>Convenção confirmada por evidência de dump de produção (issue #55, 2026-08-13):</b> os padrões
        /// antigos (<c>{layoutName}_*.xsl</c>, <c>{targetType}*_{layoutName}.xsl</c> etc.) não batem com
        /// nenhum arquivo real e sempre caíam no fallback silencioso "primeiro XSL da pasta" — errado pra
        /// qualquer layout com mais de um XSL. O dump (<c>.claude/tmp/servidor/layoutparser/xsl/</c>) mostra,
        /// em dois casos reais (CNHI e MARELLI), que o nome é <c>{mapperName}_{layoutName}.xsl</c> — ex.:
        /// <c>MAP_CNHI_MQSERIES_SEND_ENV_TXT_XML_NFE_LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.xsl</c>. O nome do
        /// mapper não é conhecido aqui (só o do layout chega como parâmetro), então resolvemos pelo sufixo
        /// <c>*_{layoutName}.xsl</c>, que casa com a convenção real sem depender do prefixo do mapper.
        /// Sem <paramref name="layoutName"/>, ou sem nenhum arquivo casando o padrão, retorna
        /// <see langword="null"/> com log de erro claro — não há mais fallback silencioso para "qualquer XSL".
        ///
        /// <para><b>Issue #96:</b> os parâmetros <c>sourceType</c>/<c>targetType</c> foram removidos —
        /// nunca influenciaram a busca (o padrão de arquivo usa só <paramref name="layoutName"/>,
        /// ver linha do <c>pattern</c> abaixo); apareciam apenas no log de erro do caminho "sem
        /// layoutName", que os dois call-sites já registram por conta própria antes de chamar este
        /// método.</para>
        /// </summary>
        private string FindXslFile(string layoutName = null)
        {
            try
            {
                if (string.IsNullOrEmpty(layoutName))
                {
                    _logger.LogError(
                        "Não é possível localizar o XSL sem o nome do layout (convenção real é {{mapperName}}_{{layoutName}}.xsl).");
                    return null;
                }

                var pattern = $"*_{layoutName}.xsl";
                var files = Directory.GetFiles(_xslBasePath, pattern, SearchOption.AllDirectories);

                if (files.Length == 1)
                {
                    _logger.LogInformation("Arquivo XSL encontrado: {Path}", files[0]);
                    return files[0];
                }

                if (files.Length > 1)
                {
                    // Convenção real não garante mapper único por layout (multi-candidato) — escolha
                    // determinística (ordem alfabética), mas sinalizada como ambígua, nunca silenciosa.
                    var chosen = files.OrderBy(f => f, StringComparer.Ordinal).First();
                    _logger.LogWarning(
                        "Múltiplos arquivos XSL casam com o padrão {Pattern} ({Count}); usando {Path} (ordem alfabética, ambíguo)",
                        pattern, files.Length, chosen);
                    return chosen;
                }

                _logger.LogError(
                    "Nenhum arquivo XSL encontrado para o layout {LayoutName} (padrão esperado: {Pattern} em {XslBasePath})",
                    layoutName, pattern, _xslBasePath);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao procurar arquivo XSL");
                return null;
            }
        }
    }
}