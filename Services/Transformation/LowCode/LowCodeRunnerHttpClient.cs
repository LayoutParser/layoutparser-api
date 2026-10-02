using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>Resposta 200 de POST /v1/transform.</summary>
    public sealed record LowCodeRunnerTransformResponse(string Output, long DurationMs, string? MapperId, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Cliente HTTP do LayoutParserLowCodeRunner (serviço Windows, contrato v1). Ativo quando
    /// <c>LowCode:BaseUrl</c> está preenchida. Singleton: cria o HttpClient por chamada via
    /// <see cref="IHttpClientFactory"/> (nome <see cref="HttpClientName"/>) para não congelar DNS/handler.
    ///
    /// <para>Regras do contrato: Content-Length SEMPRE (corpo bufferizado, nunca chunked — senão 411),
    /// exatamente um entre mapperId/mapperName, X-Correlation-ID, cancelamento fecha a conexão.</para>
    /// </summary>
    public class LowCodeRunnerHttpClient
    {
        public const string HttpClientName = "lowcode-runner";
        public const string CorrelationHeader = "X-Correlation-ID";
        public const int MaxQueueFullRetries = 2;
        private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);

        private readonly IHttpClientFactory _factory;
        private readonly LowCodeRunnerOptions _opt;
        private readonly ILogger<LowCodeRunnerHttpClient> _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public LowCodeRunnerHttpClient(
            IHttpClientFactory factory,
            IOptions<LowCodeRunnerOptions> options,
            ILogger<LowCodeRunnerHttpClient> logger,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _factory = factory;
            _opt = options.Value;
            _logger = logger;
            _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        }

        public static bool IsConfigured(LowCodeRunnerOptions o) => !string.IsNullOrWhiteSpace(o.BaseUrl);

        /// <summary>
        /// POST /v1/transform. Em 503 com Retry-After (fila cheia) tenta de novo até
        /// <see cref="MaxQueueFullRetries"/> vezes, SOMENTE se a espera couber no orçamento total da
        /// chamada (<see cref="LowCodeCandidatesBudget"/>); senão falha com queue_full.
        /// </summary>
        public virtual async Task<LowCodeRunnerTransformResponse> TransformAsync(
            string document, string fileName, string? mapperId, string? mapperName,
            string correlationId, CancellationToken cancellationToken = default)
        {
            var hasId = !string.IsNullOrWhiteSpace(mapperId);
            var hasName = !string.IsNullOrWhiteSpace(mapperName);
            if (hasId == hasName)
                throw new LowCodeRunnerException(LowCodeRunnerException.InvalidRequest, "Informe exatamente um entre mapperId e mapperName");

            var payload = new Dictionary<string, object?>
            {
                ["document"] = document ?? "",
                ["fileName"] = fileName,
            };
            if (hasId) payload["mapperId"] = mapperId; else payload["mapperName"] = mapperName;
            var body = JsonSerializer.SerializeToUtf8Bytes(payload);

            var budget = LowCodeCandidatesBudget.Calculate(_opt.MultiCandidateTopN, _opt.MaxConcurrentRunners,
                _opt.RunnerTimeoutSeconds, _opt.CandidatesRequestTimeoutSeconds).EffectiveSeconds;
            var sw = Stopwatch.StartNew();

            for (var tentativa = 0; ; tentativa++)
            {
                try
                {
                    using var json = await SendAsync(HttpMethod.Post, "/v1/transform", body, correlationId, cancellationToken);
                    return ParseOk(json);
                }
                catch (LowCodeRunnerException ex) when (ex.Code == LowCodeRunnerException.QueueFull && tentativa < MaxQueueFullRetries)
                {
                    var espera = RetryAfterOf(ex);
                    if (sw.Elapsed + espera >= TimeSpan.FromSeconds(budget))
                    {
                        _logger.LogWarning("Runner low-code com fila cheia; Retry-After de {RetryAfterSeconds}s nao cabe no orcamento de {BudgetSeconds}s (corr={CorrelationId})",
                            espera.TotalSeconds, budget, correlationId);
                        throw;
                    }
                    _logger.LogInformation("Runner low-code com fila cheia; nova tentativa {Tentativa}/{Max} em {RetryAfterSeconds}s (corr={CorrelationId})",
                        tentativa + 1, MaxQueueFullRetries, espera.TotalSeconds, correlationId);
                    await _delay(espera, cancellationToken);
                }
            }
        }

        /// <summary>
        /// POST /v1/transform/batch. Sem retry (o runner já faz as ondas e devolve parcial). Devolve o
        /// JSON cru do batch; quem consome interpreta results[] (status ok|failed|timeout|skipped).
        /// </summary>
        public virtual async Task<JsonDocument> TransformBatchAsync(
            string document, string fileName, IReadOnlyList<string> mapperIds, int? budgetSeconds,
            string correlationId, CancellationToken cancellationToken = default)
        {
            if (mapperIds.Count == 0)
                throw new LowCodeRunnerException(LowCodeRunnerException.InvalidRequest, "Batch sem candidatos");
            var payload = new Dictionary<string, object?>
            {
                ["document"] = document ?? "",
                ["fileName"] = fileName,
                ["candidates"] = mapperIds.Select(id => new { mapperId = id }).ToList(),
            };
            if (budgetSeconds is > 0) payload["budgetSeconds"] = budgetSeconds;
            var body = JsonSerializer.SerializeToUtf8Bytes(payload);
            return await SendAsync(HttpMethod.Post, "/v1/transform/batch", body, correlationId, cancellationToken);
        }

        /// <summary>GET /v1/health (readiness barato, nunca deep=true). Nunca lança: true/false + detalhe.</summary>
        public virtual async Task<(bool Ok, string Detail)> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(HealthTimeout);
                using var http = _factory.CreateClient(HttpClientName);
                using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return resp.IsSuccessStatusCode
                    ? (true, "Runner low-code HTTP respondeu /v1/health.")
                    : (false, $"Runner low-code HTTP respondeu {(int)resp.StatusCode} em /v1/health.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sonda GET /v1/health do runner low-code falhou");
                return (false, "Runner low-code HTTP inacessivel (" + ex.GetType().Name + ").");
            }
        }

        private static LowCodeRunnerTransformResponse ParseOk(JsonDocument doc)
        {
            var r = doc.RootElement;
            var warnings = new List<string>();
            if (r.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
                warnings.AddRange(w.EnumerateArray().Select(x => x.ToString()));
            return new LowCodeRunnerTransformResponse(
                r.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                r.TryGetProperty("durationMs", out var d) && d.TryGetInt64(out var ms) ? ms : 0,
                r.TryGetProperty("mapperId", out var m) ? m.GetString() : null,
                warnings);
        }

        private async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[] body, string correlationId,
            CancellationToken ct)
        {
            try
            {
                using var http = _factory.CreateClient(HttpClientName);
                using var req = new HttpRequestMessage(method, path);
                // ByteArrayContent => Content-Length conhecido (nunca chunked, que o runner rejeita com 411).
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                req.Content = content;
                req.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    try { return JsonDocument.Parse(text); }
                    catch (JsonException jx)
                    {
                        throw new LowCodeRunnerException(LowCodeRunnerException.RuntimeError, "Resposta invalida do runner low-code", (int)resp.StatusCode, inner: jx);
                    }
                }
                throw MapError(resp, text, correlationId);
            }
            catch (LowCodeRunnerException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                // Sem cancelamento do chamador => estourou HttpClient.Timeout (execucao longa demais).
                throw new LowCodeRunnerException(LowCodeRunnerException.Timeout, "Timeout aguardando o runner low-code", inner: ex);
            }
            catch (HttpRequestException ex)
            {
                // Conexao recusada, DNS, timeout de conexao.
                throw new LowCodeRunnerException(LowCodeRunnerException.RunnerUnavailable, "runner indisponível neste host", inner: ex);
            }
        }

        private static LowCodeRunnerException MapError(HttpResponseMessage resp, string text, string correlationId)
        {
            var status = (int)resp.StatusCode;
            string? error = null; int? exitCode = null;
            try
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (d.RootElement.TryGetProperty("error", out var e)) error = e.ToString();
                    if (d.RootElement.TryGetProperty("exitCode", out var x) && x.TryGetInt32(out var xi)) exitCode = xi;
                }
            }
            catch (JsonException) { /* corpo nao-JSON: segue so com o status */ }

            // Nunca expoe caminho de disco/segredo do host do runner.
            var msg = LowCodeErrorSanitizer.ForWire(string.IsNullOrWhiteSpace(error) ? $"Runner low-code respondeu HTTP {status}" : error)!;

            var hasRetryAfter = resp.Headers.RetryAfter != null;
            var code = status switch
            {
                400 => LowCodeRunnerException.InvalidRequest,
                404 => LowCodeRunnerException.MapperNotFound,
                422 => LowCodeRunnerException.TransformFailed,
                504 => LowCodeRunnerException.Timeout,
                503 => hasRetryAfter ? LowCodeRunnerException.QueueFull : LowCodeRunnerException.RunnerUnavailable,
                _ => LowCodeRunnerException.RuntimeError
            };
            var ex = new LowCodeRunnerException(code, msg, status, exitCode);
            if (code == LowCodeRunnerException.QueueFull)
                ex.Data["RetryAfterSeconds"] = RetryAfterSeconds(resp.Headers.RetryAfter);
            return ex;
        }

        private static double RetryAfterSeconds(RetryConditionHeaderValue? h)
        {
            if (h?.Delta is { } d) return Math.Max(0, d.TotalSeconds);
            if (h?.Date is { } dt) return Math.Max(0, (dt - DateTimeOffset.UtcNow).TotalSeconds);
            return 30;
        }

        private static TimeSpan RetryAfterOf(LowCodeRunnerException ex) =>
            TimeSpan.FromSeconds(ex.Data["RetryAfterSeconds"] is double s ? s : 30);
    }
}
