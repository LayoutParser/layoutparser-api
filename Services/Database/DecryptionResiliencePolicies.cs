using System.Net;

using Polly;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Políticas Polly do <see cref="HttpClient"/> nomeado do serviço LayoutParserDecrypt (ADR
    /// segregação Decrypt/LowCodeRunner, 2026-09-25). Descriptografia é etapa CRÍTICA do pipeline
    /// (diferente do Redis, que é cache opcional) — o objetivo aqui não é "degradar sem", é evitar
    /// martelar um serviço fora do ar e dar uma resposta rápida/clara quando ele estiver indisponível.
    ///
    /// <para>Contrato do serviço (repo layoutparser-decrypt): <c>503</c> = ocupado (com
    /// <c>Retry-After</c>) ou em desligamento → transitório, vale repetir; <c>504</c> = estourou o timeout
    /// de 30 s do servidor → falha da chamada, <b>não</b> repetir (repetir multiplicaria a espera por 4);
    /// <c>422</c>/<c>413</c>/<c>400</c> = erro do chamador/da cifra, nunca repetir; <c>500</c> = erro
    /// interno, não repetir.</para>
    /// </summary>
    public static class DecryptionResiliencePolicies
    {
        /// <summary>Teto do <c>Retry-After</c> honrado (o servidor manda 1 s; protege contra valor absurdo).</summary>
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

        /// <summary>Resposta que vale repetir: 503 (ocupado/desligando) ou 408.</summary>
        public static bool IsRetryableStatus(HttpStatusCode status)
            => status == HttpStatusCode.ServiceUnavailable || status == HttpStatusCode.RequestTimeout;

        /// <summary>
        /// Retry (3 tentativas) só para 503/408 e falhas de rede. Espera o <c>Retry-After</c> do servidor
        /// (limitado a <see cref="MaxRetryAfter"/>); sem o header, backoff exponencial ~1 s, ~2 s, ~4 s.
        /// </summary>
        public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
            Policy
                .Handle<HttpRequestException>()
                .OrResult<HttpResponseMessage>(r => IsRetryableStatus(r.StatusCode))
                .WaitAndRetryAsync(
                    3,
                    (attempt, outcome, _) => ComputeDelay(attempt, outcome?.Result),
                    (_, _, _, _) => Task.CompletedTask);

        /// <summary>
        /// Circuit breaker: após 5 falhas consecutivas, abre o circuito por 30 s — respostas subsequentes
        /// falham rápido (<see cref="Polly.CircuitBreaker.BrokenCircuitException"/>). Conta falha de rede,
        /// 500, 502 e 504; <b>não</b> conta 503: "ocupado" é backpressure normal do serviço, não indisponibilidade.
        /// </summary>
        public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
            Policy
                .Handle<HttpRequestException>()
                .OrResult<HttpResponseMessage>(r => CountsAsBreakerFailure(r.StatusCode))
                .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));

        /// <summary>500, 502 e 504 abrem o circuito; 503 e 4xx não.</summary>
        public static bool CountsAsBreakerFailure(HttpStatusCode status)
            => status == HttpStatusCode.InternalServerError
               || status == HttpStatusCode.BadGateway
               || status == HttpStatusCode.GatewayTimeout;

        /// <summary>Espera antes da próxima tentativa: <c>Retry-After</c> (segundos) limitado, senão backoff exponencial.</summary>
        public static TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
        {
            var retryAfter = response?.Headers.RetryAfter?.Delta;
            if (retryAfter is TimeSpan delta && delta >= TimeSpan.Zero)
                return delta > MaxRetryAfter ? MaxRetryAfter : delta;

            return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        }
    }
}
