using LayoutParserApi.Services.Health;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Health
{
    /// <summary>
    /// Trava a semântica de readiness do P1.3: dependência ESSENCIAL fora → Unhealthy (503);
    /// dependência OPCIONAL fora → Degraded (200, resiliência é princípio do projeto).
    /// </summary>
    public class ReadinessHealthCheckTests
    {
        // ---- Catálogo: gate duro da readiness, em TRÊS estados ----------------------------------
        //
        // (a) Aquecendo → Unhealthy, mas marcado transitório (o retry ainda pode recuperar).
        // (b) Pronto (> 0 layouts) → Healthy — ÚNICO estado saudável.
        // (c) Vazio (warm-up concluiu com 0) → Unhealthy definitivo. NUNCA Healthy: catálogo sem
        //     layouts não serve requisição nenhuma, e um deploy não pode declarar sucesso com ele.

        [Fact]
        public async Task Catalogo_vazio_apos_warmup_e_unhealthy_definitivo()
        {
            var state = new CatalogWarmupState();
            state.SetResult(0); // warm-up CONCLUIU e o catálogo veio vazio

            var result = await new CatalogHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(nameof(CatalogWarmupStatus.Vazio), result.Data["estado"]);
            Assert.Equal(false, result.Data["transitorio"]); // exige intervenção, não se resolve sozinho
            Assert.Equal(0, result.Data["layoutCount"]);
        }

        [Fact]
        public async Task Catalogo_ainda_nao_concluido_e_unhealthy_transitorio()
        {
            // Warm-up não rodou/está em retry (Completed=false) → instância não está pronta, mas
            // isso NÃO é falha definitiva: o backoff da issue #67 ainda pode recuperar sozinho.
            var state = new CatalogWarmupState();
            state.RegisterFailedAttempt("SqlException");

            var result = await new CatalogHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(nameof(CatalogWarmupStatus.Aquecendo), result.Data["estado"]);
            Assert.Equal(true, result.Data["transitorio"]);
            Assert.Equal(1, result.Data["tentativasFalhas"]);
            Assert.Contains("AQUECENDO", result.Description);
        }

        [Fact]
        public async Task Catalogo_populado_e_healthy()
        {
            var state = new CatalogWarmupState();
            state.SetResult(42);

            var result = await new CatalogHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Equal(nameof(CatalogWarmupStatus.Pronto), result.Data["estado"]);
            Assert.Equal(42, result.Data["layoutCount"]);
        }

        /// <summary>
        /// Os dois estados NÃO saudáveis respondem o mesmo HTTP (503), então só o payload os separa.
        /// Sem essa distinção o operador não sabe se espera (aquecendo) ou se age (vazio definitivo).
        /// </summary>
        [Fact]
        public async Task Aquecendo_e_vazio_sao_ambos_unhealthy_mas_distinguiveis()
        {
            var aquecendo = new CatalogWarmupState();

            var vazio = new CatalogWarmupState();
            vazio.SetResult(0);

            var rAquecendo = await new CatalogHealthCheck(aquecendo).CheckHealthAsync(new HealthCheckContext());
            var rVazio = await new CatalogHealthCheck(vazio).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, rAquecendo.Status);
            Assert.Equal(HealthStatus.Unhealthy, rVazio.Status);
            Assert.NotEqual(rAquecendo.Data["estado"], rVazio.Data["estado"]);
            Assert.NotEqual(rAquecendo.Data["transitorio"], rVazio.Data["transitorio"]);
            Assert.NotEqual(rAquecendo.Description, rVazio.Description);
        }

        // ---- Dependências opcionais: Degraded, não Unhealthy ------------------------------------

        [Fact]
        public async Task Redis_ausente_e_degraded_nao_unhealthy()
        {
            // Injeção opcional nula = Redis fora do container.
            var result = await new RedisHealthCheck(null).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        [Fact]
        public async Task Runner_sem_path_e_degraded()
        {
            var options = Options.Create(new LowCodeRunnerOptions { RunnerPath = "" });

            var result = await new LowCodeRunnerHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        /// <summary>
        /// A2 (gate #107/#108): mesmo com RunnerPath válido, AllowedPackageGuids vazio é
        /// Degraded — a query de mapper vira IN (NULL) e nenhuma transformação low-code funciona,
        /// mas parse/catálogo seguem servindo (mesma severidade do resto deste health check).
        /// </summary>
        [Fact]
        public async Task Runner_com_AllowedPackageGuids_vazio_e_degraded()
        {
            var options = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = System.Reflection.Assembly.GetExecutingAssembly().Location, // arquivo que existe de verdade
                AllowedPackageGuids = new List<string>()
            });

            var result = await new LowCodeRunnerHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
            Assert.Contains("AllowedPackageGuids", result.Description);
        }

        [Fact]
        public async Task Runner_com_AllowedPackageGuids_preenchido_e_healthy()
        {
            var options = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = System.Reflection.Assembly.GetExecutingAssembly().Location,
                AllowedPackageGuids = new List<string> { "11111111-1111-1111-1111-111111111111" }
            });

            var result = await new LowCodeRunnerHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }

        // ---- Ollama: config órfã (Url ausente/localhost) é sinal forte neste projeto ------------

        [Fact]
        public async Task Ollama_com_url_localhost_e_degraded()
        {
            var options = Options.Create(new OllamaOptions { Url = "http://localhost:11434" });

            var result = await new OllamaConfigHealthCheck(options, (_, _) => Task.FromResult(false)).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        [Fact]
        public async Task Ollama_com_url_127_0_0_1_sem_resposta_e_degraded()
        {
            var options = Options.Create(new OllamaOptions { Url = "http://127.0.0.1:11434" });

            var result = await new OllamaConfigHealthCheck(options, (_, _) => Task.FromResult(false)).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        [Fact]
        public async Task Ollama_com_url_127_0_0_1_respondendo_e_healthy()
        {
            var options = Options.Create(new OllamaOptions { Url = "http://127.0.0.1:11434" });
            string? sondado = null;

            var result = await new OllamaConfigHealthCheck(options, (url, _) => { sondado = url; return Task.FromResult(true); })
                .CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Equal("http://127.0.0.1:11434", sondado);
        }

        [Fact]
        public async Task Ollama_remoto_nao_aciona_a_sonda()
        {
            var options = Options.Create(new OllamaOptions { Url = "http://10.0.0.42:11434" });

            var result = await new OllamaConfigHealthCheck(options, (_, _) => throw new InvalidOperationException("não deveria sondar"))
                .CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }

        [Fact]
        public async Task Ollama_com_url_vazia_e_degraded()
        {
            var options = Options.Create(new OllamaOptions { Url = "" });

            var result = await new OllamaConfigHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        [Fact]
        public async Task Ollama_com_url_de_vm_remota_e_healthy()
        {
            var options = Options.Create(new OllamaOptions { Url = "http://10.0.0.42:11434" });

            var result = await new OllamaConfigHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }

        // ---- Agregação: dependência forçada a falhar → readiness responde 503 -------------------

        /// <summary>
        /// "Com uma dependência forçada a falhar, /health/ready responde 503." O 503 vem do
        /// mapeamento <c>Unhealthy → 503</c> configurado em Program.cs (MapHealthChecks
        /// ResultStatusCodes); aqui provamos a metade que decide isso: o agregado das sondas "ready"
        /// vira <b>Unhealthy</b> quando o catálogo está vazio.
        /// </summary>
        [Fact]
        public async Task Readiness_com_catalogo_vazio_agrega_unhealthy()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var state = new CatalogWarmupState();
            state.SetResult(0); // dependência essencial forçada a falhar
            services.AddSingleton(state);

            services.AddHealthChecks()
                .Add(new HealthCheckRegistration("catalog",
                    sp => new CatalogHealthCheck(sp.GetRequiredService<CatalogWarmupState>()),
                    HealthStatus.Unhealthy, new[] { "ready" }))
                // Redis Degraded no mesmo lote não pode "puxar" o agregado para pior nem mascarar.
                .Add(new HealthCheckRegistration("redis",
                    _ => new RedisHealthCheck(null),
                    HealthStatus.Unhealthy, new[] { "ready" }));

            await using var provider = services.BuildServiceProvider();
            var svc = provider.GetRequiredService<HealthCheckService>();

            var report = await svc.CheckHealthAsync(r => r.Tags.Contains("ready"));

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
        }

        /// <summary>
        /// Trava a decisão de NÃO reportar o warm-up em andamento como <c>Degraded</c>: Degraded
        /// mapeia para <b>200</b> em /health/ready e o smoke test do deploy exige 200 estrito — a
        /// instância seria declarada pronta com o catálogo ainda vazio, exatamente a classe de bug
        /// que esta sonda fecha. Unhealthy (503) mantém o smoke test retentando, que é o correto:
        /// o laço do deploy já trata 503 como "ainda não pronto", não como falha definitiva.
        /// </summary>
        [Fact]
        public async Task Readiness_com_catalogo_aquecendo_agrega_unhealthy_e_nao_degraded()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            // Warm-up em andamento/retry: nunca chamou SetResult.
            var state = new CatalogWarmupState();
            state.RegisterFailedAttempt("SqlException");
            services.AddSingleton(state);

            services.AddHealthChecks()
                .Add(new HealthCheckRegistration("catalog",
                    sp => new CatalogHealthCheck(sp.GetRequiredService<CatalogWarmupState>()),
                    HealthStatus.Unhealthy, new[] { "ready" }));

            await using var provider = services.BuildServiceProvider();
            var report = await provider.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(r => r.Tags.Contains("ready"));

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
            Assert.Equal(true, report.Entries["catalog"].Data["transitorio"]);
        }

        [Fact]
        public async Task Readiness_so_com_opcionais_degradadas_nao_e_unhealthy()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var state = new CatalogWarmupState();
            state.SetResult(7); // catálogo OK
            services.AddSingleton(state);

            services.AddHealthChecks()
                .Add(new HealthCheckRegistration("catalog",
                    sp => new CatalogHealthCheck(sp.GetRequiredService<CatalogWarmupState>()),
                    HealthStatus.Unhealthy, new[] { "ready" }))
                .Add(new HealthCheckRegistration("redis",
                    _ => new RedisHealthCheck(null),
                    HealthStatus.Unhealthy, new[] { "ready" }));

            await using var provider = services.BuildServiceProvider();
            var report = await provider.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(r => r.Tags.Contains("ready"));

            // Redis Degraded + catálogo Healthy → Degraded (200), NUNCA Unhealthy (503).
            Assert.NotEqual(HealthStatus.Unhealthy, report.Status);
        }
    }
}
