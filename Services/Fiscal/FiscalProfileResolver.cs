using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Security;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>Resultado da validação em cascata do §2.4 do ADR (issue #379). <c>Error</c> é a mensagem PT-BR específica a devolver em 422.</summary>
    public sealed record FiscalProfileValidationResult(bool IsValid, string? Error, FiscalResolvedXsd? ResolvedXsd);

    /// <summary>
    /// Cruza um <see cref="FiscalProfile"/> com <c>XsdValidation:DocumentTypes</c> (issue #379, ADR
    /// §2.4/§2.6) — valida em cascata na escrita (<c>PUT .../fiscal-profile</c>) e resolve o XSD eco
    /// (<c>resolvedXsd</c>) na leitura (GET de draft/release). A chave do appsettings é <c>CTE</c> mas
    /// o valor canônico de <see cref="FiscalDocumentType"/> é <c>CTe</c> — normalizado aqui (ADR §2.4)
    /// em vez de mexer no appsettings versionado.
    /// </summary>
    public interface IFiscalProfileResolver
    {
        /// <summary>Cascata completa do §2.4 — usada pelo PUT antes de persistir.</summary>
        FiscalProfileValidationResult Validate(FiscalProfile profile);

        /// <summary>Só o eco derivado (sem revalidar operation/jurisdiction) — usado pelos GETs de draft/release.</summary>
        FiscalResolvedXsd? Resolve(string documentType, string schemaVersion);
    }

    public sealed class FiscalProfileResolver : IFiscalProfileResolver
    {
        // Normaliza a chave "CTE" (appsettings) → "CTe" (FiscalDocumentType.Cte) sem tocar no
        // appsettings.json versionado (ADR §2.4, opção "mapear no resolver").
        private static readonly IReadOnlyDictionary<string, string> ConfigKeyByDocumentType =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FiscalDocumentType.Nfe] = "NFe",
                [FiscalDocumentType.Cte] = "CTE",
                [FiscalDocumentType.NfCom] = "NFCom",
                [FiscalDocumentType.Mdfe] = "MDFe",
            };

        private readonly IConfiguration _configuration;
        private readonly string? _xsdBasePath;
        private readonly ILogger<FiscalProfileResolver> _logger;

        public FiscalProfileResolver(IConfiguration configuration, ILogger<FiscalProfileResolver> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _xsdBasePath = new LayoutParserApi.Services.Storage.StoragePaths(configuration).Xsd;
        }

        public FiscalProfileValidationResult Validate(FiscalProfile profile)
        {
            // 1. documentType — enum fechado.
            if (!FiscalDocumentType.IsValid(profile.DocumentType))
                return new FiscalProfileValidationResult(false, $"Campo \"documentType\" inválido: deve ser um de {string.Join(", ", FiscalDocumentType.All)}.", null);

            // 2. Existe entrada em XsdValidation:DocumentTypes para o tipo.
            var entry = ReadDocumentTypeEntry(profile.DocumentType);
            if (entry == null)
                return new FiscalProfileValidationResult(false, $"Tipo de documento \"{profile.DocumentType}\" sem XSD configurado neste ambiente.", null);

            // 3. schemaVersion == XsdVersion configurado OU arquivo XSD existe sob XsdValidation:BasePath.
            if (!string.Equals(profile.SchemaVersion, entry.XsdVersion, StringComparison.OrdinalIgnoreCase) && !XsdFileExists(profile.SchemaVersion))
                return new FiscalProfileValidationResult(false, $"Versão de schema \"{profile.SchemaVersion}\" não instalada neste ambiente para \"{profile.DocumentType}\".", null);

            // 4. operation / jurisdiction — enums fechado/semiaberto.
            if (!FiscalOperation.IsValid(profile.Operation))
                return new FiscalProfileValidationResult(false, $"Campo \"operation\" inválido: deve ser um de {string.Join(", ", FiscalOperation.All)}.", null);

            if (!FiscalJurisdiction.IsValid(profile.Jurisdiction))
                return new FiscalProfileValidationResult(false, "Campo \"jurisdiction\" inválido: deve ser uma UF válida ou \"BR\".", null);

            return new FiscalProfileValidationResult(true, null, new FiscalResolvedXsd(entry.XsdVersion, entry.Namespace, entry.RootElement));
        }

        public FiscalResolvedXsd? Resolve(string documentType, string schemaVersion)
        {
            var entry = ReadDocumentTypeEntry(documentType);
            return entry == null ? null : new FiscalResolvedXsd(entry.XsdVersion, entry.Namespace, entry.RootElement);
        }

        private DocumentTypeEntry? ReadDocumentTypeEntry(string documentType)
        {
            if (!ConfigKeyByDocumentType.TryGetValue(documentType, out var configKey))
                return null;

            var section = _configuration.GetSection($"XsdValidation:DocumentTypes:{configKey}");
            if (!section.Exists())
                return null;

            var xsdVersion = section["XsdVersion"];
            var ns = section["Namespace"];
            var rootElement = section["RootElement"];
            if (string.IsNullOrWhiteSpace(xsdVersion) || string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(rootElement))
                return null;

            return new DocumentTypeEntry(xsdVersion, ns, rootElement);
        }

        private bool XsdFileExists(string version)
        {
            if (string.IsNullOrWhiteSpace(_xsdBasePath) || string.IsNullOrWhiteSpace(version))
                return false;

            try
            {
                var versionPath = SafePathResolver.Resolve(_xsdBasePath, version);
                return versionPath != null && Directory.Exists(versionPath) && Directory.GetFiles(versionPath, "*.xsd", SearchOption.AllDirectories).Length > 0;
            }
            catch (Exception ex)
            {
                // Degrada graciosamente (dotnet-standards.md): I/O de disco pode falhar (permissão,
                // path de rede fora do ar) — trata como "versão não instalada", nunca derruba o PUT.
                _logger.LogWarning(ex, "Falha ao checar arquivo XSD para a versão {SchemaVersion}.", version);
                return false;
            }
        }

        private sealed record DocumentTypeEntry(string XsdVersion, string Namespace, string RootElement);
    }
}
