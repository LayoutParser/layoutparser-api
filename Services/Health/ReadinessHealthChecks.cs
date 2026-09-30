using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace LayoutParserApi.Services.Health
{
    /// <summary>
    /// Sondas de readiness (P1.3 do plano de segurança). Cada uma testa UMA dependência de verdade,
    /// ao contrário do health anterior que devolvia 200 sem tocar em nada.
    ///
    /// <para><b>Severidade e mapeamento HTTP</b> (em /health/ready): Unhealthy → 503, Degraded e
    /// Healthy → 200. Redis e runner ausentes são <b>Degraded</b> (a app serve sem eles — resiliência
    /// é princípio do projeto); SQL fora e catálogo vazio são <b>Unhealthy</b> (a API não consegue
    /// servir layout).</para>
    /// </summary>
    public sealed class SqlServerHealthCheck : IHealthCheck
    {
        private readonly IConfiguration _configuration;

        public SqlServerHealthCheck(IConfiguration configuration) => _configuration = configuration;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            // Connection string com timeout CURTO de propósito: é sonda, não caminho de dados (que
            // usa 30s em LayoutDatabaseService). Uma sonda que espera 30s não serve de sonda.
            var server = _configuration["Database:Server"] ?? "";
            var database = _configuration["Database:Database"] ?? "";
            var userId = _configuration["Database:UserId"] ?? "";
            var password = _configuration["Database:Password"] ?? string.Empty;
            var encrypt = _configuration["Database:Encrypt"]?.ToLower() ?? "false";

            var connectionString =
                $"Server={server};Database={database};User Id={userId};Password={password};" +
                $"TrustServerCertificate=true;Encrypt={encrypt};" +
                "Connection Timeout=3;Command Timeout=3;Pooling=true;";

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 3 };
                await command.ExecuteScalarAsync(cancellationToken);
                return HealthCheckResult.Healthy("SQL Server respondeu SELECT 1.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("SQL Server indisponivel (SELECT 1 falhou).", ex);
            }
        }
    }

    /// <summary>Redis ausente/desconectado = <b>Degraded</b> (cache opera por disco/banco), nunca Unhealthy.</summary>
    public sealed class RedisHealthCheck : IHealthCheck
    {
        private readonly IConnectionMultiplexer? _redis;

        // Injeção OPCIONAL: IConnectionMultiplexer só está no container quando a conexão subiu.
        public RedisHealthCheck(IConnectionMultiplexer? redis = null) => _redis = redis;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (_redis is null || !_redis.IsConnected)
                return HealthCheckResult.Degraded("Redis ausente/desconectado — cache opera sem Redis (por disco/banco).");

            try
            {
                var latency = await _redis.GetDatabase().PingAsync();
                return HealthCheckResult.Healthy($"Redis respondeu PING ({latency.TotalMilliseconds:F0}ms).");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Degraded("Redis falhou no PING — cache degradado.", ex);
            }
        }
    }

    /// <summary>
    /// Existência do executável de descriptografia. Ausente = <b>Degraded</b> (informativo): o gate
    /// duro é o <see cref="CatalogHealthCheck"/> — sem decryptor o catálogo volta vazio e AQUELE
    /// vira Unhealthy. Este só explica a causa.
    /// </summary>
    public sealed class DecryptorHealthCheck : IHealthCheck
    {
        private readonly IDecryptionService _decryption;

        public DecryptorHealthCheck(IDecryptionService decryption) => _decryption = decryption;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_decryption.IsDecryptorAvailable
                ? HealthCheckResult.Healthy("Executavel de descriptografia encontrado.")
                : HealthCheckResult.Degraded("LayoutParserDecrypt.exe ausente — catalogo tende a ficar vazio (ver catalog)."));
        }
    }

    /// <summary>Existência do runner low-code. Ausente = <b>Degraded</b> (transformação indisponível, mas parse/catálogo servem).</summary>
    public sealed class LowCodeRunnerHealthCheck : IHealthCheck
    {
        private readonly IOptions<LowCodeRunnerOptions> _options;

        public LowCodeRunnerHealthCheck(IOptions<LowCodeRunnerOptions> options) => _options = options;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var options = _options.Value;
            var path = options.RunnerPath;

            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(HealthCheckResult.Degraded("LowCode:RunnerPath nao configurado — transformacao low-code indisponivel."));

            if (!File.Exists(path))
                return Task.FromResult(HealthCheckResult.Degraded($"LowCode:RunnerPath nao existe ({path}) — transformacao low-code indisponivel."));

            // ✅ Gate #107/#108: AllowedPackageGuids vazio faz a query de mapper virar `IN (NULL)`,
            // nunca batendo com nada — nenhum erro visível, só "mapper não encontrado" longe da causa.
            // O startup (A1, ver Program.cs) já falha o boot quando a seção LowCode existe e o campo
            // está vazio; esta sonda cobre o caso em que a validação de boot foi contornada (env var
            // setada depois do bind, teste manual, etc.) — mesma severidade Degraded do resto deste
            // health check: parse/catálogo seguem servindo, só a transformação low-code fica capenga.
            if (options.AllowedPackageGuids is null || options.AllowedPackageGuids.Count == 0)
                return Task.FromResult(HealthCheckResult.Degraded(
                    "LowCode:AllowedPackageGuids vazio — nenhum mapper sera encontrado (query vira IN (NULL)); transformacao low-code indisponivel."));

            return Task.FromResult(HealthCheckResult.Healthy("Runner low-code encontrado."));
        }
    }

    /// <summary>
    /// Config órfã do Ollama: <c>Ollama:Url</c> ausente ou apontando para localhost/127.0.0.1 é sinal
    /// forte de que a seção não foi configurada para este host. Desde a migração Linux a API pode rodar no MESMO
    /// host do Ollama (VM 172.25.32.5), onde loopback é válido — por isso, com loopback, o check sonda
    /// <c>GET /api/tags</c> (timeout 2s): respondeu = Healthy; silêncio = Degraded (config órfã). <b>Degraded</b>: diagnóstico via IA é funcionalidade
    /// opcional (Gap 2) — parse/catálogo/transformação low-code seguem servindo sem ela.
    /// </summary>
    public sealed class OllamaConfigHealthCheck : IHealthCheck
    {
        private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(2) };

        private readonly IOptions<OllamaOptions> _options;
        private readonly Func<string, CancellationToken, Task<bool>> _probe;

        /// <param name="probe">Sonda de alcance do Ollama (URL base → respondeu?). Injetável nos testes; o padrão faz <c>GET /api/tags</c> com timeout curto.</param>
        public OllamaConfigHealthCheck(IOptions<OllamaOptions> options, Func<string, CancellationToken, Task<bool>>? probe = null)
        {
            _options = options;
            _probe = probe ?? DefaultProbeAsync;
        }

        private static async Task<bool> DefaultProbeAsync(string baseUrl, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await ProbeClient.GetAsync(baseUrl.TrimEnd('/') + "/api/tags", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var url = _options.Value.Url;

            if (string.IsNullOrWhiteSpace(url))
                return HealthCheckResult.Degraded("Ollama:Url nao configurado — diagnostico via IA indisponivel.");

            // Loopback vale quando a API roda no MESMO host do Ollama (topologia Linux atual). Só é "config
            // órfã" se nada responde ali (ex.: API no Windows apontando pra localhost sem Ollama local).
            if (url.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                return await _probe(url, cancellationToken)
                    ? HealthCheckResult.Healthy($"Ollama local respondendo em {url}.")
                    : HealthCheckResult.Degraded(
                        $"Ollama:Url aponta para localhost/127.0.0.1 ({url}) e nada respondeu ali — sinal de config orfa (o Ollama roda em outro host?).");
            }

            return HealthCheckResult.Healthy($"Ollama:Url configurado: {url}.");
        }
    }

    /// <summary>
    /// Contagem do warm-up. É o gate que fecha a classe "deploy publica versão inoperante e declara
    /// sucesso" — e distingue <b>TRÊS</b> estados, não dois:
    ///
    /// <list type="bullet">
    ///   <item><b>Aquecendo</b> (warm-up não concluiu / retry em andamento) → Unhealthy, mas marcado
    ///   como <c>transitorio=true</c>: é "ainda não pronto", não "quebrado".</item>
    ///   <item><b>Pronto</b> (concluiu com layouts &gt; 0) → Healthy. Único estado saudável.</item>
    ///   <item><b>Vazio</b> (concluiu e o catálogo veio VAZIO) → Unhealthy <c>transitorio=false</c>:
    ///   falha definitiva, exige intervenção (SQL/decryptor fora, catálogo sem layouts).</item>
    /// </list>
    ///
    /// <para><b>Por que "Aquecendo" é Unhealthy e não Degraded:</b> Degraded mapeia para 200 em
    /// <c>/health/ready</c> (ver Program.cs) e o smoke test de deploy exige 200 ESTRITO — reportar
    /// Degraded enquanto aquece faria o deploy declarar sucesso com o catálogo ainda vazio, que é
    /// exatamente o bug que esta sonda existe para fechar. O smoke test já trata 503 como "ainda não
    /// pronto" e retenta, então manter Unhealthy não trava deploy nenhum; o que muda é a
    /// <b>descrição/payload</b>, que agora diz claramente qual dos dois 503 é.</para>
    /// </summary>
    public sealed class CatalogHealthCheck : IHealthCheck
    {
        private readonly CatalogWarmupState _state;

        public CatalogHealthCheck(CatalogWarmupState state) => _state = state;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var status = _state.Status;

            // Payload legível pelo operador (o corpo do /health/ready aparece no log do smoke test)
            // e por monitoramento — permite alertar só no estado definitivo, sem barulho de warm-up.
            var data = new Dictionary<string, object>
            {
                ["estado"] = status.ToString(),
                ["layoutCount"] = _state.LayoutCount,
                ["transitorio"] = status == CatalogWarmupStatus.Aquecendo,
                ["tentativasFalhas"] = _state.FailedAttempts
            };

            if (!string.IsNullOrEmpty(_state.LastFailureReason))
                data["ultimaFalha"] = _state.LastFailureReason!;

            var resultado = status switch
            {
                CatalogWarmupStatus.Pronto =>
                    HealthCheckResult.Healthy($"Catalogo com {_state.LayoutCount} layouts.", data),

                CatalogWarmupStatus.Vazio =>
                    HealthCheckResult.Unhealthy(
                        "Catalogo de layouts VAZIO apos warm-up CONCLUIDO (SQL/decryptor fora, ou catalogo sem layouts) — " +
                        "falha DEFINITIVA, nao se resolve sozinha: exige intervencao.",
                        exception: null,
                        data: data),

                // Aquecendo (inclui a 1ª tentativa e todo retry com backoff da issue #67).
                _ =>
                    HealthCheckResult.Unhealthy(
                        $"Catalogo AQUECENDO — warm-up ainda nao concluiu ({_state.FailedAttempts} tentativa(s) falharam ate agora). " +
                        "Transitorio: o retry com backoff ainda pode recuperar sozinho, NAO e falha definitiva.",
                        exception: null,
                        data: data)
            };

            return Task.FromResult(resultado);
        }
    }

    /// <summary>
    /// Issue #90 — gate de capacidade explícito para os 3 adapters de <c>IMappingExplanationAdapter</c>
    /// (sysmiddle/tcl/xslt): antes, "registrado no DI" era tratado como "disponível", e a dependência
    /// real (catálogo de mappers, SQL) só falhava tarde, no primeiro request. Cada adapter roda seu
    /// próprio <see cref="IMappingExplanationAdapter.CheckAvailabilityAsync"/> (timeout curto, nunca
    /// lança); esta sonda agrega o pior status entre os 3.
    ///
    /// <para><b>Nunca Unhealthy</b> — MVP de observabilidade (design §"O que falta de decisão externa"):
    /// o objetivo é reportar capacidade degradada, não recusar requests nem travar o boot/deploy.
    /// Pior caso agregado vira <c>Degraded</c> (200 em <c>/health/ready</c>), nunca <c>Unhealthy</c>.</para>
    /// </summary>
    public sealed class MappingExplanationCapabilityHealthCheck : IHealthCheck
    {
        private readonly IEnumerable<IMappingExplanationAdapter> _adapters;
        private readonly ILogger<MappingExplanationCapabilityHealthCheck> _logger;

        public MappingExplanationCapabilityHealthCheck(
            IEnumerable<IMappingExplanationAdapter> adapters,
            ILogger<MappingExplanationCapabilityHealthCheck> logger)
        {
            _adapters = adapters;
            _logger = logger;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var data = new Dictionary<string, object>();
            var anyDegraded = false;

            foreach (var adapter in _adapters)
            {
                CapabilityHealth health;
                try
                {
                    health = await adapter.CheckAvailabilityAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Defesa extra: mesmo que a implementação quebre o contrato "nunca lança", o gate
                    // de capacidade em si não pode derrubar a sonda inteira (princípio de resiliência).
                    _logger.LogWarning(ex, "CheckAvailabilityAsync do adapter {Engine} lançou — tratado como degradado.", adapter.Engine);
                    health = new CapabilityHealth(CapabilityStatus.Unavailable, $"CheckAvailabilityAsync lançou: {ex.Message}");
                }

                data[adapter.Engine] = new { status = health.Status.ToString(), reason = health.Reason };
                if (health.Status != CapabilityStatus.Healthy)
                    anyDegraded = true;
            }

            return anyDegraded
                ? HealthCheckResult.Degraded("Uma ou mais capacidades de explicação de mapeamento estão degradadas — ver detalhe por engine.", data: data)
                : HealthCheckResult.Healthy("Todas as capacidades de explicação de mapeamento estão saudáveis.", data);
        }
    }
}
