using Polly;
using Polly.Extensions.Http;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Políticas Polly do <see cref="HttpClient"/> nomeado do serviço LayoutParserDecrypt (ADR
    /// segregação Decrypt/LowCodeRunner, 2026-09-25). Descriptografia é etapa CRÍTICA do pipeline
    /// (diferente do Redis, que é cache opcional) — o objetivo aqui não é "degradar sem", é evitar
    /// martelar um serviço fora do ar e dar uma resposta rápida/clara quando ele estiver indisponível.
    /// </summary>
    public static class DecryptionResiliencePolicies
    {
        /// <summary>
        /// Retry com backoff exponencial (3 tentativas: ~1s, ~2s, ~4s) para erros transitórios
        /// (5xx, 408, exceções de rede) — <c>HandleTransientHttpError()</c> já cobre isso.
        /// </summary>
        public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

        /// <summary>
        /// Circuit breaker: após 5 falhas consecutivas, abre o circuito por 30s — respostas
        /// subsequentes falham rápido (<see cref="BrokenCircuitException"/>) em vez de esperar
        /// timeout de novo a cada request enquanto o serviço está fora do ar.
        /// </summary>
        public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
    }
}
