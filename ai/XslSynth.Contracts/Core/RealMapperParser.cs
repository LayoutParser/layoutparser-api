using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using XslSynth.Model;

namespace XslSynth.Core;

/// <summary>
/// Passo 1 do loop, para o MapeadorVO REAL do Sysmiddle (descriptografado).
///
/// Diferente do <see cref="MapperExtractor"/> (que assume GUID==XPath do sample
/// sintético), este lê a estrutura REAL:
///   • LinkMappingItem: InputLayoutGuid/TargetLayoutGuid são GUIDs (FLD_/TAG_/…),
///     não paths. A FOLHA de destino é derivada do sufixo de <c>Name</c>
///     (convenção Sysmiddle: <c>Descricao_nomeDaTag</c>).
///   • Rule.ContentValue é DSL Sysmiddle. O caminho de destino é extraído da
///     primeira atribuição <c>T.&lt;path&gt;</c> (já é o path completo na NF-e).
///
/// ⚠️ ENCODING: o arquivo declara <c>encoding="utf-16"</c> mas os bytes são UTF-8
/// (com BOM). Lemos como bytes, removemos o BOM e trocamos a declaração para
/// <c>utf-8</c> antes de parsear — senão o XmlReader/XDocument falha.
/// </summary>
public sealed class RealMapperParser
{
    // Captura a primeira atribuição de saída T.<path> = ... na DSL.

    public MapperVo ParseFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        var text = DecodeAndFixDeclaration(raw);
        return Parse(XDocument.Parse(text));
    }

    /// <summary>Decodifica UTF-8 (removendo BOM) e corrige a declaração utf-16→utf-8.</summary>
    public static string DecodeAndFixDeclaration(byte[] raw)
    {
        // UTF8.GetString respeitando/descartando o BOM manualmente.
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(raw);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..]; // remove BOM residual

        // Troca APENAS na declaração XML (primeira ocorrência).
        return Regex.Replace(text, "encoding=\"utf-16\"", "encoding=\"utf-8\"",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
    }

    public MapperVo Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidOperationException("MapperVO vazio.");

        var mapper = new MapperVo
        {
            MapperGuid = root.Element("MapperGuid")?.Value,
            Name = root.Element("Name")?.Value,
            Description = root.Element("Description")?.Value,
            InputLayoutGuid = root.Element("InputLayoutGuid")?.Value,
            TargetLayoutGuid = root.Element("TargetLayoutGuid")?.Value,
            XslContent = root.Element("XslContent")?.Value ?? root.Element("Xsl")?.Value
        };

        foreach (var el in root.Descendants("LinkMappingItem"))
        {
            var name = el.Element("Name")?.Value;
            var targetGuid = el.Element("TargetLayoutGuid")?.Value;
            mapper.LinkMappings.Add(new LinkMappingItem
            {
                Name = name,
                Sequence = ParseInt(el.Element("Sequence")?.Value),
                ElementGuid = el.Element("ElementGuid")?.Value,
                InputGuid = el.Element("InputLayoutGuid")?.Value,
                TargetGuid = targetGuid,
                TargetType = GuidPrefix(targetGuid),
                TargetLeafName = LeafFromName(name),
                IsToTruncateValue = ParseBool(el.Element("IsToTruncateValue")?.Value),
                RemoveWhiteSpaceType = el.Element("RemoveWhiteSpaceType")?.Value,
                DefaultValue = el.Element("DefaultValue")?.Value,
                AllowEmpty = ParseBool(el.Element("AllowEmpty")?.Value),
                IsRequired = ParseBool(el.Element("IsRequired")?.Value)
            });
        }

        foreach (var el in root.Descendants("Rule"))
        {
            var dsl = el.Element("ContentValue")?.Value;
            var targetGuid = el.Element("TargetElementGuid")?.Value;
            mapper.Rules.Add(new MapperRule
            {
                Name = el.Element("Name")?.Value,
                Sequence = ParseInt(el.Element("Sequence")?.Value),
                Description = el.Element("Description")?.Value,
                ElementGuid = el.Element("ElementGuid")?.Value,
                TargetElementGuid = targetGuid,
                TargetType = GuidPrefix(targetGuid),
                ParentElement = el.Element("ParentElement")?.Value,
                ContentValue = dsl,
                // Path de destino vem da DSL (T.<path>); fallback = sufixo do Name.
                TargetPath = TargetPathFromDsl(dsl) ?? LeafFromName(el.Element("Name")?.Value),
                IsRequired = ParseBool(el.Element("IsRequired")?.Value)
            });
        }

        return mapper;
    }

    /// <summary>Extrai o primeiro caminho de saída <c>T.&lt;path&gt;</c> da DSL.</summary>
    public static string? TargetPathFromDsl(string? dsl)
    {
        if (string.IsNullOrWhiteSpace(dsl)) return null;
        return SysmiddleDslTokenizer.TargetPaths(dsl).FirstOrDefault();
    }

    /// <summary>
    /// Nome da folha de destino a partir da convenção Sysmiddle <c>Descricao_tag</c>:
    /// o trecho após o ÚLTIMO '_'. Ex.: "NomeDoMunicipio_xMun" → "xMun".
    /// </summary>
    public static string? LeafFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var idx = name.LastIndexOf('_');
        return idx >= 0 && idx < name.Length - 1 ? name[(idx + 1)..] : name;
    }

    private static string? GuidPrefix(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return null;
        var idx = guid.IndexOf('_');
        return idx > 0 ? guid[..idx] : null;
    }

    private static int ParseInt(string? v) => int.TryParse(v, out var n) ? n : 0;
    private static bool ParseBool(string? v) => bool.TryParse(v, out var b) && b;
}
