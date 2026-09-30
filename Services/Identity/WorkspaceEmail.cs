using System.Net.Mail;

namespace LayoutParserApi.Services.Identity
{
    /// <summary>Normalização/validação de e-mail usada por identidade e convites de workspace.</summary>
    public static class WorkspaceEmail
    {
        private const int MaxLength = 320;

        /// <summary>Minúsculas + trim; <c>null</c> se vazio ou inválido.</summary>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var value = raw.Trim().ToLowerInvariant();
            if (value.Length > MaxLength)
                return null;

            // MailAddress aceita "Nome <a@b>"; exigimos que o endereço seja exatamente o texto informado.
            return MailAddress.TryCreate(value, out var address) && address.Address == value && value.Contains('@')
                ? value
                : null;
        }
    }
}
