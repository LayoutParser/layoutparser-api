using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LayoutParserApi.Services.Health;
using LayoutParserApi.Services.Transformation.LowCode;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Transformation;

/// <summary>Cliente HTTP do runner (issue #641): cada status, Content-Length, correlação, retry e health.</summary>
public class LowCodeRunnerHttpClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<long?> ContentLengths { get; } = new();
        public List<string> Paths { get; } = new();
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _respostas = new();
        public FakeHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> r) { _respostas.Enqueue(r); return this; }
        public FakeHandler Enqueue(HttpResponseMessage r) => Enqueue(_ => r);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            Paths.Add(request.RequestUri!.PathAndQuery);
            ContentLengths.Add(request.Content?.Headers.ContentLength);
            return Task.FromResult(_respostas.Dequeue()(request));
        }
    }

    private sealed class Factory(HttpMessageHandler h, bool semBase = false) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => semBase
            ? new(h, disposeHandler: false)
            : new(h, disposeHandler: false) { BaseAddress = new Uri("http://runner.test:5230") };
    }

    private sealed class DelayedCancelHandler(int ms, Exception ex) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(ms, CancellationToken.None);
            throw ex;
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw ex;
    }

    private static HttpResponseMessage Json(HttpStatusCode s, string body, int? retryAfter = null)
    {
        var r = new HttpResponseMessage(s) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter != null) r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter.Value));
        return r;
    }

    private static string Err(int exit, string msg = "falha") => $"{{\"error\":\"{msg}\",\"exitCode\":{exit},\"correlationId\":\"c\"}}";
    private const string Ok = "{\"durationMs\":12,\"mapperId\":\"M1\",\"output\":\"<a/>\",\"warnings\":[\"w1\"]}";

    private static LowCodeRunnerHttpClient Novo(HttpMessageHandler h, List<TimeSpan>? esperas = null, LowCodeRunnerOptions? opt = null) =>
        new(new Factory(h), Options.Create(opt ?? new LowCodeRunnerOptions { BaseUrl = "http://runner.test:5230" }),
            NullLogger<LowCodeRunnerHttpClient>.Instance,
            (t, _) => { esperas?.Add(t); return Task.CompletedTask; });

    private static Task<LowCodeRunnerTransformResponse> Chamar(LowCodeRunnerHttpClient c, CancellationToken ct = default) =>
        c.TransformAsync("doc", "f.txt", "M1", null, "corr-123", ct);

    [Fact]
    public async Task Sucesso_EnviaContentLength_Correlacao_MapperId_ERota()
    {
        var h = new FakeHandler().Enqueue(Json(HttpStatusCode.OK, Ok));
        var r = await Chamar(Novo(h));

        Assert.Equal("<a/>", r.Output);
        Assert.Equal(12, r.DurationMs);
        Assert.Equal("w1", Assert.Single(r.Warnings));
        Assert.Equal("/v1/transform", h.Paths[0]);
        Assert.NotNull(h.ContentLengths[0]);
        Assert.True(h.ContentLengths[0] > 0);
        Assert.Null(h.Requests[0].Headers.TransferEncodingChunked);
        Assert.Equal("corr-123", h.Requests[0].Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task MapperName_SemId_EnviaApenasMapperName_EAmbosOuNenhumDaInvalidRequest()
    {
        string? corpo = null;
        var h = new FakeHandler().Enqueue(req => { corpo = req.Content!.ReadAsStringAsync().Result; return Json(HttpStatusCode.OK, Ok); });
        await Novo(h).TransformAsync("d", "f", null, "Nome", "c");
        Assert.Contains("\"mapperName\":\"Nome\"", corpo);
        Assert.DoesNotContain("mapperId", corpo);

        var c = Novo(new FakeHandler());
        var e1 = await Assert.ThrowsAsync<LowCodeRunnerException>(() => c.TransformAsync("d", "f", "a", "b", "c"));
        var e2 = await Assert.ThrowsAsync<LowCodeRunnerException>(() => c.TransformAsync("d", "f", null, null, "c"));
        Assert.Equal("invalid_request", e1.Code);
        Assert.Equal("invalid_request", e2.Code);
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(404, "mapper_not_found")]
    [InlineData(422, "transform_failed")]
    [InlineData(504, "timeout")]
    [InlineData(500, "runtime_error")]
    [InlineData(503, "runner_unavailable")] // sem Retry-After
    public async Task StatusDeErro_MapeiaParaCodigoEstavel(int status, string codigo)
    {
        var h = new FakeHandler().Enqueue(Json((HttpStatusCode)status, Err(7)));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(Novo(h)));
        Assert.Equal(codigo, ex.Code);
        Assert.Equal(status, ex.HttpStatus);
        Assert.Equal(7, ex.ExitCode);
        Assert.Single(h.Requests); // sem retry fora de queue_full
    }

    [Fact]
    public async Task MensagemDeErro_PassaPeloSanitizer_SemCaminhoDeDisco()
    {
        var h = new FakeHandler().Enqueue(Json(HttpStatusCode.UnprocessableEntity, Err(1, "falhou em C:\\\\inetpub\\\\x\\\\y.xml agora")));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(Novo(h)));
        Assert.DoesNotContain("inetpub", ex.Message);
        Assert.Contains(LowCodeErrorSanitizer.PathPlaceholder, ex.Message);
    }

    [Fact]
    public async Task ConexaoRecusada_ViraRunnerUnavailable()
    {
        var c = Novo(new ThrowingHandler(new HttpRequestException("refused")));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(c));
        Assert.Equal("runner_unavailable", ex.Code);
    }

    [Fact]
    public async Task TimeoutDoHttpClient_SemCancelamentoDoChamador_ViraTimeout()
    {
        // cancelamento so apos o tempo total (1s) => execucao longa demais
        var c = Novo(new DelayedCancelHandler(1100, new TaskCanceledException("t", new TimeoutException())),
            opt: new LowCodeRunnerOptions { BaseUrl = "http://runner.test:5230", HttpTimeoutSeconds = 1 });
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(c));
        Assert.Equal("timeout", ex.Code);
    }

    [Fact]
    public async Task TimeoutDeConexao_CancelamentoRapido_ViraRunnerUnavailable()
    {
        var c = Novo(new ThrowingHandler(new TaskCanceledException("connect", new TimeoutException())));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(c));
        Assert.Equal("runner_unavailable", ex.Code);
    }

    [Fact]
    public async Task TimeoutDeConexao_CausaSocket_ViraRunnerUnavailable_MesmoAposOTempoTotal()
    {
        var c = Novo(new DelayedCancelHandler(1100, new TaskCanceledException("c", new System.Net.Sockets.SocketException())),
            opt: new LowCodeRunnerOptions { BaseUrl = "http://runner.test:5230", HttpTimeoutSeconds = 1 });
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(c));
        Assert.Equal("runner_unavailable", ex.Code);
    }

    [Fact]
    public async Task Redirect307_NaoESeguido_ViraRuntimeError_SemVazarLocation()
    {
        var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        r.Headers.Location = new Uri("http://evil.test/segredo");
        var h = new FakeHandler().Enqueue(r).Enqueue(Json(HttpStatusCode.OK, Ok));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(Novo(h)));
        Assert.Equal("runtime_error", ex.Code);
        Assert.DoesNotContain("evil", ex.Message);
        Assert.Single(h.Requests); // POST com o documento nao reenviado
    }

    [Fact]
    public async Task BaseUrlSemEsquema_NoSendAsync_ViraRunnerUnavailableSanitizada()
    {
        var c = new LowCodeRunnerHttpClient(new Factory(new FakeHandler(), semBase: true),
            Options.Create(new LowCodeRunnerOptions { BaseUrl = "lowcoderunner.local:5230" }),
            NullLogger<LowCodeRunnerHttpClient>.Instance);
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(c));
        Assert.Equal("runner_unavailable", ex.Code);
        Assert.DoesNotContain("lowcoderunner", ex.Message);
    }

    [Theory]
    [InlineData("lowcoderunner.local:5230", false)]
    [InlineData("nao e url", false)]
    [InlineData("ftp://x:1", false)]
    [InlineData("", false)]
    [InlineData("http://runner.local:5230", true)]
    [InlineData("https://runner.local", true)]
    public void BaseUrl_Validacao_ExigeHttpOuHttps_EInvalidaNaoConta(string url, bool valida)
    {
        Assert.Equal(valida, LowCodeRunnerHttpClient.IsValidBaseUrl(url));
        Assert.Equal(valida, LowCodeRunnerHttpClient.IsConfigured(new LowCodeRunnerOptions { BaseUrl = url }));
    }

    [Theory]
    [InlineData("queue_full", "queue_full")]
    [InlineData("timeout", "timeout")]
    [InlineData("mapper_not_found", "mapper_not_found")]
    [InlineData(null, "runner_unavailable")]
    [InlineData("", "runner_unavailable")]
    public void PathwayDiagnostic_ErrorCode_ParaCode(string? errorCode, string esperado) =>
        Assert.Equal(esperado, LayoutParserApi.Models.Transformation.PathwayDiagnostic.ResolveRunnerFailureCode(errorCode));

    [Fact]
    public async Task CancelamentoDoChamador_PropagaOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Chamar(Novo(new FakeHandler()), cts.Token));
    }

    [Fact]
    public async Task QueueFull_RetentaRespeitandoRetryAfter_ESucede()
    {
        var esperas = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue(Json(HttpStatusCode.ServiceUnavailable, Err(0), retryAfter: 5))
            .Enqueue(Json(HttpStatusCode.OK, Ok));
        var r = await Chamar(Novo(h, esperas));
        Assert.Equal("<a/>", r.Output);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(esperas));
    }

    [Fact]
    public async Task QueueFull_NoMaximoDuasTentativasExtras()
    {
        var esperas = new List<TimeSpan>();
        var h = new FakeHandler();
        for (var i = 0; i < 3; i++) h.Enqueue(Json(HttpStatusCode.ServiceUnavailable, Err(0), retryAfter: 1));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(Novo(h, esperas)));
        Assert.Equal("queue_full", ex.Code);
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal(2, esperas.Count);
    }

    [Fact]
    public async Task QueueFull_RetryAfterQueEstouraOrcamento_NaoRetenta()
    {
        // orcamento efetivo = min(ondas*180, 90) = 90s; Retry-After de 120s nao cabe.
        var esperas = new List<TimeSpan>();
        var h = new FakeHandler().Enqueue(Json(HttpStatusCode.ServiceUnavailable, Err(0), retryAfter: 120));
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => Chamar(Novo(h, esperas)));
        Assert.Equal("queue_full", ex.Code);
        Assert.Single(h.Requests);
        Assert.Empty(esperas);
    }

    [Fact]
    public async Task Batch_EnviaContentLength_ERotaCorreta()
    {
        var h = new FakeHandler().Enqueue(Json(HttpStatusCode.OK, "{\"results\":[]}"));
        using var doc = await Novo(h).TransformBatchAsync("d", "f", new[] { "A", "B" }, 60, "corr");
        Assert.Equal("/v1/transform/batch", h.Paths[0]);
        Assert.True(h.ContentLengths[0] > 0);
    }

    [Fact]
    public async Task Health_UsaRotaSemDeep_E200EhOk()
    {
        var h = new FakeHandler().Enqueue(Json(HttpStatusCode.OK, "{}"));
        var (ok, _) = await Novo(h).CheckHealthAsync();
        Assert.True(ok);
        Assert.Equal("/v1/health", h.Paths[0]);
        Assert.DoesNotContain("deep", h.Paths[0]);
    }

    private static LowCodeRunnerOptions OptHttp() => new()
    {
        BaseUrl = "http://runner.test:5230",
        AllowedPackageGuids = new List<string> { "PAC_x" }
    };

    [Fact]
    public async Task HealthCheck_RunnerFora_EhDegradedNuncaUnhealthy()
    {
        var http = Novo(new ThrowingHandler(new HttpRequestException("refused")));
        var r = await new LowCodeRunnerHealthCheck(Options.Create(OptHttp()), http).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, r.Status);
    }

    [Fact]
    public async Task HealthCheck_Runner503_EhDegraded_E200EhHealthy()
    {
        var down = Novo(new FakeHandler().Enqueue(Json(HttpStatusCode.ServiceUnavailable, "{}")));
        var r1 = await new LowCodeRunnerHealthCheck(Options.Create(OptHttp()), down).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, r1.Status);

        var up = Novo(new FakeHandler().Enqueue(Json(HttpStatusCode.OK, "{}")));
        var r2 = await new LowCodeRunnerHealthCheck(Options.Create(OptHttp()), up).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, r2.Status);
    }

    [Fact]
    public async Task Servico_SemRunnerPathESemBaseUrl_LancaRunnerUnavailableClaro()
    {
        var svc = new LowCodeTransformationService(NullLogger<LowCodeTransformationService>.Instance,
            Options.Create(new LowCodeRunnerOptions { SysmiddleDir = "x", GlobalFolder = "y" }),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var ex = await Assert.ThrowsAsync<LowCodeRunnerException>(() => svc.TransformAsync("d", mapperId: "M"));
        Assert.Equal("runner_unavailable", ex.Code);
        Assert.Contains("runner indisponível neste host", ex.Message);
    }

    [Fact]
    public async Task Servico_ComBaseUrl_DelegaAoHttp_SemExigirSysmiddleDir()
    {
        var h = new FakeHandler().Enqueue(Json(HttpStatusCode.OK, Ok));
        var opt = OptHttp();
        var svc = new LowCodeTransformationService(NullLogger<LowCodeTransformationService>.Instance, Options.Create(opt),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), Novo(h, opt: opt));
        Assert.Equal("<a/>", await svc.TransformAsync("d", mapperId: "M1", fileName: "f.txt"));
        Assert.Equal("/v1/transform", h.Paths[0]);
    }
}
