using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Database
{
    public class CachedLayoutService : ICachedLayoutService
    {
        private readonly ILayoutDatabaseService _layoutDatabaseService;
        private readonly ILayoutCacheService _cacheService;
        private readonly ILogger<CachedLayoutService> _logger;

        public CachedLayoutService(
            ILayoutDatabaseService layoutDatabaseService,
            ILayoutCacheService cacheService,
            ILogger<CachedLayoutService> logger)
        {
            _layoutDatabaseService = layoutDatabaseService;
            _cacheService = cacheService;
            _logger = logger;
        }

        public async Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request)
        {
            try
            {
                // Normalizar SearchTerm: se vazio ou null, usar "all" como chave de cache
                var searchTerm = string.IsNullOrWhiteSpace(request.SearchTerm) ? "all" : request.SearchTerm;
                var cacheKey = searchTerm;

                _logger.LogInformation("Buscando layouts com termo: {SearchTerm}", searchTerm);

                // Tentar buscar no cache primeiro
                var cachedLayouts = await _cacheService.GetCachedLayoutsAsync(cacheKey);
                if (cachedLayouts != null)
                {
                    _logger.LogInformation("Retornando layouts do cache: {Count} layouts", cachedLayouts.Count);
                    return new LayoutSearchResponse
                    {
                        Success = true,
                        Layouts = cachedLayouts,
                        TotalFound = cachedLayouts.Count
                    };
                }

                // Se não estiver no cache, buscar no banco
                _logger.LogInformation("Buscando layouts no banco de dados...");
                var layouts = await _layoutDatabaseService.SearchLayoutsAsync(request);

                if (layouts.Success && layouts.Layouts.Any())
                    await _cacheService.SetCachedLayoutsAsync(cacheKey, layouts.Layouts);

                return layouts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts");
                return new LayoutSearchResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<LayoutRecord?> GetLayoutByIdAsync(int id)
        {
            try
            {
                _logger.LogInformation("Buscando layout por ID: {Id}", id);

                // Tentar buscar no cache primeiro
                var cachedLayout = await _cacheService.GetCachedLayoutByIdAsync(id);
                if (cachedLayout != null)
                {
                    _logger.LogInformation("Retornando layout do cache: {Name}", cachedLayout.Name);
                    return cachedLayout;
                }

                // Se não estiver no cache, buscar no banco
                var layout = await _layoutDatabaseService.GetLayoutByIdAsync(id);
                if (layout != null)
                    await _cacheService.SetCachedLayoutByIdAsync(id, layout);

                return layout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout por ID: {Id}", id);
                return null;
            }
        }

        public async Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid)
        {
            try
            {
                _logger.LogInformation("Buscando layout por GUID: {Guid}", layoutGuid);

                if (string.IsNullOrEmpty(layoutGuid))
                {
                    _logger.LogWarning("GUID do layout é nulo ou vazio");
                    return null;
                }

                // Normalizar GUID (remover prefixos como LAY_ se houver)
                var normalizedGuid = NormalizeLayoutGuid(layoutGuid);

                // Tentar buscar no cache primeiro (chave "all")
                var cachedLayouts = await _cacheService.GetCachedLayoutsAsync("all");
                if (cachedLayouts != null && cachedLayouts.Any())
                {
                    // Buscar layout no cache por GUID
                    var layout = cachedLayouts.FirstOrDefault(l =>
                    {
                        var layoutGuidStr = l.LayoutGuid != Guid.Empty ? l.LayoutGuid.ToString() : "";
                        var normalizedLayoutGuid = NormalizeLayoutGuid(layoutGuidStr);
                        return GuidMatches(normalizedLayoutGuid, normalizedGuid) || GuidMatches(normalizedLayoutGuid, layoutGuid);
                    });

                    if (layout != null)
                    {
                        _logger.LogInformation("Layout encontrado no cache por GUID: {Name} (GUID: {Guid})", layout.Name, layoutGuid);
                        return layout;
                    }
                }

                // Issue #433: SearchLayoutsAsync filtra por [Name] LIKE e ProjectId=2 (busca por nome,
                // nunca por GUID). Aqui vai uma consulta real por LayoutGuid, qualquer tipo/projeto.
                _logger.LogInformation("Layout nao encontrado no cache, buscando no banco por GUID: {Guid}", layoutGuid);
                var dbLayout = await _layoutDatabaseService.GetLayoutByGuidAsync(layoutGuid);
                if (dbLayout != null)
                {
                    _logger.LogInformation("Layout encontrado no banco por GUID: {Name} (GUID: {Guid})", dbLayout.Name, layoutGuid);
                    await _cacheService.SetCachedLayoutByIdAsync(dbLayout.Id, dbLayout);
                    return dbLayout;
                }

                _logger.LogWarning("Layout nao encontrado por GUID: {Guid}", layoutGuid);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout por GUID: {Guid}", layoutGuid);
                return null;
            }
        }

        /// <summary>
        /// Normaliza GUID do layout (remove prefixos como LAY_)
        /// </summary>
        private string NormalizeLayoutGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";

            var normalized = guid.Trim();

            // Remover prefixo LAY_ se houver (case-insensitive)
            if (normalized.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);

            // Remover espaços e converter para minúsculas
            normalized = normalized.Trim().ToLowerInvariant();

            // Remover chaves {} se houver (caso venha de Guid.ToString())
            normalized = normalized.Replace("{", "").Replace("}", "");

            return normalized;
        }

        /// <summary>
        /// Verifica se dois GUIDs correspondem (após normalização)
        /// </summary>
        private bool GuidMatches(string guid1, string guid2)
        {
            if (string.IsNullOrEmpty(guid1) || string.IsNullOrEmpty(guid2))
                return false;

            var norm1 = NormalizeLayoutGuid(guid1);
            var norm2 = NormalizeLayoutGuid(guid2);

            // Comparação exata após normalização
            if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                return true;

            // Comparação parcial (caso um contenha o outro)
            if (norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) || norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase))
            {
                // Verificar se não é apenas uma coincidência parcial muito pequena
                if (norm1.Length >= 8 && norm2.Length >= 8)
                    return true;
            }

            return false;
        }

        public async Task RefreshCacheFromDatabaseAsync()
        {
            try
            {
                _logger.LogInformation("Iniciando atualização do cache a partir do banco de dados");

                // Buscar todos os layouts do banco (sem filtro WHERE)
                var request = new LayoutSearchRequest
                {
                    SearchTerm = "", // String vazia = buscar todos os layouts
                    MaxResults = 1000
                };

                var response = await _layoutDatabaseService.SearchLayoutsAsync(request);

                if (response.Success && response.Layouts.Any())
                {
                    // Salvar no cache
                    await _cacheService.SetCachedLayoutsAsync("all", response.Layouts); // Usar "all" como chave de cache

                    // Salvar layouts individuais no cache
                    foreach (var layout in response.Layouts)
                        await _cacheService.SetCachedLayoutByIdAsync(layout.Id, layout);

                    _logger.LogInformation("Cache atualizado com {Count} layouts do banco de dados", response.Layouts.Count);
                }
                else
                    _logger.LogWarning("Nenhum layout encontrado no banco para atualizar o cache");

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar cache a partir do banco de dados");
            }
        }

        public async Task ClearCacheAsync()
        {
            try
            {
                await _cacheService.ClearCacheAsync();
                _logger.LogInformation("Cache limpo com sucesso");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao limpar cache");
            }
        }

        /// <summary>
        /// Expõe o serviço de banco de dados para uso externo
        /// </summary>
        public ILayoutDatabaseService GetLayoutDatabaseService()
        {
            return _layoutDatabaseService;
        }
    }
}