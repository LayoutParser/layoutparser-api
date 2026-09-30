namespace LayoutParserApi.Services.Email
{
    /// <summary>E-mail de texto puro a enviar. Nunca carrega token/segredo no corpo.</summary>
    public sealed record EmailMessage(string To, string Subject, string TextBody);

    /// <summary>
    /// Porta de envio de e-mail. Um adaptador por provedor (hoje SMTP/Gmail; SES/Brevo depois), escolhido
    /// por configuração — nenhuma regra de negócio conhece o provedor. Lança em falha; quem chama (o
    /// worker do outbox) decide retry.
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>Verdadeiro quando o provedor está configurado; senão nada é enfileirado.</summary>
        bool IsConfigured { get; }

        Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
    }
}
