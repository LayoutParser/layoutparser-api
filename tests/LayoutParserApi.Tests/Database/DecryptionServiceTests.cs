using System.Net;
using System.Text;

using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Logging;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Testes de <see cref="DecryptionService"/> pós ADR segregação Decrypt/LowCodeRunner
    /// (2026-09-25): a chamada por <c>Process.Start</c> virou HttpClient para o serviço
    /// LayoutParserDecrypt. Trava o mesmo P1.1 de antes — descriptografia que NÃO ocorreu tem que
    /// FALHAR explícito, nunca devolver a cifra como se fosse texto claro — agora no caminho HTTP.
    /// </summary>
    public class DecryptionServiceTests
    {
        /// <summary>Handler fake — nunca bate na rede de verdade, captura o request e devolve uma resposta fixa.</summary>
        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _responseBody;
            private readonly string _responseContentType;

            public HttpRequestMessage? RequestCapturado { get; private set; }
            public string? BodyCapturado { get; private set; }

            public FakeHttpMessageHandler(HttpStatusCode statusCode, string responseBody, string responseContentType = "text/plain")
            {
                _statusCode = statusCode;
                _responseBody = responseBody;
                _responseContentType = responseContentType;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCapturado = request;
                if (request.Content != null)
                    BodyCapturado = await request.Content.ReadAsStringAsync(cancellationToken);

                return new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, _responseContentType)
                };
            }
        }

        private static DecryptionService ComClienteNaoConfigurado()
        {
            // HttpClient sem BaseAddress == serviço "não encontrado" (mesma semântica do antigo
            // "exe não existe"). O handler não importa aqui — a chamada nem chega a sair.
            var httpClient = new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, string.Empty));
            return new DecryptionService(httpClient, NullLogger<DecryptionService>.Instance);
        }

        private static (DecryptionService Service, FakeHttpMessageHandler Handler) ComRespostaFixa(HttpStatusCode statusCode, string body, string contentType = "text/plain")
        {
            var handler = new FakeHttpMessageHandler(statusCode, body, contentType);
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://decrypt-fake.local/") };
            return (new DecryptionService(httpClient, NullLogger<DecryptionService>.Instance), handler);
        }

        [Fact]
        public void IsDecryptorAvailable_falso_quando_baseaddress_nao_configurado()
        {
            Assert.False(ComClienteNaoConfigurado().IsDecryptorAvailable);
        }

        [Fact]
        public void IsDecryptorAvailable_verdadeiro_quando_baseaddress_configurado()
        {
            var (svc, _) = ComRespostaFixa(HttpStatusCode.OK, "qualquer");
            Assert.True(svc.IsDecryptorAvailable);
        }

        [Fact]
        public async Task Servico_nao_configurado_lanca_em_vez_de_ecoar_a_cifra()
        {
            var svc = ComClienteNaoConfigurado();
            const string cifra = "conteudo-cifrado-qualquer";

            var ex = await Assert.ThrowsAsync<DecryptionException>(() => svc.DecryptContentAsync(cifra));

            // A cifra NUNCA pode voltar (nem na mensagem de erro) como se fosse resultado válido.
            Assert.DoesNotContain(cifra, ex.Message);
        }

        [Fact]
        public async Task Entrada_vazia_devolve_vazio_nao_e_falha()
        {
            var svc = ComClienteNaoConfigurado();

            Assert.Equal(string.Empty, await svc.DecryptContentAsync(""));
        }

        [Fact]
        public async Task Resposta_200_devolve_corpo_como_texto_decriptado()
        {
            var (svc, handler) = ComRespostaFixa(HttpStatusCode.OK, "<xml>texto claro</xml>");

            var resultado = await svc.DecryptContentAsync("XXXconteudo-cifrado");

            Assert.Equal("<xml>texto claro</xml>", resultado);
            Assert.NotNull(handler.RequestCapturado);
            Assert.Equal(HttpMethod.Post, handler.RequestCapturado!.Method);
            Assert.Equal("decrypt", handler.RequestCapturado.RequestUri!.AbsolutePath.TrimStart('/'));
            Assert.Equal("XXXconteudo-cifrado", handler.BodyCapturado);
        }

        [Fact]
        public async Task CorrelationId_propagado_no_header_X_Correlation_ID()
        {
            var (svc, handler) = ComRespostaFixa(HttpStatusCode.OK, "ok");

            CorrelationContext.CurrentId = "corr-de-teste-123";
            try
            {
                await svc.DecryptContentAsync("conteudo-cifrado");
            }
            finally
            {
                CorrelationContext.CurrentId = null;
            }

            Assert.True(handler.RequestCapturado!.Headers.TryGetValues("X-Correlation-ID", out var valores));
            Assert.Equal("corr-de-teste-123", Assert.Single(valores));
        }

        [Fact]
        public async Task Resposta_422_com_contrato_de_erro_lanca_DecryptionException_com_mensagem_do_servico()
        {
            var (svc, _) = ComRespostaFixa(
                HttpStatusCode.UnprocessableEntity,
                "{\"error\":\"Falha ao descriptografar conteúdo: chave inválida\",\"correlationId\":\"corr-1\"}",
                "application/json");

            var ex = await Assert.ThrowsAsync<DecryptionException>(() => svc.DecryptContentAsync("conteudo-cifrado-invalido"));

            Assert.Contains("chave inválida", ex.Message);
        }

        [Fact]
        public async Task Resposta_500_sem_json_lanca_DecryptionException_com_corpo_bruto()
        {
            var (svc, _) = ComRespostaFixa(HttpStatusCode.InternalServerError, "serviço fora do ar", "text/plain");

            var ex = await Assert.ThrowsAsync<DecryptionException>(() => svc.DecryptContentAsync("conteudo-cifrado"));

            Assert.Contains("serviço fora do ar", ex.Message);
        }
    }
}
