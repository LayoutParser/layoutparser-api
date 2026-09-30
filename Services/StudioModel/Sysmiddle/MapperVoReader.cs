using System.Globalization;
using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.StudioModel.Sysmiddle
{
    /// <summary>Vínculo lido do MapperVO. <c>SourceId</c>/<c>TargetId</c> são ElementGuids (nomes enganosos no XML).</summary>
    public sealed record RawLink(string Id, string? SourceId, string? TargetId, int Order, string? Name, StudioLinkOpts Opts);

    public sealed record RawRule(string Id, string? AnchorId, string? Name, string? Code, bool PrePos);

    public sealed record MapperReadResult(
        string? MapperGuid,
        string? Name,
        string? InputLayoutGuid,
        string? TargetLayoutGuid,
        List<RawLink> Links,
        List<RawRule> Rules,
        HashSet<string> VariantFields);

    /// <summary>
    /// Leitor PRÓPRIO do MapperVO (design §3.2/§8-8). Tolerante a ordem/presença de elementos (lookup por nome
    /// local). ATENÇÃO ao nome enganoso: dentro de <c>LinkMappingItem</c>, <c>InputLayoutGuid</c> = ElementGuid
    /// da ORIGEM e <c>TargetLayoutGuid</c> = ElementGuid do DESTINO. XML inválido lança
    /// <see cref="System.Xml.XmlException"/> (chamador degrada com <c>MAPPER_UNREADABLE</c>).
    /// </summary>
    public static class MapperVoReader
    {
        public static MapperReadResult Read(string xml)
        {
            var root = XDocument.Parse(xml).Root ?? throw new System.Xml.XmlException("MapperVO sem elemento raiz.");

            var variants = new HashSet<string>(StringComparer.Ordinal);
            VariantFieldNames.Collect(root, variants);

            var links = new List<RawLink>();
            var idx = 0;
            foreach (var item in Descendants(root, "LinkMappings", "LinkMappingItem"))
            {
                idx++;
                var id = Text(item, "ElementGuid");
                if (string.IsNullOrWhiteSpace(id)) id = $"link:{idx}";
                var opts = new StudioLinkOpts(
                    Empty(Text(item, "RemoveWhiteSpaceType")),
                    Bool(Text(item, "IsToTruncateValue")),
                    Empty(Text(item, "DefaultValue")),
                    Bool(Text(item, "AllowEmpty")),
                    Bool(Text(item, "NotCreateGroupTagOnlyChilds")),
                    Bool(Text(item, "CreateEmptyElement")),
                    Bool(Text(item, "UniqueOccurrence")),
                    Bool(Text(item, "CreateValuesDirectBySourceElement")),
                    Bool(Text(item, "IsToUseDecimalMapper")),
                    Empty(Text(item, "FullXPath")));
                links.Add(new RawLink(
                    id,
                    Empty(Text(item, "InputLayoutGuid")),
                    Empty(Text(item, "TargetLayoutGuid")),
                    Int(Text(item, "Sequence")) ?? idx,
                    Empty(Text(item, "Name")),
                    opts));
            }

            var rules = new List<RawRule>();
            idx = 0;
            foreach (var item in Descendants(root, "Rules", "Rule"))
            {
                idx++;
                var id = Text(item, "ElementGuid");
                if (string.IsNullOrWhiteSpace(id)) id = $"rule:{idx}";
                // Código da regra: ContentValue (observado); Code aceito por tolerância a versões.
                var code = Text(item, "ContentValue", trim: false) ?? Text(item, "Code", trim: false);
                rules.Add(new RawRule(
                    id,
                    Empty(Text(item, "TargetElementGuid")),
                    Empty(Text(item, "Name")),
                    code,
                    Bool(Text(item, "IsPrePosRule")) ?? false));
            }

            return new MapperReadResult(
                Empty(Text(root, "MapperGuid")), Empty(Text(root, "Name")),
                Empty(Text(root, "InputLayoutGuid")), Empty(Text(root, "TargetLayoutGuid")),
                links, rules, variants);
        }

        private static IEnumerable<XElement> Descendants(XElement root, string container, string item)
            => root.Elements().Where(e => e.Name.LocalName == container)
                   .SelectMany(c => c.Elements().Where(e => e.Name.LocalName == item));

        private static string? Text(XElement el, string localName, bool trim = true)
        {
            var c = el.Elements().FirstOrDefault(x => x.Name.LocalName == localName);
            if (c == null) return null;
            return trim ? c.Value.Trim() : c.Value;
        }

        private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        private static bool? Bool(string? s) => s != null && bool.TryParse(s, out var b) ? b : null;
    }
}
