namespace LayoutParserApi.Services.Email
{
    /// <summary>Mascara o destinatário para log/rastreio (ex.: <c>l***@gmail.com</c>) — nunca expõe o endereço completo.</summary>
    public static class EmailMasking
    {
        public static string Mask(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return "***";
            var at = email.IndexOf('@');
            if (at <= 0)
                return "***";
            return email[0] + "***" + email[at..];
        }
    }
}
