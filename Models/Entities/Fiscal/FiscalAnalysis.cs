namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>Origem do registro da análise (coluna <c>Source</c>).</summary>
    public static class FiscalAnalysisSource
    {
        public const string Upload = "upload";
        public const string Auto = "auto";
    }

    /// <summary>Como o layout chegou à análise (coluna <c>LayoutMode</c>).</summary>
    public static class FiscalAnalysisLayoutMode
    {
        /// <summary>Arquivo de layout anexado pelo usuário (<c>/upload</c>) — guardado como file <c>layout</c>.</summary>
        public const string File = "file";

        /// <summary>Layout do catálogo (<c>/auto</c>) — guarda só o <c>LayoutGuid</c>, nunca o XML descriptografado.</summary>
        public const string Catalog = "catalog";
    }

    /// <summary>
    /// Nome de exibição do layout na API de histórico. Apenas apresentação: o dado gravado
    /// (<c>LayoutName</c>) não é alterado. Fallback: LayoutName, LayoutGuid, "Layout não informado".
    /// </summary>
    public static class FiscalAnalysisLayoutDisplay
    {
        public const string NotInformed = "Layout não informado";

        public static string Resolve(string? layoutName, string? layoutGuid)
        {
            if (!string.IsNullOrWhiteSpace(layoutName)) return layoutName.Trim();
            if (!string.IsNullOrWhiteSpace(layoutGuid)) return layoutGuid.Trim();
            return NotInformed;
        }
    }

    /// <summary>Papel de um arquivo dentro da análise (coluna <c>Role</c>).</summary>
    public static class FiscalAnalysisFileRole
    {
        public const string Document = "document";
        public const string Layout = "layout";
    }

    /// <summary>Linha de <c>tbLpFiscalAnalysis</c> (issue #366, ADR adr-historico-analises-fiscais-366 §2.1).</summary>
    public sealed record FiscalAnalysisRecord(
        Guid AnalysisId,
        Guid WorkspaceId,
        Guid OwnerUserId,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        string Source,
        string LayoutMode,
        string? LayoutGuid,
        string? LayoutName,
        string? DetectedType);

    /// <summary>
    /// Linha de <c>tbLpFiscalAnalysisFile</c>. <see cref="StoragePath"/> é relativo à raiz
    /// <c>ML:FiscalAnalysesPath</c> e NUNCA é exposto em resposta HTTP.
    /// </summary>
    public sealed record FiscalAnalysisFileRecord(
        Guid AnalysisFileId,
        Guid AnalysisId,
        string Role,
        string OriginalFileName,
        long SizeBytes,
        string Sha256,
        string? MimeSniffed,
        string StoragePath);

    /// <summary>Item de listagem (resumo agregado dos arquivos).</summary>
    public sealed record FiscalAnalysisSummary(
        Guid AnalysisId,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        string Source,
        string? LayoutName,
        string? LayoutGuid,
        string? DetectedType,
        int FileCount,
        long TotalSizeBytes);

    /// <summary>Página de resultados da listagem.</summary>
    public sealed record FiscalAnalysisPage(int Page, int PageSize, int Total, IReadOnlyList<FiscalAnalysisSummary> Items);

    /// <summary>Análise completa com seus arquivos (uso interno — contém <c>StoragePath</c>).</summary>
    public sealed record FiscalAnalysisDetail(FiscalAnalysisRecord Analysis, IReadOnlyList<FiscalAnalysisFileRecord> Files);

    /// <summary>Referência mínima para a purga (apagar disco + linha).</summary>
    public sealed record FiscalAnalysisExpiredRef(Guid AnalysisId, Guid WorkspaceId);

    /// <summary>Arquivo que o chamador (controller de parse) quer registrar — já em memória.</summary>
    public sealed record FiscalAnalysisFileInput(string Role, string OriginalFileName, byte[] Content);

    /// <summary>Pedido de registro de uma análise, montado pelo <c>ParseController</c>.</summary>
    public sealed record FiscalAnalysisRegistration(
        Guid WorkspaceId,
        Guid OwnerUserId,
        string Source,
        string LayoutMode,
        string? LayoutGuid,
        string? LayoutName,
        string? DetectedType,
        IReadOnlyList<FiscalAnalysisFileInput> Files);
}
