using System.Reflection;

using LayoutParserApi.Services.Filters;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

using Xunit;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Regressão de isolamento cross-workspace (LayoutParserReact#196, "Usuário A não acessa
    /// workspace B alterando a URL"). Varre por REFLEXÃO todo controller: toda ação cuja rota
    /// (controller + ação) contém <c>{workspaceId}</c> precisa estar protegida por
    /// <see cref="RequireWorkspaceRoleAttribute"/> (no controller ou na ação). Uma ação nova com
    /// <c>{workspaceId}</c> e sem o filtro faz este teste falhar — o que impede o "esqueci o filtro"
    /// de chegar em produção.
    /// </summary>
    public class WorkspaceIsolationReflectionTests
    {
        // Justificativas reutilizadas (o motivo de cada exceção é o mesmo dentro do grupo).
        private const string ViaStoreDraft =
            "Sem filtro, mas o isolamento é feito no corpo da ação: GetDraftIfMemberAsync/GetReleaseIfMemberAsync/GetSuiteIfMemberAsync fazem JOIN com tbLpWorkspaceMembership (m.UserId = usuário atual) e a ação ainda exige X.WorkspaceId == {workspaceId} da rota; não-membro/workspace trocado -> 404. Sem checagem de PAPEL (qualquer membro, inclusive Viewer) — decisão de produto, não brecha cross-workspace.";
        private const string ViaMembershipManual =
            "Sem filtro, mas a ação chama GetWorkspaceForMemberAsync/GetWorkspaceIfMemberAsync(workspaceId, userId) e devolve 404 para não-membro (ou delega a serviço que faz o mesmo, exigindo package.WorkspaceId == {workspaceId}).";

        /// <summary>
        /// Exceções APROVADAS (ação com {workspaceId} na rota sem o atributo). Granularidade
        /// POR AÇÃO, de propósito: uma ação nova no mesmo controller NÃO herda a exceção e falha o
        /// teste. Cada uma só é aceita porque foi lida e confirmada como isolada por membership
        /// explícito (não é "dispensa" de isolamento). Chave: "Controller.Action".
        /// Candidatas a migrar para <c>[RequireWorkspaceRole]</c> (uniformizar e ganhar RBAC).
        /// </summary>
        private static readonly Dictionary<string, string> ExcecoesAprovadas = new(StringComparer.Ordinal)
        {
            ["WorkspacesController.GetWorkspace"] =
                "GET /api/workspaces/{workspaceId}: a própria ação consulta GetWorkspaceForMemberAsync(workspaceId, userId) e devolve 404 uniforme para não-membro (WorkspacesControllerTests).",

            ["AdminController.WorkspaceMembers"] =
                "GET /api/admin/workspaces/{workspaceId}/members: visão GLOBAL do super-administrador, cross-workspace de propósito. Protegida por [RequireSudo] no controller (não-sudo -> 404; sudo só por e-mail em Security:SudoEmails, com auditoria por acesso) — ver SudoTests.",

            ["FiscalMappingPackagesController.CreatePackage"] = ViaMembershipManual,
            ["FiscalMappingPackagesController.ListProjects"] = ViaMembershipManual,
            ["FiscalMappingPackagesController.CreateRevision"] = ViaMembershipManual,
            ["FiscalMappingPackagesController.GetExcelInventory"] = ViaMembershipManual,
            ["FiscalMappingPackagesController.GetPackage"] = ViaMembershipManual,
            ["MappingExplanationController.GetExplanation"] = ViaMembershipManual,
            ["MappingDraftsController.CreateDraft"] = ViaMembershipManual,

            ["MappingDraftsController.GetDraft"] = ViaStoreDraft,
            ["MappingDraftsController.CreateSuggestionJob"] = ViaStoreDraft,
            ["MappingDraftsController.GetSuggestionJob"] = ViaStoreDraft,
            ["MappingDraftsController.CancelSuggestionJob"] = ViaStoreDraft,
            ["MappingDraftsController.UpdateRule"] = ViaStoreDraft,
            ["MappingCompilationController.Compile"] = ViaStoreDraft,
            ["MappingCompilationController.GetCompileJob"] = ViaStoreDraft,
            ["MappingCompilationController.GetRelease"] = ViaStoreDraft,
            ["MappingCompilationController.DiffReleases"] = ViaStoreDraft,
            ["MappingCompilationController.CreateTestRun"] = ViaStoreDraft,
            ["MappingCompilationController.GetTestRunJob"] = ViaStoreDraft,
            ["TestSuiteController.ListSuites"] = ViaStoreDraft,
            ["TestSuiteController.GetSuite"] = ViaStoreDraft,
            ["TestSuiteController.ListFixtures"] = ViaStoreDraft,
            ["TestSuiteController.ListRuns"] = ViaStoreDraft,
        };

        private static IEnumerable<Type> Controllers() =>
            typeof(Program).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

        private static IEnumerable<string> Templates(MemberInfo m) =>
            m.GetCustomAttributes(inherit: true)
                .OfType<IRouteTemplateProvider>()
                .Select(a => a.Template)
                .Where(t => !string.IsNullOrEmpty(t))!;

        private static bool TemTemplateComWorkspaceId(Type controller, MethodInfo action)
        {
            var controllerTemplates = Templates(controller).ToList();
            foreach (var t in Templates(action))
            {
                // "~/..." ignora o prefixo do controller (ex.: MappingGovernanceController.List).
                if (t!.StartsWith("~/", StringComparison.Ordinal) || t.StartsWith("/", StringComparison.Ordinal))
                {
                    if (t.Contains("{workspaceId", StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }
                if (t.Contains("{workspaceId", StringComparison.OrdinalIgnoreCase)) return true;
                if (controllerTemplates.Any(c => c!.Contains("{workspaceId", StringComparison.OrdinalIgnoreCase))) return true;
            }

            // Ação sem template próprio herda a rota do controller.
            if (!Templates(action).Any())
                return controllerTemplates.Any(c => c!.Contains("{workspaceId", StringComparison.OrdinalIgnoreCase));

            return false;
        }

        private static bool TemFiltro(Type controller, MethodInfo action) =>
            controller.GetCustomAttributes<RequireWorkspaceRoleAttribute>(inherit: true).Any()
            || action.GetCustomAttributes<RequireWorkspaceRoleAttribute>(inherit: true).Any();

        private static IEnumerable<(Type Controller, MethodInfo Action)> AcoesComWorkspaceId()
        {
            foreach (var c in Controllers())
                foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.GetCustomAttributes<NonActionAttribute>().Any()) continue;
                    if (!m.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any()) continue;
                    if (TemTemplateComWorkspaceId(c, m))
                        yield return (c, m);
                }
        }

        [Fact]
        public void Varredura_encontra_as_acoes_com_workspaceId_na_rota()
        {
            // Sanidade da própria varredura: se a reflexão quebrar e devolver vazio, o teste
            // principal passaria "no vazio". Piso conservador (hoje há dezenas).
            var acoes = AcoesComWorkspaceId().ToList();
            Assert.True(acoes.Count >= 20, $"Varredura achou só {acoes.Count} ações com {{workspaceId}} — reflexão provavelmente quebrada.");
        }

        [Fact]
        public void Toda_acao_com_workspaceId_na_rota_tem_RequireWorkspaceRole_ou_excecao_aprovada()
        {
            var violacoes = AcoesComWorkspaceId()
                .Where(x => !TemFiltro(x.Controller, x.Action))
                .Where(x => !ExcecoesAprovadas.ContainsKey($"{x.Controller.Name}.{x.Action.Name}"))
                .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.True(violacoes.Count == 0,
                "Ações com {workspaceId} na rota SEM [RequireWorkspaceRole] (e fora da lista de exceções aprovadas): "
                + string.Join(", ", violacoes));
        }

        [Fact]
        public void Excecoes_aprovadas_nao_ficaram_obsoletas()
        {
            // Se uma exceção passou a ter o filtro (ou foi removida), a lista precisa encolher —
            // evita "exceção fantasma" que mascare uma ação nova com o mesmo nome.
            var acoes = AcoesComWorkspaceId().ToList();
            var obsoletas = ExcecoesAprovadas.Keys
                .Where(k => !acoes.Any(x =>
                    $"{x.Controller.Name}.{x.Action.Name}" == k
                    && !TemFiltro(x.Controller, x.Action)))
                .ToList();

            Assert.True(obsoletas.Count == 0, "Exceções sem ação correspondente desprotegida: " + string.Join(", ", obsoletas));
        }
    }
}
