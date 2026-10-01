namespace LayoutParserApi.Models.Dtos.Fiscal
{
    /// <summary>
    /// Níveis de confiabilidade de uma <see cref="ExplainedRule"/> — nunca inventa "authoritative"
    /// sem gramática/AST reconhecida por trás (Slice 4 — issue #226/#227, design §1).
    /// </summary>
    public static class MappingExplanationSupportLevel
    {
        /// <summary>Regra reconhecida 100% pela gramática/AST conhecida.</summary>
        public const string Authoritative = "authoritative";

        /// <summary>Reconhecida mas com heurística/ainda não revisada por humano.</summary>
        public const string BestEffort = "best_effort";

        /// <summary>Elemento reconhecido como "existe" mas sem semântica traduzível.</summary>
        public const string Opaque = "opaque";

        /// <summary>Elemento fora de qualquer gramática esperada, ou artefato inexistente.</summary>
        public const string Unsupported = "unsupported";
    }

    /// <summary>Capacidades do motor por trás de uma explicação (Slice 4, design §1). Nunca lidas de config — cada adapter hard-coda as suas.</summary>
    /// <param name="DeterministicTest">
    /// <c>true</c> quando o Fiscal Test Lab (<see cref="IMappingTestRunService"/>) consegue executar
    /// este engine de ponta a ponta e a mesma entrada sempre produz o mesmo resultado — propriedade
    /// do TEST RUNNER local, não do gabarito de geração (não confundir com <c>validationBasis:
    /// declared_dsl</c> do gerador de amostras, que descreve a origem do XML esperado, não se o
    /// motor de teste consegue rodá-lo). <c>tcl</c>/<c>xslt</c> = <c>true</c> (#421);
    /// <c>sysmiddle</c> = <c>false</c> — nunca participa de <c>createTestRun</c>, só explica
    /// (read-only), e o runner real segue bloqueado por licença FiatMQ (ver
    /// <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §2). Default
    /// <c>true</c> só existe para não quebrar call sites existentes ao tornar o parâmetro aditivo —
    /// todo adapter real deve declarar o valor explicitamente.
    /// </param>
    public sealed record EngineCapabilities(bool Execute, bool Explain, bool Author, bool Compile, bool Publish, bool DeterministicTest = true);

    /// <summary>Referência de schema de origem/destino (quando resolvível).</summary>
    public sealed record SchemaRef(string? LayoutGuid, string? Description);

    /// <summary>Evidência que sustenta uma <see cref="ExplainedRule"/> (mesmo shape de <c>MappingDraftRuleEvidence</c>).</summary>
    public sealed record EvidenceRef(string Kind, string Reference);

    /// <summary>Uma regra de mapeamento já traduzida para o contrato canônico, PT-BR, determinístico (sem LLM).</summary>
    public sealed record ExplainedRule(
        string RuleId,
        IReadOnlyList<string> SourceRefs,
        IReadOnlyList<string> TargetRefs,
        string? Condition,
        IReadOnlyList<string> Operations,
        string Cardinality,
        IReadOnlyList<EvidenceRef> Evidence,
        string HumanDescription,
        string? TechnicalDetail,
        string SupportLevel,
        IReadOnlyList<string>? Functions = null);

    /// <summary>
    /// Contrato canônico de explicação de mapeamento (Slice 4 — issue #226/#227), independente do
    /// motor (<c>engine=sysmiddle|tcl|xslt</c>). <c>GET .../explanation</c> sempre retorna 200 com
    /// este contrato, mesmo 100% <c>opaque</c> — nunca falha por não entender uma regra.
    /// </summary>
    public sealed record MappingExplanation(
        string MappingId,
        string Version,
        string Engine,
        EngineCapabilities Capabilities,
        SchemaRef? SourceSchema,
        SchemaRef? TargetSchema,
        IReadOnlyList<ExplainedRule> Rules,
        string? Description,
        IReadOnlyList<string> Limitations,
        int OpaqueRuleCount);
}
