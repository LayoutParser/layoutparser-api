using System.Reflection;

using Microsoft.Extensions.Configuration;


namespace LayoutParserApi.Services.Sysmiddle
{
    /// <summary>Parâmetro de uma função da DSL Sysmiddle.</summary>
    public sealed record SysmiddleFunctionParameter(string Name, string Type);

    /// <summary>
    /// Função da DSL Sysmiddle. <c>Origin</c> = <c>builtin</c> (embutida na DSL) ou <c>ndd-custom</c>
    /// (extraída por reflection de <c>ndd.ConnectUs.Functions.dll</c>).
    /// </summary>
    public sealed record SysmiddleFunctionInfo(
        string Name, string Origin, IReadOnlyList<SysmiddleFunctionParameter> Parameters, string ReturnType, string Description);

    /// <summary>Catálogo de funções da DSL Sysmiddle (builtins + funções NDD).</summary>
    public interface ISysmiddleFunctionCatalog
    {
        IReadOnlyList<SysmiddleFunctionInfo> GetAll();
        SysmiddleFunctionInfo? Lookup(string name);
    }

    /// <summary>
    /// Catálogo SOMENTE LEITURA: builtins estáticos + funções NDD lidas via <see cref="MetadataLoadContext"/>
    /// (reflection-only — nenhum código da DLL é executado).
    /// Degrada graciosamente: DLL ausente/inválida ⇒ só builtins.
    ///
    /// <para><b>Limite honesto:</b> as funções NDD são as classes públicas de <c>ndd.ConnectUs.Functions.Functions</c>
    /// (herdam <c>FunctionMemberImpl</c>); Name/descrição/parâmetros reais são strings ofuscadas só resolvidas em
    /// runtime, então o nome exposto é o da CLASSE (pode diferir do nome usado na DSL, ex.: <c>FormaterDecimal</c>)
    /// e os parâmetros saem como <c>pars:object[]</c> (assinatura real <c>Execute(object[])</c>).</para>
    /// </summary>
    public sealed class SysmiddleFunctionCatalog : ISysmiddleFunctionCatalog
    {
        public const string OriginBuiltin = "builtin";
        public const string OriginNddCustom = "ndd-custom";

        private static SysmiddleFunctionInfo B(string name, string desc, string ret, params (string n, string t)[] p) =>
            new(name, OriginBuiltin, p.Select(x => new SysmiddleFunctionParameter(x.n, x.t)).ToList(), ret, desc);

        private static readonly SysmiddleFunctionInfo[] Builtins =
        {
            B("IsNullOrEmpty", "Verdadeiro quando o texto é nulo ou vazio.", "bool", ("valor", "string")),
            B("Trim", "Remove espaços no início e no fim do texto.", "string", ("valor", "string")),
            B("Substring", "Extrai um trecho do texto a partir de uma posição (base 0) com o tamanho informado.", "string",
                ("valor", "string"), ("inicio", "int"), ("tamanho", "int")),
            B("Contains", "Verdadeiro quando o texto contém o trecho informado.", "bool", ("valor", "string"), ("trecho", "string")),
            B("Concat", "Concatena dois ou mais valores em um único texto.", "string", ("valores", "object[]")),
            B("ConcatString", "Concatena textos em um único texto.", "string", ("valores", "string[]")),
            B("GetLength", "Retorna o tamanho (número de caracteres) do texto.", "int", ("valor", "string")),
            B("ConvertToInt32", "Converte o valor para inteiro de 32 bits.", "int", ("valor", "string")),
            B("GetValueFromContext", "Lê um valor do contexto de execução do mapeador.", "string", ("chave", "string")),
            B("GetDictionaryValuesFromElement", "Monta um dicionário de valores a partir de um elemento do documento.", "object", ("elemento", "string")),
            B("GetSumElementValuesFunction", "Soma os valores numéricos de um elemento repetido do documento.", "decimal", ("elemento", "string")),
        };

        // Descrições curadas das funções NDD observadas em mapeadores reais.
        private static readonly Dictionary<string, (string Desc, string Ret)> Curated = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FormaterDecimal"] = ("Formata um valor decimal com a quantidade de casas definida (função NDD).", "string"),
            ["RemoveZerosLeft"] = ("Remove os zeros à esquerda de um texto numérico (função NDD).", "string"),
            ["GetConfigParametersValue"] = ("Lê um parâmetro de configuração do ambiente — tem efeito externo, não é determinística (função NDD).", "string"),
        };

