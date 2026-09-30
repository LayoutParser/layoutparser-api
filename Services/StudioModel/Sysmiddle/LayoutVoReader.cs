using System.Globalization;
using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.StudioModel.Sysmiddle
{
    /// <summary>Referência a um <c>DataTypeVO</c> (GUID + nome, este <c>null</c> se o catálogo não está disponível).</summary>
    public sealed record StudioDataTypeRef(string Guid, string? Name);

    /// <summary>Conjunto fechado de elementos OPCIONAIS de versões novas (design §8-8) — alimenta <c>variantFields</c>.</summary>
    public static class VariantFieldNames
    {
        public static readonly string[] All =
        {
            "FullXPath", "ElementType", "ParentElement", "UniqueOccurrence", "CreateValuesDirectBySourceElement",
            "IsToUseDecimalMapper", "ElementWithoutValue", "XPathLinkMappings", "StaticValueMappings", "IsXPathMapper",
        };

        public static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

        public static void Collect(XElement root, HashSet<string> into)
        {
            foreach (var e in root.DescendantsAndSelf())
                if (Set.Contains(e.Name.LocalName)) into.Add(e.Name.LocalName);
        }
    }

    /// <summary>Resultado da leitura de um LayoutVO. <c>Nodes</c> em ordem de árvore (pré-ordem, irmãos por <c>order</c>).</summary>
    public sealed record LayoutReadResult(
        string? LayoutGuid,
        string? Name,
        string Format,
        List<string> RootIds,
        List<KeyValuePair<string, StudioNode>> Nodes,
        List<StudioDiagnostic> Diagnostics,
        HashSet<string> VariantFields);

    /// <summary>
    /// Leitor PRÓPRIO do LayoutVO Sysmiddle (design §3.2/§8-6) — não reutiliza <c>GuidXPathCatalog.BuildTree</c>
    /// (que achata Choice/Sequence e não lê props). Tolerante: lookup por nome local (sem depender de ordem
    /// nem de namespace), ignora elementos desconhecidos, aceita o quirk <c>&lt;Elements&gt;&lt;Elements&gt;</c>
    /// do TagElementVO e nunca lança por tipo/elemento desconhecido. XML inválido lança <see cref="System.Xml.XmlException"/>
    /// (o chamador degrada o lado para <c>available:false</c>).
    /// </summary>
    public static class LayoutVoReader
    {
        // (nome da prop no contrato, elemento XML, tipo: s=string, i=int, b=bool)
        private static readonly (string Prop, string Xml, char K) Min = ("minOccurs", "MinimalOccurrence", 'i');
        private static readonly (string Prop, string Xml, char K) Max = ("maxOccurs", "MaximumOccurrence", 'i');
        private static readonly (string Prop, string Xml, char K) Initial = ("initialValue", "InitialValue", 's');
        private static readonly (string Prop, string Xml, char K) Trim = ("trim", "RemoveWhiteSpaceType", 's');
        private static readonly (string Prop, string Xml, char K) Length = ("length", "LengthField", 'i');
        private static readonly (string Prop, string Xml, char K) StaticVal = ("staticValue", "StaticValue", 's');
        private static readonly (string Prop, string Xml, char K) IsStatic = ("isStaticValue", "IsStaticValue", 'b');
        private static readonly (string Prop, string Xml, char K) CData = ("useCData", "IsUseCData", 'b');
        private static readonly (string Prop, string Xml, char K) AcceptEmpty = ("acceptEmpty", "AcceptEmpty", 'b');

        private static readonly Dictionary<string, (string Prop, string Xml, char K)[]> Spec = new()
        {
            ["line"] = new[]
            {
                Initial, Min, Max,
                ("validateLength", "IsToValidateLengthCharacters", 'b'),
                ("validateFieldLesserLength", "IsToValidateFieldLesserLength", 'b'),
                ("positionalGroupRepetition", "IsPositionalGroupRepetition", 'b'),
                ("notRealizeParser", "NotRealizeParser", 'b'),
                ("createOnlyChildren", "CreateOnlyChildren", 'b'),
            },
            ["field"] = new[]
            {
                Length, ("align", "AlignmentType", 's'), Trim, StaticVal, IsStatic,
                ("isSequential", "IsSequential", 'b'), ("startValue", "StartValue", 'i'),
                ("incrementValue", "IncrementValue", 'i'), ("caseSensitive", "IsCaseSensitiveValue", 'b'),
                Initial, Min, Max,
            },
            ["repeaterGroup"] = new[] { Min, Max, Initial },
            ["grouper"] = new[] { Min, Max },
            ["groupTag"] = new[]
            {
                Min, Max, ("notCreateGroupTagOnlyChilds", "NotCreateGroupTagOnlyChilds", 'b'), AcceptEmpty, CData,
            },
            ["tag"] = new[]
            {
                CData, AcceptEmpty, ("containsAttribute", "IsContainsAttribute", 'b'), Min, Max, Length, Trim, StaticVal, IsStatic,
            },
            ["attribute"] = new[] { Trim, StaticVal, IsStatic },
            ["choice"] = new[] { Min, Max },
            ["sequence"] = new[] { Min, Max },
            ["jsonObject"] = new[] { Min, Max },
            ["characterIgnoreGroup"] = new[] { Min, Max },
            ["groupWithoutOrder"] = new[] { Min, Max },
        };

        private static readonly HashSet<string> TypesWithDataType = new() { "field", "tag", "attribute", "jsonObject" };

        private static readonly HashSet<string> CommonConsumed = new(StringComparer.Ordinal)
        {
            "ElementGuid", "Description", "Sequence", "Name", "IsRequired", "DataTypeGuid", "Element", "Elements",
        };

        /// <param name="tree"><c>input</c> | <c>target</c>.</param>
        /// <param name="dataTypeName">Resolvedor opcional GUID <c>DAT_</c> → nome; <c>null</c> = sem catálogo (nome vira <c>?</c> no display).</param>
        public static LayoutReadResult Read(string xml, string tree, Func<string, string?>? dataTypeName = null)
        {
            var root = XDocument.Parse(xml).Root ?? throw new System.Xml.XmlException("LayoutVO sem elemento raiz.");

            var variants = new HashSet<string>(StringComparer.Ordinal);
            VariantFieldNames.Collect(root, variants);

            var format = ResolveFormat(root);
            var ctx = new Ctx(tree, format, dataTypeName);

            var rootItems = ItemsOf(root);
            WalkSiblings(rootItems, parentId: null, parentPath: string.Empty, ctx);

            var rootIds = ctx.Nodes.Where(n => n.Value.ParentId == null).Select(n => n.Key).ToList();
            return new LayoutReadResult(
                Text(root, "LayoutGuid"), Text(root, "Name"), format, rootIds, ctx.Nodes, ctx.Diagnostics, variants);
        }

        private sealed class Ctx
        {
            public Ctx(string tree, string format, Func<string, string?>? dt) { Tree = tree; Format = format; DataTypeName = dt; }
            public string Tree { get; }
            public string Format { get; }
            public Func<string, string?>? DataTypeName { get; }
            public List<KeyValuePair<string, StudioNode>> Nodes { get; } = new();
            public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
            public List<StudioDiagnostic> Diagnostics { get; } = new();
        }

        private static void WalkSiblings(List<XElement> items, string? parentId, string parentPath, Ctx ctx)
        {
            // Ordem: Sequence (quando presente), empate/ausência = posição no documento.
            var ordered = items
                .Select((el, idx) => (el, idx, seq: IntOrNull(Text(el, "Sequence"))))
                .OrderBy(x => x.seq ?? int.MaxValue)
                .ThenBy(x => x.idx)
                .ToList();

            var positional = ctx.Format == "text-positional";
            int? runningOffset = 0;
            var position = 0;

            foreach (var (el, docIdx, seq) in ordered)
            {
                position++;
                var xsiType = XsiType(el);
                var type = NodeTypeMap.Resolve(xsiType);
                var name = Text(el, "Name") ?? string.Empty;

                string path;
                if (NodeTypeMap.IsStructuralOnly(type)) path = parentPath;
                else if (type == "attribute") path = parentPath.Length == 0 ? "@" + name : parentPath + "/@" + name;
                else path = parentPath.Length == 0 ? name : parentPath + "/" + name;

                var id = Text(el, "ElementGuid");
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = $"idx:{ctx.Tree}:{parentId ?? "root"}/{docIdx}";
                    ctx.Diagnostics.Add(new StudioDiagnostic("MISSING_ELEMENT_GUID", "warning",
                        "Elemento sem ElementGuid; id sintético gerado.", Id: id, Path: path));
                }
                if (!ctx.Ids.Add(id))
                {
                    ctx.Diagnostics.Add(new StudioDiagnostic("DUPLICATE_NODE_ID", "warning",
                        "ElementGuid repetido no layout; nó ignorado.", Id: id, Path: path));
                    continue;
                }

                if (type == NodeTypeMap.Unknown)
                    ctx.Diagnostics.Add(new StudioDiagnostic("UNKNOWN_NODE_TYPE", "warning",
                        "Tipo de elemento fora da tabela conhecida.", Id: id, XsiType: xsiType));

                var children = ItemsOf(el);
                var (props, typeName, length) = BuildProps(el, type, xsiType, ctx);

                // offset: só posicional, só campos; soma dos length dos irmãos-campo anteriores.
                if (positional && type == "field")
                {
                    if (runningOffset.HasValue) props["offset"] = runningOffset.Value;
                    else ctx.Diagnostics.Add(new StudioDiagnostic("OFFSET_UNRESOLVED", "warning",
                        "Offset não calculável: campo anterior sem length.", Id: id));
                    runningOffset = runningOffset.HasValue && length.HasValue ? runningOffset + length : null;
                }

                var required = BoolOrFalse(Text(el, "IsRequired"));
                var display = new StudioNodeDisplay(
                    children.Count == 0 && type is "field" or "tag" or "attribute"
                        ? DisplayTextBuilder.ForValue(name, required, typeName)
                        : DisplayTextBuilder.ForContainer(name, required,
                            props.TryGetValue("minOccurs", out var mn) ? mn as int? : null,
                            props.TryGetValue("maxOccurs", out var mx) ? mx as int? : null),
                    NodeTypeMap.IconFor(type),
                    false);

                ctx.Nodes.Add(new(id, new StudioNode(ctx.Tree, type, name, path, parentId, seq ?? position, required, display, props)));

                if (children.Count > 0) WalkSiblings(children, id, path, ctx);
            }
        }

        private static (Dictionary<string, object?> Props, string? TypeName, int? Length) BuildProps(XElement el, string type, string? xsiType, Ctx ctx)
        {
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            var consumed = new HashSet<string>(CommonConsumed, StringComparer.Ordinal);

            var desc = Text(el, "Description");
            if (!string.IsNullOrEmpty(desc)) props["description"] = desc;

            if (type == NodeTypeMap.Unknown && xsiType != null) props["xsiType"] = xsiType;

            int? length = null;
            if (Spec.TryGetValue(type, out var spec))
            {
                foreach (var (prop, xml, k) in spec)
                {
                    consumed.Add(xml);
                    var raw = Text(el, xml);
                    if (raw == null) continue;
                    object? value = k switch
                    {
                        'i' => IntOrNull(raw),
                        'b' => BoolOrNull(raw),
                        _ => raw.Length == 0 ? null : raw,
                    };
                    if (value == null) continue;
                    props[prop] = value;
                    if (prop == "length") length = (int)value;
                }
            }

            string? typeName = null;
            if (TypesWithDataType.Contains(type))
            {
                var dt = Text(el, "DataTypeGuid");
                if (!string.IsNullOrWhiteSpace(dt))
                {
                    try { typeName = ctx.DataTypeName?.Invoke(dt); } catch { typeName = null; }
                    props["dataType"] = new StudioDataTypeRef(dt, typeName);
                }
            }

            // Escalares lidos mas não mapeados: não perder fidelidade de leitura (design §2.2).
            Dictionary<string, string>? extra = null;
            foreach (var c in el.Elements())
            {
                var n = c.Name.LocalName;
                if (consumed.Contains(n) || VariantFieldNames.Set.Contains(n) || c.HasElements) continue;
                var v = c.Value.Trim();
                if (v.Length == 0) continue;
                (extra ??= new(StringComparer.Ordinal))[n] = v;
            }
            if (extra != null) props["extra"] = extra;

            return (props, typeName, length);
        }

        // ---- helpers de XML (lookup por nome local; sem depender de namespace/ordem) ----

        private static string ResolveFormat(XElement root)
        {
            var layoutType = Text(root, "LayoutType");
            if (!string.IsNullOrWhiteSpace(layoutType))
            {
                switch (layoutType.Replace(" ", "").ToLowerInvariant())
                {
                    case "textpositional": return "text-positional";
                    case "textdelimited": return "text-delimited";
                    case "xml": return "xml";
                    case "json": return "json";
                }
            }
            var xsi = XsiType(root) ?? string.Empty;
            if (xsi.Contains("Xml", StringComparison.OrdinalIgnoreCase)) return "xml";
            if (xsi.Contains("Json", StringComparison.OrdinalIgnoreCase)) return "json";
            if (xsi.Contains("Text", StringComparison.OrdinalIgnoreCase)) return "text-positional";
            return "unknown";
        }

        private static string? XsiType(XElement el)
            => el.Attributes().FirstOrDefault(a => a.Name.LocalName == "type" && a.Name.Namespace != XNamespace.None)?.Value
               ?? el.Attribute("type")?.Value;

        private static bool IsItem(XElement e)
            => e.Elements().Any(c => c.Name.LocalName == "ElementGuid") || XsiType(e) != null;

        /// <summary>
        /// Itens-filhos de um nó: aceita <c>Elements/Element</c> e o quirk <c>Elements/Elements</c>; wrappers
        /// sem ElementGuid/xsi:type são achatados.
        /// </summary>
        private static List<XElement> ItemsOf(XElement node)
        {
            var result = new List<XElement>();
            foreach (var container in node.Elements().Where(c => c.Name.LocalName == "Elements" && !IsItem(c)))
                Collect(container, result);
            return result;
        }

        private static void Collect(XElement container, List<XElement> into)
        {
            foreach (var c in container.Elements().Where(c => c.Name.LocalName is "Element" or "Elements"))
            {
                if (IsItem(c)) into.Add(c);
                else Collect(c, into);
            }
        }

        private static string? Text(XElement el, string localName)
        {
            var c = el.Elements().FirstOrDefault(x => x.Name.LocalName == localName);
            return c?.Value.Trim(); // Trim: o XML real traz quebras de linha dentro do texto.
        }

        private static int? IntOrNull(string? s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        private static bool? BoolOrNull(string? s)
            => s == null ? null : bool.TryParse(s, out var b) ? b : null;

        private static bool BoolOrFalse(string? s) => BoolOrNull(s) ?? false;
    }
}
