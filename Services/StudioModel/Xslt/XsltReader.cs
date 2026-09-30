using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

namespace LayoutParserApi.Services.StudioModel.Xslt
{
    /// <summary>Resultado da leitura do XSLT: nós de entrada sintetizados, vínculos, regras e diagnósticos. Nunca lança.</summary>
    public sealed record XsltReadResult(
        List<KeyValuePair<string, StudioNode>> InputNodes,
        List<string> InputRootIds,
        Dictionary<string, StudioLink> Links,
        Dictionary<string, StudioRule> Rules,
        HashSet<string> LinkedTargetIds,
        List<StudioDiagnostic> Diagnostics);

    /// <summary>
    /// Leitor TOLERANTE e SOMENTE LEITURA do XSLT de um mapper (Fase 3 do studio-model). Mapeia por CAMINHO:
    /// <c>xsl:value-of/@select</c> → link; <c>xsl:for-each/@select</c> → link com <c>iterates:true</c> (contêiner → contêiner);
    /// o destino é o elemento literal de saída (pilha de nomes) resolvido contra a árvore de destino do layout.
    /// O que não é estruturável (<c>xsl:choose/if/call-template/variable</c>, expressões com função) vira regra com
    /// <c>opaque[{reason, span}]</c> — <c>span</c> é intervalo de caracteres dentro de <c>code</c> (o trecho serializado).
    /// Um vínculo por destino (link OU rule): o primeiro vence e o restante vira <c>XSLT_TARGET_ALREADY_BOUND</c>.
    /// IDs determinísticos: <c>xslt:link:&lt;caminho-destino&gt;</c>, <c>xslt:rule:&lt;caminho-destino&gt;</c>,
    /// <c>xslt:in:&lt;caminho-origem&gt;</c>. Puro, sem I/O. DTD proibido (sem XXE); XSLT inválido vira diagnóstico.
    /// </summary>
    public static class XsltReader
    {
        public const string IdPrefix = "xslt:";
        public const string XslNamespace = "http://www.w3.org/1999/XSL/Transform";
        private const int MaxDepth = 64;
        private const int MaxCodeLength = 4000;

        public static XsltReadResult Read(string? xslt, IReadOnlyDictionary<string, StudioNode>? targetNodes)
        {
            var w = new Walker(targetNodes ?? new Dictionary<string, StudioNode>());
            var result = w.Result;

            XDocument doc;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var sr = new StringReader(xslt ?? string.Empty);
                using var xr = XmlReader.Create(sr, settings);
                doc = XDocument.Load(xr);
            }
            catch (Exception)
            {
                // Sem conteúdo do XSLT na mensagem (pode conter dado do cliente).
                result.Diagnostics.Add(new StudioDiagnostic("XSLT_UNPARSEABLE", "warning", "XSLT ausente ou com XML inválido; vínculos e regras indisponíveis."));
                return result;
            }

            var root = doc.Root;
            if (root == null || root.Name.Namespace.NamespaceName != XslNamespace)
            {
                result.Diagnostics.Add(new StudioDiagnostic("XSLT_UNPARSEABLE", "warning", "Documento não é uma folha de estilo XSLT; vínculos e regras indisponíveis."));
                return result;
            }

            foreach (var inc in root.Elements().Where(e => IsXsl(e, "import") || IsXsl(e, "include")))
                result.Diagnostics.Add(new StudioDiagnostic("XSLT_EXTERNAL_REFERENCE", "warning", "xsl:import/xsl:include não resolvido (leitura isolada).", Path: (string?)inc.Attribute("href")));

            var templates = root.Elements().Where(e => IsXsl(e, "template")).ToList();
            if (templates.Count == 0)
            {
                result.Diagnostics.Add(new StudioDiagnostic("XSLT_NO_TEMPLATE", "warning", "XSLT sem nenhum xsl:template."));
                return result;
            }

            // Variáveis/parâmetros globais: sem ancoragem possível → regra opaca órfã (nada some do modelo).
            var g = 0;
            foreach (var v in root.Elements().Where(e => IsXsl(e, "variable") || IsXsl(e, "param")))
                w.AddGlobalRule($"{IdPrefix}rule:@global#{++g}", v, OpaqueReason(v));

