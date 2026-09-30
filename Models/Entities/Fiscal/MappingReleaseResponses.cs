using System.Text.Json.Serialization;

namespace LayoutParserApi.Models.Entities.Fiscal
{
    // DTOs de resposta HTTP das releases de mapeamento. Existem (em vez de objetos anônimos) porque o
    // AddJsonOptions global usa WhenWritingNull e o front espera todo campo nullable PRESENTE com valor
    // null — e só um tipo nomeado aceita [JsonIgnore(Condition = Never)] por propriedade. Os campos não
    // anuláveis mantêm nome e formato idênticos aos das respostas anteriores.

    /// <summary>Corpo de <c>GET/PATCH</c> release do <c>MappingCompilationController</c>.</summary>
    public sealed class MappingReleaseResponse
    {
        public Guid ReleaseId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid DraftId { get; init; }
        public string Engine { get; init; } = string.Empty;
        public IReadOnlyList<MappingReleaseArtifact> Artifacts { get; init; } = Array.Empty<MappingReleaseArtifact>();
        public IReadOnlyList<Guid> SourceRuleIds { get; init; } = Array.Empty<Guid>();
        public IReadOnlyList<MappingReleaseCompileDiagnostic> CompileDiagnostics { get; init; } = Array.Empty<MappingReleaseCompileDiagnostic>();
        public string RulesSnapshotHash { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public MappingTestRunSummary? TestRunSummary { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public IReadOnlyList<MappingTestRunDivergenceGroup>? DivergencesByRuleId { get; init; }

        public string Status { get; init; } = string.Empty;
        public string ArtifactSource { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Guid? DerivedFromReleaseId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? ManualEditReason { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public IReadOnlyList<string>? ManuallyEditedArtifactKinds { get; init; }
        public bool RulesDesynced { get; init; }
        public string CorrelationId { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public string ETag { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public FiscalProfileResponse? FiscalProfile { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public RequiredCoverageResponse? RequiredCoverage { get; init; }
    }

    /// <summary>Perfil fiscal + <c>resolvedXsd</c> derivado (issue #379, ADR §2.6).</summary>
    public sealed class FiscalProfileResponse
    {
        public string DocumentType { get; init; } = string.Empty;
        public string SchemaVersion { get; init; } = string.Empty;
        public string Operation { get; init; } = string.Empty;
        public string Jurisdiction { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public FiscalResolvedXsd? ResolvedXsd { get; init; }
    }

    /// <summary>Cobertura estática de destinos obrigatórios (issue #380, #198.5).</summary>
    public sealed class RequiredCoverageResponse
    {
        public double Percent { get; init; }
        public IReadOnlyList<string> Uncovered { get; init; } = Array.Empty<string>();
    }

    /// <summary>Corpo das transições de governança (approve/publish/rollback/deprecate/archive).</summary>
    public sealed class MappingReleaseGovernanceResponse
    {
        public string Origin { get; init; } = string.Empty;
        public Guid ReleaseId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid DraftId { get; init; }
        public string Engine { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Guid? ApprovedByUserId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTimeOffset? ApprovedAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? ApprovalJustification { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Guid? PublishedByUserId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Guid? PreviousPublishedReleaseId { get; init; }

        public string CorrelationId { get; init; } = string.Empty;
        public string ETag { get; init; } = string.Empty;
    }
}
