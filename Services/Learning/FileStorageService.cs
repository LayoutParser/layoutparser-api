using LayoutParserApi.Services.Storage;
using LayoutParserApi.Models.Learning;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Serviço para gerenciar armazenamento de arquivos e diretórios
    /// </summary>
    public class FileStorageService
    {
        private readonly ILogger<FileStorageService> _logger;
        private readonly string _basePath;

        public FileStorageService(IConfiguration configuration, ILogger<FileStorageService> logger, StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _basePath = (storagePaths ?? new StoragePaths(configuration)).Examples;

            // Garantir que o diretório base existe
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
                _logger.LogInformation("Diretório base criado: {Path}", _basePath);
            }
        }

        /// <summary>
        /// Salva modelo aprendido em JSON
        /// </summary>
        public async Task<bool> SaveLearnedModelAsync(string fileDirectory, LayoutModel model)
        {
            try
            {
                var modelPath = Path.Combine(fileDirectory, "layout_learned.json");
                var json = System.Text.Json.JsonSerializer.Serialize(model, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                await File.WriteAllTextAsync(modelPath, json);
                _logger.LogInformation("Modelo aprendido salvo: {Path}", modelPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar modelo aprendido");
                return false;
            }
        }

        /// <summary>
        /// Carrega modelo aprendido de JSON
        /// </summary>
        public async Task<LayoutModel> LoadLearnedModelAsync(string fileDirectory)
        {
            try
            {
                var modelPath = Path.Combine(fileDirectory, "layout_learned.json");
                if (!File.Exists(modelPath))
                    return null;

                var json = await File.ReadAllTextAsync(modelPath);
                var model = System.Text.Json.JsonSerializer.Deserialize<LayoutModel>(json);
                return model;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar modelo aprendido");
                return null;
            }
        }
    }
}