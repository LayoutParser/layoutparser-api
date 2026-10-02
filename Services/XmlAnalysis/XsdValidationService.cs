using LayoutParserApi.Services.Storage;
using LayoutParserApi.Models.XmlAnalysis;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Services.Security;
using LayoutParserApi.Services.XmlAnalysis.Models;

using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Serviço para validação de XML usando XSD (Schema Definition)
    /// </summary>
    public class XsdValidationService
    {
        private readonly ILogger<XsdValidationService> _logger;
        private readonly string _xsdBasePath;
        private readonly string _pdfBasePath;
        private readonly XmlDocumentTypeDetector _documentTypeDetector;
        private readonly IConfiguration _configuration;
        private readonly PdfOrientationReader _pdfOrientationReader;

        public XsdValidationService(
            ILogger<XsdValidationService> logger,
            IConfiguration configuration,
            XmlDocumentTypeDetector documentTypeDetector,
            PdfOrientationReader pdfOrientationReader,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _configuration = configuration;
            _documentTypeDetector = documentTypeDetector;
            _pdfOrientationReader = pdfOrientationReader;
            _xsdBasePath = (storagePaths ?? new StoragePaths(configuration)).Xsd;
            _pdfBasePath = (storagePaths ?? new StoragePaths(configuration)).Pdf;
        }

        /// <summary>
        /// Transforma XML de documento fiscal removendo wrapper de envio e adicionando namespace
        /// </summary>
        public string TransformDocumentXml(string xmlContent, DocumentTypeInfo docType = null)
        {
            // Se não forneceu docType, detectar
            if (docType == null)
                docType = _documentTypeDetector.DetectDocumentType(xmlContent);

            // Chamar método específico baseado no tipo
            return docType?.Type switch
            {
                "NFe" => TransformNFeXml(xmlContent),
                "CTE" => TransformCTeXml(xmlContent),
                "NFCom" => TransformNFComXml(xmlContent),
                "MDFe" => TransformMDFeXml(xmlContent),
                _ => TransformNFeXml(xmlContent) // Fallback para NFe
            };
        }

        /// <summary>
        /// Transforma XML NFe removendo tag enviNFe e adicionando namespace
        /// </summary>
        public string TransformNFeXml(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null)
                    return xmlContent;

                // Se a tag raiz for enviNFe, remover e pegar o conteúdo de NFe
                if (root.Name.LocalName == "enviNFe")
                {
                    // Encontrar elemento NFe dentro de enviNFe
                    var nfeElement = root.Element(XName.Get("NFe", root.Name.NamespaceName)) ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "NFe");

                    if (nfeElement != null)
                    {
                        // Obter namespace do enviNFe original
                        var nfeNamespace = root.GetDefaultNamespace().NamespaceName;
                        if (string.IsNullOrEmpty(nfeNamespace))
                            nfeNamespace = "http://www.portalfiscal.inf.br/nfe";

                        // Criar novo elemento NFe com namespace
                        var newNfeElement = new XElement(XName.Get("NFe", nfeNamespace));

                        // Copiar atributos do NFe original (exceto namespaces que serão definidos)
                        foreach (var attr in nfeElement.Attributes())
                            if (!attr.Name.LocalName.StartsWith("xmlns") && attr.Name != XNamespace.Xmlns + "xsi")
                                newNfeElement.SetAttributeValue(attr.Name, attr.Value);

                        // Adicionar namespace principal
                        newNfeElement.SetAttributeValue(XName.Get("xmlns"), nfeNamespace);

                        // Adicionar namespace xsi se necessário
                        var xsiAttr = nfeElement.Attribute(XName.Get("xsi", "http://www.w3.org/2000/xmlns/"));
                        if (xsiAttr == null)
                        {
                            var xsiNs = XNamespace.Xmlns + "xsi";
                            newNfeElement.SetAttributeValue(xsiNs, "http://www.w3.org/2001/XMLSchema-instance");
                        }
                        else
                            newNfeElement.SetAttributeValue(XNamespace.Xmlns + "xsi", xsiAttr.Value);

                        // Copiar elementos filhos recursivamente preservando estrutura
                        CopyElementsRecursively(nfeElement, newNfeElement);

                        // Criar novo documento
                        var newDoc = new XDocument(newNfeElement);

                        // Salvar XML transformado no resultado (para referência)
                        return newDoc.ToString();
                    }
                }

                return xmlContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML NFe");
                return xmlContent;
            }
        }

        /// <summary>
        /// Valida XML contra XSD (detecta automaticamente o tipo de documento)
        /// </summary>
        public async Task<XsdValidationResult> ValidateXmlAgainstXsdAsync(string xmlContent, string xsdVersion = null, string layoutName = null)
        {
            var result = new XsdValidationResult
            {
                IsValid = true,
                Errors = new List<XsdValidationError>(),
                Warnings = new List<string>()
            };

            try
            {
                // 1. Detectar tipo de documento automaticamente
                DocumentTypeInfo docType = null;

                if (!string.IsNullOrEmpty(layoutName))
                {
                    // Tentar detectar pelo nome do layout primeiro
                    docType = _documentTypeDetector.DetectFromLayoutName(layoutName);
                    _logger.LogInformation("Tipo detectado pelo layout: {Type}, XSD: {XsdVersion}", docType.Type, docType.XsdVersion);
                }

                // Se não detectou pelo layout ou não tinha layout, detectar pelo conteúdo XML
                if (docType == null || docType.Type == "UNKNOWN" || string.IsNullOrEmpty(docType.XsdVersion))
                {
                    docType = _documentTypeDetector.DetectDocumentType(xmlContent);
                    _logger.LogInformation("Tipo detectado pelo XML: {Type}, XSD: {XsdVersion}", docType.Type, docType.XsdVersion);
                }

                // Usar XSD fornecido explicitamente ou o detectado
                var finalXsdVersion = xsdVersion ?? docType?.XsdVersion;

                if (string.IsNullOrEmpty(finalXsdVersion))
                {
                    result.IsValid = false;
                    result.Errors.Add(new XsdValidationError
                    {
                        LineNumber = 0,
                        LinePosition = 0,
                        Severity = "Error",
                        Message = "Não foi possível detectar o tipo de documento fiscal. Verifique se o XML é NFe, CTE, NFCom ou MDFe."
                    });
                    _logger.LogWarning("Tipo de documento não detectado");
                    return result;
                }

                // 2. Transformar XML se necessário (remover wrapper de envio)
                var transformedXml = TransformDocumentXml(xmlContent, docType);
                result.TransformedXml = transformedXml;
                result.DocumentType = docType.Type;
                result.XsdVersion = finalXsdVersion;
                _logger.LogInformation("XML transformado para validação XSD. Tipo: {Type}, Versão XSD: {XsdVersion}", docType.Type, finalXsdVersion);

                // 3. Encontrar arquivo XSD
                var xsdPath = FindXsdFile(finalXsdVersion);
                if (string.IsNullOrEmpty(xsdPath) || !File.Exists(xsdPath))
                {
                    result.IsValid = false;
                    result.Errors.Add(new XsdValidationError
                    {
                        LineNumber = 0,
                        LinePosition = 0,
                        Severity = "Error",
                        Message = $"Arquivo XSD não encontrado: {finalXsdVersion}"
                    });
                    _logger.LogWarning("Arquivo XSD não encontrado: {XsdVersion}", finalXsdVersion);
                    return result;
                }

                // 4. Carregar schema XSD com namespace correto
                var schemas = new XmlSchemaSet();
                schemas.ValidationEventHandler += (sender, e) =>
                {
                    _logger.LogWarning("Aviso ao carregar schema XSD: {Message}", e.Message);
                };

                // Obter namespace do tipo de documento
                var targetNamespace = docType?.Namespace;
                if (string.IsNullOrEmpty(targetNamespace))
                {
                    // Tentar obter do XML transformado
                    try
                    {
                        var xDoc = XDocument.Parse(transformedXml);
                        targetNamespace = xDoc.Root?.GetDefaultNamespace().NamespaceName;
                    }
                    catch { }
                }

                // Se ainda não tem namespace, usar padrão baseado no tipo
                if (string.IsNullOrEmpty(targetNamespace))
                {
                    targetNamespace = docType?.Type switch
                    {
                        "NFe" => "http://www.portalfiscal.inf.br/nfe",
                        "CTE" => "http://www.portalfiscal.inf.br/cte",
                        "NFCom" => "http://www.portalfiscal.inf.br/nfcom",
                        "MDFe" => "http://www.portalfiscal.inf.br/mdfe",
                        _ => "http://www.portalfiscal.inf.br/nfe"
                    };
                }

                // ✅ SCS0018 (issue #88, achado real corrigido): xsdPath vem de FindXsdFile(finalXsdVersion),
                // que agora resolve a versão com SafePathResolver.Resolve (mesmo padrão do projeto) antes
                // de qualquer Directory.GetFiles — o SCS não reconhece o resolver como sanitizador.
#pragma warning disable SCS0018
                using (var reader = XmlReader.Create(xsdPath))
                {
                    var schema = XmlSchema.Read(reader, (sender, e) =>
                    {
                        _logger.LogError("Erro ao ler schema XSD: {Message}", e.Message);
                    });

                    if (schema != null)
                        // Adicionar schema com namespace correto
                        schemas.Add(schema);
                    else
                    {
                        // Fallback: adicionar diretamente pelo namespace
                        reader.Close();
                        using (var reader2 = XmlReader.Create(xsdPath))
                        {
                            schemas.Add(targetNamespace, reader2);
                        }
                    }
                }
#pragma warning restore SCS0018

                schemas.Compile();

                // 5. Configurar settings de validação
                var settings = new XmlReaderSettings
                {
                    ValidationType = ValidationType.Schema,
                    Schemas = schemas,
                    ValidationFlags = XmlSchemaValidationFlags.ProcessInlineSchema |
                                     XmlSchemaValidationFlags.ProcessSchemaLocation |
                                     XmlSchemaValidationFlags.ReportValidationWarnings,
                    // DTD proibido e sem resolver externo para evitar XXE (CodeQL #5)
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                // 6. Coletar erros de validação
                var validationErrors = new List<XsdValidationError>();
                settings.ValidationEventHandler += (sender, e) =>
                {
                    if (e.Severity == XmlSeverityType.Error)
                    {
                        validationErrors.Add(new XsdValidationError
                        {
                            LineNumber = e.Exception?.LineNumber ?? 0,
                            LinePosition = e.Exception?.LinePosition ?? 0,
                            Severity = "Error",
                            Message = e.Message
                        });
                    }
                    else if (e.Severity == XmlSeverityType.Warning)
                        result.Warnings.Add(e.Message);
                };

                // 6. Validar XML
                using (var xmlReader = XmlReader.Create(new StringReader(transformedXml), settings))
                {
                    try
                    {
                        while (xmlReader.Read()) { }
                    }
                    catch (XmlSchemaValidationException ex)
                    {
                        validationErrors.Add(new XsdValidationError
                        {
                            LineNumber = ex.LineNumber,
                            LinePosition = ex.LinePosition,
                            Severity = "Error",
                            Message = ex.Message
                        });
                    }
                    catch (XmlException ex)
                    {
                        validationErrors.Add(new XsdValidationError
                        {
                            LineNumber = ex.LineNumber,
                            LinePosition = ex.LinePosition,
                            Severity = "Error",
                            Message = ex.Message
                        });
                    }
                }

                result.Errors = validationErrors;
                result.IsValid = validationErrors.Count == 0;

                _logger.LogInformation("Validação XSD concluída: {IsValid}, {ErrorCount} erros, {WarningCount} avisos", result.IsValid, result.Errors.Count, result.Warnings.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante validação XSD");
                result.IsValid = false;
                result.Errors.Add(new XsdValidationError
                {
                    LineNumber = 0,
                    LinePosition = 0,
                    Severity = "Error",
                    Message = $"Erro interno durante validação: {ex.Message}"
                });
                return result;
            }
        }

        /// <summary>
        /// Carrega o <see cref="XmlSchemaSet"/> compilado para uma versão de XSD (issue #380,
        /// #198.5 — cobertura de destinos obrigatórios). Reaproveita a mesma resolução de arquivo
        /// (<see cref="FindXsdFile"/>) usada em <see cref="ValidateXmlAgainstXsdAsync"/> — não
        /// reinventa o parser de XSD. Degrada gracioso (dotnet-standards.md §Resiliência): arquivo
        /// ausente/schema inválido devolve <c>null</c> (logado), nunca lança para o chamador.
        /// </summary>
        public XmlSchemaSet? TryLoadSchemaSet(string xsdVersion)
        {
            try
            {
                var xsdPath = FindXsdFile(xsdVersion);
                if (string.IsNullOrEmpty(xsdPath) || !File.Exists(xsdPath))
                {
                    _logger.LogWarning("Arquivo XSD não encontrado para cálculo de cobertura obrigatória: {XsdVersion}", xsdVersion);
                    return null;
                }

                var schemas = new XmlSchemaSet();
                schemas.ValidationEventHandler += (sender, e) =>
                    _logger.LogWarning("Aviso ao carregar schema XSD (cobertura obrigatória): {Message}", e.Message);

                // ✅ SCS0018: xsdPath vem de FindXsdFile, já confinado a _xsdBasePath via SafePathResolver.
#pragma warning disable SCS0018
                using var reader = XmlReader.Create(xsdPath);
                var schema = XmlSchema.Read(reader, (sender, e) =>
                    _logger.LogError("Erro ao ler schema XSD (cobertura obrigatória): {Message}", e.Message));
#pragma warning restore SCS0018

                if (schema == null)
                    return null;

                schemas.Add(schema);
                schemas.Compile();
                return schemas;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar XSD para cálculo de cobertura obrigatória (versão {XsdVersion}).", xsdVersion);
                return null;
            }
        }

        /// <summary>
        /// Encontra arquivo XSD na pasta especificada
        /// </summary>
        private string FindXsdFile(string version)
        {
            // ✅ SCS0018 (issue #88, achado real — não era só drift de linha): "version" chega
            // sem validação a partir de ValidateXmlAgainstXsdAsync(xsdVersion), que por sua vez é
            // ["FromBody"] cru em XmlAnalysisController.ValidateXsd (request.XsdVersion) e
            // MappingTestRunService. Diferente de GetOrientationsAsync (já usa SafePathResolver
            // desde a PR #278/issue #172), este caminho nunca ganhou o mesmo tratamento — um
            // xsdVersion como "..\..\..\Windows\win.ini" (ou qualquer pasta fora de _xsdBasePath)
            // chegava direto ao Path.Combine/Directory.GetFiles. Fechado com o mesmo
            // SafePathResolver.Resolve já usado em GetOrientationsAsync/DocumentController/
            // MetricsController/ParseController.
            var versionPath = SafePathResolver.Resolve(_xsdBasePath, version);
            if (versionPath == null)
            {
                _logger.LogWarning("Versão de XSD rejeitada para leitura de schema: {XsdVersion}", LogMessageSanitizer.Sanitize(version));
                return null;
            }

            if (!Directory.Exists(versionPath))
            {
                _logger.LogWarning("Pasta XSD não encontrada: {Path}", versionPath);
                return null;
            }

            // Procurar arquivo .xsd (pode ter vários)
            // ✅ SCS0018 (issue #88, achado real corrigido): versionPath já é o retorno do
            // SafePathResolver.Resolve acima — confinado a _xsdBasePath.
#pragma warning disable SCS0018
            var xsdFiles = Directory.GetFiles(versionPath, "*.xsd", SearchOption.AllDirectories);
#pragma warning restore SCS0018

            // Priorizar arquivo principal (geralmente o maior ou com nome específico)
            var mainXsd = xsdFiles
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

            if (mainXsd != null)
            {
                _logger.LogInformation("Arquivo XSD encontrado: {Path}", mainXsd);
                return mainXsd;
            }

            return null;
        }

        /// <summary>
        /// Obtém orientações do PDF para correção de erros
        /// </summary>
        public async Task<XsdOrientationResult> GetOrientationsAsync(string xsdVersion = "PL_010b_NT2025_002_v1.30", List<string> errorCodes = null)
        {
            var result = new XsdOrientationResult
            {
                Success = false,
                Orientations = new List<string>()
            };

            try
            {
                // xsdVersion vem de parâmetro de request (controller) — nunca combine direto num
                // Path.Combine sem validar. SafePathResolver barra "..", separador de caminho e
                // qualquer resultado que escape de _pdfBasePath (path traversal, CodeQL cs/path-injection).
                var pdfPath = SafePathResolver.Resolve(_pdfBasePath, xsdVersion);
                if (pdfPath == null)
                {
                    _logger.LogWarning("Versão de XSD rejeitada para leitura de orientações PDF: {XsdVersion}", LogMessageSanitizer.Sanitize(xsdVersion));
                    result.Orientations.Add("Pasta de orientações PDF não encontrada.");
                    return result;
                }

                if (!Directory.Exists(pdfPath))
                {
                    _logger.LogWarning("Pasta PDF não encontrada: {Path}", pdfPath);
                    result.Orientations.Add("Pasta de orientações PDF não encontrada.");
                    return result;
                }

                // Leitura real do conteúdo do(s) PDF(s) (issue #172) — a mensagem genérica abaixo
                // é o fallback de degradação, não mais o comportamento único.
                PdfOrientationReadResult? leitura = null;
                try
                {
                    leitura = _pdfOrientationReader.ReadOrientations(pdfPath, errorCodes);
                }
                catch (Exception ex)
                {
                    // Degrade gracioso: falha na leitura do PDF (biblioteca indisponível, PDF
                    // protegido/corrompido) não pode quebrar a resposta de validação — cai no
                    // fallback genérico abaixo, igual ao comportamento anterior a esta issue.
                    _logger.LogWarning(ex, "Falha ao ler conteúdo dos PDFs de orientação em {Path}", pdfPath);
                }

                if (leitura is { Success: true, Trechos.Count: > 0 })
                {
                    result.Orientations.Add($"Orientações extraídas da documentação oficial (versão {xsdVersion}):");
                    foreach (var trecho in leitura.Trechos)
                        result.Orientations.Add($"[{trecho.Arquivo}] {trecho.Texto}");
                }
                else
                {
                    // Fallback genérico — mesmo texto de antes da issue #172, preservado para não
                    // quebrar quem já depende dessas mensagens quando não há PDF/trecho aplicável.
                    result.Orientations.Add("Para corrigir os erros de validação XSD:");
                    result.Orientations.Add("1. Verifique se todos os campos obrigatórios estão preenchidos");
                    result.Orientations.Add("2. Confirme que os valores estão nos formatos corretos (CNPJ, CPF, datas, etc.)");
                    result.Orientations.Add("3. Valide que os códigos de produto, CFOP e outras referências estão corretos");
                    result.Orientations.Add("4. Consulte a documentação oficial da SEFAZ para a versão " + xsdVersion);
                }

                if (errorCodes != null && errorCodes.Any())
                    result.Orientations.Add($"Erros específicos detectados: {string.Join(", ", errorCodes)}");

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter orientações do PDF");
                result.Orientations.Add($"Erro ao ler orientações: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Transforma XML CTE removendo tag enviCTe e adicionando namespace
        /// </summary>
        public string TransformCTeXml(string xmlContent)
        {
            return TransformDocumentWrapper(xmlContent, "enviCTe", "CTe", "http://www.portalfiscal.inf.br/cte");
        }

        /// <summary>
        /// Transforma XML NFCom removendo tag enviNFCom e adicionando namespace
        /// </summary>
        public string TransformNFComXml(string xmlContent)
        {
            return TransformDocumentWrapper(xmlContent, "enviNFCom", "NFCom", "http://www.portalfiscal.inf.br/nfcom");
        }

        /// <summary>
        /// Transforma XML MDFe removendo tag enviMDFe e adicionando namespace
        /// </summary>
        public string TransformMDFeXml(string xmlContent)
        {
            return TransformDocumentWrapper(xmlContent, "enviMDFe", "MDFe", "http://www.portalfiscal.inf.br/mdfe");
        }

        /// <summary>
        /// Método genérico para transformar documentos fiscais removendo wrapper de envio
        /// </summary>
        private string TransformDocumentWrapper(string xmlContent, string wrapperTag, string documentTag, string targetNamespace)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null)
                    return xmlContent;

                // Se a tag raiz for o wrapper, remover e pegar o conteúdo do documento
                if (root.Name.LocalName == wrapperTag)
                {
                    var documentElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == documentTag);

                    if (documentElement != null)
                    {
                        // Obter namespace do wrapper original
                        var docNamespace = root.GetDefaultNamespace().NamespaceName;
                        if (string.IsNullOrEmpty(docNamespace))                        
                            docNamespace = targetNamespace;                        

                        // Criar novo elemento com namespace
                        var newDocElement = new XElement(XName.Get(documentTag, docNamespace));

                        // Copiar atributos do documento original (exceto namespaces)
                        foreach (var attr in documentElement.Attributes())                        
                            if (!attr.Name.LocalName.StartsWith("xmlns") && attr.Name != (XNamespace.Xmlns + "xsi") && !attr.Name.ToString().Contains("schemaLocation"))
                                newDocElement.SetAttributeValue(attr.Name.LocalName, attr.Value);                            

                        // Adicionar namespace principal
                        newDocElement.SetAttributeValue(XName.Get("xmlns"), docNamespace);

                        // Adicionar namespace xsi se necessário
                        var xsiAttr = root.Attribute(XName.Get("xsi", XNamespace.Xmlns.NamespaceName));
                        if (xsiAttr == null)
                        {
                            var xsiNs = XNamespace.Xmlns + "xsi";
                            newDocElement.SetAttributeValue(xsiNs, "http://www.w3.org/2001/XMLSchema-instance");
                        }
                        else
                        {
                            var xsiNs = XNamespace.Xmlns + "xsi";
                            newDocElement.SetAttributeValue(xsiNs, xsiAttr.Value);
                        }

                        // Copiar elementos filhos recursivamente
                        CopyElementsRecursively(documentElement, newDocElement);

                        // Criar novo documento
                        var newDoc = new XDocument(newDocElement);

                        return newDoc.ToString();
                    }
                }

                return xmlContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML {DocumentTag}", documentTag);
                return xmlContent;
            }
        }

        /// <summary>
        /// Copia elementos recursivamente preservando estrutura
        /// </summary>
        private void CopyElementsRecursively(XElement source, XElement target)
        {
            foreach (var child in source.Elements())
            {
                var newChild = new XElement(child.Name);

                // Copiar atributos
                foreach (var attr in child.Attributes())                
                    newChild.SetAttributeValue(attr.Name, attr.Value);                

                // Copiar valor de texto se não tiver filhos
                if (!child.HasElements && !string.IsNullOrWhiteSpace(child.Value))                
                    newChild.Value = child.Value;                

                // Copiar filhos recursivamente
                if (child.HasElements)                
                    CopyElementsRecursively(child, newChild);               

                target.Add(newChild);
            }
        }
    }
}