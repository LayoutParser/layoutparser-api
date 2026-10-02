using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Models.Fiscal;
using LayoutParserApi.Services.Fiscal;

namespace LayoutParserApi.Services.Catalog
{
    /// <summary>
    /// Adaptador Neogrid (issue #629): envolve <see cref="IReferenceExampleCatalogService"/> SEM reescrevê-lo.
    /// Pasta = <c>DocType</c> (1º nível real do corpus: nfe/cte/nfse/mdfe…); cada exemplo gera até 2 itens
    /// (TCL e XSL) ligados por <c>pairedCatalogId</c>. A chave do item inclui o nome do arquivo com extensão
    /// (<c>{docType}/{versao}/{arquivo.ext}</c>) — desvio do desenho D2, que usava só o baseName e faria o
    /// par TCL/XSL colidir no mesmo <c>catalogId</c>.
    /// </summary>
    public sealed class NeogridCatalogSource : IMappingCatalogSource
    {
        private readonly IReferenceExampleCatalogService _examples;
        private readonly ILogger<NeogridCatalogSource> _logger;

        public NeogridCatalogSource(IReferenceExampleCatalogService examples, ILogger<NeogridCatalogSource> logger)
        {
            _examples = examples;
            _logger = logger;
        }

        public SourceSystem System => SourceSystem.Neogrid;

        public async Task<MappingCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            var examples = await _examples.ListAsync(null, cancellationToken);
            var folders = new Dictionary<Guid, MappingCatalogFolderDto>();
            var items = new List<MappingCatalogItemDto>();

            foreach (var example in examples)
            {
                var projectKey = example.DocType.Trim().ToLowerInvariant();
                var folderId = CatalogIdGenerator.ForFolder(SourceSystem.Neogrid, projectKey);
                folders.TryAdd(folderId, new MappingCatalogFolderDto(
                    folderId, SourceSystem.Neogrid, projectKey, null, example.DocType, false));

                var tcl = BuildItem(example, projectKey, folderId, example.TclFileName, MappingCatalogEngine.Tcl);
                var xslEngine = example.XslFileName != null
                    && Path.GetExtension(example.XslFileName).Equals(".xslt", StringComparison.OrdinalIgnoreCase)
                    ? MappingCatalogEngine.Xslt : MappingCatalogEngine.Xsl;
                var xsl = BuildItem(example, projectKey, folderId, example.XslFileName, xslEngine);

                if (tcl != null && xsl != null)
                {
                    tcl = tcl with { PairedCatalogId = xsl.CatalogId };
                    xsl = xsl with { PairedCatalogId = tcl.CatalogId };
                }
                // Hash do corpo (só do próprio arquivo) para detectar mudança; falha de I/O => sem hash.
                var content = await SafeContentAsync(example.Id, cancellationToken);
                if (tcl != null) items.Add(tcl with { ContentHash = Hash(content?.TclContent) });
                if (xsl != null) items.Add(xsl with { ContentHash = Hash(content?.XslContent) });
            }

            // Corpus vazio/ausente é indistinguível de falha de configuração (o serviço degrada para lista
            // vazia): nunca "completo" nesse caso, para o sync não retirar o catálogo inteiro.
            var complete = items.Count > 0;
            _logger.LogInformation("Neogrid: {Folders} pastas, {Items} itens lidos (completo={Complete}).",
                folders.Count, items.Count, complete);
            return new MappingCatalogSnapshot(complete, folders.Values.ToList(), items);
        }

        public async Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef sourceRef, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourceRef.SourceRefJson))
                return null;
            string? id, kind;
            try
            {
                using var doc = JsonDocument.Parse(sourceRef.SourceRefJson);
                id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
                kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
            if (id == null || kind == null)
                return null;

            var content = await _examples.GetContentAsync(id, cancellationToken);
            if (content == null)
                return null;
            var isTcl = kind == MappingCatalogEngine.Tcl;
            var body = isTcl ? content.TclContent : content.XslContent;
            return body == null ? null : new MappingContent(body, kind);
        }

        private static MappingCatalogItemDto? BuildItem(ReferenceExample example, string projectKey, Guid folderId, string? fileName, string engine)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;
            var itemKey = $"{projectKey}/{example.Version}/{fileName}";
            var catalogId = CatalogIdGenerator.ForItem(SourceSystem.Neogrid, projectKey, itemKey);
            var refJson = JsonSerializer.Serialize(new { id = example.Id, kind = engine == MappingCatalogEngine.Tcl ? MappingCatalogEngine.Tcl : "xsl" });
            return new MappingCatalogItemDto(catalogId, folderId, SourceSystem.Neogrid, itemKey, engine,
                Path.GetFileNameWithoutExtension(fileName), example.Version, example.DocType, null, refJson, false);
        }

        private async Task<ReferenceExampleContent?> SafeContentAsync(string id, CancellationToken ct)
        {
            try { return await _examples.GetContentAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Neogrid: falha ao ler conteúdo do exemplo {ExampleId} para o hash.", id);
                return null;
            }
        }

        private static string? Hash(string? body)
            => body == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
