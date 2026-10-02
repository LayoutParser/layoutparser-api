namespace LayoutParserApi.Models
{
    // Models de request
    public class TransformationRequest
    {
        public string InputContent { get; set; }
        public string LayoutName { get; set; }
        /// <summary>
        /// LayoutGuid retornado pelo parse. Opcional para manter compatibilidade com clientes
        /// existentes; aceita o formato com prefixo LAY_.
        /// </summary>
        public string? LayoutGuid { get; set; }
        public string SourceDocumentType { get; set; }
        public string TargetDocumentType { get; set; }
        public bool Validate { get; set; } = false;
        public string ExpectedOutput { get; set; }
        /// <summary>
        /// Issue #636: <c>catalogId</c> (GUID) do item de catálogo (<c>api/mapping-catalog</c>, engine <c>tcl</c>) a usar
        /// como FALLBACK do TCL quando não há arquivo em disco. Opcional; só vale para entrada TXT.
        /// </summary>
        public Guid? CatalogId { get; set; }
    }
}
