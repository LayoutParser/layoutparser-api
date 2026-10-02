using System.Text.RegularExpressions;
using XslSynth.Model;
using XslSynth.Prompting;

namespace XslSynth.Core;

/// <summary>
/// Camada 0 do desenho de contexto/prompt (docs/architecture/design-dsl-mapper-prompt-ia-2026-08-16.md
/// §2): parser DETERMINÍSTICO (sem IA) da gramática Sysmiddle real — <c>#.</c>/<c>$.</c>/<c>I.</c>/
/// <c>T.</c>, <c>if/else</c> com <c>begin/end</c>, operadores <c>= != &gt; &lt; &amp;&amp;</c>, chamadas
/// de função bare (<c>Nome(args)</c>). Ao contrário do <see cref="DslBlockInterpreter"/> (que produz
/// XSLT só para o padrão "guarded emit" dominante), este produz uma árvore de decisão COMPLETA como
/// <see cref="StructuredRule"/> — cobre <c>else</c>, aninhamento, condição composta e atribuição de
/// temp dentro do <c>begin</c>, que são exatamente os traços que o <see cref="DslBlockInterpreter"/>
/// deixa como stub (ver <see cref="XslSynth.Synthesis.DslTraits"/>).
///
/// Não gera XSLT (isso é papel do transpilador determinístico/LLM downstream) — produz só a
/// representação estruturada que o LLM consome, nunca o <c>ContentValue</c> bruto (regra
/// não-negociável do desenho: dado fiscal sensível não vai cru pro prompt).
///
/// Limite honesto: cobre a sintaxe confirmada na amostra real (story/103). Construções fora
/// desse subconjunto (loops com corpo complexo, atribuição condicional dentro de expressão)
/// não quebram o parser — ele resolve o que reconhece e marca o resto como token opaco dentro
/// da condição/valor (string bruta), nunca lança exceção para DSL real.
/// </summary>
public sealed class DslStructuredParser
{
    /// <summary>Ponto de entrada: <c>MapperRule</c> real → JSON estruturado (Camada 0 → Camada 1).</summary>
    public StructuredRule Parse(MapperRule rule)
    {
        var code = StripMarkers(rule.ContentValue ?? "");
        var tokens = Tokenize(code);
        var statements = new DslParser(tokens).ParseProgram();

        var bindings = new Dictionary<string, DslExpr>(StringComparer.Ordinal);
        var branches = new List<StructuredBranch>();
        Walk(statements, activeConditions: new List<(string, DslExpr)>(), bindings, branches);

        var target = branches.Count > 0 ? branches[0].Target : rule.TargetPath;
        var loopType = DetectLoopType(code);
        string? staticValue = null;
        if (branches.Count == 1 && branches[0].Sources.Count == 0 && branches[0].Functions.Count == 0
            && branches[0].Condition == "true")
        {
            staticValue = TryExtractStaticLiteral(statements);
        }

        return new StructuredRule(
            StructuredRuleSchema.Version,
            rule.ElementGuid,
            rule.Name,
            target,
            branches,
            staticValue,
            loopType);
    }

    // ── Detecção de loop (a amostra real não tem, mas o dono citou que existe no padrão) ──
    private static readonly Regex LoopRx = new(@"\b(foreach|for|while)\s*\(", RegexOptions.Compiled);

