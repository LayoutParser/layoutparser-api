using LayoutParserApi.Models.Dtos.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Resolve a árvore dupla (origem/destino) + regras de um mapeador Sysmiddle real (issue #425,
    /// ADR "adr-layout-tree-endpoint-425"). Read-only, mesmo grau de sensibilidade de
    /// <see cref="IMappingExplanationAdapter"/> — não é descoberta de catálogo (issue #417),
    /// é leitura de UM mapeador já identificado pelo chamador via <c>mappingId</c>/<c>MapperGuid</c>.
    /// </summary>
    public interface ILayoutTreeService
    {
        /// <summary>
        /// Devolve <c>null</c> quando o mapper Sysmiddle não existe no catálogo (<c>tbMapper</c>) —
        /// o controller traduz para 404. Nunca lança para layout ausente/ilegível: degrada para
        /// árvore vazia daquele lado (mesmo padrão de <see cref="XslSynth.Core.GuidXPathCatalog"/>).
        /// </summary>
        Task<LayoutTreeResponse?> GetLayoutTreeAsync(string mappingId, CancellationToken cancellationToken, LayoutTreeDslOptions? dslOptions = null);
    }
}
