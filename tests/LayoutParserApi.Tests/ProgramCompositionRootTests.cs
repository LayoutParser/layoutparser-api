using LayoutParserApi.Controllers;
using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Validation;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LayoutParserApi.Tests
{
    /// <summary>
    /// Issue #90: exercita a COMPOSIÇÃO REAL de <c>Program.cs</c> via
    /// <see cref="WebApplicationFactory{TEntryPoint}"/>, em vez de uma <c>ServiceCollection</c>
    /// montada à mão pelo teste (a mesma classe de defeito da issue #33, materializada de novo na
    /// mutação M4 da PR #89 — ver <c>DataGenerationControllerDiTests</c> para o histórico).
    ///
    /// <para>Trade-off documentado em <c>DataGenerationControllerDiTests</c> (linhas 31-37) sobre
    /// por que <c>WebApplicationFactory&lt;Program&gt;</c> tinha sido descartado: o host real lê
    /// <c>Logging:File:Directory</c> do <c>appsettings.json</c> (caminho de produção), tenta
    /// conectar no Redis e liga <c>IHostedService</c>s que dependem de SQL. A destrava é exatamente
    /// a recomendada pelo <c>@lp-qa</c> nesse comentário: <c>appsettings.Testing.json</c>
    /// (ambiente <c>Testing</c>) redireciona esses três pontos para valores que falham rápido/local,
    /// sem exigir Redis/SQL no ar e sem escrever no diretório de log do servidor.</para>
    ///
    /// <para>O host sobe DE VERDADE (<c>CreateClient()</c> força <c>WebHost.Build()+Start()</c>),
    /// então apagar um <c>AddScoped</c>/<c>AddSingleton</c>/<c>AddHostedService</c> do
    /// <c>Program.cs</c> faz este teste falhar — diferente da suíte anterior, que só provava
    /// resolvibilidade de uma cópia da composição.</para>
    /// </summary>
    public class ProgramCompositionRootTests : IClassFixture<ProgramCompositionRootTests.ApiFactory>
    {
        private readonly ApiFactory _factory;

        public ProgramCompositionRootTests(ApiFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// <c>WebApplicationFactory</c> customizada só para forçar o ambiente <c>Testing</c> (o
        /// gatilho de convenção do ASP.NET Core para carregar <c>appsettings.Testing.json</c>) —
        /// nenhum serviço é substituído/mockado aqui: é a composição real de <c>Program.cs</c>.
        /// </summary>
        public class ApiFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Testing");
            }
        }

        /// <summary>
        /// O host precisa terminar de subir (<c>Build()+Start()</c>) sem lançar — isso já falha se
        /// QUALQUER registro obrigatório sumir do <c>Program.cs</c> (dependência não resolvível de
        /// um controller/serviço referenciado na composição).
        /// </summary>
        [Fact]
        public void Host_sobe_com_a_composicao_real_do_Program_cs()
        {
            using var scope = _factory.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider);
        }

        /// <summary>
        /// Guarda específica do critério de aceite da issue #90: a mutação M4 (comentar o
        /// <c>AddHostedService&lt;AiCandidateStoreCleanupBackgroundService&gt;()</c>) tem que deixar
        /// este teste VERMELHO. <c>IHostedService</c> não é resolvível por tipo concreto via
        /// <c>GetService</c> — o container expõe todos os hosted services registrados através da
        /// coleção <see cref="IHostedService"/>, então localizamos o nosso ali dentro.
        /// </summary>
        [Fact]
        public void Program_cs_registra_os_tres_hosted_services_esperados()
        {
            var hostedServices = _factory.Services.GetServices<IHostedService>().ToList();

            Assert.Contains(hostedServices, s => s is CachePermanentWarmupBackgroundService);
            Assert.Contains(hostedServices, s => s is AiCandidateStoreCleanupBackgroundService);
            Assert.Contains(hostedServices, s => s is LayoutValidationBackgroundService);
        }

        // --- Grupos de DI cobertos além de Generation (issue #90 pede 2-3 grupos) ---

        /// <summary>Studio-model: o serviço precisa enxergar o adaptador Sysmiddle registrado em Program.cs.</summary>
        [Fact]
        public void Studio_model_resolve_o_adaptador_sysmiddle()
        {
            using var scope = _factory.Services.CreateScope();
            var adapters = scope.ServiceProvider.GetServices<LayoutParserApi.Services.StudioModel.IStudioModelAdapter>().ToList();
            Assert.Contains(adapters, a => a.Engine == "sysmiddle");
        }

        [Theory]
        // Cache
        [InlineData(typeof(ILayoutCacheService))]
        // Database
        [InlineData(typeof(ILayoutDatabaseService))]
        [InlineData(typeof(IDecryptionService))]
        [InlineData(typeof(MapperDatabaseService))]
        [InlineData(typeof(ICachedLayoutService))]
        // XML Analysis
        [InlineData(typeof(XmlAnalysisService))]
        [InlineData(typeof(XsdValidationService))]
        [InlineData(typeof(XmlDocumentTypeDetector))]
        // Transformation
        [InlineData(typeof(TransformationPipelineService))]
        [InlineData(typeof(TclGeneratorService))]
        [InlineData(typeof(XslGeneratorService))]
        // Parsing
        [InlineData(typeof(ILineSplitter))]
        [InlineData(typeof(ILayoutValidator))]
        [InlineData(typeof(ILayoutParserService))]
        // Mapper Cache
        [InlineData(typeof(ICachedMapperService))]
        // Studio-model (Fase 1)
        [InlineData(typeof(IStudioModelService))]
        // Learning
        [InlineData(typeof(ExampleLearningService))]
        [InlineData(typeof(LayoutLearningService))]
        // Validation
        [InlineData(typeof(LayoutValidationService))]
        [InlineData(typeof(DocumentValidationService))]
        // Audit/Logging
        [InlineData(typeof(IAuditLogger))]
        [InlineData(typeof(ITechLogger))]
        // LowCode (Transformation, Singleton)
        [InlineData(typeof(LowCodeTransformationService))]
        [InlineData(typeof(LowCodeAutoTransformationService))]
        public void Servico_do_grupo_de_DI_resolve_sem_excecao(Type tipoDoServico)
        {
            using var scope = _factory.Services.CreateScope();

            var servico = scope.ServiceProvider.GetRequiredService(tipoDoServico);

            Assert.NotNull(servico);
        }

        /// <summary>
        /// Controller real de um endpoint que depende de vários grupos ao mesmo tempo
        /// (transformação low-code, IA, validação, banco) — resolver ele prova que a fiação entre
        /// grupos também está correta, não só cada grupo isolado.
        /// </summary>
        [Fact]
        public void TransformationExecutionController_resolve_pela_composicao_real()
        {
            using var scope = _factory.Services.CreateScope();

            // Controllers do MVC não ficam registrados no container por tipo (o pipeline padrão os
            // ativa via IControllerActivator/ActivatorUtilities, não via GetRequiredService) — usar
            // ActivatorUtilities aqui reproduz a MESMA forma de ativação usada em runtime pelo
            // framework, resolvendo cada dependência do construtor pelo container real.
            var controller = ActivatorUtilities.CreateInstance<TransformationExecutionController>(scope.ServiceProvider);

            Assert.NotNull(controller);
        }

        /// <summary>
        /// O grupo Generation continua coberto — não é redundante em relação ao gate existente
        /// (<c>AddGenerationServices</c>/<c>DataGenerationControllerDiTests</c>): aquele prova que o
        /// GRUPO, isolado, resolve; este prova que ele resolve DENTRO da composição real completa
        /// (nenhum outro registro do Program.cs conflita/sombra ele).
        /// </summary>
        [Fact]
        public void DataGenerationController_resolve_pela_composicao_real()
        {
            using var scope = _factory.Services.CreateScope();

            var controller = ActivatorUtilities.CreateInstance<DataGenerationController>(scope.ServiceProvider);

            Assert.NotNull(controller);
        }
    }
}
