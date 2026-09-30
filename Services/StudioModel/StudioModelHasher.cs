using System.Security.Cryptography;
using System.Text;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// <c>rawHash</c> e <c>eTag</c> (design §5). Hash do conteúdo exato (UTF-8, sem reindentar/reordenar).
    /// O eTag inclui <c>schemaVersion</c> para invalidar caches do front quando o CONTRATO muda.
    /// </summary>
    public static class StudioModelHasher
    {
        /// <summary><c>sha256:&lt;hex&gt;</c> do texto; <c>null</c> se o texto é nulo.</summary>
        public static string? HashSource(string? content)
            => content == null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        /// <summary>Hash agregado: SHA-256 de <c>mapper|input-layout|target-layout</c> (hashes por fonte; ausente = vazio).</summary>
        public static string HashArtifact(string? mapperHash, string? inputHash, string? targetHash)
            => HashSource($"{mapperHash}|{inputHash}|{targetHash}")!;

        /// <summary>Base64 de 16 bytes do SHA-256 (hashes por fonte + schemaVersion), entre aspas (forma HTTP).</summary>
        public static string ComputeETag(string? mapperHash, string? inputHash, string? targetHash, int schemaVersion)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{mapperHash}|{inputHash}|{targetHash}|v{schemaVersion}"));
            return "\"" + Convert.ToBase64String(bytes, 0, 16) + "\"";
        }
    }
}
