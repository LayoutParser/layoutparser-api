namespace LayoutParserApi.Services.Email
{
    public sealed record OutboxEmail(Guid EmailId, string ToEmail, string Subject, string Body, int Attempts, string Template = "", string DedupeKey = "");

    /// <summary>Resultado do enqueue: <c>Enqueued=false</c> = deduplicado (EmailId do e-mail já existente quando conhecido).</summary>
    public sealed record EnqueueResult(bool Enqueued, Guid? EmailId);

    /// <summary>Linha do outbox SEM corpo (rastreio). <c>Status</c> já normalizado: pending|sent|failed.</summary>
    public sealed record OutboxEmailStatus(
        Guid EmailId, string ToEmail, string Template, string Status, int Attempts,
        DateTime CreatedAt, DateTime? NextAttemptAt, DateTime? SentAt, string? LastError);

    /// <summary>Outbox em SQL (banco <c>IdentityDatabase:*</c>): falha de e-mail nunca desfaz o vínculo do membro.</summary>
    public interface IEmailOutboxStore
    {
        /// <summary>Enfileira; <c>false</c> se já há e-mail igual (mesmo destino+template+chave) nas últimas 24h (anti-abuso).</summary>
        Task<EnqueueResult> EnqueueAsync(string toEmail, string template, string dedupeKey, string subject, string body, CancellationToken cancellationToken);

        /// <summary>Próximo e-mail vencido (pending/retry), marcando-o como em envio; <c>null</c> se não há.</summary>
        Task<OutboxEmail?> ClaimNextAsync(int maxAttempts, CancellationToken cancellationToken);

        Task MarkSentAsync(Guid emailId, CancellationToken cancellationToken);

        Task MarkFailedAsync(Guid emailId, string error, int maxAttempts, CancellationToken cancellationToken);

        Task<int> CountSentLast24hAsync(CancellationToken cancellationToken);

        /// <summary>Consulta de rastreio (somente leitura), por chave de dedupe (workspace) e e-mail opcional, mais recentes primeiro.</summary>
        Task<IReadOnlyList<OutboxEmailStatus>> ListAsync(string dedupeKey, string? toEmail, int skip, int take, CancellationToken cancellationToken);

        /// <summary>Status do e-mail mais recente por destinatário (chave minúscula) no workspace.</summary>
        Task<IReadOnlyDictionary<string, string>> GetLatestStatusByEmailAsync(string dedupeKey, CancellationToken cancellationToken);
    }
}
