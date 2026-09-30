using System.Text.Json.Serialization;

namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Status de uma <see cref="MappingRelease"/> (Slice 5 — issue #231, design §2 — estendido pelo
    /// Slice 7, issue #94, governança/publicação). <c>InReview</c>/<c>Approved</c>/<c>Published</c>/
    /// <c>Deprecated</c>/<c>Archived</c> são novos deste slice; não reabrem o enum, só o estendem.
    /// </summary>
    public static class MappingReleaseStatus
    {
        /// <summary>Artefato(s) gerados pelo transpilador determinístico — ainda sem teste.</summary>
        public const string DraftCompiled = "draft_compiled";

        /// <summary>Passou no Fiscal Test Lab (XSD válido + diff canônico sem divergência).</summary>
        public const string TestPassed = "test_passed";

        /// <summary>Falhou no Fiscal Test Lab — <c>RequiredGatesPassed=false</c>, bloqueia entrada em <see cref="InReview"/>.</summary>
        public const string TestFailed = "test_failed";

        /// <summary>Em revisão humana — entrada automática pelo endpoint <c>approve</c>, nunca persiste isolada.</summary>
        public const string InReview = "in_review";

        /// <summary>Aprovada por <c>Reviewer</c>/<c>FiscalAdmin</c> — pronta para publicação.</summary>
        public const string Approved = "approved";

        /// <summary>Publicada e imutável — nenhuma escrita de artefato depois disso; edição gera nova revisão.</summary>
        public const string Published = "published";

        /// <summary>Substituída por outra publicação (publish de outra release ou alvo de rollback).</summary>
        public const string Deprecated = "deprecated";

        /// <summary>Fora de uso definitivamente — reservado para retenção/expurgo futuro (não usado ainda pelos endpoints deste slice).</summary>
        public const string Archived = "archived";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            DraftCompiled, TestPassed, TestFailed, InReview, Approved, Published, Deprecated, Archived
        };
    }

    /// <summary>"tcl" ou "xslt" — o conteúdo gerado pelo <c>MappingDraftRuleTranspiler</c> para o motor.</summary>
    public sealed record MappingReleaseArtifact(string Kind, string Content, string Hash, DateTimeOffset GeneratedAt);

    /// <summary>
    /// Origem do(s) artefato(s) de uma <see cref="MappingRelease"/> (issue #381, ADR
    /// <c>adr-edicao-manual-artefato-versionamento-2026-09-10.md</c> §2.1). <c>Compiled</c> é o
    /// default — todas as releases nascidas de <c>POST .../compile</c>. <c>ManualEdit</c> marca uma
    /// release derivada via <c>PATCH .../artifacts/{engine}</c>: o artefato deixou de ser função das
    /// regras estruturadas, e o diff por regra do Fiscal Test Lab fica desabilitado para os kinds editados.
    /// </summary>
    public static class MappingReleaseArtifactSource
    {
        public const string Compiled = "compiled";
        public const string ManualEdit = "manual_edit";
    }

    /// <summary>Diagnóstico de uma regra que não pôde ser transpilada (espelha <c>TranspileDiagnostic</c>, persistido).</summary>
    public sealed record MappingReleaseCompileDiagnostic(Guid RuleId, string Severity, string Message);

    /// <summary>
    /// Uma divergência encontrada pelo Fiscal Test Lab, com provenance completa: nó do XML →
    /// regra de origem (via <c>lp:ruleId</c>/atributo <c>ruleId</c> embutido pelo transpilador) →
    /// evidência da regra (Slice 3) → campo/posição de origem (<c>SourceRefs</c> da regra).
    /// </summary>
    public sealed record MappingTestRunDivergence(
        string Kind,
        string XPath,
        // Contrato nullable = sempre presente: o global usa WhenWritingNull, mas o front espera as
        // chaves com valor null (não ausentes).
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Expected,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Actual,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? RuleId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? SourceRefs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<MappingDraftRuleEvidence>? Evidence);

    /// <summary>Resumo do Fiscal Test Lab — <see cref="RequiredGatesPassed"/> é o contrato com o Slice 7 (design §2).</summary>
    public sealed record MappingTestRunSummary(
        int Passed,
        int Failed,
        double CoveragePercent,
        bool RequiredGatesPassed,
        bool XsdValid,
        IReadOnlyList<string> XsdErrors,
        IReadOnlyList<MappingTestRunDivergence> Divergences,
        // Issue #380 (#198.2b): XML real produzido pelo XSLT e o gabarito sanitizado usados no
        // diff canônico deste test-run — persistidos para permitir diff release×release
        // (GET .../releases/diff) via CanonicalDiffer sem precisar reexecutar o XSLT. Trailing
        // com default — não quebra call sites existentes. null quando o test-run não chegou a
        // produzir XML (falha antes/durante a aplicação do XSLT, ou engine=tcl sem runner).
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ActualXml = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExpectedXml = null);

    /// <summary>
    /// Diff granular por regra (issue #367 / LayoutParserReact #228): agrupa as divergências de
    /// <see cref="MappingTestRunSummary.Divergences"/> que resolveram provenance até a mesma
    /// <c>RuleId</c> — "por ruleId, o(s) XPath(s) afetados + o diff naquele escopo" (critério de
    /// aceite da #367). Não substitui <see cref="MappingTestRunSummary.Divergences"/> (agregado por
    /// release já consumido pelo front em <c>8cff185</c>) — é uma view adicional sobre os mesmos dados.
    /// </summary>
    public sealed record MappingTestRunDivergenceGroup(
        Guid RuleId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? SourceRefs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<MappingDraftRuleEvidence>? Evidence,
        IReadOnlyList<MappingTestRunDivergence> Diffs);

    /// <summary>Helpers de projeção sobre <see cref="MappingTestRunSummary"/> — issue #367.</summary>
    public static class MappingTestRunSummaryExtensions
    {
        /// <summary>
        /// Agrupa <see cref="MappingTestRunSummary.Divergences"/> por <c>RuleId</c>. Divergências sem
        /// provenance resolvida (<c>RuleId == null</c> — ex.: nó estrutural extra/faltante que
        /// nenhuma regra aceita/editada tentou gerar) ficam de fora do agrupamento; elas continuam
        /// visíveis na lista plana <see cref="MappingTestRunSummary.Divergences"/>.
        /// </summary>
        public static IReadOnlyList<MappingTestRunDivergenceGroup> GroupDivergencesByRule(this MappingTestRunSummary summary)
            => summary.Divergences
                .Where(d => d.RuleId.HasValue)
                .GroupBy(d => d.RuleId!.Value)
                .Select(g => new MappingTestRunDivergenceGroup(
                    g.Key,
                    g.First().SourceRefs,
                    g.First().Evidence,
                    g.ToList()))
                .ToList();
    }

    /// <summary>
    /// Artefato compilado + resultado de teste de um <see cref="MappingDraft"/> (Slice 5 — issue #231).
    /// Nasce em <see cref="MappingReleaseStatus.DraftCompiled"/> na compilação, evolui para
    /// <c>test_passed</c>/<c>test_failed</c> após o Fiscal Test Lab. Nunca <c>approved</c>/<c>published</c>
    /// nesta etapa (Slice 7).
    /// </summary>
    public class MappingRelease
    {
        public Guid ReleaseId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid DraftId { get; set; }

        /// <summary>"tcl" ou "xslt" — nunca "sysmiddle", herdado do draft.</summary>
        public string Engine { get; set; } = string.Empty;

        public IReadOnlyList<MappingReleaseArtifact> Artifacts { get; set; } = Array.Empty<MappingReleaseArtifact>();

        /// <summary>Snapshot dos RuleId accepted/edited no momento da compilação — proveniência até a decisão humana.</summary>
        public IReadOnlyList<Guid> SourceRuleIds { get; set; } = Array.Empty<Guid>();

        public IReadOnlyList<MappingReleaseCompileDiagnostic> CompileDiagnostics { get; set; } = Array.Empty<MappingReleaseCompileDiagnostic>();

        /// <summary>Hash do conjunto de regras accepted/edited (idempotência: mesmo draft + mesmo snapshot não duplica).</summary>
        public string RulesSnapshotHash { get; set; } = string.Empty;

        public MappingTestRunSummary? TestRunSummary { get; set; }

        public string Status { get; set; } = MappingReleaseStatus.DraftCompiled;

        public string CorrelationId { get; set; } = string.Empty;

        public Guid CreatedByJobId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>Ambiente onde a release está/esteve ativa (Slice 7) — não onde foi testada.</summary>
        public string Environment { get; set; } = "development";

        public Guid? ApprovedByUserId { get; set; }

        public DateTimeOffset? ApprovedAt { get; set; }

        public string? ApprovalJustification { get; set; }

        public Guid? PublishedByUserId { get; set; }

        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>Snapshot gravado no momento do <c>publish</c>: a release que estava <c>Published</c> antes desta (design §3, rollback).</summary>
        public Guid? PreviousPublishedReleaseId { get; set; }

        /// <summary>Snapshot congelado do perfil fiscal do draft na hora da compilação (issue #379, ADR §2.1/§2.2). Imutável desde a criação da release. <c>null</c> = draft compilado sem perfil definido.</summary>
        public FiscalProfile? FiscalProfile { get; set; }

        /// <summary>"compiled" (default) ou "manual_edit" (issue #381, ADR §2.1).</summary>
        public string ArtifactSource { get; set; } = MappingReleaseArtifactSource.Compiled;

        /// <summary>Release-base cuja cópia de artefato foi editada à mão. <c>null</c> para releases <c>compiled</c>.</summary>
        public Guid? DerivedFromReleaseId { get; set; }

        /// <summary><c>justification</c> do <c>PATCH .../artifacts/{engine}</c> — obrigatória para <c>manual_edit</c>.</summary>
        public string? ManualEditReason { get; set; }

        /// <summary>Quais artefatos (<c>Artifacts</c>) foram editados à mão. Vazio para releases <c>compiled</c>.</summary>
        public IReadOnlyList<string> ManuallyEditedArtifactKinds { get; set; } = Array.Empty<string>();

        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Registro imutável de cada transição de estado de uma <see cref="MappingRelease"/> (Slice 7 —
    /// issue #94). Uma linha por transição — <c>Justification</c> obrigatória em
    /// <c>approve</c>/<c>publish</c>/<c>rollback</c>. Não reaproveita nenhum log genérico existente:
    /// é o contrato de auditoria específico de governança citado na spec §12/§14.
    /// </summary>
    public class MappingTransition
    {
        public Guid TransitionId { get; set; }

        public Guid ReleaseId { get; set; }

        public string FromStatus { get; set; } = string.Empty;

        public string ToStatus { get; set; } = string.Empty;

        public Guid ActorUserId { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string? Justification { get; set; }

        /// <summary>JSON livre com o snapshot dos gates que passaram no momento da transição (ex.: <see cref="MappingTestRunSummary"/>).</summary>
        public string? ChecksSnapshot { get; set; }
    }
}
