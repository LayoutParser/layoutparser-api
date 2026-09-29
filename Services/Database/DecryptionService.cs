using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Contrato de erro estruturado devolvido pelo serviço LayoutParserDecrypt (400/422) — espelha
    /// <c>DecryptErrorResponse</c> do repo LayoutParserDecrypt.
    /// </summary>
    internal sealed class DecryptHttpErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; set; }
    }

    /// <summary>
    /// Cliente HTTP do serviço LayoutParserDecrypt (ADR segregação Decrypt/LowCodeRunner,
    /// 2026-09-25) — substitui a chamada por <c>Process.Start</c> do executável legado local.
    ///
    /// A descriptografia em si continua rodando em .NET Framework 4.8.1 (ver comentário no
    /// LayoutParserDecrypt.csproj daquele repo: RijndaelManaged com chave/IV de tamanho não-AES não
    /// funciona no shim de .NET moderno) — só o TRANSPORTE muda, de subprocess local para HTTP.
    /// </summary>
    public class DecryptionService : IDecryptionService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<DecryptionService> _logger;
        private readonly bool _isConfigured;

        public DecryptionService(HttpClient httpClient, ILogger<DecryptionService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            // O HttpClient já vem com BaseAddress setado (ou não) pela factory registrada em
            // Program.cs, a partir de "LayoutParserDecrypt:BaseUrl". Sem BaseAddress, o serviço
            // não foi configurado — mesma semântica do antigo "exe não encontrado".
            _isConfigured = _httpClient.BaseAddress != null;

            if (!_isConfigured)
                _logger.LogWarning("Serviço LayoutParserDecrypt não configurado. Configure 'LayoutParserDecrypt:BaseUrl' no appsettings.json");
        }

        /// <inheritdoc />
        public bool IsDecryptorAvailable => _isConfigured;

        /// <inheritdoc />
        public async Task<string> DecryptContentAsync(string encryptedContent)
        {
            if (string.IsNullOrEmpty(encryptedContent))
            {
                _logger.LogWarning("Conteúdo criptografado está vazio");
                return string.Empty;
            }

            // ✅ P1.1: se o serviço não está configurado, FALHA EXPLÍCITA — nunca devolve a cifra
            // como se fosse texto claro. O chamador (warm-up / test-decryption) trata a exceção.
            if (!_isConfigured)
            {
                _logger.LogError("Serviço LayoutParserDecrypt não configurado (LayoutParserDecrypt:BaseUrl ausente). Nada foi descriptografado.");
                throw new DecryptionException("Serviço de descriptografia não configurado. Nada foi descriptografado.");
            }

            return await DecryptUsingHttpServiceAsync(encryptedContent);
        }

        private async Task<string> DecryptUsingHttpServiceAsync(string encryptedContent)
        {
            var corr = CorrelationContext.CurrentId ?? Guid.NewGuid().ToString();

            _logger.LogInformation("Descriptografando conteúdo via serviço HTTP (tamanho: {Size} caracteres, corr={CorrelationId})", encryptedContent.Length, corr);

            using var request = new HttpRequestMessage(HttpMethod.Post, "decrypt")
            {
                Content = new StringContent(encryptedContent, Encoding.UTF8, "text/plain")
            };
            request.Headers.Add("X-Correlation-ID", corr);

            HttpResponseMessage response;
            try
            {
                // Timeout defensivo do lado do cliente — defesa em profundidade além do timeout do
                // servidor (Polly cobre retry/circuit breaker; este token cobre o "não fica pendurado
                // para sempre" caso as políticas não capturem o cenário).
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                response = await _httpClient.SendAsync(request, timeoutCts.Token);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogError(ex, "Timeout ao chamar o serviço LayoutParserDecrypt (corr={CorrelationId})", corr);
                throw new DecryptionException($"Serviço de descriptografia excedeu o timeout (corr={corr}).", ex);
            }
            catch (Exception ex)
            {
                // Cobre BrokenCircuitException (Polly) e falhas de conexão/DNS — o circuito aberto
                // já é sinal de "serviço fora do ar", não precisa reembrulhar com mensagem diferente.
                _logger.LogError(ex, "Falha de comunicação com o serviço LayoutParserDecrypt (corr={CorrelationId})", corr);
                throw new DecryptionException($"Falha ao comunicar com o serviço de descriptografia (corr={corr}).", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string errorMessage;
                    try
                    {
                        var errorBody = await response.Content.ReadFromJsonAsync<DecryptHttpErrorResponse>();
                        errorMessage = errorBody?.Error ?? $"HTTP {(int)response.StatusCode}";
                    }
                    catch
                    {
                        // Corpo não é JSON válido (ex.: erro de infra antes de chegar no app) — usa
                        // o texto cru como fallback, nunca engole silenciosamente.
                        errorMessage = await response.Content.ReadAsStringAsync();
                    }

                    _logger.LogError("Serviço LayoutParserDecrypt falhou (HTTP {StatusCode}, corr={CorrelationId}): {Error}", (int)response.StatusCode, corr, errorMessage);
                    throw new DecryptionException($"Serviço de descriptografia falhou (HTTP {(int)response.StatusCode}, corr={corr}): {errorMessage}");
                }

                var decrypted = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Descriptografia concluída via HTTP (corr={CorrelationId}, outputChars={OutputChars})", corr, decrypted.Length);
                return decrypted;
            }
        }
    }
}
