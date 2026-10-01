namespace XslSynth.Core;

/// <summary>Tipo de referência da DSL Sysmiddle (prefixos reais: <c>I.</c>, <c>T.</c>, <c>#.</c>, <c>$.</c>).</summary>
public enum SysmiddleRefKind { Input, Target, Hash, Dollar }

/// <summary>Referência extraída da DSL. <see cref="End"/> é o índice (exclusivo) onde a leitura parou.</summary>
public readonly record struct SysmiddleRef(SysmiddleRefKind Kind, string Path, int Start, int End)
{
    public char Prefix => Kind switch
    {
        SysmiddleRefKind.Input => 'I', SysmiddleRefKind.Target => 'T',
        SysmiddleRefKind.Hash => '#', _ => '$'
    };
}

/// <summary>
/// Extração fiel das referências da DSL Sysmiddle, espelhando a tokenização real do motor
/// (análise ConnectUS, seção B1). Somente leitura/explicação — não reescreve regras.
/// <list type="bullet">
/// <item><c>I.</c> vai até o 1º separador de <c>, - + * ! | &amp; } \r \n \t \a \b \v \f ; = ) &gt; &lt;</c>
/// (espaço NÃO separa, mas há Trim); <c>/ . [ ] ( : @ $</c> fazem parte do caminho.</item>
/// <item><c>T.</c> vai até <c>;</c>/quebra de linha e só vale como alvo de atribuição.</item>
/// <item><c>#.</c>/<c>$.</c> usam os mesmos separadores de <c>I.</c> mais espaço, <c>/</c> e <c>:</c>.</item>
/// <item>Literais entre aspas simples e comentários <c>//</c> e <c>/* */</c> são ignorados (aspas duplas NÃO protegem).</item>
/// <item><c>F.</c>, <c>S.</c>, <c>N.</c> são categorias do autocomplete, não prefixos.</item>
/// </list>
/// </summary>
public static class SysmiddleDslTokenizer
{
    private const string InputSeparators = ",-+*!|&}\r\n\t\a\b\v\f;=)><";

    /// <summary>Se há literal/comentário em <paramref name="i"/>, devolve o índice logo após ele; senão -1.</summary>
    public static int SkipLiteralOrComment(string s, int i)
    {
        if (i >= s.Length) return -1;
        if (s[i] == '\'')
        {
            var close = s.IndexOf('\'', i + 1);
            return close < 0 ? s.Length : close + 1;
        }
        if (s[i] == '/' && i + 1 < s.Length)
        {
            if (s[i + 1] == '/')
            {
                var nl = s.IndexOf('\n', i + 2);
                return nl < 0 ? s.Length : nl;
            }
            if (s[i + 1] == '*')
            {
                var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return close < 0 ? s.Length : close + 2;
            }
        }
        return -1;
    }

    /// <summary>Tenta ler uma referência começando exatamente em <paramref name="i"/>.</summary>
    public static bool TryReadRef(string s, int i, out SysmiddleRef r)
    {
        r = default;
        if (i + 1 >= s.Length || s[i + 1] != '.') return false;
        var c = s[i];
        if (c is not ('I' or 'T' or '#' or '$')) return false;
        // Não é prefixo se colado a um identificador (ex.: "FI.x", "X.I.y").
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] is '_' or '.')) return false;

        var start = i + 2;
        var j = start;
        if (c == 'T')
        {
            while (j < s.Length && s[j] != ';' && s[j] != '\n' && s[j] != '\r') j++;
            var eq = s.IndexOf('=', start, j - start);
            if (eq < 0) return false;
            if (eq + 1 < s.Length && s[eq + 1] == '=') return false;      // comparação, não atribuição
            if (eq > start && s[eq - 1] is '!' or '<' or '>') return false;
            var tp = s[start..eq].Trim();
            if (tp.Length == 0) return false;
            r = new SysmiddleRef(SysmiddleRefKind.Target, tp, i, eq);
            return true;
        }

        var extra = c == 'I' ? "" : " /:";
        while (j < s.Length && InputSeparators.IndexOf(s[j]) < 0 && extra.IndexOf(s[j]) < 0) j++;
        var path = s[start..j].Trim();
        if (path.Length == 0) return false;
        var kind = c switch { 'I' => SysmiddleRefKind.Input, '#' => SysmiddleRefKind.Hash, _ => SysmiddleRefKind.Dollar };
        r = new SysmiddleRef(kind, path, i, j);
        return true;
    }

    /// <summary>Todas as referências da DSL, em ordem, ignorando literais e comentários. Nunca lança.</summary>
    public static List<SysmiddleRef> Scan(string? dsl)
    {
        var list = new List<SysmiddleRef>();
        if (string.IsNullOrEmpty(dsl)) return list;
        var i = 0;
        while (i < dsl.Length)
        {
            var skip = SkipLiteralOrComment(dsl, i);
            if (skip >= 0) { i = skip; continue; }
            if (TryReadRef(dsl, i, out var r)) { list.Add(r); i = r.End; continue; }
            i++;
        }
        return list;
    }

    /// <summary>Referências <c>I.</c> (comparação posterior deve ser OrdinalIgnoreCase).</summary>
    public static IEnumerable<string> InputPaths(string? dsl) =>
        Scan(dsl).Where(r => r.Kind == SysmiddleRefKind.Input).Select(r => r.Path);

    /// <summary>Alvos <c>T.</c> de atribuição (comparação posterior deve ser ordinal exata).</summary>
    public static IEnumerable<string> TargetPaths(string? dsl) =>
        Scan(dsl).Where(r => r.Kind == SysmiddleRefKind.Target).Select(r => r.Path);
}
