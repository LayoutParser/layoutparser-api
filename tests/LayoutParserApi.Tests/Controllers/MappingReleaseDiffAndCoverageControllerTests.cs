using System.Xml.Schema;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #380 — diff release×release (#198.2b) e cobertura estática de destinos obrigatórios
    /// (#198.5) em <see cref="MappingCompilationController"/>. Dublês mínimos (mesmo padrão de
    /// <c>MappingArtifactEditControllerTests</c>) — só implementam o que os dois endpoints exercitam.
    /// </summary>
    public class MappingReleaseDiffAndCoverageControllerTests : IDisposable
    {
        private readonly string _xsdTempDir = Path.Combine(Path.GetTempPath(), "lp-tests-xsd-" + Guid.NewGuid());

        public void Dispose()
        {
            if (Directory.Exists(_xsdTempDir))
                Directory.Delete(_xsdTempDir, recursive: true);
        }

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public Dictionary<Guid, MappingDraftDetail> Drafts { get; } = new();
            public Task<bool> RevisionBelongsToWorkspacePackageAsync(Guid workspaceId, Guid packageId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult(true);
            public Task<(IReadOnlyList<MappingDraftSummary> Items, int TotalCount)> ListByWorkspaceAsync(
                Guid workspaceId, int page, int pageSize, string? engine, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por este fake — cobertos em MappingDraftsControllerListTests.");

            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());
            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Drafts.TryGetValue(draftId, out var d) ? d : null);
            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification, IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftDetail?> SetFiscalProfileAsync(Guid draftId, Guid userId, FiscalProfile profile, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public List<MappingReleaseDetail> Releases { get; } = new();

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken, FiscalProfile? fiscalProfile = null)
                => throw new NotSupportedException();
            public Task<CreateManualEditOutcome> CreateManualEditArtifactReleaseAsync(Guid workspaceId, Guid draftId, string engine, string content, string manualEditReason, string expectedArtifactHash, Guid actorUserId, string correlationId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Releases.FirstOrDefault(r => r.ReleaseId == releaseId));
            public Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? status, Guid? draftId, string? environment, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> DeprecateAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> ArchiveAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FakeFiscalProfileResolver : IFiscalProfileResolver
        {
            public FiscalResolvedXsd? ResolvedXsd { get; set; }
            public FiscalProfileValidationResult Validate(FiscalProfile profile) => new(true, null, ResolvedXsd);
            public FiscalResolvedXsd? Resolve(string documentType, string schemaVersion) => ResolvedXsd;
        }

        private static MappingReleaseDetail MakeRelease(
            Guid workspaceId, Guid draftId, MappingTestRunSummary? testRunSummary = null,
            IReadOnlyList<Guid>? sourceRuleIds = null, FiscalProfile? fiscalProfile = null) => new(
            Guid.NewGuid(), workspaceId, draftId, "xslt", Array.Empty<MappingReleaseArtifact>(),
            sourceRuleIds ?? Array.Empty<Guid>(), Array.Empty<MappingReleaseCompileDiagnostic>(), "rules-hash",
            testRunSummary, MappingReleaseStatus.DraftCompiled, "corr-0", DateTimeOffset.UtcNow, "AAAA",
            "development", null, null, null, null, null, null, fiscalProfile,
            MappingReleaseArtifactSource.Compiled, null, null, Array.Empty<string>());

        private MappingCompilationController BuildController(
            FakeDraftStore draftStore, FakeReleaseStore releaseStore, Guid userId,
            IFiscalProfileResolver? fiscalProfileResolver = null, IRequiredCoverageCalculator? coverageCalculator = null)
        {
            var xsdValidationService = new XsdValidationService(
                NullLogger<XsdValidationService>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XsdValidation:BasePath"] = _xsdTempDir,
                }).Build(),
                new XmlDocumentTypeDetector(NullLogger<XmlDocumentTypeDetector>.Instance),
                new PdfOrientationReader(NullLogger<PdfOrientationReader>.Instance));

            return new MappingCompilationController(
                draftStore, releaseStore, compileService: null!, testRunService: null!,
                fiscalProfileResolver ?? new FakeFiscalProfileResolver(),
                xsdValidationService, coverageCalculator ?? new RequiredCoverageCalculator(),
                new FakeCurrentUser { UserId = userId }, NullLogger<MappingCompilationController>.Instance);
        }

        private const string SimpleSchema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="http://teste.local/nfe"
                       elementFormDefault="qualified">
              <xs:element name="NFe">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="cUF" type="xs:string" minOccurs="1"/>
                    <xs:element name="natOp" type="xs:string" minOccurs="1"/>
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """;

        private void SeedXsdFile(string version)
        {
            var versionDir = Path.Combine(_xsdTempDir, version);
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, "schema.xsd"), SimpleSchema);
        }

        // ============================= #198.2b — diff release×release =============================

        [Fact]
        public async Task DiffReleases_releasesIdenticas_RetornaListaVazia()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var summary = new MappingTestRunSummary(1, 0, 100, true, true, Array.Empty<string>(), Array.Empty<MappingTestRunDivergence>(),
                ActualXml: "<NFe><cUF>35</cUF></NFe>", ExpectedXml: "<NFe><cUF>35</cUF></NFe>");
            var releaseA = MakeRelease(workspaceId, draftId, summary);
            var releaseB = MakeRelease(workspaceId, draftId, summary);
            releaseStore.Releases.Add(releaseA);
            releaseStore.Releases.Add(releaseB);

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var result = await controller.DiffReleases(workspaceId, draftId, releaseA.ReleaseId, releaseB.ReleaseId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var diffs = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("diffs")!.GetValue(ok.Value)!;
            Assert.Empty(diffs.Cast<object>());
        }

        [Fact]
        public async Task DiffReleases_releasesDivergentes_AgrupaPorElemento()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var summaryA = new MappingTestRunSummary(1, 0, 100, true, true, Array.Empty<string>(), Array.Empty<MappingTestRunDivergence>(),
                ActualXml: "<NFe><cUF>35</cUF></NFe>", ExpectedXml: "<NFe><cUF>35</cUF></NFe>");
            var summaryB = summaryA with { ActualXml = "<NFe><cUF>31</cUF></NFe>" };
            var releaseA = MakeRelease(workspaceId, draftId, summaryA);
            var releaseB = MakeRelease(workspaceId, draftId, summaryB);
            releaseStore.Releases.Add(releaseA);
            releaseStore.Releases.Add(releaseB);

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var result = await controller.DiffReleases(workspaceId, draftId, releaseA.ReleaseId, releaseB.ReleaseId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var diffsByElement = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("diffsByElement")!.GetValue(ok.Value)!;
            var groups = diffsByElement.Cast<object>().ToList();
            Assert.Single(groups);
            var element = (string)groups[0].GetType().GetProperty("element")!.GetValue(groups[0])!;
            Assert.Equal("cUF", element);
        }

        [Fact]
        public async Task DiffReleases_umaReleaseSemTestRun_Retorna422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var summary = new MappingTestRunSummary(1, 0, 100, true, true, Array.Empty<string>(), Array.Empty<MappingTestRunDivergence>(),
                ActualXml: "<NFe><cUF>35</cUF></NFe>", ExpectedXml: "<NFe><cUF>35</cUF></NFe>");
            var releaseA = MakeRelease(workspaceId, draftId, summary);
            var releaseB = MakeRelease(workspaceId, draftId, testRunSummary: null); // nunca testada.
            releaseStore.Releases.Add(releaseA);
            releaseStore.Releases.Add(releaseB);

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var result = await controller.DiffReleases(workspaceId, draftId, releaseA.ReleaseId, releaseB.ReleaseId, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task DiffReleases_releaseDeOutroWorkspace_Retorna404()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var outroWorkspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var summary = new MappingTestRunSummary(1, 0, 100, true, true, Array.Empty<string>(), Array.Empty<MappingTestRunDivergence>(),
                ActualXml: "<NFe/>", ExpectedXml: "<NFe/>");
            var releaseA = MakeRelease(workspaceId, draftId, summary);
            var releaseB = MakeRelease(outroWorkspaceId, Guid.NewGuid(), summary); // workspace/draft diferentes.
            releaseStore.Releases.Add(releaseA);
            releaseStore.Releases.Add(releaseB);

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var result = await controller.DiffReleases(workspaceId, draftId, releaseA.ReleaseId, releaseB.ReleaseId, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task DiffReleases_draftDeOutroWorkspace_Retorna404()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var result = await controller.DiffReleases(Guid.NewGuid(), draftId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        // ============================= #198.5 — cobertura de obrigatórios =============================

        [Fact]
        public async Task GetRelease_semFiscalProfile_RequiredCoverageNulo()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            var release = MakeRelease(workspaceId, draftId, fiscalProfile: null);
            releaseStore.Releases.Add(release);

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var result = await controller.GetRelease(workspaceId, draftId, release.ReleaseId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var requiredCoverage = ok.Value!.GetType().GetProperty("requiredCoverage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(ok.Value);
            Assert.Null(requiredCoverage);
        }

        [Fact]
        public async Task GetRelease_comFiscalProfileEXsdReal_CalculaCoberturaParcial()
        {
            SeedXsdFile("v1");
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();

            var ruleId = Guid.NewGuid();
            var rule = new MappingDraftRuleDetail(
                ruleId, draftId, new[] { "/origem/cuf" }, new[] { "/NFe/cUF" }, "copy", "{}", "{}", "1:1",
                Array.Empty<MappingDraftRuleEvidence>(), "high", MappingDraftRuleStatus.Accepted,
                Array.Empty<string>(), DateTimeOffset.UtcNow, "AAAA");
            draftStore.Drafts[draftId] = new MappingDraftDetail(
                draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, new[] { rule });

            var fiscalProfile = new FiscalProfile(FiscalDocumentType.Nfe, "v1", FiscalOperation.Outbound, "SP");
            var release = MakeRelease(workspaceId, draftId, sourceRuleIds: new[] { ruleId }, fiscalProfile: fiscalProfile);
            releaseStore.Releases.Add(release);

            var resolver = new FakeFiscalProfileResolver { ResolvedXsd = new FiscalResolvedXsd("v1", "http://teste.local/nfe", "NFe") };
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId, fiscalProfileResolver: resolver);

            var result = await controller.GetRelease(workspaceId, draftId, release.ReleaseId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var requiredCoverage = ok.Value!.GetType().GetProperty("requiredCoverage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(ok.Value);
            Assert.NotNull(requiredCoverage);
            var percent = (double)requiredCoverage!.GetType().GetProperty("percent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(requiredCoverage)!;
            var uncovered = (System.Collections.IEnumerable)requiredCoverage.GetType().GetProperty("uncovered", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(requiredCoverage)!;

            // cUF coberto (TargetRefs da regra accepted), natOp obrigatório e não coberto → 50%.
            Assert.Equal(50, percent);
            Assert.Contains("/NFe/natOp", uncovered.Cast<string>());
        }

        [Fact]
        public async Task GetRelease_xsdAusenteNoDisco_DegradaParaNulo()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var fiscalProfile = new FiscalProfile(FiscalDocumentType.Nfe, "versao-sem-arquivo", FiscalOperation.Outbound, "SP");
            var release = MakeRelease(workspaceId, draftId, fiscalProfile: fiscalProfile);
            releaseStore.Releases.Add(release);

            var resolver = new FakeFiscalProfileResolver { ResolvedXsd = new FiscalResolvedXsd("versao-sem-arquivo", "http://teste.local/nfe", "NFe") };
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId, fiscalProfileResolver: resolver);

            var result = await controller.GetRelease(workspaceId, draftId, release.ReleaseId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var requiredCoverage = ok.Value!.GetType().GetProperty("requiredCoverage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(ok.Value);
            Assert.Null(requiredCoverage); // sem arquivo XSD no BasePath — degrada, não lança.
        }
    }
}
