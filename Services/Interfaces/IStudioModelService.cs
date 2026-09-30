using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Fachada do endpoint <c>GET .../mappings/{mapperGuid}/studio-model</c> — escolhe o adaptador por engine.</summary>
    public interface IStudioModelService
    {
        /// <summary>
        /// <c>null</c> = mapper inexistente (404). Engine inválido lança <c>StudioModelInvalidEngineException</c> (400);
        /// engine sem adaptador lança <c>StudioModelEngineNotSupportedException</c> (501);
        /// fonte fora lança <c>StudioModelUnavailableException</c> (503).
        /// </summary>
        Task<StudioModelDocument?> GetAsync(Guid workspaceId, string mapperGuid, string? engine, CancellationToken ct);
    }
}
