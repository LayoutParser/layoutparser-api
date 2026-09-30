namespace LayoutParserApi.Services.Email
{
    /// <summary>Template v1 do e-mail de boas-vindas. Sem token/segredo — só link para o portal.</summary>
    public static class WelcomeEmailTemplate
    {
        public const string Name = "welcome-v1";

        public static (string Subject, string Body) Render(string workspaceName, string portalUrl)
        {
            // Nome de workspace vem do usuário: remove quebras de linha (evita injeção de cabeçalho/corpo).
            var safeName = new string(workspaceName.Where(c => !char.IsControl(c)).ToArray());
            var link = string.IsNullOrWhiteSpace(portalUrl) ? string.Empty : $"\n\nAcesse o portal: {portalUrl}";
            return ($"Você foi adicionado ao workspace \"{safeName}\" no LayoutParser",
                $"Olá,\n\nvocê foi cadastrado no portal LayoutParser e vinculado ao workspace \"{safeName}\".{link}\n\nSe você não esperava este e-mail, pode ignorá-lo.");
        }
    }
}
