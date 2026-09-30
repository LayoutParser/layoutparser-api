using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.StudioModel.Tcl
{
    /// <summary>Resultado da leitura do TCL: nós na ordem da árvore, raízes e diagnósticos. Nunca lança.</summary>
    public sealed record TclReadResult(
        List<KeyValuePair<string, StudioNode>> Nodes,
        List<string> RootIds,
        List<StudioDiagnostic> Diagnostics);

    /// <summary>
    /// Leitor TOLERANTE do TCL do nosso motor (<c>&lt;MAP&gt;&lt;LINE identifier name&gt;&lt;FIELD name length/&gt;&lt;CHILD&gt;NOME&lt;/CHILD&gt;&lt;/LINE&gt;&lt;/MAP&gt;</c>).
    /// Parser próprio (não reutiliza <c>Scripts/GenerateTclAndXsl.cs</c>, que tem bug conhecido de CHILD/duplicação).
    /// IDs determinísticos <c>tcl:&lt;caminho-por-nome&gt;</c>; homônimos irmãos ganham sufixo <c>~2, ~3</c> por ordem
    /// (+ <c>AMBIGUOUS_NAME_PATH</c>). <c>offset</c> = soma dos <c>length</c> anteriores na mesma linha.
    /// Puro, sem I/O. DTD proibido (sem XXE); XML inválido vira diagnóstico, não exceção.
    /// </summary>
    public static class TclReader
    {
        public const string IdPrefix = "tcl:";

        private sealed record LineDef(string Name, string? Identifier, List<XElement> Items, int DocIndex);

        public static TclReadResult Read(string? tcl)
        {
            var result = new TclReadResult(new(), new(), new());

            XDocument doc;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var sr = new StringReader(tcl ?? string.Empty);
                using var xr = XmlReader.Create(sr, settings);
                doc = XDocument.Load(xr);
            }
            catch (Exception)
            {
                // Sem conteúdo do TCL na mensagem (pode conter dado do cliente).
                result.Diagnostics.Add(new StudioDiagnostic("TCL_UNPARSEABLE", "warning", "TCL ausente ou com XML inválido; árvore indisponível."));
                return result;
            }

            var lineEls = doc.Root?.Descendants().Where(e => Is(e, "LINE")).ToList() ?? new List<XElement>();
            var lines = new List<LineDef>();
            var n = 0;
            foreach (var el in lineEls)
            {
                n++;
                var name = Attr(el, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"LINE#{n}";
                    result.Diagnostics.Add(new StudioDiagnostic("TCL_MISSING_NAME", "warning", "LINE sem atributo name; nome sintético gerado.", Path: name));
                }
                lines.Add(new LineDef(name.Trim(), Attr(el, "identifier"), el.Elements().ToList(), n));
            }

            if (lines.Count == 0)
            {
                result.Diagnostics.Add(new StudioDiagnostic("TCL_NO_LINES", "warning", "TCL sem nenhuma LINE."));
                return result;
            }

            // Primeira definição vence por nome (CHILD referencia linha por nome).
            var byName = new Dictionary<string, LineDef>(StringComparer.Ordinal);
            foreach (var l in lines) byName.TryAdd(l.Name, l);

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in lines)
                foreach (var it in l.Items.Where(i => Is(i, "CHILD")))
                    if (byName.ContainsKey(it.Value.Trim()) && it.Value.Trim() != l.Name) referenced.Add(it.Value.Trim());

            var visited = new HashSet<LineDef>();
            var roots = lines.Where(l => !referenced.Contains(l.Name)).ToList();
            var siblingNames = new Dictionary<string, int>(StringComparer.Ordinal);
            var position = 0;
            foreach (var root in roots)
                EmitLine(root, null, string.Empty, ++position, siblingNames, new HashSet<LineDef>(), visited, byName, result);

            // Linhas só alcançáveis por ciclo: viram raízes (nada some do modelo).
            foreach (var l in lines.Where(l => !visited.Contains(l)))
            {
                result.Diagnostics.Add(new StudioDiagnostic("TCL_CHILD_CYCLE", "warning", "LINE só alcançável por referência CHILD cíclica; promovida a raiz.", Path: l.Name));
                EmitLine(l, null, string.Empty, ++position, siblingNames, new HashSet<LineDef>(), visited, byName, result);
            }

            // AMBIGUOUS_NAME_PATH: irmãos homônimos (ids já sufixados).
            foreach (var g in result.Nodes.GroupBy(kv => (kv.Value.ParentId, kv.Value.Path)).Where(g => g.Count() >= 2))
                result.Diagnostics.Add(new StudioDiagnostic("AMBIGUOUS_NAME_PATH", "warning",
                    "Mais de um nó com o mesmo caminho por nome; ids desambiguados com sufixo ~N.",
                    Path: g.Key.Path, Ids: g.Select(x => x.Key).ToList()));

            return result;
        }

        private const int MaxDepth = 64;

        private static void EmitLine(
            LineDef line, string? parentId, string parentPath, int order,
            Dictionary<string, int> rootSiblingNames, HashSet<LineDef> stack, HashSet<LineDef> visited,
            Dictionary<string, LineDef> byName, TclReadResult result)
        {
            // Nomes de irmãos são controlados por quem chama: raiz usa rootSiblingNames; filhos usam mapa local.
            if (stack.Count >= MaxDepth)
            {
                result.Diagnostics.Add(new StudioDiagnostic("TCL_MAX_DEPTH", "warning", $"Profundidade de CHILD acima de {MaxDepth}; subárvore ignorada.", Path: parentPath + "/" + line.Name));
                visited.Add(line);
                return;
            }
            var id = UniqueId(parentId, line.Name, rootSiblingNames);
            var path = parentPath.Length == 0 ? line.Name : parentPath + "/" + line.Name;
            visited.Add(line);
            stack.Add(line);

            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(line.Identifier)) props["identifier"] = line.Identifier;
            result.Nodes.Add(new(id, new StudioNode("input", "line", line.Name, path, parentId, order, false,
                new StudioNodeDisplay(DisplayTextBuilder.ForContainer(line.Name, false, null, null), "line", false), props)));
            if (parentId == null) result.RootIds.Add(id);

            int? offset = 0;
            var childNames = new Dictionary<string, int>(StringComparer.Ordinal);
            var pos = 0;
            var fieldIdx = 0;
            foreach (var it in line.Items)
            {
                if (Is(it, "FIELD"))
                {
                    fieldIdx++;
                    var fname = Attr(it, "name");
                    if (string.IsNullOrWhiteSpace(fname))
                    {
                        fname = $"FIELD#{fieldIdx}";
                        result.Diagnostics.Add(new StudioDiagnostic("TCL_MISSING_NAME", "warning", "FIELD sem atributo name; nome sintético gerado.", Path: path + "/" + fname));
                    }
                    fname = fname.Trim();
                    var fid = UniqueId(id, fname, childNames);
                    var fprops = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var (length, decimals) = ParseLength(Attr(it, "length"));
                    if (length.HasValue) fprops["length"] = length.Value;
                    if (decimals.HasValue) fprops["decimals"] = decimals.Value;
                    if (offset.HasValue) fprops["offset"] = offset.Value;
                    else result.Diagnostics.Add(new StudioDiagnostic("OFFSET_UNRESOLVED", "warning", "Offset não calculável: campo anterior sem length.", Id: fid));
                    offset = offset.HasValue && length.HasValue ? offset + length : null;
                    result.Nodes.Add(new(fid, new StudioNode("input", "field", fname, path + "/" + fname, id, ++pos, false,
                        new StudioNodeDisplay(DisplayTextBuilder.ForValue(fname, false, null), "field", false), fprops)));
                }
                else if (Is(it, "CHILD"))
                {
                    var cname = it.Value.Trim();
                    if (!byName.TryGetValue(cname, out var child))
                    {
                        result.Diagnostics.Add(new StudioDiagnostic("TCL_UNKNOWN_CHILD", "warning", "CHILD referencia LINE inexistente; ignorado.", Path: path + "/" + cname));
                        continue;
                    }
                    if (stack.Contains(child))
                    {
                        result.Diagnostics.Add(new StudioDiagnostic("TCL_CHILD_CYCLE", "warning", "CHILD cíclico ignorado.", Path: path + "/" + cname));
                        continue;
                    }
                    EmitLine(child, id, path, ++pos, childNames, stack, visited, byName, result);
                }
            }

            stack.Remove(line);
        }

        /// <summary>Id <c>tcl:caminho</c>; segundo homônimo irmão recebe <c>~2</c>, terceiro <c>~3</c>…</summary>
        private static string UniqueId(string? parentId, string name, Dictionary<string, int> siblings)
        {
            siblings[name] = siblings.TryGetValue(name, out var c) ? c + 1 : 1;
            var seg = siblings[name] == 1 ? name : $"{name}~{siblings[name]}";
            return parentId == null ? IdPrefix + seg : parentId + "/" + seg;
        }

        /// <summary><c>"6"</c> → (6,null); <c>"15,2,0"</c> → (15,2); ausente/ilegível → (null,null).</summary>
        private static (int? Length, int? Decimals) ParseLength(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);
            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            int? len = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null;
            int? dec = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
            return (len, len.HasValue ? dec : null);
        }

        private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

        private static string? Attr(XElement e, string name)
            => e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}