            var idx = 0;
            foreach (var t in templates)
            {
                idx++;
                w.Begin(idx);
                var match = ((string?)t.Attribute("match"))?.Trim();
                var ctx = match != null && SimplePath(match, out var p) ? Normalize(p) : string.Empty;
                w.WalkChildren(t, new List<string>(), ctx, 0, null);
            }

            w.Finish();
            return result;
        }

        // ---------- helpers ----------

        private static bool IsXsl(XElement e, string local) => e.Name.Namespace.NamespaceName == XslNamespace && e.Name.LocalName == local;
        private static bool IsXsl(XElement e) => e.Name.Namespace.NamespaceName == XslNamespace;

        private static readonly Regex SimplePathRx = new(@"^(\.|/?@?[\w\-.:]+(/@?[\w\-.:]+)*)$", RegexOptions.Compiled);

        /// <summary>Caminho simples (nomes separados por <c>/</c>, sem predicado/função/operador). <c>.</c> = contexto.</summary>
        private static bool SimplePath(string? s, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (!SimplePathRx.IsMatch(s) || s.Contains("..")) return false;
            path = s;
            return true;
        }

        private static string Normalize(string p) => p.Trim('/');

        private static string Combine(string ctx, string select)
        {
            if (select == ".") return ctx;
            if (select.StartsWith('/')) return Normalize(select);
            return ctx.Length == 0 ? Normalize(select) : ctx + "/" + Normalize(select);
        }

        private static string OpaqueReason(XElement el)
        {
            var local = el.Name.LocalName;
            var name = (string?)el.Attribute("name");
            return local switch
            {
                "call-template" => "call-template:" + name,
                "variable" => "variable:" + name,
                "param" => "param:" + name,
                "apply-templates" => "apply-templates",
                _ => "xsl:" + local,
            };
        }

        private static readonly Regex StringLit = new(@"'[^']*'|""[^""]*""", RegexOptions.Compiled);
        private static readonly Regex FuncRx = new(@"([A-Za-z_][\w\-.:]*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex VarRx = new(@"\$[\w\-.]+", RegexOptions.Compiled);
        private static readonly Regex TokenRx = new(@"@?[A-Za-z_][\w\-.]*(?::[A-Za-z_][\w\-.]*)?(?:/@?[A-Za-z_][\w\-.]*)*", RegexOptions.Compiled);
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "and", "or", "div", "mod", "true", "false", "text", "node" };

        /// <summary>Extrai (reads, functions) das expressões <c>select</c>/<c>test</c> do trecho, relativizando reads ao contexto.</summary>
        internal static (List<string> Reads, List<string> Functions) ScanExpressions(XElement el, string ctx)
        {
            var reads = new List<string>(); var funcs = new List<string>();
            foreach (var e in el.DescendantsAndSelf())
                foreach (var attr in new[] { "select", "test", "value" })
                {
                    var expr = (string?)e.Attribute(attr);
                    if (string.IsNullOrWhiteSpace(expr)) continue;
                    expr = StringLit.Replace(expr, " ");
                    foreach (Match m in FuncRx.Matches(expr)) if (!funcs.Contains(m.Groups[1].Value)) funcs.Add(m.Groups[1].Value);
                    expr = FuncRx.Replace(expr, " ");
                    expr = VarRx.Replace(expr, " ");
                    foreach (Match m in TokenRx.Matches(expr))
                    {
                        if (Keywords.Contains(m.Value)) continue;
                        var full = Combine(ctx, m.Value);
                        if (!reads.Contains(full)) reads.Add(full);
                    }
                }
            return (reads, funcs);
        }

        // ---------- percurso ----------

        private sealed class Walker
        {
            private readonly IReadOnlyDictionary<string, StudioNode> _targets;
            private readonly Dictionary<string, StudioNode> _inputByPath = new(StringComparer.Ordinal);
            private readonly List<string> _inputOrder = new();
            private readonly HashSet<string> _iterated = new(StringComparer.Ordinal);
            private readonly HashSet<string> _bound = new(StringComparer.Ordinal);
            private readonly HashSet<string> _unresolvedSeen = new(StringComparer.Ordinal);
            private int _tpl, _counter;

