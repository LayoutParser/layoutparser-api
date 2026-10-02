using LayoutParserApi.Services.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>Leitura/escrita do <see cref="RetrainingState"/> durável.</summary>
    public interface IRetrainingStateStore
    {
        /// <summary>Carrega o estado; se o arquivo não existir ou estiver corrompido, devolve um
        /// estado novo já com <see cref="RetrainingState.CreatedUtc"/> setado (e persiste).</summary>
        RetrainingState Load();

        /// <summary>Persiste o estado de forma atômica (escreve em arquivo temporário e move).</summary>
        void Save(RetrainingState state);

        /// <summary>Executa <paramref name="mutate"/> sobre o estado sob lock de processo e persiste
        /// o resultado. Devolve o estado já mutado.</summary>
        RetrainingState Update(Action<RetrainingState> mutate);
    }

    /// <summary>
    /// Implementação file-based do estado de retraining (F4.2, issue #351). Segue o mesmo padrão
    /// de persistência de F3 (arquivo na árvore de <c>XslSynth:TrainingDataPath</c>) — nada de SQL
    /// compartilhado. Lock só de processo (a API roda como instância única na VM; concorrência real
    /// aqui é entre o background service e o hook de F3 no mesmo processo).
    /// </summary>
    public class FileRetrainingStateStore : IRetrainingStateStore
    {
        private static readonly object Gate = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly ILogger<FileRetrainingStateStore> _logger;
        private readonly string _stateFilePath;

        public FileRetrainingStateStore(
            ILogger<FileRetrainingStateStore> logger,
            IOptions<RetrainingOptions> options,
            IConfiguration configuration)
        {
            _logger = logger;

            var trainingDataPath = new StoragePaths(configuration).AiTrainingData;

            _stateFilePath = !string.IsNullOrWhiteSpace(options.Value.StateFilePath)
                ? options.Value.StateFilePath!
                : Path.Combine(trainingDataPath, "retraining-state.json");
        }

        public RetrainingState Load()
        {
            lock (Gate)
            {
                return LoadUnlocked();
            }
        }

        public void Save(RetrainingState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            lock (Gate)
            {
                SaveUnlocked(state);
            }
        }

        public RetrainingState Update(Action<RetrainingState> mutate)
        {
            ArgumentNullException.ThrowIfNull(mutate);
            lock (Gate)
            {
                var state = LoadUnlocked();
                mutate(state);
                SaveUnlocked(state);
                return state;
            }
        }

        private RetrainingState LoadUnlocked()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath);
                    var loaded = JsonSerializer.Deserialize<RetrainingState>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        if (loaded.CreatedUtc == default)
                            loaded.CreatedUtc = DateTime.UtcNow;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Estado de retraining ilegível em {Path} — recomeçando de um estado novo (contador zerado)",
                    _stateFilePath);
            }

            var fresh = new RetrainingState { CreatedUtc = DateTime.UtcNow };
            SaveUnlocked(fresh);
            return fresh;
        }

        private void SaveUnlocked(RetrainingState state)
        {
            try
            {
                var dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var tmp = _stateFilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(tmp, _stateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // Best-effort: persistência de estado de retraining não pode derrubar o request de
                // síntese nem o background service (dotnet-standards.md §Resiliência).
                _logger.LogWarning(ex, "Falha ao persistir o estado de retraining em {Path}", _stateFilePath);
            }
        }
    }
}
