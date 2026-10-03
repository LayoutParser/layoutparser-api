namespace LayoutParserApi.Models.Dtos.Fiscal
{
    /// <summary>Cardinalidade de um nó (issue #425), de <c>MinimalOccurrence</c>/<c>MaximumOccurrence</c> — <c>null</c> quando o LayoutVO não declara (ex.: atributo).</summary>
    public sealed record LayoutTreeCardinality(int? Min, int? Max);

    /// <summary>
    /// Nó recursivo da árvore de layout (issue #425). <c>Kind</c> é <c>"group"</c> (tem filhos),
    /// <c>"element"</c> (folha) ou <c>"attribute"</c>. <c>ElementGuid</c> é o GUID estável
    /// (TAG_/GRT_/ATT_/FLD_/LIN_…) que casa com <see cref="LayoutTreeRule.SourceElementGuid"/>/
    /// <see cref="LayoutTreeRule.TargetElementGuid"/> — pode ser <c>null</c> em nós sem GUID no XML.
    /// </summary>
    public sealed record LayoutTreeNodeDto(
        string? ElementGuid,
        string Name,
        string Kind,
        LayoutTreeCardinality? Cardinality,
        IReadOnlyList<LayoutTreeNodeDto> Children);

    /// <summary>Domínio de <see cref="LayoutTreeSide.Kind"/> realmente emitido hoje (valores inalterados).</summary>
    public static class LayoutTreeKinds
    {
        /// <summary>LayoutVO posicional (<c>TextLayoutVO</c>).</summary>
        public const string Text = "text";
        /// <summary>LayoutVO de XML (<c>XmlLayoutVO</c>).</summary>
        public const string Xml = "xml";
        /// <summary>Layout não resolvido ou de tipo sem leitor. (json/smartdb NÃO são emitidos hoje.)</summary>
        public const string Unknown = "unknown";
    }

    /// <summary>Motivos de <see cref="LayoutTreeSide.UnavailableReason"/> (só quando a árvore não foi materializada).</summary>
    public static class LayoutTreeUnavailableReasons
    {
        /// <summary>GUID ausente no mapper, layout fora do índice/banco ou falha na consulta.</summary>
        public const string LayoutNotFound = "layout-not-found";
        /// <summary>Layout XML sem nós materializáveis (árvore dependeria de XSD, ainda não derivada).</summary>
        public const string XsdUnresolved = "xsd-unresolved";
        /// <summary>Conteúdo do LayoutVO vazio, XML inválido ou sem elementos legíveis.</summary>
        public const string LayoutUnreadable = "layout-unreadable";
        /// <summary>Tipo de LayoutVO sem leitor (kind <c>unknown</c>).</summary>
        public const string UnsupportedKind = "unsupported-kind";
    }

    /// <summary>
    /// Uma das duas árvores (origem/destino) — layout resolvido, tipo e raízes.
    /// <c>Kind</c> ∈ {<c>text</c>, <c>xml</c>, <c>unknown</c>} (ver <see cref="LayoutTreeKinds"/>).
    /// <c>UnavailableReason</c> é opcional (omitido quando a árvore foi materializada) — ver
    /// <see cref="LayoutTreeUnavailableReasons"/>.
    /// </summary>
    public sealed record LayoutTreeSide(string? LayoutGuid, string Kind, IReadOnlyList<LayoutTreeNodeDto> Roots, string? UnavailableReason = null);

    /// <summary>
    /// Vínculo direto campo→campo (<c>LinkMappingItemVO</c> real do Sysmiddle) entre um nó da
    /// árvore de origem e um nó da árvore de destino, pelos mesmos GUIDs que já aparecem em
    /// <c>MappingExplanation</c> (Slice 4).
    /// </summary>
    public sealed record LayoutTreeRule(string RuleId, string? SourceElementGuid, string? TargetElementGuid);

    /// <summary>
    /// Contrato de <c>GET .../mappings/{mappingId}/layout-tree</c> (issue #425, ADR de 2026-09-16).
    ///
    /// <para><b>Limitations (issue #430):</b> <c>Rules</c> cobre só vínculo direto campo→campo
    /// (<c>LinkMappingItemVO</c>) — regras condicionais/DSL (<c>MapperRule</c>/branches, que aparecem
    /// em <c>GET .../explanation</c> com <c>sourceRefs</c>/<c>targetRefs</c> prefixados <c>I.</c>/<c>T.</c>)
    /// NÃO entram aqui. A origem dessas regras é texto da DSL (ex. <c>I.xMun</c>), não um GUID de nó do
    /// catálogo — resolver isso exigiria reconstruir o contexto de nomes do parser DSL por fora do
    /// catálogo GUID→XPath existente (<c>GuidXPathCatalog</c> resolve por GUID, não por nome de campo),
    /// o que é desproporcional ao escopo desta issue e arriscaria "inventar" um GUID sem garantia de
    /// unicidade. Quando o mapper tem regras DSL, <c>Limitations</c> sinaliza isso explicitamente —
    /// o front deve tratar essas regras (presentes em <c>explanation.rules</c>) como "sem linha
    /// desenhável no layout-tree, só lista".</para>
    /// </summary>
    public sealed record LayoutTreeResponse(
        string MapperGuid,
        LayoutTreeSide Source,
        LayoutTreeSide Target,
        IReadOnlyList<LayoutTreeRule> Rules,
        IReadOnlyList<string> Limitations,
        LayoutTreeDslRules? DslRules = null);

    /// <summary>
    /// Regra condicional/DSL do mapper (aditivo ao <c>rules[]</c>). Reaproveita a tradução de
    /// <c>GET .../explanation</c>: <c>SourceRefs</c>/<c>TargetRefs</c> são texto prefixado <c>I.</c>/<c>T.</c>.
    /// <c>SourceNodeGuids</c>/<c>TargetNodeGuids</c> só são preenchidos quando o texto casa de forma
    /// UNÍVOCA (caminho completo ou nome) com um nó da árvore; ambíguo/inexistente → vazio (nunca inventa).
    /// <c>Resolved</c> = todas as refs de origem e destino casaram com exatamente um nó (e há ao menos uma).
    /// <c>Kind</c> ∈ {<c>dsl</c>, <c>opaque</c>} (<c>opaque</c> = fora da gramática/funções não traduzíveis).
    /// </summary>
    public sealed record LayoutTreeDslRule(
        string RuleId,
        string Name,
        string Kind,
        bool Authoritative,
        string SupportLevel,
        IReadOnlyList<string> SourceRefs,
        IReadOnlyList<string> TargetRefs,
        IReadOnlyList<string> SourceNodeGuids,
        IReadOnlyList<string> TargetNodeGuids,
        bool Resolved,
        string? Condition,
        string Description,
        string? TechnicalDetail);

    /// <summary>
    /// Página de regras DSL. <c>Total</c> = após o filtro (<c>targetNodeGuid</c>) e antes da paginação;
    /// <c>Unresolved</c> = quantas do mapper inteiro não puderam ser vinculadas a nós.
    /// </summary>
    public sealed record LayoutTreeDslRules(int Total, int Unresolved, int Offset, int Limit, IReadOnlyList<LayoutTreeDslRule> Items);

    /// <summary>Opções de <c>dslRules[]</c> no layout-tree: filtro por nó de destino e paginação.</summary>
    public sealed record LayoutTreeDslOptions(string? TargetNodeGuid = null, int Offset = 0, int Limit = LayoutTreeDslOptions.DefaultLimit)
    {
        public const int DefaultLimit = 200;
        public const int MaxLimit = 500;
    }
}