            public XsltReadResult Result { get; } = new(new(), new(), new(), new(), new(StringComparer.Ordinal), new());

            public Walker(IReadOnlyDictionary<string, StudioNode> targets) { _targets = targets; }

            public void Begin(int templateIndex) { _tpl = templateIndex; _counter = 0; }

            public void WalkChildren(XElement parent, List<string> stack, string ctx, int depth, string? iter)
            {
                if (depth >= MaxDepth)
                {
                    Result.Diagnostics.Add(new StudioDiagnostic("XSLT_MAX_DEPTH", "warning", $"Profundidade de XSLT acima de {MaxDepth}; subárvore ignorada.", Path: string.Join("/", stack)));
                    return;
                }
                foreach (var el in parent.Elements())
                {
                    if (!IsXsl(el)) { Literal(el, stack, ctx, depth, iter); continue; }
                    switch (el.Name.LocalName)
                    {
                        case "value-of":
                        case "copy-of":
                            ValueOf(el, stack, ctx);
                            break;
                        case "for-each":
                            ForEach(el, stack, ctx, depth);
                            break;
                        case "attribute":
                            Attribute(el, stack, ctx);
                            break;
                        case "text": case "sort": case "comment": case "fallback": case "output": case "key": case "strip-space": case "preserve-space":
                            break; // sem efeito sobre vínculos
                        default:
                            Opaque(el, stack, ctx);
                            break;
                    }
                }
            }

            private void Literal(XElement el, List<string> stack, string ctx, int depth, string? iter)
            {
                stack.Add(el.Name.LocalName);
                var path = string.Join("/", stack);
                if (iter != null) AddLink(path, iter, true);

                // Atributos com AVT: {caminho} puro vira link para @attr; AVT composto vira regra opaca.
                foreach (var a in el.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace.NamespaceName != XslNamespace))
                {
                    var v = a.Value.Trim();
                    if (!v.Contains('{')) continue;
                    var tpath = path + "/@" + a.Name.LocalName;
                    if (v.StartsWith('{') && v.EndsWith('}') && !v[1..^1].Contains('{') && !v[1..^1].Contains('}') && SimplePath(v[1..^1], out var sp))
                        AddLink(tpath, Combine(ctx, sp), false);
                    else
                    {
                        var (reads, funcs) = (ScanAvt(v, ctx));
                        AddRuleRaw(tpath, stack, v, "avt:" + a.Name.LocalName, reads, funcs);
                    }
                }

                WalkChildren(el, stack, ctx, depth + 1, null);
                stack.RemoveAt(stack.Count - 1);
            }

            private static (List<string>, List<string>) ScanAvt(string v, string ctx)
            {
                var holder = new XElement("x", new XAttribute("select", string.Join(" ", Regex.Matches(v, @"\{([^{}]*)\}").Select(m => m.Groups[1].Value))));
                return ScanExpressions(holder, ctx);
            }

            private void ValueOf(XElement el, List<string> stack, string ctx)
            {
                var sel = ((string?)el.Attribute("select"))?.Trim();
                if (SimplePath(sel, out var p))
                {
                    AddLink(stack.Count > 0 ? string.Join("/", stack) : null, Combine(ctx, p), false);
                    return;
                }
                // Expressão (função/operador/predicado/variável): não estruturável por caminho.
                Opaque(el, stack, ctx, "xpath-expression");
            }

            private void ForEach(XElement el, List<string> stack, string ctx, int depth)
            {
                var sel = ((string?)el.Attribute("select"))?.Trim();
                if (!SimplePath(sel, out var p))
                {
                    Opaque(el, stack, ctx, "for-each:expressão");
                    return;
                }
                var src = Combine(ctx, p);
                _iterated.Add(src);
                var hasLiteral = el.Elements().Any(c => !IsXsl(c));
                // Sem elemento literal filho, o laço alimenta o elemento corrente (contêiner → contêiner).
                if (!hasLiteral && stack.Count > 0) AddLink(string.Join("/", stack), src, true);
                EnsureInput(src);
                WalkChildren(el, stack, src, depth + 1, hasLiteral ? src : null);
            }