        private readonly Dictionary<string, SysmiddleFunctionInfo> _byName;

        private SysmiddleFunctionCatalog(Dictionary<string, SysmiddleFunctionInfo> byName) => _byName = byName;

        public IReadOnlyList<SysmiddleFunctionInfo> GetAll() =>
            _byName.Values.OrderBy(f => f.Origin, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public SysmiddleFunctionInfo? Lookup(string name) => _byName.GetValueOrDefault(name);

        /// <summary>Catálogo só com os builtins (sem DLL) — usado como default e como degradação.</summary>
        public static SysmiddleFunctionCatalog BuiltinOnly() => Build(null, null);

        /// <summary>Lê o caminho de <c>SysmiddleFunctions:DllPath</c> ou procura nos locais padrão do repo/deploy.</summary>
        public static SysmiddleFunctionCatalog Create(IConfiguration configuration, string contentRoot, ILogger? logger)
        {
            var candidates = new List<string>();
            var configured = configuration["SysmiddleFunctions:DllPath"];
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
            var rel = Path.Combine("tools", "LowCodeRunner", "Functions", "ndd.ConnectUs.Functions.dll");
            candidates.Add(Path.Combine(contentRoot, rel));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, rel));

            var dll = candidates.FirstOrDefault(File.Exists);
            if (dll == null)
                logger?.LogWarning("DLL de funções NDD não encontrada ({Candidatos}) — catálogo só com builtins.", string.Join(" | ", candidates));
            return Build(dll, logger);
        }

        /// <summary>Constrói o catálogo; <paramref name="dllPath"/> ausente/ilegível ⇒ só builtins.</summary>
        public static SysmiddleFunctionCatalog Build(string? dllPath, ILogger? logger)
        {
            var map = new Dictionary<string, SysmiddleFunctionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in Builtins) map[b.Name] = b;

            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                try
                {
                    foreach (var name in ReadNddFunctionNames(dllPath))
                    {
                        if (map.ContainsKey(name)) continue; // builtin tem precedência
                        Curated.TryGetValue(name, out var cur);
                        map[name] = new SysmiddleFunctionInfo(name, OriginNddCustom,
                            new[] { new SysmiddleFunctionParameter("pars", "object[]") }, cur.Ret ?? "object",
                            cur.Desc ?? "Função NDD ConnectUs. Semântica e assinatura exata não extraíveis (assembly ofuscado); " +
                                        "recebe os argumentos como object[].");
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Falha ao extrair funções NDD de {Dll} — catálogo só com builtins.", dllPath);
                }
            }

            // Funções curadas vistas em mapeadores reais, mesmo se a DLL faltar/nome divergir.
            foreach (var (name, cur) in Curated)
                if (!map.ContainsKey(name))
                    map[name] = new SysmiddleFunctionInfo(name, OriginNddCustom,
                        new[] { new SysmiddleFunctionParameter("pars", "object[]") }, cur.Ret, cur.Desc);

            return new SysmiddleFunctionCatalog(map);
        }

        /// <summary>Nomes das classes de função NDD via <see cref="MetadataLoadContext"/> (somente metadata).</summary>
        private static IEnumerable<string> ReadNddFunctionNames(string dllPath)
        {
            var dir = Path.GetDirectoryName(dllPath)!;
            var paths = Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
                .Concat(Directory.GetFiles(dir, "*.dll")).Append(dllPath).Distinct();
            using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
            var asm = mlc.LoadFromAssemblyPath(dllPath);
            IEnumerable<Type> types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null)!; }

            var names = new List<string>();
            foreach (var t in types)
            {
                if (!t.IsPublic || t.IsAbstract || t.Namespace != "ndd.ConnectUs.Functions.Functions") continue;
                try
                {
                    var inherits = false;
                    for (var b = t.BaseType; b != null; b = b.BaseType)
                        if (b.Name == "FunctionMemberImpl" || b.Name == "FunctionMember") { inherits = true; break; }
                    if (inherits) names.Add(t.Name);
                }
                catch { /* tipo com base não resolvível: ignora */ }
            }
            return names;
        }
    }
}