    private static string? DetectLoopType(string code)
    {
        var m = LoopRx.Match(code);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? TryExtractStaticLiteral(IReadOnlyList<DslStmt> statements)
    {
        foreach (var s in statements)
            if (s is DslAssign { Lhs.Prefix: 'T' } a && a.Rhs is DslLiteral lit)
                return lit.Value;
        return null;
    }

    // ── Achatamento da AST em ramos (branches) ──────────────────────────────────

    private static void Walk(IReadOnlyList<DslStmt> statements, List<(string Text, DslExpr Expr)> activeConditions,
        Dictionary<string, DslExpr> bindings, List<StructuredBranch> branches)
    {
        // cópia local da tabela de símbolos: a DSL é sequencial e os temps são resolvidos
        // no valor vigente no ponto de uso — mas ramos irmãos (if/else) não devem vazar
        // bindings um para o outro, então cada bloco recebe sua própria cópia.
        var local = new Dictionary<string, DslExpr>(bindings, StringComparer.Ordinal);

        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case DslAssign assign when assign.Lhs.Prefix is '#' or '$':
                    local[assign.Lhs.Path] = assign.Rhs;
                    break;

                case DslAssign assign when assign.Lhs.Prefix == 'T':
                    var sources = new List<string>();
                    var functions = new List<string>();
                    CollectRefs(assign.Rhs, local, sources, functions, depth: 0);
                    // as condições que guardam este ramo também consomem origens/funções (ex.:
                    // IsNullOrEmpty(#.vBC) precisa de vBC e da própria função para reproduzir o
                    // guard na saída) — entram no mesmo ramo, não só o rhs da atribuição.
                    foreach (var (_, expr) in activeConditions)
                        CollectRefs(expr, local, sources, functions, depth: 0);
                    var condition = activeConditions.Count == 0 ? "true" : string.Join(" && ", activeConditions.Select(c => c.Text));
                    branches.Add(new StructuredBranch(
                        condition, assign.Lhs.Path,
                        sources.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                        functions.Distinct(StringComparer.Ordinal).ToList()));
                    break;

                case DslIf ifs:
                    var thenConditions = new List<(string, DslExpr)>(activeConditions) { (RenderCondition(ifs.Condition), ifs.Condition) };
                    Walk(ifs.Then, thenConditions, local, branches);
                    if (ifs.Else is { Count: > 0 })
                    {
                        // mesma expressão para fins de coleta de refs (negação não muda quais
                        // origens/funções o guard consome) — só o texto renderizado carrega o "!(...)".
                        var elseConditions = new List<(string, DslExpr)>(activeConditions)
                            { ($"!({RenderCondition(ifs.Condition)})", ifs.Condition) };
                        Walk(ifs.Else, elseConditions, local, branches);
                    }

                    // Um if/else usado só para computar temp (sem emitir T. em nenhum ramo, ex.:
                    // o fallback de data #.AAMM na regra da chave de acesso) não aparece como
                    // branch — mas o valor calculado precisa ficar disponível para as instruções
                    // SEGUINTES neste mesmo bloco. Como não sabemos em parse-time qual ramo roda,
                    // mesclamos as duas alternativas (DslBinary "||" só para carregar refs de
                    // ambos os lados — CollectRefs caminha nos dois) — conservador: a origem de
                    // QUALQUER ramo possível entra nas sources do uso posterior.
                    var thenTemps = CollectAssignedTemps(ifs.Then, local);
                    var elseTemps = ifs.Else.Count > 0 ? CollectAssignedTemps(ifs.Else, local) : new();
                    foreach (var key in thenTemps.Keys.Union(elseTemps.Keys, StringComparer.Ordinal))
                    {
                        var hasThen = thenTemps.TryGetValue(key, out var te);
                        var hasElse = elseTemps.TryGetValue(key, out var ee);
                        local[key] = hasThen && hasElse ? new DslBinary("||", te!, ee!) : (hasThen ? te! : ee!);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Coleta as atribuições a temp (<c>#./$.</c>) feitas dentro de um bloco de statements —
    /// inclusive dentro de <c>if/else</c> aninhados sem emissão <c>T.</c> (mescla os dois ramos,
    /// já resolvidos contra <paramref name="outer"/>). Usada só para propagar bindings de volta
    /// ao escopo pai quando o if/else serve puramente para calcular um valor (ver comentário de
    /// uso em <see cref="Walk"/>).
    /// </summary>
    private static Dictionary<string, DslExpr> CollectAssignedTemps(
        IReadOnlyList<DslStmt> statements, IReadOnlyDictionary<string, DslExpr> outer)
    {
        var local = new Dictionary<string, DslExpr>(outer, StringComparer.Ordinal);
        var result = new Dictionary<string, DslExpr>(StringComparer.Ordinal);

        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case DslAssign { Lhs.Prefix: '#' or '$' } assign:
                    local[assign.Lhs.Path] = assign.Rhs;
                    result[assign.Lhs.Path] = assign.Rhs;
                    break;
                case DslIf ifs:
                    var thenTemps = CollectAssignedTemps(ifs.Then, local);
                    var elseTemps = ifs.Else.Count > 0 ? CollectAssignedTemps(ifs.Else, local) : new();
                    foreach (var key in thenTemps.Keys.Union(elseTemps.Keys, StringComparer.Ordinal))
                    {
                        var hasThen = thenTemps.TryGetValue(key, out var te);
                        var hasElse = elseTemps.TryGetValue(key, out var ee);
                        var merged = hasThen && hasElse ? new DslBinary("||", te!, ee!) : (hasThen ? te! : ee!);
                        local[key] = merged;
                        result[key] = merged;
                    }
                    break;
            }
        }
        return result;
    }

    /// <summary>Coleta origens I. e chamadas F.* usadas por uma expressão, resolvendo #./$. via bindings.</summary>
    private static void CollectRefs(DslExpr expr, IReadOnlyDictionary<string, DslExpr> bindings,
        List<string> sources, List<string> functions, int depth)
    {
        if (depth > 32) return; // guarda contra ciclo/recursão patológica em binding malformado
        switch (expr)
        {
            case DslRef { Prefix: 'I' } r:
                sources.Add(r.Path);
                break;
            case DslRef { Prefix: '#' or '$' } r:
                if (bindings.TryGetValue(r.Path, out var bound))
                    CollectRefs(bound, bindings, sources, functions, depth + 1);
                break;
            case DslCall call:
                functions.Add(call.Name);
                foreach (var arg in call.Args) CollectRefs(arg, bindings, sources, functions, depth + 1);
                break;
            case DslBinary bin:
                CollectRefs(bin.Left, bindings, sources, functions, depth + 1);
                CollectRefs(bin.Right, bindings, sources, functions, depth + 1);
                break;
            // DslLiteral: nada a coletar.
        }
    }

    /// <summary>Renderiza a condição de um <c>if</c> num texto legível e estável para o prompt.</summary>
    private static string RenderCondition(DslExpr expr) => expr switch
    {
        DslBinary bin => $"{RenderCondition(bin.Left)} {bin.Op} {RenderCondition(bin.Right)}",
        DslRef { Prefix: '#' or '$' } r => r.Path,
        DslRef { Prefix: 'I' } r => $"I.{r.Path}",
        DslLiteral lit => $"'{lit.Value}'",
        DslCall call => $"{call.Name}({string.Join(",", call.Args.Select(RenderCondition))})",
        _ => "?"
    };

    private static string StripMarkers(string dsl) =>
        dsl.Replace("%beginRuleContent;", "").Replace("%endRuleContent;", "")
            .Replace("&amp;", "&").Replace("&gt;", ">").Replace("&lt;", "<").Trim();

    // ── Tokenizer ────────────────────────────────────────────────────────────

    private static readonly Regex TokenRx = new(
        @"\G(?:(?<str>'[^']*')" +
        @"|(?<num>\d+(\.\d+)?)" +
        @"|(?<op>!=|==|&&|=|>|<)" +
        @"|(?<punct>[(),;])" +
        @"|(?<ident>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled);

    private static List<DslToken> Tokenize(string code)
    {
        var tokens = new List<DslToken>();
        // Refs (I./T./#./$.) e comentários vêm do tokenizador compartilhado (fiel ao motor);
        // o restante segue por regex ancorada (\G) a partir da posição corrente.
        var pos = 0;
        while (pos < code.Length)
        {
            var skip = SysmiddleDslTokenizer.SkipLiteralOrComment(code, pos);
            if (skip >= 0 && code[pos] != '\'') { pos = skip; continue; }
            if (SysmiddleDslTokenizer.TryReadRef(code, pos, out var rf))
            {
                tokens.Add(new DslToken(DslTokenKind.Ref, rf.Prefix + "." + rf.Path));
                pos = rf.End;
                continue;
            }
            var m = TokenRx.Match(code, pos);
            if (!m.Success) { pos++; continue; }
            pos = m.Index + Math.Max(1, m.Length);
            if (m.Groups["str"].Success)
            {
                var raw = m.Groups["str"].Value;
                tokens.Add(new DslToken(DslTokenKind.String, raw[1..^1]));
            }
            else if (m.Groups["num"].Success)
                tokens.Add(new DslToken(DslTokenKind.Number, m.Groups["num"].Value));
            else if (m.Groups["op"].Success)
                tokens.Add(new DslToken(DslTokenKind.Op, m.Groups["op"].Value));
            else if (m.Groups["punct"].Success)
                tokens.Add(new DslToken(DslTokenKind.Punct, m.Groups["punct"].Value));
            else if (m.Groups["ident"].Success)
            {
                var word = m.Groups["ident"].Value;
                tokens.Add(new DslToken(word switch
                {
                    "if" => DslTokenKind.KwIf,
                    "else" => DslTokenKind.KwElse,
                    "begin" => DslTokenKind.KwBegin,
                    "end" => DslTokenKind.KwEnd,
                    _ => DslTokenKind.Ident
                }, word));
            }
        }
        tokens.Add(new DslToken(DslTokenKind.Eof, ""));
        return tokens;
    }
}

// ── Tokens ──────────────────────────────────────────────────────────────────

internal enum DslTokenKind { Ref, String, Number, Op, Punct, Ident, KwIf, KwElse, KwBegin, KwEnd, Eof }

internal readonly record struct DslToken(DslTokenKind Kind, string Text);

// ── AST ─────────────────────────────────────────────────────────────────────

internal abstract record DslStmt;
internal sealed record DslAssign(DslRef Lhs, DslExpr Rhs) : DslStmt;
internal sealed record DslIf(DslExpr Condition, IReadOnlyList<DslStmt> Then, IReadOnlyList<DslStmt> Else) : DslStmt;

internal abstract record DslExpr;
internal sealed record DslLiteral(string Value) : DslExpr;
internal sealed record DslRef(char Prefix, string Path) : DslExpr;
internal sealed record DslCall(string Name, IReadOnlyList<DslExpr> Args) : DslExpr;
internal sealed record DslBinary(string Op, DslExpr Left, DslExpr Right) : DslExpr;

/// <summary>
/// Parser recursivo-descendente da gramática Sysmiddle confirmada na amostra real:
/// <code>
/// program    := statement*
/// statement  := assign | ifStmt
/// assign     := ref '=' expr ';'
/// ifStmt     := 'if' '(' condition ')' 'begin' statement* 'end' [ 'else' 'begin' statement* 'end' ]
/// condition  := expr (('=' | '!=' | '>' | '<' | '&&') expr)*   (associa à esquerda, sem precedência —
///                a amostra real não tem parênteses aninhados dentro de condição composta)
/// expr       := ref | literal | number | call
/// call       := ident '(' (expr (',' expr)*)? ')'
/// </code>
/// Tolerante: token inesperado não lança — a instrução/expressão corrente é descartada e o
/// parser resincroniza no próximo ';' ou 'end' (DSL real nunca deve derrubar o pipeline).
/// </summary>
internal sealed class DslParser
{
    private readonly List<DslToken> _tokens;
    private int _pos;

    public DslParser(List<DslToken> tokens) => _tokens = tokens;

    private DslToken Cur => _tokens[_pos];
    private DslToken Advance() => _tokens[_pos++];
    private bool Check(DslTokenKind k) => Cur.Kind == k;
    private bool Match(DslTokenKind k) { if (!Check(k)) return false; _pos++; return true; }

    public List<DslStmt> ParseProgram()
    {
        var stmts = new List<DslStmt>();
        while (!Check(DslTokenKind.Eof))
        {
            var before = _pos;
            var stmt = ParseStatement();
            if (stmt is not null) stmts.Add(stmt);
            if (_pos == before) _pos++; // evita loop infinito em token não reconhecido
        }
        return stmts;
    }

    private DslStmt? ParseStatement()
    {
        if (Check(DslTokenKind.KwIf)) return ParseIf();
        if (Check(DslTokenKind.Ref)) return ParseAssign();
        // token solto fora do padrão (ex.: 'end'/'else' órfão de resync) — ignora.
        Advance();
        return null;
    }

    private DslStmt? ParseAssign()
    {
        var lhsTok = Advance();
        var lhs = ToRef(lhsTok.Text);
        if (!Match(DslTokenKind.Op) || _tokens[_pos - 1].Text != "=")
        {
            SkipToDelimiter();
            return null;
        }
        var rhs = ParseExpr();
        Match(DslTokenKind.Punct); // ';' — tolera ausência
        return lhs is null || rhs is null ? null : new DslAssign(lhs, rhs);
    }

    private DslIf? ParseIf()
    {
        Advance(); // if
        if (!Match(DslTokenKind.Punct)) return null; // '('
        var condition = ParseCondition();
        Match(DslTokenKind.Punct); // ')'
        if (!Match(DslTokenKind.KwBegin)) return null;
        var thenBlock = ParseBlockUntilEnd();

        List<DslStmt> elseBlock = new();
        if (Check(DslTokenKind.KwElse))
        {
            Advance();
            if (Match(DslTokenKind.KwBegin))
                elseBlock = ParseBlockUntilEnd();
        }
        return condition is null ? null : new DslIf(condition, thenBlock, elseBlock);
    }

    private List<DslStmt> ParseBlockUntilEnd()
    {
        var stmts = new List<DslStmt>();
        while (!Check(DslTokenKind.KwEnd) && !Check(DslTokenKind.Eof))
        {
            var before = _pos;
            var stmt = ParseStatement();
            if (stmt is not null) stmts.Add(stmt);
            if (_pos == before) _pos++;
        }
        Match(DslTokenKind.KwEnd);
        return stmts;
    }

    /// <summary>Condição composta: expr (op expr)* — associação à esquerda, sem precedência.</summary>
    private DslExpr? ParseCondition()
    {
        var left = ParseExpr();
        if (left is null) return null;
        while (Check(DslTokenKind.Op))
        {
            var op = Advance().Text;
            var right = ParseExpr();
            if (right is null) break;
            left = new DslBinary(op, left, right);
        }
        return left;
    }

    private DslExpr? ParseExpr()
    {
        if (Check(DslTokenKind.Ref))
        {
            var tok = Advance();
            return ToRef(tok.Text);
        }
        if (Check(DslTokenKind.String))
            return new DslLiteral(Advance().Text);
        if (Check(DslTokenKind.Number))
            return new DslLiteral(Advance().Text);
        if (Check(DslTokenKind.Ident))
        {
            var name = Advance().Text;
            var args = new List<DslExpr>();
            if (Match(DslTokenKind.Punct)) // '('
            {
                if (!(Cur.Kind == DslTokenKind.Punct && Cur.Text == ")"))
                {
                    var first = ParseCondition(); // aceita operadores dentro de args (ex.: != True())
                    if (first is not null) args.Add(first);
                    while (Cur.Kind == DslTokenKind.Punct && Cur.Text == ",")
                    {
                        Advance();
                        var next = ParseCondition();
                        if (next is not null) args.Add(next);
                    }
                }
                Match(DslTokenKind.Punct); // ')'
            }
            return new DslCall(name, args);
        }
        return null;
    }

    private void SkipToDelimiter()
    {
        while (!Check(DslTokenKind.Eof) && !(Cur.Kind == DslTokenKind.Punct && Cur.Text == ";")
               && !Check(DslTokenKind.KwEnd))
            Advance();
        Match(DslTokenKind.Punct);
    }

    private static DslRef? ToRef(string text)
    {
        if (text.Length < 3 || text[1] != '.') return null;
        return new DslRef(text[0], text[2..]);
    }
}
