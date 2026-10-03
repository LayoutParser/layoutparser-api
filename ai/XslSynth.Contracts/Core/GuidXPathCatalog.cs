using System.Xml.Linq;

namespace XslSynth.Core;

// ─────────────────────────────────────────────────────────────────────────────
// GuidXPathCatalog — A3 (Trilha A, plano multi-sessão §7.4): resolve os GUIDs
// reais do Sysmiddle (TAG_/GRT_/ATT_ do TargetLayoutGuid; LIN_/FLD_ do
// InputLayoutGuid) para o XPath COMPLETO no leiaute NF-e, destravando os 237
// LinkMappings do MapperVO real (hoje resolvidos só até a FOLHA, por convenção
// de nome — ver LinkMappingTranspiler §"LIMITE HONESTO").
//
// Fonte: o arquivo LayoutVO exportado do Connect Us (ex.:
// Documentos/Layout/layout-nfe.xml) — a MESMA árvore que o painel usa para
// desenhar o leiaute, com <ElementGuid> em cada nó. Achado no ambiente local:
// o LayoutGuid desse arquivo (LAY_767be1dd-…) bate EXATAMENTE com o
// TargetLayoutGuid do mapeador SEND_ENV real — não é fixture, é o leiaute de
// produção. Estrutura (por xsi:type):
//   GroupTagElementVO / TagElementVO → contribuem um segmento de XPath (Name).
//   AttributeElementVO               → contribui "@Name" no XPath do PAI.
//   ChoiceElementVO / SequenceElementVO → wrappers ESTRUTURAIS puramente de
//     ocorrência (Name literal "Choice"/"Sequence") — NÃO entram no XPath,
//     só repassam o caminho do pai aos filhos (mesma convenção do XSD
//     xs:choice/xs:sequence em XsdLeiauteIndex).
//
// 100% determinístico, sem LLM. Degrade gracioso: arquivo ausente/formato
// inesperado → catálogo vazio (Resolve retorna false), nunca derruba o
// chamador (ver .claude/rules/dotnet-standards.md — resiliência).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Um nó do LayoutVO real, indexado por <see cref="ElementGuid"/>.</summary>
/// <param name="ElementGuid">GUID prefixado (TAG_/GRT_/ATT_/FLD_/LIN_…) — chave do catálogo.</param>
/// <param name="XPath">Caminho sem namespace ("enviNFe/NFe/infNFe/dest/enderDest/xMun"; atributo: ".../@versao").</param>
/// <param name="Name">Nome local do elemento/atributo.</param>
/// <param name="IsAttribute">Veio de um AttributeElementVO.</param>
/// <param name="IsGroup">Tem filhos próprios no LayoutVO (GroupTagElementVO com Elements).</param>
/// <param name="MinOccurs">
/// De <c>MinimalOccurrence</c> (nó <c>ParentOccurrenceVO</c> — issue #425). <c>null</c> quando o
/// nó não declara ocorrência (ex.: atributo).
/// </param>
/// <param name="MaxOccurs">De <c>MaximumOccurrence</c> — mesma origem/degradação de <see cref="MinOccurs"/>.</param>
public sealed record GuidXPathEntry(
    string ElementGuid, string XPath, string Name, bool IsAttribute, bool IsGroup,
    int? MinOccurs = null, int? MaxOccurs = null);

/// <summary>
/// Nó recursivo da árvore de layout (issue #425 — endpoint <c>GET .../layout-tree</c>). Mesma
/// convenção de <see cref="GuidXPathEntry"/> (wrappers Choice/Sequence não viram nó, só repassam
/// os filhos ao pai), mas preservando a hierarquia completa em vez de um dicionário achatado.
/// </summary>
/// <param name="Kind"><c>"group"</c> (tem filhos), <c>"element"</c> (folha) ou <c>"attribute"</c>.</param>
public sealed record LayoutTreeNode(
    string? ElementGuid, string Name, string Kind, int? MinOccurs, int? MaxOccurs,
    IReadOnlyList<LayoutTreeNode> Children, string? XsiType = null);

/// <summary>Catálogo GUID→XPath construído a partir de um LayoutVO exportado (Connect Us).</summary>
public sealed class GuidXPathCatalog
{
    // Wrappers estruturais do LayoutVO: Name literal, NÃO viram segmento de XPath.
    private static readonly HashSet<string> WrapperTypes = new(StringComparer.Ordinal)
        { "ChoiceElementVO", "SequenceElementVO" };

    private readonly Dictionary<string, GuidXPathEntry> _byGuid;

    /// <summary>Todos os nós resolvidos (para diagnóstico/relatório).</summary>
    public IReadOnlyCollection<GuidXPathEntry> Entries => _byGuid.Values;

    /// <summary>GUID do LayoutVO (LayoutGuid) — para conferir contra InputLayoutGuid/TargetLayoutGuid do mapeador.</summary>
    public string? LayoutGuid { get; }

    private GuidXPathCatalog(Dictionary<string, GuidXPathEntry> byGuid, string? layoutGuid)
    {
        _byGuid = byGuid;
        LayoutGuid = layoutGuid;
    }

