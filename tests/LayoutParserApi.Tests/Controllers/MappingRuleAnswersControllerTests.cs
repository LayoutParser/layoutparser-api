using System.Reflection;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #422 — resposta livre do revisor às perguntas em aberto da IA. Fakes em memória
    /// reproduzem o contrato do store SQL (versão append-only, idempotência por texto idêntico).
    /// </summary>
    public class MappingRuleAnswersControllerTests
    {
        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        /// <summary>Só GetDraftIfMemberAsync é exercitado: devolve o draft se o usuário for "membro" do workspace dele.</summary>
        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public MappingDraftDetail? Draft { get; set; }
            public Guid? MemberUserId { get; set; }

            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Draft != null && Draft.DraftId == draftId && MemberUserId == userId ? Draft : null);

            public Task<bool> RevisionBelongsToWorkspacePackageAsync(Guid workspaceId, Guid packageId, Guid revisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<(IReadOnlyList<MappingDraftSummary> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? engine, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification, IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<MappingDraftDetail?> SetFiscalProfileAsync(Guid draftId, Guid userId, FiscalProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FakeAnswerStore : IMappingRuleAnswerStore
        {
            public List<MappingRuleAnswer> Rows { get; } = new();
            public bool Throw { get; set; }

            public Task<(MappingRuleAnswer Answer, bool Created)> SaveAnswerAsync(
                Guid workspaceId, Guid draftId, Guid ruleId, int questionIndex, string questionText,
                string answerText, Guid userId, string? userName, CancellationToken cancellationToken)
            {
                if (Throw) throw new InvalidOperationException("sql fora");
                var latest = Rows.Where(r => r.RuleId == ruleId && r.QuestionIndex == questionIndex).OrderByDescending(r => r.Version).FirstOrDefault();
                if (latest != null && latest.AnswerText == answerText)
                    return Task.FromResult((latest, false));
                var row = new MappingRuleAnswer(Guid.NewGuid(), workspaceId, draftId, ruleId, questionIndex, questionText, answerText,
                    userId, userName, DateTimeOffset.UtcNow, (latest?.Version ?? 0) + 1);
                Rows.Add(row);
                return Task.FromResult((row, true));
            }

            public Task<IReadOnlyList<MappingRuleAnswer>> ListAsync(Guid draftId, Guid? ruleId, bool includeHistory, CancellationToken cancellationToken)
            {
                IEnumerable<MappingRuleAnswer> q = Rows.Where(r => r.DraftId == draftId && (ruleId == null || r.RuleId == ruleId));
                if (!includeHistory)
                    q = q.GroupBy(r => (r.RuleId, r.QuestionIndex)).Select(g => g.OrderByDescending(r => r.Version).First());
                return Task.FromResult((IReadOnlyList<MappingRuleAnswer>)q.OrderBy(r => r.QuestionIndex).ThenBy(r => r.Version).ToList());
            }
        }

        private sealed class Ctx
        {
            public Guid WorkspaceId = Guid.NewGuid();
            public Guid UserId = Guid.NewGuid();
            public Guid DraftId = Guid.NewGuid();
            public Guid RuleId = Guid.NewGuid();
            public FakeDraftStore Drafts = new();
            public FakeAnswerStore Answers = new();
            public MappingRuleAnswersController Controller = null!;
        }

        private static Ctx Build(params string[] questions)
        {
            var c = new Ctx();
            var rule = new MappingDraftRuleDetail(
                c.RuleId, c.DraftId, new[] { "a" }, new[] { "b" }, "map", "[]", "[]", "1:1",
                Array.Empty<MappingDraftRuleEvidence>(), "low", MappingDraftRuleStatus.NeedsInput, questions, DateTimeOffset.UtcNow, "etag");
            c.Drafts.Draft = new MappingDraftDetail(c.DraftId, c.WorkspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, new[] { rule });
            c.Drafts.MemberUserId = c.UserId;
            c.Controller = new MappingRuleAnswersController(c.Drafts, c.Answers,
                new FakeCurrentUser { UserId = c.UserId, Name = "revisor" }, NullLogger<MappingRuleAnswersController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return c;
        }

        private static Task<IActionResult> Put(Ctx c, int idx, string? text)
            => c.Controller.SaveAnswer(c.WorkspaceId, c.DraftId, c.RuleId, idx, new SaveQuestionAnswerRequest { Answer = text }, CancellationToken.None);

        private static List<object> Items(IActionResult r)
        {
            var payload = Assert.IsType<OkObjectResult>(r).Value!;
            return ((System.Collections.IEnumerable)payload.GetType().GetProperty("items")!.GetValue(payload)!).Cast<object>().ToList();
        }

        private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

        [Fact]
        public async Task Registrar_resposta_grava_com_snapshot_da_pergunta_e_autoria()
        {
            var c = Build("Qual fonte usar para cUF?");

            var ok = Assert.IsType<OkObjectResult>(await Put(c, 0, "  Use a tabela IBGE.  "));

            Assert.Equal("Use a tabela IBGE.", Prop<string>(ok.Value!, "answer"));
            Assert.Equal("Qual fonte usar para cUF?", Prop<string>(ok.Value!, "question"));
            Assert.Equal(1, Prop<int>(ok.Value!, "version"));
            Assert.Equal(c.UserId, Prop<Guid>(ok.Value!, "answeredByUserId"));
            Assert.Equal("revisor", Prop<string>(ok.Value!, "answeredByName"));
        }

        [Fact]
        public async Task Reenviar_mesmo_texto_e_idempotente_e_texto_novo_cria_versao()
        {
            var c = Build("P1");

            await Put(c, 0, "A");
            await Put(c, 0, "A");
            Assert.Single(c.Answers.Rows);

            var ok = Assert.IsType<OkObjectResult>(await Put(c, 0, "B"));
            Assert.Equal(2, Prop<int>(ok.Value!, "version"));
            Assert.Equal(2, c.Answers.Rows.Count);
        }

        [Fact]
        public async Task Leitura_por_regra_e_por_draft_traz_so_a_versao_mais_recente_ou_historico()
        {
            var c = Build("P1", "P2");
            await Put(c, 0, "A");
            await Put(c, 0, "B");
            await Put(c, 1, "C");

            var porRegra = Items(await c.Controller.ListByRule(c.WorkspaceId, c.DraftId, c.RuleId));
            Assert.Equal(2, porRegra.Count);
            Assert.Equal("B", Prop<string>(porRegra[0], "answer"));

            var porDraft = Items(await c.Controller.ListByDraft(c.WorkspaceId, c.DraftId));
            Assert.Equal(2, porDraft.Count);

            var historico = Items(await c.Controller.ListByDraft(c.WorkspaceId, c.DraftId, includeHistory: true));
            Assert.Equal(3, historico.Count);
        }

        [Fact]
        public async Task Chaves_inexistentes_retornam_404()
        {
            var c = Build("P1");

            Assert.IsType<NotFoundResult>(await Put(c, 1, "x"));    // pergunta fora do intervalo
            Assert.IsType<NotFoundResult>(await Put(c, -1, "x"));
            Assert.IsType<NotFoundResult>(await c.Controller.SaveAnswer(c.WorkspaceId, c.DraftId, Guid.NewGuid(), 0, new SaveQuestionAnswerRequest { Answer = "x" }, CancellationToken.None)); // regra
            Assert.IsType<NotFoundResult>(await c.Controller.SaveAnswer(c.WorkspaceId, Guid.NewGuid(), c.RuleId, 0, new SaveQuestionAnswerRequest { Answer = "x" }, CancellationToken.None)); // draft
            Assert.IsType<NotFoundResult>(await c.Controller.ListByRule(c.WorkspaceId, c.DraftId, Guid.NewGuid()));
        }

        [Fact]
        public async Task Draft_de_outro_workspace_ou_nao_membro_retorna_404()
        {
            var c = Build("P1");

            Assert.IsType<NotFoundResult>(await c.Controller.SaveAnswer(Guid.NewGuid(), c.DraftId, c.RuleId, 0, new SaveQuestionAnswerRequest { Answer = "x" }, CancellationToken.None));
            Assert.IsType<NotFoundResult>(await c.Controller.ListByDraft(Guid.NewGuid(), c.DraftId));

            c.Drafts.MemberUserId = Guid.NewGuid(); // usuário deixa de ser membro
            Assert.IsType<NotFoundResult>(await Put(c, 0, "x"));
            Assert.IsType<NotFoundResult>(await c.Controller.ListByDraft(c.WorkspaceId, c.DraftId));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Texto_vazio_retorna_400(string? texto)
        {
            var c = Build("P1");
            Assert.IsType<BadRequestObjectResult>(await Put(c, 0, texto));
            Assert.Empty(c.Answers.Rows);
        }

        [Fact]
        public async Task Texto_acima_do_limite_retorna_400_e_no_limite_passa()
        {
            var c = Build("P1");
            Assert.IsType<BadRequestObjectResult>(await Put(c, 0, new string('x', MappingRuleAnswersController.MaxAnswerLength + 1)));
            Assert.IsType<OkObjectResult>(await Put(c, 0, new string('x', MappingRuleAnswersController.MaxAnswerLength)));
        }

        [Fact]
        public async Task Falha_do_store_degrada_para_503()
        {
            var c = Build("P1");
            c.Answers.Throw = true;
            var r = Assert.IsType<ObjectResult>(await Put(c, 0, "x"));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, r.StatusCode);
        }

        private static WorkspaceRoleLevel LevelOf(string method)
        {
            var attr = typeof(MappingRuleAnswersController).GetMethod(method)!.GetCustomAttribute<RequireWorkspaceRoleAttribute>();
            Assert.NotNull(attr);
            return (WorkspaceRoleLevel)attr!.Arguments![0];
        }

        [Fact]
        public void Rbac_escrita_exige_operador_e_leitura_e_para_leitor()
        {
            Assert.Equal(WorkspaceRoleLevel.Operator, LevelOf(nameof(MappingRuleAnswersController.SaveAnswer)));
            Assert.Equal(WorkspaceRoleLevel.Viewer, LevelOf(nameof(MappingRuleAnswersController.ListByDraft)));
            Assert.Equal(WorkspaceRoleLevel.Viewer, LevelOf(nameof(MappingRuleAnswersController.ListByRule)));
        }
    }
}
