using System.Reflection;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Fiscal;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #345 (ADR docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md): cobre
    /// (1) a gravação best-effort de <c>tbFieldCorrectionContext</c> em <c>execute-candidates</c>
    /// (via reflection sobre o método privado, mesma técnica já usada por
    /// <see cref="TransformationExecutionControllerUserIsolationTests"/> para
    /// <c>TryEnqueueAiCandidate</c> — rodar o método público inteiro exigiria o runner x86 real) e
    /// (2) o endpoint público <c>POST field-correction</c> ponta-a-ponta contra um
    /// <see cref="IFieldCorrectionStore"/> fake em memória.
    /// </summary>
    public class TransformationExecutionControllerFieldCorrectionTests
    {
        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; } = "tester";
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => UserId != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId { get; set; } = Guid.NewGuid();
        }

        /// <summary>Fake em memória — nunca toca em SQL real, cobre os 6 métodos de <see cref="IFieldCorrectionStore"/>.</summary>
        private sealed class FakeFieldCorrectionStore : IFieldCorrectionStore
        {
            public FieldCorrectionContext? SavedContext { get; private set; }
            public int SaveContextCallCount { get; private set; }
            public bool ThrowOnGetContext { get; set; }
            public List<(FieldCorrectionReportInput Input, Guid ReportedByUserId)> CreatedReports { get; } = new();
            public List<FieldCorrectionReportSummary> Reports { get; } = new();

            public Task SaveContextAsync(FieldCorrectionContext context, CancellationToken cancellationToken)
            {
                SavedContext = context;
                SaveContextCallCount++;
                return Task.CompletedTask;
            }

            public Task<FieldCorrectionContext?> GetContextAsync(string documentId, CancellationToken cancellationToken)
            {
                if (ThrowOnGetContext)
                    throw new InvalidOperationException("IdentityDatabase indisponível (simulado)");

                return Task.FromResult(SavedContext != null && SavedContext.DocumentId == documentId ? SavedContext : null);
            }

            public Task<Guid> CreateReportAsync(FieldCorrectionReportInput input, Guid reportedByUserId, CancellationToken cancellationToken)
            {
                var id = Guid.NewGuid();
                CreatedReports.Add((input, reportedByUserId));
                Reports.Add(new FieldCorrectionReportSummary(
                    id, input.DocumentId, input.CandidateId, input.FieldPath,
                    input.ObservedValue, input.ExpectedValue, input.Justification,
                    reportedByUserId, FieldCorrectionReportStatus.Pending, DateTimeOffset.UtcNow, null, null));
                return Task.FromResult(id);
            }

            public Task<IReadOnlyList<FieldCorrectionReportSummary>> ListPendingReportsAsync(int limit, CancellationToken cancellationToken)
            {
                IReadOnlyList<FieldCorrectionReportSummary> pending = Reports
                    .Where(r => r.Status == FieldCorrectionReportStatus.Pending)
                    .OrderBy(r => r.CreatedAtUtc)
                    .Take(limit)
                    .ToList();
                return Task.FromResult(pending);
            }

            public Task<FieldCorrectionReportSummary?> GetReportAsync(Guid reportId, CancellationToken cancellationToken)
                => Task.FromResult(Reports.FirstOrDefault(r => r.ReportId == reportId));

            public Task<bool> TransitionStatusAsync(Guid reportId, string newStatus, Guid reviewedByUserId, CancellationToken cancellationToken)
            {
                var idx = Reports.FindIndex(r => r.ReportId == reportId);
                if (idx < 0 || Reports[idx].Status != FieldCorrectionReportStatus.Pending)
                    return Task.FromResult(false);

                Reports[idx] = Reports[idx] with
                {
                    Status = newStatus,
                    ReviewedByUserId = reviewedByUserId,
                    ReviewedAtUtc = DateTimeOffset.UtcNow
                };
                return Task.FromResult(true);
            }
        }

        private sealed class FakeWorkspaces : LayoutParserApi.Services.Interfaces.IIdentityWorkspaceService
        {
            public string Role { get; init; } = "viewer";
            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken ct)
                => throw new NotSupportedException();
            public Task<LayoutParserApi.Services.Interfaces.WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken ct)
            {
                var w = new LayoutParserApi.Services.Interfaces.WorkspaceSummary(Guid.NewGuid(), "WS", "shared", Role, DateTimeOffset.UtcNow);
                return Task.FromResult(new LayoutParserApi.Services.Interfaces.WorkspaceMeResult(w.WorkspaceId, new[] { w }));
            }
            public Task<LayoutParserApi.Services.Interfaces.WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct)
                => throw new NotSupportedException();
        }

        private static (TransformationExecutionController Controller, FakeFieldCorrectionStore Store, FakeCurrentUser User, string TrainingDataPath) BuildController(
            LayoutParserApi.Services.Interfaces.IIdentityWorkspaceService? identityWorkspaces = null)
        {
            var store = new FakeFieldCorrectionStore();
            var user = new FakeCurrentUser();

            var trainingDataPath = Path.Combine(Path.GetTempPath(), "lp-tests-training-" + Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XslSynth:TrainingDataPath"] = trainingDataPath })
                .Build();
            var trainingCapture = new TrainingDataCaptureService(
                NullLogger<TrainingDataCaptureService>.Instance, config);

            // scopeFactory real (necessário: TryPersistFieldCorrectionContext abre um IServiceScope
            // próprio dentro do Task.Run, mesmo padrão de TryEnqueueAiCandidate) resolvendo o MESMO
            // fake singleton, para o teste poder inspecionar o que foi gravado.
            var services = new ServiceCollection();
            services.AddSingleton<IFieldCorrectionStore>(store);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var controller = new TransformationExecutionController(
                NullLogger<TransformationExecutionController>.Instance,
                pipelineService: null!,
                validatorService: null!,
                learningService: null!,
                autoGenerator: null!,
                lowCode: null!,
                lowCodeAuto: null!,
                layoutDb: null!,
                lowCodeOptions: Options.Create(new LowCodeRunnerOptions()),
                aiCandidateService: new NoopAiCandidateService(),
                aiFallbackGate: new NoopAiFallbackSuppressionGate(),
                aiUserInstructionStore: new AiUserInstructionStore(),
                aiUserSessionStore: null!,
                currentUser: user,
                mapperDb: null!,
                layoutParser: null!,
                fieldMappingComposition: null!,
                scopeFactory: scopeFactory,
                canaryAlert: new LayoutParserApi.Services.Security.CanaryAlertService(
                    NullLogger<LayoutParserApi.Services.Security.CanaryAlertService>.Instance),
                fieldCorrectionStore: store,
                trainingDataCapture: trainingCapture,
                identityWorkspaces: identityWorkspaces);

            return (controller, store, user, trainingDataPath);
        }

        private sealed class NoopAiCandidateService : IAiTransformationCandidateService
        {
            public Task EnqueueAsync(
                string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
                string inputContent, string? groundTruthXml, CancellationToken cancellationToken,
                IReadOnlyList<LayoutParserApi.Models.Entities.ParsedField>? parsedFields = null) => Task.CompletedTask;

            public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken) =>
                Task.FromResult(new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound });
        }

        private sealed class NoopAiFallbackSuppressionGate : IAiFallbackSuppressionGate
        {
            public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt)
            {
                retryAt = default;
                return false;
            }

            public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown) { }
            public void ClearCooldown(Guid layoutGuid) { }
        }

        // --- TryPersistFieldCorrectionContext (best-effort, chamado de dentro de execute-candidates) ---

        [Fact]
        public async Task TryPersistFieldCorrectionContext_grava_gabarito_sysmiddle_quando_existe()
        {
            var (controller, store, _, _) = BuildController();

            var request = new TransformationRequest { InputContent = "linha-txt", LayoutName = "LAYOUT_X" };
            var layoutGuid = Guid.NewGuid();
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };
            var mapperGuid = Guid.NewGuid().ToString();
            var candidates = new List<TransformationCandidate>
            {
                new TransformationCandidate
                {
                    CandidateId = $"sysmiddle-{mapperGuid}",
                    Pathway = "sysmiddle",
                    TransformedXml = "<xml>gabarito</xml>"
                }
            };
            var documentId = DocumentIdCalculator.Calculate(request.InputContent, layoutGuid.ToString());

            var method = typeof(TransformationExecutionController)
                .GetMethod("TryPersistFieldCorrectionContext", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Método TryPersistFieldCorrectionContext não encontrado.");

            method.Invoke(controller, new object?[] { request, layoutRecord, candidates, documentId, layoutGuid.ToString() });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.SavedContext == null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.NotNull(store.SavedContext);
            Assert.Equal(documentId, store.SavedContext!.DocumentId);
            Assert.Equal(mapperGuid, store.SavedContext.MapperGuid);
            Assert.Equal("<xml>gabarito</xml>", store.SavedContext.GroundTruthXml);
            Assert.Equal(layoutGuid.ToString(), store.SavedContext.LayoutGuid);
            Assert.Equal("LAYOUT_X", store.SavedContext.LayoutName);
        }

        [Fact]
        public async Task TryPersistFieldCorrectionContext_sem_candidato_sysmiddle_grava_sem_gabarito()
        {
            var (controller, store, _, _) = BuildController();

            var request = new TransformationRequest { InputContent = "<xml/>", LayoutName = "LAYOUT_Y" };
            var layoutGuid = Guid.NewGuid();
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };
            var candidates = new List<TransformationCandidate>(); // entrada XML — sem pathway sysmiddle
            var documentId = DocumentIdCalculator.Calculate(request.InputContent, layoutGuid.ToString());

            var method = typeof(TransformationExecutionController)
                .GetMethod("TryPersistFieldCorrectionContext", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(controller, new object?[] { request, layoutRecord, candidates, documentId, layoutGuid.ToString() });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.SavedContext == null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.NotNull(store.SavedContext);
            Assert.Null(store.SavedContext!.MapperGuid);
            Assert.Null(store.SavedContext.GroundTruthXml);
        }

        // --- POST field-correction ---

        [Fact]
        public async Task ReportFieldCorrection_documento_valido_retorna_202_e_grava_pending()
        {
            var (controller, store, user, _) = BuildController();
            var context = new FieldCorrectionContext(
                "doc_abc123", "mapper-1", "MeuMapper", "layout-guid-1", "LAYOUT_X",
                "<xml>entrada</xml>", "<xml>gabarito</xml>", DateTimeOffset.UtcNow);
            await store.SaveContextAsync(context, CancellationToken.None);

            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_abc123",
                CandidateId = "tclxsl-1",
                FieldPath = "/nfeProc/NFe/infNFe/ide/nNF",
                ObservedValue = "001",
                ExpectedValue = "1",
                Justification = "Não deveria ter zero à esquerda"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(result);
            Assert.Single(store.CreatedReports);
            var (input, reportedBy) = store.CreatedReports[0];
            Assert.Equal("doc_abc123", input.DocumentId);
            Assert.Equal("001", input.ObservedValue);
            Assert.Equal("1", input.ExpectedValue);
            Assert.Equal(user.UserId, reportedBy);
        }

        [Fact]
        public async Task ReportFieldCorrection_so_Leitor_retorna_403_e_nao_grava()
        {
            var (controller, store, _, _) = BuildController(new FakeWorkspaces { Role = "viewer" });
            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_x", CandidateId = "c", FieldPath = "/a", ObservedValue = "x", ExpectedValue = "y"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, obj.StatusCode);
            Assert.Empty(store.CreatedReports);
        }

        [Theory]
        [InlineData("operator")]
        [InlineData("mapper")]
        [InlineData("fiscal_admin")]
        public async Task ReportFieldCorrection_Operador_ou_acima_passa_pelo_gate_de_papel(string role)
        {
            var (controller, store, _, _) = BuildController(new FakeWorkspaces { Role = role });
            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_inexistente", CandidateId = "c", FieldPath = "/a", ObservedValue = "x", ExpectedValue = "y"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result); // passou do gate; parou no contexto inexistente
        }

        [Fact]
        public async Task ReportFieldCorrection_documentId_sem_contexto_retorna_404()
        {
            var (controller, _, _, _) = BuildController();
            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_inexistente",
                CandidateId = "tclxsl-1",
                FieldPath = "/a/b",
                ObservedValue = "x",
                ExpectedValue = "y"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task ReportFieldCorrection_sem_identidade_resolvida_retorna_404_failclosed()
        {
            var (controller, _, user, _) = BuildController();
            user.UserId = null; // identidade não resolvida (TrustedIdentityMiddleware não confiou na origem)

            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_qualquer",
                CandidateId = "tclxsl-1",
                FieldPath = "/a/b",
                ObservedValue = "x",
                ExpectedValue = "y"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result); // sem corpo — distinto do 404 "contexto expirado" acima
        }

        [Theory]
        [InlineData("", "cand", "/a", "obs", "exp")]
        [InlineData("doc_1", "", "/a", "obs", "exp")]
        [InlineData("doc_1", "cand", "", "obs", "exp")]
        [InlineData("doc_1", "cand", "/a", "", "exp")]
        [InlineData("doc_1", "cand", "/a", "obs", "")]
        public async Task ReportFieldCorrection_campo_obrigatorio_ausente_retorna_400(
            string documentId, string candidateId, string fieldPath, string observedValue, string expectedValue)
        {
            var (controller, _, _, _) = BuildController();
            var request = new FieldCorrectionRequest
            {
                DocumentId = documentId,
                CandidateId = candidateId,
                FieldPath = fieldPath,
                ObservedValue = observedValue,
                ExpectedValue = expectedValue
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task ReportFieldCorrection_IdentityDatabase_indisponivel_degrada_para_404_sem_lancar()
        {
            var (controller, store, _, _) = BuildController();
            store.ThrowOnGetContext = true;

            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_qualquer",
                CandidateId = "tclxsl-1",
                FieldPath = "/a/b",
                ObservedValue = "x",
                ExpectedValue = "y"
            };

            // Não pode lançar — resiliência (dotnet-standards.md): dependência externa indisponível
            // degrada para uma resposta clara, nunca derruba o request.
            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        // --- Curadoria: GET field-correction/pending + POST field-correction/{id}/review (issue #346) ---

        /// <summary>Cria um contexto + um reporte pending e devolve (store, reportId).</summary>
        private static async Task<(FakeFieldCorrectionStore Store, Guid ReportId)> SeedPendingReportAsync(
            TransformationExecutionController controller, FakeFieldCorrectionStore store,
            string observed = "001", string expected = "1", string groundTruth = "<xml>gabarito</xml>")
        {
            var context = new FieldCorrectionContext(
                "doc_cur", "mapper-1", "MeuMapper", "layout-guid-1", "LAYOUT_X",
                "<xml>entrada</xml>", groundTruth, DateTimeOffset.UtcNow);
            await store.SaveContextAsync(context, CancellationToken.None);

            var report = new FieldCorrectionRequest
            {
                DocumentId = "doc_cur",
                CandidateId = "tclxsl-1",
                FieldPath = "/nfeProc/NFe/infNFe/ide/nNF",
                ObservedValue = observed,
                ExpectedValue = expected,
                Justification = "zero à esquerda"
            };
            var created = await controller.ReportFieldCorrection(report, CancellationToken.None);
            var accepted = Assert.IsType<AcceptedResult>(created);
            var reportId = (Guid)accepted.Value!.GetType().GetProperty("reportId")!.GetValue(accepted.Value)!;
            return (store, reportId);
        }

        private static string? ReadCapturedJsonl(string trainingDataPath)
        {
            if (!Directory.Exists(trainingDataPath)) return null;
            var files = Directory.GetFiles(trainingDataPath, "runtime-capture-*.jsonl");
            return files.Length == 0 ? null : string.Concat(files.Select(File.ReadAllText));
        }

        [Fact]
        public async Task ListPendingFieldCorrections_retorna_somente_pendentes()
        {
            var (controller, store, _, _) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store);

            var pendingBefore = Assert.IsType<OkObjectResult>(await controller.ListPendingFieldCorrections(100, CancellationToken.None));
            Assert.Equal(1, (int)pendingBefore.Value!.GetType().GetProperty("count")!.GetValue(pendingBefore.Value)!);

            await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "rejected" }, CancellationToken.None);

            var pendingAfter = Assert.IsType<OkObjectResult>(await controller.ListPendingFieldCorrections(100, CancellationToken.None));
            Assert.Equal(0, (int)pendingAfter.Value!.GetType().GetProperty("count")!.GetValue(pendingAfter.Value)!);
        }

        [Fact]
        public async Task ReviewFieldCorrection_accepted_transiciona_e_gera_jsonl_com_source_correto()
        {
            var (controller, store, user, trainingPath) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store, observed: "001", expected: "1");

            var result = await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "Accepted" }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            var report = await store.GetReportAsync(reportId, CancellationToken.None);
            Assert.Equal(FieldCorrectionReportStatus.ReviewedAccepted, report!.Status);
            Assert.Equal(user.UserId, report.ReviewedByUserId);
            Assert.NotNull(report.ReviewedAtUtc);

            var jsonl = ReadCapturedJsonl(trainingPath);
            Assert.NotNull(jsonl);
            Assert.Contains("\"source\":\"human-correction-reviewed\"", jsonl);
            Assert.Contains("\"output\":\"1\"", jsonl);

            if (Directory.Exists(trainingPath)) Directory.Delete(trainingPath, true);
        }

        [Fact]
        public async Task ReviewFieldCorrection_rejected_transiciona_e_NAO_gera_jsonl()
        {
            var (controller, store, _, trainingPath) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store);

            var result = await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "rejected" }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            var report = await store.GetReportAsync(reportId, CancellationToken.None);
            Assert.Equal(FieldCorrectionReportStatus.ReviewedRejected, report!.Status);
            Assert.Null(ReadCapturedJsonl(trainingPath));
        }

        [Fact]
        public async Task ReviewFieldCorrection_segunda_chamada_retorna_409_idempotente()
        {
            var (controller, store, _, trainingPath) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store);

            var first = await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "accepted" }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(first);

            var second = await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "rejected" }, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(second);

            var report = await store.GetReportAsync(reportId, CancellationToken.None);
            Assert.Equal(FieldCorrectionReportStatus.ReviewedAccepted, report!.Status);

            if (Directory.Exists(trainingPath)) Directory.Delete(trainingPath, true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("maybe")]
        public async Task ReviewFieldCorrection_decision_invalida_retorna_400(string? decision)
        {
            var (controller, store, _, _) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store);

            var result = await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = decision }, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task ReviewFieldCorrection_sem_identidade_resolvida_retorna_404_failclosed()
        {
            var (controller, store, user, _) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store);
            user.UserId = null;

            var result = await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "accepted" }, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task ReviewFieldCorrection_reportId_inexistente_retorna_409()
        {
            var (controller, _, _, _) = BuildController();

            var result = await controller.ReviewFieldCorrection(Guid.NewGuid(), new FieldCorrectionReviewRequest { Decision = "accepted" }, CancellationToken.None);

            Assert.IsType<ConflictObjectResult>(result);
        }

        [Fact]
        public async Task ReviewFieldCorrection_accepted_nao_sobrescreve_groundTruth_do_contexto()
        {
            var (controller, store, _, trainingPath) = BuildController();
            var (_, reportId) = await SeedPendingReportAsync(controller, store, groundTruth: "<xml>GABARITO-ORIGINAL</xml>");
            var saveCallsBefore = store.SaveContextCallCount;

            await controller.ReviewFieldCorrection(reportId, new FieldCorrectionReviewRequest { Decision = "accepted" }, CancellationToken.None);

            Assert.Equal("<xml>GABARITO-ORIGINAL</xml>", store.SavedContext!.GroundTruthXml);
            Assert.Equal(saveCallsBefore, store.SaveContextCallCount);

            if (Directory.Exists(trainingPath)) Directory.Delete(trainingPath, true);
        }
    }
}
