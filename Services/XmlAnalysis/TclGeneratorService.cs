using System.Text;
using System.Xml.Linq;

using LayoutParserApi.Services.XmlAnalysis.Models;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Gera arquivo TCL a partir de layout XML MQSeries
    /// </summary>
    public class TclGeneratorService
    {
        private readonly ILogger<TclGeneratorService> _logger;

        public TclGeneratorService(ILogger<TclGeneratorService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gera arquivo TCL a partir de layout XML
        /// </summary>
        public async Task<string> GenerateTclFromLayoutAsync(string layoutXmlPath, string outputPath = null)
        {
            try
            {
                _logger.LogInformation("Iniciando geração de TCL a partir do layout: {Path}", layoutXmlPath);

                // Ler layout XML
                var layoutXml = await File.ReadAllTextAsync(layoutXmlPath, Encoding.UTF8);
                var layoutDoc = XDocument.Parse(layoutXml);

                // Gerar TCL
                var tclContent = GenerateTclContent(layoutDoc);

                // Salvar arquivo se outputPath fornecido
                if (!string.IsNullOrEmpty(outputPath))
                {
                    await File.WriteAllTextAsync(outputPath, tclContent, Encoding.UTF8);
                    _logger.LogInformation("Arquivo TCL gerado com sucesso: {Path}", outputPath);
                }

                return tclContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar TCL a partir do layout");
                throw;
            }
        }

        /// <summary>
        /// Gera o TCL a partir do XML de layout já em memória (sem I/O; usado pelo studio-model, Fase 2).
        /// Lança <see cref="System.Xml.XmlException"/> se o XML for inválido — o chamador degrada.
        /// </summary>
        public string GenerateTclFromLayoutXml(string layoutXml)
            => GenerateTclContent(XDocument.Parse(layoutXml));

        /// <summary>
        /// Gera conteúdo TCL a partir do documento de layout
        /// </summary>
        private string GenerateTclContent(XDocument layoutDoc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<MAP>");

            // Extrair elementos de linha do layout
            var lineElements = ExtractLineElements(layoutDoc);

            // Ordenar linhas por sequência
            var orderedLines = lineElements.OrderBy(l => GetLineSequence(l.Key)).ToList();

            // Gerar definições de linha no formato TCL
            foreach (var line in orderedLines)
            {
                var lineIdentifier = GetLineIdentifier(line.Key);
                var lineName = line.Key;
                var fields = line.Value;

                sb.AppendLine($"\t<LINE identifier=\"{lineIdentifier}\" name=\"{lineName}\">");

                // Adicionar campos (ordenados por posição)
                var sortedFields = fields.OrderBy(f => f.StartPosition).ToList();
                foreach (var field in sortedFields)
                {
                    var fieldName = SanitizeFieldName(field.Name);
                    var fieldLength = field.Length;
                    var lengthAttr = fieldLength.ToString();

                    // Para campos decimais, usar formato "15,2,0"
                    if (field.IsDecimal && field.DecimalPlaces > 0)
                        lengthAttr = $"{fieldLength},{field.DecimalPlaces},0";

                    sb.AppendLine($"\t\t<FIELD name=\"{fieldName}\" length=\"{lengthAttr}\"/>");
                }

                // Adicionar filhos se houver (buscar linhas filhas no layout)
                var children = GetChildrenElements(layoutDoc, lineName);
                foreach (var child in children)
                    sb.AppendLine($"\t\t<CHILD>{child}</CHILD>");

                sb.AppendLine("\t</LINE>");
                sb.AppendLine();
            }

            sb.AppendLine("</MAP>");
            return sb.ToString();
        }

        /// <summary>
        /// Extrai elementos de linha do layout XML
        /// </summary>
        private Dictionary<string, List<FieldInfo>> ExtractLineElements(XDocument layoutDoc)
        {
            var lines = new Dictionary<string, List<FieldInfo>>();

            try
            {
                var root = layoutDoc.Root;
                if (root == null) return lines;

                // Procurar elementos de linha (LineElementVO)
                var lineElements = root.Descendants()
                    .Where(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "LineElementVO" ||
                               (e.Name.LocalName == "Element" && e.Parent?.Name.LocalName == "Elements")).ToList();

                foreach (var lineElement in lineElements)
                {
                    // Verificar se é realmente uma linha
                    var typeAttr = lineElement.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"));
                    if (typeAttr?.Value != "LineElementVO")
                    {
                        // Verificar se tem filhos que são FieldElementVO
                        var hasFields = lineElement.Descendants().Any(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "FieldElementVO");
                        if (!hasFields) continue;
                    }

                    var lineName = GetLineName(lineElement);
                    if (string.IsNullOrEmpty(lineName)) continue;

                    var fields = ExtractFieldsFromLine(lineElement);
                    if (fields.Any())
                        lines[lineName] = fields;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao extrair elementos de linha");
            }

            return lines;
        }

        /// <summary>
        /// Extrai campos de uma linha
        /// </summary>
        private List<FieldInfo> ExtractFieldsFromLine(XElement lineElement)
        {
            var fields = new List<FieldInfo>();

            // Procurar campos filhos (FieldElementVO)
            var fieldElements = lineElement.Descendants()
                .Where(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "FieldElementVO").ToList();

            // Se não encontrou, procurar elementos filhos diretos
            if (!fieldElements.Any())
            {
                fieldElements = lineElement.Elements().Where(e => e.Name.LocalName == "Element" || e.Name.LocalName == "FieldElement").ToList();
            }

            int currentPosition = 1; // Posição acumulativa

            foreach (var fieldElem in fieldElements.OrderBy(f => GetFieldSequence(f)))
            {
                var fieldInfo = ExtractFieldInfo(fieldElem);
                if (fieldInfo != null)
                {
                    // Se StartValue é 1, significa que é posição relativa, usar posição acumulativa
                    if (fieldInfo.StartPosition == 1 && fields.Any())
                    {
                        // Calcular posição acumulativa
                        var lastField = fields.Last();
                        fieldInfo.StartPosition = lastField.StartPosition + lastField.Length;
                    }

                    fields.Add(fieldInfo);
                    currentPosition = fieldInfo.StartPosition + fieldInfo.Length;
                }
            }

            return fields;
        }

        /// <summary>
        /// Obtém sequência de um campo para ordenação
        /// </summary>
        private int GetFieldSequence(XElement fieldElement)
        {
            var sequenceElem = fieldElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sequence")
                              ?? fieldElement.Element("Sequence");
            if (int.TryParse(sequenceElem?.Value, out var seq))
                return seq;
            
            return 0;
        }

        /// <summary>
        /// Extrai informações de um campo
        /// </summary>
        private FieldInfo ExtractFieldInfo(XElement fieldElement)
        {
            try
            {
                // Nome do campo
                var nameElem = fieldElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name") ?? fieldElement.Element("Name");
                var name = nameElem?.Value;

                // Tamanho do campo (LengthField)
                var lengthElem = fieldElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "LengthField") ?? fieldElement.Element("LengthField");
                var length = int.TryParse(lengthElem?.Value, out var l) ? l : 0;

                // StartValue (posição inicial relativa)
                var startValueElem = fieldElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "StartValue") ?? fieldElement.Element("StartValue");
                var startValue = int.TryParse(startValueElem?.Value, out var sv) ? sv : 1;

                if (string.IsNullOrEmpty(name) || length == 0)
                    return null;

                // Detectar se é decimal (procura por campos de valor monetário)
                var isDecimal = name.Contains("Valor") || name.Contains("Preco") || name.Contains("Total") ||
                               (name.Length > 1 && name.StartsWith("v") && char.IsUpper(name[1])) || name.Contains("vBC") || name.Contains("vICMS") || name.Contains("vST");

                // Para campos decimais, tentar detectar casas decimais
                var decimalPlaces = 2; // Padrão
                if (isDecimal)
                {
                    // Procurar por padrões como "15,2" no nome ou descrição
                    var descElem = fieldElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "Description");
                    var desc = descElem?.Value ?? "";
                    var decimalMatch = System.Text.RegularExpressions.Regex.Match(desc, @"(\d+),(\d+)");
                    if (decimalMatch.Success && int.TryParse(decimalMatch.Groups[2].Value, out var dp))
                        decimalPlaces = dp;
                }

                return new FieldInfo
                {
                    Name = name,
                    Length = length,
                    StartPosition = startValue,
                    IsDecimal = isDecimal,
                    DecimalPlaces = decimalPlaces
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair informações do campo");
                return null;
            }
        }

        /// <summary>
        /// Obtém nome da linha
        /// </summary>
        private string GetLineName(XElement element)
        {
            var nameElem = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")
                          ?? element.Element("Name");
            if (nameElem != null)
                return nameElem.Value;

            var nameAttr = element.Attribute("Name");
            if (nameAttr != null)
                return nameAttr.Value;

            return null;
        }

        /// <summary>
        /// Obtém identificador da linha para TCL
        /// </summary>
        private string GetLineIdentifier(string lineName)
        {
            if (string.IsNullOrEmpty(lineName))
                return "UNKNOWN";

            // HEADER -> HEADER (especial)
            if (lineName == "HEADER") return "HEADER";
            if (lineName == "TRAILER" || lineName.Contains("TRAILER")) return "TRAILER";

            // LINHA000 -> A, LINHA001 -> B, LINHA002 -> C, etc.
            if (lineName.StartsWith("LINHA"))
            {
                var numberStr = lineName.Replace("LINHA", "");
                if (int.TryParse(numberStr, out var num))
                {
                    // Mapear números para letras sequenciais
                    // 000 -> A (65), 001 -> B (66), etc.
                    if (num <= 25)
                        return ((char)('A' + num)).ToString();
                    else if (num <= 51)
                    {
                        // AA, AB, AC, etc.
                        var first = (char)('A' + (num - 26) / 26);
                        var second = (char)('A' + (num - 26) % 26);
                        return first.ToString() + second.ToString();
                    }
                    else
                        // Usar número formatado: Z01, Z02, etc.
                        return "Z" + num.ToString("00");
                }
            }

            // Para outras linhas, usar primeira letra
            var firstChar = lineName.Where(char.IsLetter).FirstOrDefault();
            if (firstChar != default(char))
                return firstChar.ToString().ToUpper();

            return "UNKNOWN";
        }

        /// <summary>
        /// Obtém sequência da linha para ordenação
        /// </summary>
        private int GetLineSequence(string lineName)
        {
            if (lineName == "HEADER") return 0;
            if (lineName == "TRAILER") return 9999;

            if (lineName.StartsWith("LINHA"))
            {
                var number = lineName.Replace("LINHA", "");
                if (int.TryParse(number, out var num))
                    return num + 1;
            }

            return 5000;
        }

        /// <summary>
        /// Obtém elementos filhos de uma linha
        /// </summary>
        private List<string> GetChildrenElements(XDocument layoutDoc, string lineName)
        {
            var children = new List<string>();

            try
            {
                // Procurar linhas que têm ParentElement apontando para esta linha
                var allLines = layoutDoc.Descendants()
                    .Where(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "LineElementVO").ToList();

                foreach (var line in allLines)
                {
                    var parentElem = line.Descendants().FirstOrDefault(e => e.Name.LocalName == "ParentElement");
                    if (parentElem != null)
                    {
                        var parentValue = parentElem.Value;
                        if (parentValue.Contains(lineName))
                        {
                            var childName = GetLineName(line);
                            if (!string.IsNullOrEmpty(childName) && childName != lineName)
                                children.Add(childName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao obter elementos filhos");
            }

            return children.Distinct().ToList();
        }

        /// <summary>
        /// Sanitiza nome do campo para TCL
        /// </summary>
        private string SanitizeFieldName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName))
                return "Unknown";

            // Remover caracteres especiais e espaços
            var sanitized = fieldName.Replace(" ", "").Replace("-", "").Replace("_", "");

            // Primeira letra minúscula (camelCase)
            if (sanitized.Length > 0)
                sanitized = char.ToLower(sanitized[0]) + sanitized.Substring(1);

            return sanitized;
        }
    }
}