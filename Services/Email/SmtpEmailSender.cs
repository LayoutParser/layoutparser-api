using System.Net;
using System.Net.Mail;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Email
{
    /// <summary>Adaptador SMTP genérico (STARTTLS). Configurado para o Gmail na fase 1.</summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly SmtpOptions _options;

        public SmtpEmailSender(IOptions<EmailOptions> options) => _options = options.Value.Smtp;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.Username)
            && !string.IsNullOrWhiteSpace(_options.Password)
            && !string.IsNullOrWhiteSpace(FromAddress);

        private string FromAddress => string.IsNullOrWhiteSpace(_options.From) ? _options.Username : _options.From;

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = true, // STARTTLS na 587
                Credentials = new NetworkCredential(_options.Username, _options.Password),
                Timeout = 30_000
            };
            using var mail = new MailMessage
            {
                From = new MailAddress(FromAddress, _options.FromName),
                Subject = message.Subject,
                Body = message.TextBody,
                IsBodyHtml = false
            };
            mail.To.Add(message.To);

            await client.SendMailAsync(mail, cancellationToken);
        }
    }
}
