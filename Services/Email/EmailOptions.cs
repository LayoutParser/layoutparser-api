namespace LayoutParserApi.Services.Email
{
    /// <summary>
    /// Config <c>Email:*</c> (segredos SÓ por env: <c>Email__Smtp__Password</c> etc. — nunca no repositório).
    /// Fase 1: SMTP do Gmail (smtp.gmail.com:587 STARTTLS) com senha de app, remetente = a própria conta.
    /// </summary>
    public sealed class EmailOptions
    {
        /// <summary>Base do portal para o link do e-mail (sem token).</summary>
        public string PortalUrl { get; set; } = string.Empty;

        /// <summary>Teto de envios em 24h (Gmail ~500/dia; margem de segurança).</summary>
        public int DailyLimit { get; set; } = 400;

        public int MaxAttempts { get; set; } = 5;

        /// <summary>Intervalo mínimo (min) entre e-mails ao mesmo destino no mesmo workspace no reenvio de convite (<c>Email:ResendCooldownMinutes</c>).</summary>
        public int ResendCooldownMinutes { get; set; } = 10;

        public SmtpOptions Smtp { get; set; } = new();
    }

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "smtp.gmail.com";
        public int Port { get; set; } = 587;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string FromName { get; set; } = "LayoutParser";
    }
}
