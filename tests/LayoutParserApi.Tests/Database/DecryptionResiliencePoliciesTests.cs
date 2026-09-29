using System.Net;
using System.Net.Http.Headers;

using LayoutParserApi.Services.Database;

using Polly.CircuitBreaker;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>Políticas do cliente do decrypt seguem o contrato do serviço (503 transitório, 504 não repete).</summary>
    public class DecryptionResiliencePoliciesTests
    {
        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable, true)]
        [InlineData(HttpStatusCode.RequestTimeout, true)]
        [InlineData(HttpStatusCode.GatewayTimeout, false)]   // timeout do servidor: não repetir
        [InlineData(HttpStatusCode.InternalServerError, false)]
        [InlineData(HttpStatusCode.UnprocessableEntity, false)]
        [InlineData(HttpStatusCode.RequestEntityTooLarge, false)]
        [InlineData(HttpStatusCode.BadRequest, false)]
        public void IsRetryableStatus_segue_o_contrato(HttpStatusCode status, bool esperado)
            => Assert.Equal(esperado, DecryptionResiliencePolicies.IsRetryableStatus(status));

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError, true)]
        [InlineData(HttpStatusCode.BadGateway, true)]
        [InlineData(HttpStatusCode.GatewayTimeout, true)]
        [InlineData(HttpStatusCode.ServiceUnavailable, false)] // ocupado é backpressure, não queda
        [InlineData(HttpStatusCode.UnprocessableEntity, false)]
        public void CountsAsBreakerFailure_ignora_503_e_4xx(HttpStatusCode status, bool esperado)
            => Assert.Equal(esperado, DecryptionResiliencePolicies.CountsAsBreakerFailure(status));

        [Fact]
        public void ComputeDelay_usa_RetryAfter_do_servidor_limitado()
        {
            using var r1 = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r1.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), DecryptionResiliencePolicies.ComputeDelay(3, r1));

            using var r2 = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r2.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(999));
            Assert.Equal(DecryptionResiliencePolicies.MaxRetryAfter, DecryptionResiliencePolicies.ComputeDelay(1, r2));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 4)]
        public void ComputeDelay_sem_RetryAfter_e_backoff_exponencial(int tentativa, int segundos)
        {
            using var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Assert.Equal(TimeSpan.FromSeconds(segundos), DecryptionResiliencePolicies.ComputeDelay(tentativa, r));
            Assert.Equal(TimeSpan.FromSeconds(segundos), DecryptionResiliencePolicies.ComputeDelay(tentativa, null));
        }

        [Fact]
        public async Task Retry_repete_503_e_devolve_o_sucesso_seguinte()
        {
            var chamadas = 0;
            var policy = DecryptionResiliencePolicies.GetRetryPolicy();

            var resposta = await policy.ExecuteAsync(() =>
            {
                chamadas++;
                var r = new HttpResponseMessage(chamadas < 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                if (chamadas < 2) r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(r);
            });

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.Equal(2, chamadas);
        }

        [Fact]
        public async Task Retry_nao_repete_504()
        {
            var chamadas = 0;
            var policy = DecryptionResiliencePolicies.GetRetryPolicy();

            var resposta = await policy.ExecuteAsync(() =>
            {
                chamadas++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
            });

            Assert.Equal(HttpStatusCode.GatewayTimeout, resposta.StatusCode);
            Assert.Equal(1, chamadas);
        }

        [Fact]
        public async Task Breaker_abre_apos_5_falhas_504_mas_nao_com_503()
        {
            var comum = DecryptionResiliencePolicies.GetCircuitBreakerPolicy();
            for (var i = 0; i < 5; i++)
                await comum.ExecuteAsync(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            // 503 não abre: a próxima chamada ainda passa.
            var ok = await comum.ExecuteAsync(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            var queda = DecryptionResiliencePolicies.GetCircuitBreakerPolicy();
            for (var i = 0; i < 5; i++)
                await queda.ExecuteAsync(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)));
            await Assert.ThrowsAsync<BrokenCircuitException<HttpResponseMessage>>(
                () => queda.ExecuteAsync(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        }
    }
}
