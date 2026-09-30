using System.Security.Cryptography;
using System.Text;

using System.Xml.Schema;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Editor manual de artefato TCL/XSLT (issue #381 sub-fases 5b/5c, ADR
    /// <c>adr-edicao-manual-artefato-versionamento-2026-09-10.md</c>). O dublê de
    /// <see cref="IMappingReleaseStore"/> reproduz a MESMA regra de negócio do
    /// <c>SqlMappingReleaseStore</c> real: identidade nova por
    /// <c>(DraftId, RulesSnapshotHash, ArtifactContentHash)</c>, base = release mais recente do
    /// draft/engine, e o filtro <c>ArtifactSource='compiled'</c> na compilação (regressão coberta
    /// explicitamente abaixo).
    /// </summary>
    public class MappingArtifactEditControllerTests
    {
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

        private sealed class FakeFiscalProfileResolver : IFiscalProfileResolver
        {
            public FiscalProfileValidationResult Validate(FiscalProfile profile) => new(true, null, null);
            public FiscalResolvedXsd? Resolve(string documentType, string schemaVersion) => null;
        }

        /// <summary>Issue #380: nunca exercitado por estes testes (releases sem FiscalProfile) — devolve sempre null, igual ao contrato de degradação real.</summary>
        private sealed class FakeRequiredCoverageCalculator : IRequiredCoverageCalculator
        {
            public RequiredCoverageResult? Calculate(XmlSchemaSet schemaSet, string rootElementName, string targetNamespace, IReadOnlyCollection<string> targetRefs) => null;
        }

        /// <summary>Config vazia: <c>TryLoadSchemaSet</c> degrada para null (BasePath default não existe no ambiente de teste) — suficiente para estes testes, que não exercitam FiscalProfile.</summary>
        private static XsdValidationService BuildXsdValidationService()
            => new(NullLogger<XsdValidationService>.Instance, new ConfigurationBuilder().Build(),
                new XmlDocumentTypeDetector(NullLogger<XmlDocumentTypeDetector>.Instance),
                new PdfOrientationReader(NullLogger<PdfOrientationReader>.Instance));

        /// <summary>Reproduz a lógica de <c>SqlMappingReleaseStore</c>: base = release mais recente do (DraftId, Engine); identidade nova por (DraftId, RulesSnapshotHash, ArtifactContentHash); compilação filtra ArtifactSource='compiled'.</summary>
        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public List<MappingReleaseDetail> Releases { get; } = new();
            public Dictionary<Guid, MappingReleaseDetail> ById => Releases.ToDictionary(r => r.ReleaseId);

            private static string ComputeHash(string content)
                => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken, FiscalProfile? fiscalProfile = null)
            {
                // Regressão (ADR §2.2 item 3): filtra ArtifactSource='compiled' — sem isso, uma
                // release manual_edit (mesmo RulesSnapshotHash da base) seria devolvida no lugar.
                var existing = Releases
                    .Where(r => r.DraftId == draftId && r.RulesSnapshotHash == rulesSnapshotHash && r.ArtifactSource == MappingReleaseArtifactSource.Compiled)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefault();
                if (existing != null)
                    return Task.FromResult(existing);

                var detail = new MappingReleaseDetail(
                    Guid.NewGuid(), workspaceId, draftId, engine, artifacts, sourceRuleIds, compileDiagnostics,
                    rulesSnapshotHash, null, MappingReleaseStatus.DraftCompiled, correlationId, DateTimeOffset.UtcNow, "AAAA",
                    "development", null, null, null, null, null, null, null,
                    MappingReleaseArtifactSource.Compiled, null, null, Array.Empty<string>());
                Releases.Add(detail);
                return Task.FromResult(detail);
            }

            public Task<CreateManualEditOutcome> CreateManualEditArtifactReleaseAsync(Guid workspaceId, Guid draftId, string engine, string content, string manualEditReason, string expectedArtifactHash, Guid actorUserId, string correlationId, CancellationToken cancellationToken)
            {
                var baseRelease = Releases
                    .Where(r => r.DraftId == draftId && r.Engine == engine)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefault();
                var baseArtifact = baseRelease?.Artifacts.FirstOrDefault(a => a.Kind == engine);

                if (baseRelease == null || baseArtifact == null)
                    return Task.FromResult(new CreateManualEditOutcome(CreateManualEditResult.NoBaseRelease, null, null));

                if (!string.Equals(baseArtifact.Hash, expectedArtifactHash, StringComparison.Ordinal))
                    return Task.FromResult(new CreateManualEditOutcome(CreateManualEditResult.Conflict, null, baseArtifact));

                var newHash = ComputeHash(content);

                var existingManualEdit = Releases.FirstOrDefault(r =>
                    r.DraftId == draftId && r.RulesSnapshotHash == baseRelease.RulesSnapshotHash &&
                    r.ArtifactSource == MappingReleaseArtifactSource.ManualEdit &&
                    r.Artifacts.FirstOrDefault(a => a.Kind == engine)?.Hash == newHash);
                if (existingManualEdit != null)
                    return Task.FromResult(new CreateManualEditOutcome(CreateManualEditResult.Success, existingManualEdit, null));

                var newArtifact = new MappingReleaseArtifact(engine, content, newHash, DateTimeOffset.UtcNow);
                var derived = new MappingReleaseDetail(
                    Guid.NewGuid(), workspaceId, draftId, engine, new[] { newArtifact }, baseRelease.SourceRuleIds,
                    Array.Empty<MappingReleaseCompileDiagnostic>(), baseRelease.RulesSnapshotHash, null,
                    MappingReleaseStatus.DraftCompiled, correlationId, DateTimeOffset.UtcNow, Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                    "development", null, null, null, null, null, null, null,
                    MappingReleaseArtifactSource.ManualEdit, baseRelease.ReleaseId, manualEditReason, new[] { engine });
                Releases.Add(derived);
                return Task.FromResult(new CreateManualEditOutcome(CreateManualEditResult.Success, derived, null));
            }

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

        // --- helpers ---

        private static MappingCompilationController BuildController(FakeDraftStore draftStore, FakeReleaseStore releaseStore, Guid userId)
            => new(draftStore, releaseStore, compileService: null!, testRunService: null!, new FakeFiscalProfileResolver(),
                BuildXsdValidationService(), new FakeRequiredCoverageCalculator(),
                new FakeCurrentUser { UserId = userId }, NullLogger<MappingCompilationController>.Instance);

        private static (Guid WorkspaceId, Guid DraftId, MappingReleaseDetail BaseRelease) SeedXsltDraftWithCompiledRelease(FakeDraftStore draftStore, FakeReleaseStore releaseStore, string xsltContent = "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"1.0\"><xsl:template match=\"/\"/></xsl:stylesheet>")
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            draftStore.Drafts[draftId] = draft;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xsltContent))).ToLowerInvariant();
            var artifact = new MappingReleaseArtifact("xslt", xsltContent, hash, DateTimeOffset.UtcNow);
            var release = new MappingReleaseDetail(
                Guid.NewGuid(), workspaceId, draftId, "xslt", new[] { artifact }, Array.Empty<Guid>(),
                Array.Empty<MappingReleaseCompileDiagnostic>(), "rules-hash-1", null, MappingReleaseStatus.DraftCompiled,
                "corr-0", DateTimeOffset.UtcNow, "AAAA", "development", null, null, null, null, null, null, null,
                MappingReleaseArtifactSource.Compiled, null, null, Array.Empty<string>());
            releaseStore.Releases.Add(release);

            return (workspaceId, draftId, release);
        }

        private static string IfMatchFor(string hash) => Convert.ToBase64String(Encoding.UTF8.GetBytes(hash));

        // --- fluxo feliz ---

        [Fact]
        public async Task UpdateArtifact_edicao_valida_cria_release_derivada_manual_edit()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);

            var editedXslt = "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"1.0\"><xsl:template match=\"/\"><edited/></xsl:template></xsl:stylesheet>";
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = editedXslt, Justification = "ajuste manual" }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ok.Value!;
            var artifactSource = (string)payload.GetType().GetProperty("artifactSource", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(payload)!;
            var derivedFromReleaseId = (Guid?)payload.GetType().GetProperty("derivedFromReleaseId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(payload);
            var rulesSnapshotHash = (string)payload.GetType().GetProperty("rulesSnapshotHash", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(payload)!;
            var manuallyEditedKinds = (IReadOnlyList<string>?)payload.GetType().GetProperty("manuallyEditedArtifactKinds", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(payload);
            var status = (string)payload.GetType().GetProperty("status", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(payload)!;
            var rulesDesynced = (bool)payload.GetType().GetProperty("rulesDesynced", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(payload)!;

            Assert.Equal(MappingReleaseArtifactSource.ManualEdit, artifactSource);
            Assert.Equal(baseRelease.ReleaseId, derivedFromReleaseId);
            Assert.Equal(baseRelease.RulesSnapshotHash, rulesSnapshotHash);
            Assert.Equal(new[] { "xslt" }, manuallyEditedKinds);
            Assert.Equal(MappingReleaseStatus.DraftCompiled, status);
            Assert.True(rulesDesynced);
            Assert.Equal(2, releaseStore.Releases.Count); // base + derivada, nunca sobrescreve.
        }

        // --- idempotência: mesmo content, If-Match = eTag da resposta anterior → mesma release ---

        [Fact]
        public async Task UpdateArtifact_mesmo_content_duas_vezes_converge_para_a_mesma_release()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var editedXslt = "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"1.0\"><xsl:template match=\"/\"><edited/></xsl:template></xsl:stylesheet>";

            var controller1 = BuildController(draftStore, releaseStore, userId);
            controller1.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller1.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);
            var result1 = await controller1.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = editedXslt, Justification = "ajuste manual" }, CancellationToken.None);
            var ok1 = Assert.IsType<OkObjectResult>(result1);
            var releaseId1 = (Guid)ok1.Value!.GetType().GetProperty("releaseId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(ok1.Value)!;
            var eTag1 = (string)ok1.Value!.GetType().GetProperty("eTag", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(ok1.Value)!;

            Assert.Equal(2, releaseStore.Releases.Count);

            // Reenvia a MESMA edição — If-Match agora é o eTag devolvido pela primeira chamada
            // (o artefato "atual" já é o editado).
            var controller2 = BuildController(draftStore, releaseStore, userId);
            controller2.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller2.Request.Headers["If-Match"] = $"\"{eTag1}\"";
            var result2 = await controller2.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = editedXslt, Justification = "ajuste manual (repetido)" }, CancellationToken.None);

            var ok2 = Assert.IsType<OkObjectResult>(result2);
            var releaseId2 = (Guid)ok2.Value!.GetType().GetProperty("releaseId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(ok2.Value)!;

            Assert.Equal(releaseId1, releaseId2); // convergiu — não duplicou.
            Assert.Equal(2, releaseStore.Releases.Count); // nenhuma release nova gravada.
        }

        // --- 412: If-Match divergente ---

        [Fact]
        public async Task UpdateArtifact_ifMatch_divergente_retorna_412_com_current()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, _) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor("hash-que-nao-bate");

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"1.0\"><xsl:template match=\"/\"/></xsl:stylesheet>", Justification = "x" }, CancellationToken.None);

            var conflict = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status412PreconditionFailed, conflict.StatusCode);
        }

        // --- 428: sem If-Match ---

        [Fact]
        public async Task UpdateArtifact_sem_ifMatch_retorna_428()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, _) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<a/>", Justification = "x" }, CancellationToken.None);

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status428PreconditionRequired, obj.StatusCode);
        }

        // --- 400: If-Match não é base64 ---

        [Fact]
        public async Task UpdateArtifact_ifMatch_nao_base64_retorna_400()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, _) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = "!!!nao-base64!!!";

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<a/>", Justification = "x" }, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // --- 422: justification ausente ---

        [Fact]
        public async Task UpdateArtifact_sem_justification_retorna_422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<a/>", Justification = null }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        // --- 422: content vazio ---

        [Fact]
        public async Task UpdateArtifact_content_vazio_retorna_422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "", Justification = "x" }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        // --- 422: engine=sysmiddle ---

        [Fact]
        public async Task UpdateArtifact_engine_sysmiddle_retorna_422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var result = await controller.UpdateArtifact(workspaceId, draftId, "sysmiddle",
                new UpdateArtifactRequest { Content = "<a/>", Justification = "x" }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        // --- 422: XSLT sintaticamente inválido ---

        [Fact]
        public async Task UpdateArtifact_xslt_malformado_retorna_422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<xsl:stylesheet><nao fecha", Justification = "x" }, CancellationToken.None);

            var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal(0, releaseStore.Releases.Count(r => r.ArtifactSource == MappingReleaseArtifactSource.ManualEdit));
        }

        // --- 422: TCL com chaves desbalanceadas ---

        [Fact]
        public async Task UpdateArtifact_tcl_chaves_desbalanceadas_retorna_422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            var baseContent = "proc foo {} { return 1 }";
            var baseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseContent))).ToLowerInvariant();
            releaseStore.Releases.Add(new MappingReleaseDetail(
                Guid.NewGuid(), workspaceId, draftId, "tcl", new[] { new MappingReleaseArtifact("tcl", baseContent, baseHash, DateTimeOffset.UtcNow) },
                Array.Empty<Guid>(), Array.Empty<MappingReleaseCompileDiagnostic>(), "rules-hash-tcl", null,
                MappingReleaseStatus.DraftCompiled, "corr-0", DateTimeOffset.UtcNow, "AAAA", "development",
                null, null, null, null, null, null, null, MappingReleaseArtifactSource.Compiled, null, null, Array.Empty<string>()));

            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseHash);

            var result = await controller.UpdateArtifact(workspaceId, draftId, "tcl",
                new UpdateArtifactRequest { Content = "proc foo {} { return 1", Justification = "x" }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        // --- 422: sem release compilada para servir de base ---

        [Fact]
        public async Task UpdateArtifact_sem_release_base_retorna_422()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            draftStore.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor("qualquer-hash");

            var result = await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"1.0\"/>", Justification = "x" }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        // --- 404: draft de outro workspace / sem membership ---

        [Fact]
        public async Task UpdateArtifact_draft_de_outro_workspace_retorna_404()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (_, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var workspaceDoAtacante = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var result = await controller.UpdateArtifact(workspaceDoAtacante, draftId, "xslt",
                new UpdateArtifactRequest { Content = "<a/>", Justification = "x" }, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        // --- regressão: recompilar depois de manual_edit não sobrescreve nem "some" com ela ---

        [Fact]
        public async Task Recompilar_depois_de_manual_edit_devolve_a_compiled_original_nao_a_manual_edit()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();
            var (workspaceId, draftId, baseRelease) = SeedXsltDraftWithCompiledRelease(draftStore, releaseStore);
            var userId = Guid.NewGuid();
            var controller = BuildController(draftStore, releaseStore, userId);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.Request.Headers["If-Match"] = IfMatchFor(baseRelease.Artifacts[0].Hash);

            var editedXslt = "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"1.0\"><xsl:template match=\"/\"><edited/></xsl:template></xsl:stylesheet>";
            await controller.UpdateArtifact(workspaceId, draftId, "xslt",
                new UpdateArtifactRequest { Content = editedXslt, Justification = "ajuste manual" }, CancellationToken.None);

            Assert.Equal(2, releaseStore.Releases.Count);

            // "Recompilar" com o MESMO RulesSnapshotHash da base (nada mudou nas regras) — deve
            // devolver a release compiled ORIGINAL, nunca a manual_edit (mesmo RulesSnapshotHash).
            var recompiled = await releaseStore.CreateOrGetCompiledReleaseAsync(
                workspaceId, draftId, "xslt", baseRelease.RulesSnapshotHash, Array.Empty<Guid>(),
                baseRelease.Artifacts, Array.Empty<MappingReleaseCompileDiagnostic>(), "corr-recompile", Guid.NewGuid(), CancellationToken.None);

            Assert.Equal(baseRelease.ReleaseId, recompiled.ReleaseId);
            Assert.Equal(MappingReleaseArtifactSource.Compiled, recompiled.ArtifactSource);
            Assert.Equal(2, releaseStore.Releases.Count); // no-op idempotente — não criou uma 3ª release.
        }

        // --- RBAC: 403 papel insuficiente / 200 papel suficiente, via o filtro real ---

        private static async Task<IActionResult> RunFilterAsync(RequireWorkspaceRoleFilter filter, Guid workspaceId)
        {
            var httpContext = new DefaultHttpContext();
            var routeData = new RouteData();
            routeData.Values["workspaceId"] = workspaceId.ToString();
            var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
            var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

            IActionResult? nextResult = null;
            await filter.OnActionExecutionAsync(context, () =>
            {
                nextResult = new OkResult();
                return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()));
            });

            return context.Result ?? nextResult!;
        }

        private sealed class FakeIdentityWorkspaceStore : IIdentityWorkspaceStore
        {
            public Dictionary<(Guid WorkspaceId, Guid UserId), string> Memberships { get; } = new();
            public Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
            {
                if (!Memberships.TryGetValue((workspaceId, userId), out var role))
                    return Task.FromResult<WorkspaceSummary?>(null);
                return Task.FromResult<WorkspaceSummary?>(new WorkspaceSummary(workspaceId, "Workspace", "team", role, DateTimeOffset.UtcNow));
            }
        }

        [Theory]
        [InlineData(WorkspaceRole.Reviewer)]
        [InlineData(WorkspaceRole.Operator)]
        [InlineData(WorkspaceRole.Viewer)]
        public async Task RequireWorkspaceRole_papel_insuficiente_para_editar_artefato_retorna_403(string roleInsuficiente)
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore();
            workspaceStore.Memberships[(workspaceId, userId)] = roleInsuficiente;
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(new[] { WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner }, currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        }

        [Theory]
        [InlineData(WorkspaceRole.Mapper)]
        [InlineData(WorkspaceRole.FiscalAdmin)]
        [InlineData(WorkspaceRole.Owner)]
        public async Task RequireWorkspaceRole_papel_suficiente_para_editar_artefato_deixa_passar(string roleSuficiente)
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore();
            workspaceStore.Memberships[(workspaceId, userId)] = roleSuficiente;
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(new[] { WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner }, currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId);

            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task RequireWorkspaceRole_sem_membership_retorna_404()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore();
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(new[] { WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner }, currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId);

            Assert.IsType<NotFoundResult>(result);
        }
    }
}
