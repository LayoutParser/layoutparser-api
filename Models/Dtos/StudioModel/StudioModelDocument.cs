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
    /// Diagnóstico não bloqueante (exceto <c>TARGET_HAS_LINK_AND_RULE</c>, severidade <c>error</c>).
    /// Campos opcionais variam por <c>Code</c> (ver design §2.5).
    /// </summary>
    public sealed record StudioDiagnostic(
        string Code,
        string Severity,
        string Message,
        string? Id = null,
        string? Path = null,
        List<string>? Ids = null,
        List<string>? Missing = null,
        List<string>? LinkIds = null,
        string? XsiType = null);
}
