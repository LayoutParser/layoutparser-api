using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// Escolhe o <see cref="IStudioModelAdapter"/> por <c>Engine</c> (case-insensitive). Default sem engine: <c>sysmiddle</c>.
    /// Engine conhecido sem adaptador registrado → 501; desconhecido → 400.
    /// </summary>
    public sealed class StudioModelService : IStudioModelService
    {
        public const string DefaultEngine = "sysmiddle";
        private static readonly string[] KnownEngines = { "sysmiddle", "tcl", "xslt" };

        private readonly IReadOnlyList<IStudioModelAdapter> _adapters;
        private readonly ILogger<StudioModelService> _logger;

        public StudioModelService(IEnumerable<IStudioModelAdapter> adapters, ILogger<StudioModelService> logger)
        {
            _adapters = adapters.ToList();
            _logger = logger;
        }

        public Task<StudioModelDocument?> GetAsync(Guid workspaceId, string mapperGuid, string? engine, CancellationToken ct)
        {
            var wanted = string.IsNullOrWhiteSpace(engine) ? DefaultEngine : engine.Trim();

            if (!KnownEngines.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                throw new StudioModelInvalidEngineException("Engine inválido. Use sysmiddle, tcl ou xslt.");

            var adapter = _adapters.FirstOrDefault(a => string.Equals(a.Engine, wanted, StringComparison.OrdinalIgnoreCase));
            if (adapter == null)
            {
                _logger.LogInformation("Studio-model: engine {Engine} sem adaptador nesta versão.", wanted);
                throw new StudioModelEngineNotSupportedException("Engine não suportado nesta versão.");
            }

            return adapter.LoadAsync(new StudioModelRequest(workspaceId, mapperGuid), ct);
        }
    }
}
