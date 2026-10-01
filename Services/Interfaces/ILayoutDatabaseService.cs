using LayoutParserApi.Models.Database;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ILayoutDatabaseService
    {
        Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request);
        Task<LayoutRecord?> GetLayoutByIdAsync(int id);

        /// <summary>
        /// Busca por GUID de verdade (WHERE LayoutGuid, com e sem LAY_), qualquer tipo/projeto. Null se ausente/falha.
        /// Implementação padrão retorna null para não quebrar implementações antigas/fakes.
        /// </summary>
        Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid) => Task.FromResult<LayoutRecord?>(null);
    }
}
