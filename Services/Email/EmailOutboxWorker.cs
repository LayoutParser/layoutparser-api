using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Email
{
    /// <summary>
    /// Envia o outbox em background (best-effort): respeita o teto diário e nunca derruba a API.
    /// Só o <c>EmailId</c> é logado — o endereço do destinatário não vai para o log.
    /// </summary>
    public sealed class EmailOutboxWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IEmailSender _sender;
        private readonly EmailOptions _options;
        private readonly ILogger<EmailOutboxWorker> _logger;

        public EmailOutboxWorker(IServiceScopeFactory scopes, IEmailSender sender, IOptions<EmailOptions> options, ILogger<EmailOutboxWorker> logger)
        {
            _scopes = scopes;
            _sender = sender;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_sender.IsConfigured)
            {
                _logger.LogWarning("Envio de e-mail DESATIVADO (Email:Smtp não configurado): e-mails ficam em status pending no outbox e NÃO serão enviados até configurar o SMTP e reiniciar.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DrainAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Falha no ciclo do outbox de e-mail; tentando de novo no próximo ciclo.");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>();

            var sentToday = await store.CountSentLast24hAsync(ct);
            if (sentToday >= _options.DailyLimit)
                _logger.LogWarning("Teto diário de e-mails atingido ({Sent}/{Limit}); envios pendentes aguardam a janela de 24h.", sentToday, _options.DailyLimit);

            while (sentToday < _options.DailyLimit && await store.ClaimNextAsync(_options.MaxAttempts, ct) is { } email)
            {
                try
                {
                    await _sender.SendAsync(new EmailMessage(email.ToEmail, email.Subject, email.Body), ct);
                    await store.MarkSentAsync(email.EmailId, ct);
                    sentToday++;
                    _logger.LogInformation("E-mail {EmailId} enviado (Template={Template}, WorkspaceId={WorkspaceId}, Tentativa={Attempts})",
                        email.EmailId, email.Template, email.DedupeKey, email.Attempts);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Falha ao enviar e-mail {EmailId} (Template={Template}, WorkspaceId={WorkspaceId}, Tentativa={Attempts}): {Error}",
                        email.EmailId, email.Template, email.DedupeKey, email.Attempts, ex.GetType().Name);
                    await store.MarkFailedAsync(email.EmailId, ex.GetType().Name, _options.MaxAttempts, ct);
                }
            }
        }
    }
}
