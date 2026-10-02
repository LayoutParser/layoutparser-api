using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Resolve o conteúdo XSL do artefato gerado (<c>tbGeneratedMapperArtifact</c>) para um layout
    /// (issue #642, opção b). Fallback do <see cref="TransformationPipelineService"/> quando o arquivo
    /// em <c>XslPath</c> não existe em disco.
    /// </summary>
    public interface IGeneratedXslResolver
    {
        /// <summary>
        /// Devolve o XSLT do artefato <b>Ready</b> do mapper ranqueado para o layout, ou <c>null</c>
        /// (sem layout/mapper/artefato Ready, ou qualquer falha). NUNCA lança.
        /// </summary>
        Task<string?> ResolveReadyXslAsync(string layoutName, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Só aceita status <c>ready</c>. <c>stale</c> NÃO é persistido (é calculado em leitura comparando o hash
    /// do MapperVo) e aqui não há esse hash; um XSL de mapper desatualizado produziria dado fiscal errado,
    /// então a decisão é recusar (generating/failed/ausente idem) e deixar o gatilho de criação agir.
    /// </summary>
    public class GeneratedXslResolver : IGeneratedXslResolver
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<GeneratedXslResolver> _logger;

        public GeneratedXslResolver(IServiceScopeFactory scopeFactory, ILogger<GeneratedXslResolver> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<string?> ResolveReadyXslAsync(string layoutName, CancellationToken cancellationToken = default)
        {
            var safeLayout = LogMessageSanitizer.Sanitize(layoutName);
            try
            {
                if (string.IsNullOrWhiteSpace(layoutName)) return null;

                var mapperGuid = await ResolveMapperGuidAsync(layoutName);
                if (string.IsNullOrWhiteSpace(mapperGuid)) return null;

                using var scope = _scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetService<IGeneratedMapperArtifactStore>();
                if (store is null) return null;

                var record = await store.GetAsync(mapperGuid, cancellationToken);
                if (record is null || !string.Equals(record.Status, GeneratedMapperArtifactStatus.Ready, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(record.Content))
                {
                    _logger.LogInformation("Sem artefato Ready para o layout {LayoutName} (mapper {MapperGuid}, status={Status}).",
                        safeLayout, LogMessageSanitizer.Sanitize(mapperGuid), record?.Status ?? "none");
                    return null;
                }
                return record.Content;
            }
            catch (Exception ex)
            {
                // Degrada para o comportamento atual (erro exato do pipeline) — nunca derruba o request.
                _logger.LogWarning(ex, "Falha ao consultar artefato gerado para o layout {LayoutName}; seguindo sem fallback.", safeLayout);
                return null;
            }
        }

        /// <summary>Mesma seleção do gatilho (#642): 1º mapper ranqueado com conteúdo decifrável. Virtual p/ teste.</summary>
        protected virtual async Task<string?> ResolveMapperGuidAsync(string layoutName)
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var layoutDb = sp.GetRequiredService<ILayoutDatabaseService>();
            var mapperDb = sp.GetRequiredService<MapperDatabaseService>();
            var opts = sp.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value;

            var search = await layoutDb.SearchLayoutsAsync(new global::LayoutParserApi.Models.Database.LayoutSearchRequest { SearchTerm = layoutName });
            var layout = search.Success
                ? search.Layouts.FirstOrDefault(l => string.Equals(l.Name, layoutName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (layout is null || layout.LayoutGuid == Guid.Empty) return null;

            var ranked = await mapperDb.GetRankedMapperCandidatesForLayoutGuidAsync(
                layout.LayoutGuid.ToString(), opts.ProjectId, opts.AllowedPackageGuids);
            return ranked.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.DecryptedContent))?.MapperGuid;
        }
    }
}
