using System.Security.Cryptography;
using System.Text;

namespace LayoutParserApi.Models.Catalog
{
    /// <summary>
    /// Gera o <c>catalogId</c> (e o <c>folderId</c>) do catálogo unificado como GUID v5 (RFC 4122,
    /// SHA-1) determinístico — issue #627, desenho D2.
    /// </summary>
    /// <remarks>
    /// Nome hasheado: <c>"{sourceSystem}|{sourceProjectKey}|{sourceItemKey}"</c>, com a normalização
    /// determinística abaixo (qualquer mudança aqui muda TODOS os ids persistidos — não altere):
    /// <list type="number">
    /// <item><c>sourceSystem</c> entra pelo nome de fio snake_case (<see cref="SourceSystemExtensions.ToWireName"/>).</item>
    /// <item>Cada chave: Unicode NFC, <c>Trim()</c>, <c>\</c> vira <c>/</c> (caminhos Neogrid), minúsculas invariantes.</item>
    /// <item><c>sourceProjectKey</c> nulo/vazio vira <c>"-"</c> (pasta "Sem projeto"); <c>sourceItemKey</c> vazio é inválido.</item>
    /// <item>O separador é escapado dentro das chaves (<c>%</c> -> <c>%25</c>, <c>|</c> -> <c>%7C</c>), evitando
    ///   que ("a|b","c") e ("a","b|c") produzam o mesmo nome.</item>
    /// <item>Bytes: UTF-8. Namespace fixo <see cref="CatalogNamespace"/> (nunca mude).</item>
    /// </list>
    /// O <c>folderId</c> usa o mesmo mecanismo com <c>sourceItemKey</c> reservado <c>"#folder"</c> (o
    /// caractere <c>#</c> é aceito em chaves, mas nenhuma fonte usa exatamente esse valor).
    /// </remarks>
    public static class CatalogIdGenerator
    {
        /// <summary>Namespace v5 fixo do catálogo (GUID aleatório gerado uma única vez; imutável).</summary>
        public static readonly Guid CatalogNamespace = new("6f1d3c52-8a4e-4b7f-9c21-5e0a7d94b3c8");

        public const string NoProjectKey = "-";
        private const string FolderItemKey = "#folder";

        public static Guid ForItem(SourceSystem system, string? sourceProjectKey, string sourceItemKey)
        {
            if (string.IsNullOrWhiteSpace(sourceItemKey))
                throw new ArgumentException("sourceItemKey é obrigatório.", nameof(sourceItemKey));
            return Compute(system, sourceProjectKey, sourceItemKey);
        }

        public static Guid ForFolder(SourceSystem system, string? sourceProjectKey)
            => Compute(system, sourceProjectKey, FolderItemKey);

        /// <summary>Nome canônico que alimenta o hash (exposto para diagnóstico e testes).</summary>
        public static string BuildName(SourceSystem system, string? sourceProjectKey, string sourceItemKey)
        {
            var project = NormalizeKey(sourceProjectKey);
            if (project.Length == 0)
                project = NoProjectKey;
            return $"{system.ToWireName()}|{Escape(project)}|{Escape(NormalizeKey(sourceItemKey))}";
        }

        private static Guid Compute(SourceSystem system, string? projectKey, string itemKey)
        {
            var nameBytes = Encoding.UTF8.GetBytes(BuildName(system, projectKey, itemKey));
            var nsBytes = CatalogNamespace.ToByteArray();
            SwapByteOrder(nsBytes); // RFC 4122 usa big-endian; Guid.ToByteArray é little-endian nos 3 primeiros campos.

            var data = new byte[nsBytes.Length + nameBytes.Length];
            Buffer.BlockCopy(nsBytes, 0, data, 0, nsBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, data, nsBytes.Length, nameBytes.Length);
            var hash = SHA1.HashData(data);

            var result = new byte[16];
            Array.Copy(hash, 0, result, 0, 16);
            result[6] = (byte)((result[6] & 0x0F) | (5 << 4)); // versão 5
            result[8] = (byte)((result[8] & 0x3F) | 0x80);     // variante RFC 4122
            SwapByteOrder(result);
            return new Guid(result);
        }

        private static string NormalizeKey(string? value)
            => (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim().Replace('\\', '/').ToLowerInvariant();

        private static string Escape(string value) => value.Replace("%", "%25").Replace("|", "%7C");

        private static void SwapByteOrder(byte[] guid)
        {
            (guid[0], guid[3]) = (guid[3], guid[0]);
            (guid[1], guid[2]) = (guid[2], guid[1]);
            (guid[4], guid[5]) = (guid[5], guid[4]);
            (guid[6], guid[7]) = (guid[7], guid[6]);
        }
    }
}
