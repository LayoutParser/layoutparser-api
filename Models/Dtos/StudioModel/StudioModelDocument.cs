namespace LayoutParserApi.Models.Dtos.StudioModel
{
    /// <summary>
    /// Modelo único do Mapping Studio (contrato v1, <c>schemaVersion = 1</c>), no formato UCMapper/ConnectUs:
    /// duas árvores (entrada/destino); ligações e regras penduradas no nó de destino.
    /// Ver <c>docs/architecture/studio-model-design.md</c> §2. Dicionários preservam a ORDEM DE INSERÇÃO
    /// (= ordem da árvore) para saída estável. Campos ausentes no artefato ficam <c>null</c> e não são serializados.
    /// </summary>
    public sealed record StudioModelDocument(
        int SchemaVersion,
        StudioArtifact Artifact,
        StudioCapabilities Capabilities,
        StudioTrees Trees,
        Dictionary<string, StudioNode> Nodes,
        Dictionary<string, StudioLink> Links,
        Dictionary<string, StudioRule> Rules,
        Dictionary<string, StudioDataType> Datatypes,
        List<StudioDiagnostic> Diagnostics);

    /// <summary>Identificação do artefato + hashes do(s) XML(s) autoritário(s).</summary>
    public sealed record StudioArtifact(
        string Engine,
        string Id,
        string? Name,
        string RawHash,
        string ETag,
        List<string> VariantFields,
        List<StudioSource> Sources);

    /// <summary>Fonte autoritária (<c>mapper</c> | <c>input-layout</c> | <c>target-layout</c>).</summary>
    public sealed record StudioSource(string Role, string? Id, string? RawHash);

    /// <summary>Capacidades do modelo (<c>Edit</c> é false na Fase 1, somente leitura).</summary>
    public sealed record StudioCapabilities(bool Edit, List<string> EditableOps);

    /// <summary>Árvores de entrada e destino.</summary>
    public sealed record StudioTrees(StudioTree Input, StudioTree Target);

    /// <summary><c>Format</c> ∈ text-positional | text-delimited | xml | json | unknown.</summary>
    public sealed record StudioTree(string? LayoutRef, string Format, List<string> RootIds, bool Available);

    /// <summary>Apresentação do nó; <c>Text</c> usa <c>?</c> quando o nome do DataType não é resolvido.</summary>
    public sealed record StudioNodeDisplay(string Text, string Icon, bool Linked);

    /// <summary>Nó de uma das árvores (entrada/destino), indexado por id em <c>Nodes</c>.</summary>
    public sealed record StudioNode(
        string Tree,
        string Type,
        string Name,
        string Path,
        string? ParentId,
        int Order,
        bool Required,
        StudioNodeDisplay Display,
        Dictionary<string, object?> Props);

    /// <summary>Apresentação do vínculo.</summary>
    public sealed record StudioLinkDisplay(string Text, string Icon, string? UnderTargetId);

    /// <summary>Opções do vínculo; <c>null</c> = elemento ausente no XML (nunca inventamos default).</summary>
    public sealed record StudioLinkOpts(
        string? Trim,
        bool? Truncate,
        string? Default,
        bool? AllowEmpty,
        bool? NotCreateGroupTagOnlyChilds,
        bool? CreateEmptyElement,
        bool? UniqueOccurrence,
        bool? CreateValuesDirectBySourceElement,
        bool? UseDecimalMapper,
        string? FullXPath);

    /// <summary>Vínculo origem→destino pendurado no nó de destino.</summary>
    public sealed record StudioLink(
        string? SourceId,
        string? TargetId,
        int Order,
        StudioLinkDisplay Display,
        StudioLinkOpts Opts,
        bool Iterates);

    /// <summary>Apresentação da regra.</summary>
    public sealed record StudioRuleDisplay(string Text, string Icon, string? UnderTargetId);

    /// <summary>Trecho de regra que não foi interpretado (motivo e intervalo opcional).</summary>
    public sealed record StudioOpaqueSpan(string Reason, int[]? Span);

    /// <summary>Regra (código/DSL) ancorada em um nó de destino.</summary>
    public sealed record StudioRule(
        string? AnchorId,
        StudioRuleDisplay Display,
        string? Name,
        string? Code,
        List<string> Reads,
        List<string> Writes,
        List<string> Functions,
        bool PrePos,
        List<StudioOpaqueSpan> Opaque);

    /// <summary>Tipo de dado resolvido via catálogo <c>DataTypeVO</c> (nome, ex. <c>Str_MAX</c>).</summary>
    public sealed record StudioDataType(string Name);

    /// <summary>
    /// Diagnóstico não bloqueante do studio-model (SOMENTE visualização; nada é removido/corrigido).
    /// Campos opcionais variam por <c>Code</c> e são omitidos quando nulos.
    /// Referências (#618): <c>REF_UNRESOLVED</c>, <c>REF_AMBIGUOUS</c>, <c>REF_T_CASE_MISMATCH</c>,
    /// <c>REF_INDEX_UNSUPPORTED</c>, <c>REF_NAME_BREAKS_TOKEN</c>, <c>REF_T_NOT_CONSUMED</c>.
    /// Integridade (#619): <c>LINK_ORPHAN_SOURCE</c>, <c>LINK_ORPHAN_TARGET</c>, <c>RULE_ORPHAN_TARGET</c>,
    /// <c>TARGET_LINK_AND_RULE</c> (error), <c>N1_ORDER_SENSITIVE</c>.
    /// </summary>
    /// <param name="Code">Código estável do diagnóstico.</param>
    /// <param name="Severity"><c>warning</c> ou <c>error</c>.</param>
    /// <param name="Message">Mensagem em PT-BR.</param>
    /// <param name="Id">Elemento principal afetado (ligação, regra ou destino, conforme o código).</param>
    /// <param name="Path">Caminho FullXPath citado na referência (sem o nome do layout).</param>
    /// <param name="Ids">Ids relacionados (ex.: homônimos em <c>REF_AMBIGUOUS</c>, origem/destino órfão).</param>
    /// <param name="Missing">Ids ausentes referenciados.</param>
    /// <param name="LinkIds">Ligações envolvidas, na ordem de arquivo (Sequence, depois ordem de leitura).</param>
    /// <param name="XsiType">Tipo xsi desconhecido, quando aplicável.</param>
    /// <param name="Span">Intervalo <c>[início, fim]</c> da referência (<c>I.</c>/<c>T.</c>) dentro do <c>Code</c> da regra.</param>
    /// <param name="Suggestion">Sugestão: caminho existente que difere em exatamente 1 segmento (<c>REF_UNRESOLVED</c>),
    /// caminho com a caixa correta (<c>REF_T_CASE_MISMATCH</c>) ou função alternativa (<c>REF_INDEX_UNSUPPORTED</c>).</param>
    /// <param name="RuleId">Regra que contém a referência/âncora.</param>
    /// <param name="WinnerId">Em <c>N1_ORDER_SENSITIVE</c>: ligação vencedora. É APROXIMAÇÃO ESTÁTICA (1ª ligação por
    /// Sequence cuja origem existe no modelo), sem dado de execução.</param>
    public sealed record StudioDiagnostic(
        string Code,
        string Severity,
        string Message,
        string? Id = null,
        string? Path = null,
        List<string>? Ids = null,
        List<string>? Missing = null,
        List<string>? LinkIds = null,
        string? XsiType = null,
        int[]? Span = null,
        string? Suggestion = null,
        string? RuleId = null,
        string? WinnerId = null);
}
