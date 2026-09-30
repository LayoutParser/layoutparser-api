using LayoutParserApi.Models.Dtos.StudioModel;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>Pedido de carga do modelo (Fase 1: só leitura).</summary>
    public sealed record StudioModelRequest(Guid WorkspaceId, string MapperGuid);

    /// <summary>
    /// Adaptador de engine do modelo único do Mapping Studio (design §3.1). Fase 1: só leitura;
    /// o método de escrita (<c>ApplyAsync</c>) entra nas Fases 2-4.
    /// </summary>
    public interface IStudioModelAdapter
    {
        /// <summary><c>sysmiddle</c> | <c>tcl</c> | <c>xslt</c>.</summary>
        string Engine { get; }

        /// <summary>Capacidades estáticas por engine (Fase 1: <c>edit=false</c>).</summary>
        StudioCapabilities Capabilities { get; }

        /// <summary>
        /// <c>null</c> = artefato inexistente (→ 404). Lança <see cref="StudioModelUnavailableException"/>
        /// quando a FONTE (SQL/cache) está fora (→ 503). Layout ausente/ilegível NÃO lança: degrada.
        /// </summary>
        Task<StudioModelDocument?> LoadAsync(StudioModelRequest request, CancellationToken ct);
    }

    /// <summary>Catálogo opcional de <c>DataTypeVO</c> (GUID <c>DAT_</c> → nome, ex. <c>Str_MAX</c>). Sem fonte registrada, o nome vira <c>?</c>.</summary>
    public interface IDataTypeCatalog
    {
        string? ResolveName(string dataTypeGuid);
    }
}