    /// <summary>
    /// Carrega um LayoutVO (XML exportado do Connect Us, xsi:type="XmlLayoutVO"/"TextLayoutVO").
    /// Degrade gracioso: arquivo ausente ou raiz inesperada → catálogo VAZIO (nunca lança).
    /// </summary>
    public static GuidXPathCatalog Load(string layoutPath, Action<string>? log = null)
    {
        if (!File.Exists(layoutPath))
        {
            log?.Invoke($"   [aviso] LayoutVO não encontrado ({layoutPath}) — catálogo GUID→XPath vazio.");
            return new GuidXPathCatalog(new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal), null);
        }

        string text;
        try
        {
            // Mesma pegadinha do MapperVO (RealMapperParser): o LayoutVO exportado
            // declara encoding="utf-16" no prólogo, mas os bytes são UTF-8 (com
            // BOM) — XDocument.Load direto falha ("no Unicode byte order mark").
            text = RealMapperParser.DecodeAndFixDeclaration(File.ReadAllBytes(layoutPath));
        }
        catch (Exception ex)
        {
            log?.Invoke($"   [aviso] LayoutVO ilegível ({ex.Message}) — catálogo GUID→XPath vazio.");
            return new GuidXPathCatalog(new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal), null);
        }

        return LoadFromXml(text, sourceLabel: Path.GetFileName(layoutPath), log);
    }

    /// <summary>
    /// Mesma lógica de <see cref="Load"/>, mas a partir de um LayoutVO já em memória (issue #425 —
    /// consumidor real é o lookup por GUID no banco via <c>ICachedLayoutService</c>/
    /// <c>LayoutDatabaseService</c>, não mais um caminho de arquivo local). O conteúdo já vem
    /// decodificado pelo <c>IDecryptionService</c> (mesmo padrão do <c>SysmiddleExplanationAdapter</c>
    /// com <c>Mapper.DecryptedContent</c>) — sem a pegadinha utf-16/UTF-8 do arquivo exportado.
    /// Degrade gracioso: XML ausente/malformado → catálogo VAZIO (nunca lança).
    /// </summary>
    public static GuidXPathCatalog LoadFromXml(string? xmlContent, string sourceLabel = "(memória)", Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            log?.Invoke($"   [aviso] LayoutVO vazio ({sourceLabel}) — catálogo GUID→XPath vazio.");
            return new GuidXPathCatalog(new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal), null);
        }

        XElement root;
        try
        {
            root = XDocument.Parse(xmlContent).Root
                ?? throw new InvalidOperationException("LayoutVO sem elemento raiz.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"   [aviso] LayoutVO ilegível ({ex.Message}, {sourceLabel}) — catálogo GUID→XPath vazio.");
            return new GuidXPathCatalog(new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal), null);
        }

        var layoutGuid = (string?)root.Element("LayoutGuid");
        var byGuid = new Dictionary<string, GuidXPathEntry>(StringComparer.Ordinal);

        var elementsRoot = root.Element("Elements");
        if (elementsRoot is not null)
            foreach (var el in elementsRoot.Elements("Element"))
                Caminha(el, basePath: "", byGuid);

        log?.Invoke($"   [guid-catalog] {byGuid.Count} GUIDs resolvidos de '{sourceLabel}' "
            + $"(LayoutGuid={layoutGuid ?? "?"}).");
        return new GuidXPathCatalog(byGuid, layoutGuid);
    }

    /// <summary>
    /// Constrói a árvore RECURSIVA do LayoutVO (issue #425 — endpoint de árvore dupla para o
    /// React replicar a UI do Connect Us). Reaproveita a mesma convenção de dispatch por
    /// <c>xsi:type</c>/wrappers Choice-Sequence de <see cref="Caminha"/>, mas preserva
    /// hierarquia em vez de achatar num dicionário. Degrade gracioso: XML ausente/malformado →
    /// <c>(LayoutGuid: null, Roots: [])</c>, nunca lança.
    /// </summary>
    public static (string? LayoutGuid, IReadOnlyList<LayoutTreeNode> Roots) BuildTree(
        string? xmlContent, string sourceLabel = "(memória)", Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            log?.Invoke($"   [aviso] LayoutVO vazio ({sourceLabel}) — árvore vazia.");
            return (null, Array.Empty<LayoutTreeNode>());
        }

        XElement root;
        try
        {
            root = XDocument.Parse(xmlContent).Root
                ?? throw new InvalidOperationException("LayoutVO sem elemento raiz.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"   [aviso] LayoutVO ilegível ({ex.Message}, {sourceLabel}) — árvore vazia.");
            return (null, Array.Empty<LayoutTreeNode>());
        }

        var layoutGuid = (string?)root.Element("LayoutGuid");
        var roots = CaminhaTreeFilhos(root.Element("Elements"));

        log?.Invoke($"   [guid-tree] {roots.Count} raiz(es) resolvida(s) de '{sourceLabel}' (LayoutGuid={layoutGuid ?? "?"}).");
        return (layoutGuid, roots);
    }

    private static void Caminha(XElement el, string basePath, Dictionary<string, GuidXPathEntry> byGuid)
    {
        // §trim: o LayoutVO exportado vem "pretty-printed" com quebras de linha
        // DENTRO do texto de <ElementGuid>/<Name> (indentação profunda passa de
        // 120+ colunas) — sem o Trim(), a chave do dicionário carrega espaços/
        // newlines embutidos e nunca casa com o GUID limpo do MapperVO (achado
        // real: só 1/237 LinkMappings resolvia antes deste fix).
        var tipo = ((string?)el.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type"))?.Trim() ?? "";
        var guid = ((string?)el.Element("ElementGuid"))?.Trim();
        var name = ((string?)el.Element("Name"))?.Trim() ?? "";
        var filhos = el.Element("Elements");

        var ehAtributo = tipo == "AttributeElementVO";
        var ehWrapper = WrapperTypes.Contains(tipo);

        // Wrapper (Choice/Sequence): NÃO adiciona segmento — repassa o path do pai aos filhos.
        var path = ehWrapper ? basePath
            : ehAtributo ? $"{basePath}/@{name}"
            : string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

        if (guid is not null && !ehWrapper)
        {
            var (min, max) = LeOcorrencia(el);
            byGuid[guid] = new GuidXPathEntry(guid, path, name, ehAtributo, filhos is not null, min, max);
        }

        if (filhos is not null)
        {
            // Atributo dono do caminho para os filhos: o do NÓ atual (sem alterar
            // para atributo — atributo nunca tem filhos no LayoutVO na prática).
            var pathParaFilhos = ehWrapper ? basePath : path;
            foreach (var filho in filhos.Elements("Element"))
                Caminha(filho, pathParaFilhos, byGuid);
        }
    }

    /// <summary>
    /// Mesma travessia de <see cref="Caminha"/>, mas retornando a árvore recursiva (issue #425)
    /// em vez de achatar num dicionário. Wrapper (Choice/Sequence) não vira nó — seus filhos são
    /// "adotados" como IRMÃOS diretos do nó pai (achatamento 0..N, não 1:1 — um Sequence com 3
    /// filhos flat-maps para 3 nós no pai; mesma convenção de XPath já usada pelo catálogo).
    /// </summary>
    private static LayoutTreeNode? CaminhaTree(XElement el)
    {
        var tipo = ((string?)el.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type"))?.Trim() ?? "";

        // Wrapper isolado (defensivo — não deveria aparecer como raiz de Elements/Element):
        // não tem identidade própria, então não há nó pra devolver aqui; ver CaminhaTreeFilhos
        // para o caso normal (wrapper como FILHO, achatado na lista do pai).
        if (WrapperTypes.Contains(tipo))
            return null;

        var guid = ((string?)el.Element("ElementGuid"))?.Trim();
        var name = ((string?)el.Element("Name"))?.Trim() ?? "";
        var filhos = el.Element("Elements");
        var ehAtributo = tipo == "AttributeElementVO";

        var childNodes = CaminhaTreeFilhos(filhos);
        var (min, max) = LeOcorrencia(el);
        var kind = ehAtributo ? "attribute" : filhos is not null ? "group" : "element";

        return new LayoutTreeNode(guid, name, kind, min, max, childNodes, string.IsNullOrEmpty(tipo) ? null : tipo);
    }

    /// <summary>
    /// Constrói a lista de filhos de um nó `Elements`, achatando wrappers Choice/Sequence: cada
    /// `Element` filho vira 0..N nós na lista (1 nó normal, ou os netos do wrapper quando o
    /// filho é Choice/Sequence).
    /// </summary>
    private static List<LayoutTreeNode> CaminhaTreeFilhos(XElement? elementsNode)
    {
        var result = new List<LayoutTreeNode>();
        if (elementsNode is null)
            return result;

        foreach (var filho in elementsNode.Elements("Element"))
        {
            var tipoFilho = ((string?)filho.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type"))?.Trim() ?? "";
            if (WrapperTypes.Contains(tipoFilho))
            {
                // Wrapper: seus próprios filhos entram direto na lista do pai (achatamento).
                result.AddRange(CaminhaTreeFilhos(filho.Element("Elements")));
                continue;
            }

            var node = CaminhaTree(filho);
            if (node is not null)
                result.Add(node);
        }

        return result;
    }

    /// <summary>Lê <c>MinimalOccurrence</c>/<c>MaximumOccurrence</c> (nó <c>ParentOccurrenceVO</c> — issue #425).</summary>
    private static (int? Min, int? Max) LeOcorrencia(XElement el)
    {
        var min = int.TryParse((string?)el.Element("MinimalOccurrence"), out var minParsed) ? minParsed : (int?)null;
        var max = int.TryParse((string?)el.Element("MaximumOccurrence"), out var maxParsed) ? maxParsed : (int?)null;
        return (min, max);
    }

    /// <summary>Resolve um GUID (TAG_/GRT_/ATT_/FLD_/LIN_…) para o XPath completo. Degrade: não encontrado → false.</summary>
    public bool TryResolve(string? guid, out GuidXPathEntry entry)
    {
        entry = null!;
        return guid is not null && _byGuid.TryGetValue(guid, out entry!);
    }

    public int Count => _byGuid.Count;
}