            private void Attribute(XElement el, List<string> stack, string ctx)
            {
                var name = ((string?)el.Attribute("name"))?.Trim();
                var kids = el.Elements().ToList();
                if (!string.IsNullOrEmpty(name) && !name.Contains('{') && stack.Count > 0 && kids.Count == 1 && IsXsl(kids[0], "value-of")
                    && SimplePath(((string?)kids[0].Attribute("select"))?.Trim(), out var p))
                {
                    AddLink(string.Join("/", stack) + "/@" + name, Combine(ctx, p), false);
                    return;
                }
                Opaque(el, stack, ctx, "xsl:attribute");
            }

            private void Opaque(XElement el, List<string> stack, string ctx, string? reason = null)
            {
                var target = stack.Count > 0 ? string.Join("/", stack) : null;
                var (reads, funcs) = ScanExpressions(el, ctx);
                AddRuleRaw(target, stack, null, reason ?? OpaqueReason(el), reads, funcs, el);
            }

            private void AddRuleRaw(string? targetPath, List<string> stack, string? codeOverride, string reason, List<string> reads, List<string> funcs, XElement? el = null)
            {
                var code = codeOverride ?? el!.ToString(SaveOptions.DisableFormatting);
                AddRuleCore(RuleKey(targetPath), targetPath, code, reason, reads, funcs);
            }

            /// <summary>Variável/parâmetro global: regra opaca sem âncora (vira <c>ORPHAN_LINK</c> informativo).</summary>
            public void AddGlobalRule(string id, XElement el, string reason)
            {
                var (reads, funcs) = ScanExpressions(el, string.Empty);
                AddRuleCore(id, null, el.ToString(SaveOptions.DisableFormatting), reason, reads, funcs);
            }

            private void AddRuleCore(string id, string? targetPath, string code, string reason, List<string> reads, List<string> funcs)
            {
                if (code.Length > MaxCodeLength) code = code[..MaxCodeLength];
                if (targetPath != null && !Bind(targetPath)) return;
                var tid = targetPath != null ? ResolveTarget(targetPath) : null;
                if (tid != null) Result.LinkedTargetIds.Add(tid);
                var name = targetPath?.Split('/').Last() ?? reason;
                Result.Rules[id] = new StudioRule(tid, new StudioRuleDisplay("Rule_" + name, "rule", tid), reason, code, reads,
                    targetPath != null ? new List<string> { targetPath } : new List<string>(), funcs, false,
                    new List<StudioOpaqueSpan> { new(reason, new[] { 0, code.Length }) });
            }

            private string RuleKey(string? targetPath)
                => targetPath != null ? $"{IdPrefix}rule:{targetPath}" : $"{IdPrefix}rule:@t{_tpl}#{++_counter}";

            private bool Bind(string targetPath)
            {
                if (_bound.Add(targetPath)) return true;
                Result.Diagnostics.Add(new StudioDiagnostic("XSLT_TARGET_ALREADY_BOUND", "warning",
                    "Destino já possui vínculo/regra; construção adicional não representada (um vínculo por destino).", Path: targetPath));
                return false;
            }

            private void AddLink(string? targetPath, string sourcePath, bool iterates)
            {
                var key = targetPath != null ? $"{IdPrefix}link:{targetPath}" : $"{IdPrefix}link:@t{_tpl}#{++_counter}";
                if (targetPath != null && !Bind(targetPath)) return;
                var srcId = EnsureInput(sourcePath);
                var tid = targetPath != null ? ResolveTarget(targetPath) : null;
                if (targetPath == null)
                    Result.Diagnostics.Add(new StudioDiagnostic("XSLT_UNRESOLVED_TARGET", "warning", "Construção de saída fora de qualquer elemento literal; destino indefinido.", Id: key));
                if (tid != null) Result.LinkedTargetIds.Add(tid);
                if (srcId != null) MarkLinked(srcId);
                var srcName = sourcePath.Length == 0 ? "." : sourcePath.Split('/').Last();
                var tgtName = targetPath?.Split('/').Last() ?? "?";
                Result.Links[key] = new StudioLink(srcId, tid, Result.Links.Count + 1,
                    new StudioLinkDisplay($"{srcName}_{tgtName}", "linkMapping", tid),
                    new StudioLinkOpts(null, null, null, null, null, null, null, null, null, null), iterates);
            }

