namespace LayoutParserApi.Services.Email
{
    public sealed record OutboxEmail(Guid EmailId, string ToEmail, string Subject, string Body, int Attempts);

    /// <summary>Outbox em SQL (banco <c>IdentityDatabase:*</c>): falha de e-mail nunca desfaz o vínculo do membro.</summary>
    public interface IEmailOutboxStore
    {
        /// <summary>Enfileira; <c>false</c> se já há e-mail igual (mesmo destino+template+chave) nas últimas 24h (anti-abuso).</summary>
        Task<bool> EnqueueAsync(string toEmail, string template, string dedupeKey, string subject, string body, CancellationToken cancellationToken);

        /// <summary>Próximo e-mail vencido (pending/retry), marcando-o como em envio; <c>null</c> se não há.</summary>
        Task<OutboxEmail?> ClaimNextAsync(int maxAttempts, CancellationToken cancellationToken);

        Task MarkSentAsync(Guid emailId, CancellationToken cancellationToken);

        Task MarkFailedAsync(Guid emailId, string error, int maxAttempts, CancellationToken cancellationToken);

        Task<int> CountSentLast24hAsync(CancellationToken cancellationToken);
    }
}