            private readonly HashSet<string> _linkedInputs = new(StringComparer.Ordinal);
            private void MarkLinked(string id) => _linkedInputs.Add(id);

            /// <summary>Cria (idempotente) a cadeia de nós de entrada para o caminho referenciado; devolve o id do último.</summary>
            private string? EnsureInput(string path)
            {
                if (path.Length == 0) return null;
                var parts = path.Split('/');
                string acc = string.Empty; string? parentId = null;
                foreach (var seg in parts)
                {
                    acc = acc.Length == 0 ? seg : acc + "/" + seg;
                    var id = IdPrefix + "in:" + acc;
                    if (!_inputByPath.ContainsKey(acc))
                    {
                        _inputByPath[acc] = new StudioNode("input", seg.StartsWith('@') ? "attribute" : "field", seg, acc, parentId, _inputOrder.Count + 1, false,
                            new StudioNodeDisplay(string.Empty, "field", false), new Dictionary<string, object?>(StringComparer.Ordinal));
                        _inputOrder.Add(acc);
                    }
                    parentId = id;
                }
                return parentId;
            }

            /// <summary>Destino por caminho exato; senão sufixo único; senão nome único. Sem acerto → <c>XSLT_UNRESOLVED_TARGET</c>.</summary>
            private string? ResolveTarget(string path)
            {
                var cands = _targets.Where(kv => !NodeTypeMap.IsStructuralOnly(kv.Value.Type) && kv.Value.Tree == "target").ToList();
                var hit = cands.Where(kv => kv.Value.Path == path).ToList();
                if (hit.Count == 0) hit = cands.Where(kv => kv.Value.Path.EndsWith("/" + path, StringComparison.Ordinal)).ToList();
                if (hit.Count == 0)
                {
                    var last = path.Split('/').Last();
                    hit = cands.Where(kv => kv.Value.Name == last || "@" + kv.Value.Name == last).ToList();
                }
                if (hit.Count == 1) return hit[0].Key;
                if (_unresolvedSeen.Add(path))
                    Result.Diagnostics.Add(new StudioDiagnostic("XSLT_UNRESOLVED_TARGET", "warning",
                        hit.Count == 0 ? "Destino do XSLT não encontrado na árvore de destino." : "Destino do XSLT ambíguo na árvore de destino.",
                        Path: path, Ids: hit.Count > 1 ? hit.Select(h => h.Key).ToList() : null));
                return null;
            }

            public void Finish()
            {
                // Nós de entrada: contêiner = referenciado por for-each ou com filhos; folha = campo.
                var parents = new HashSet<string>(_inputByPath.Values.Where(n => n.ParentId != null).Select(n => n.ParentId!), StringComparer.Ordinal);
                foreach (var path in _inputOrder)
                {
                    var n = _inputByPath[path];
                    var id = IdPrefix + "in:" + path;
                    var container = _iterated.Contains(path) || parents.Contains(id);
                    var linked = _linkedInputs.Contains(id);
                    var display = container
                        ? new StudioNodeDisplay(DisplayTextBuilder.ForContainer(n.Name, false, null, null), "line", linked)
                        : new StudioNodeDisplay(DisplayTextBuilder.ForValue(n.Name, false, null), n.Type == "attribute" ? "attribute" : "field", linked);
                    var node = n with { Type = container ? "line" : n.Type, Display = display };
                    Result.InputNodes.Add(new(id, node));
                    if (node.ParentId == null) Result.InputRootIds.Add(id);
                }
            }
        }
    }
}
