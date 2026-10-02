using LayoutParserApi.Services.Storage;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Exclusão mútua Ollama-inferência × treino LoRA (F4.3, issue #351, ADR §6.2). O treino de
    /// ~40h em CPU na mesma VM que serve o Ollama de produção degrada toda convergência em runtime
    /// pela duração inteira — então enquanto o treino roda, a síntese via Ollama é suspensa.
    /// </summary>
    public interface IRetrainingLock
    {
        /// <summary><c>true</c> se o <c>retraining.lock</c> existe na VM (treino em andamento).</summary>
        bool IsHeld();

        /// <summary>Caminho do arquivo de lock, pra logs/diagnóstico e pro script da VM.</summary>
        string LockFilePath { get; }
    }

    /// <summary>
    /// Implementação por arquivo. A API só LÊ o lock — quem cria/remove é o script de treino da VM
    /// (handoff <c>@lp-devops</c>). Checa idade do arquivo pra logar suspeita de lock órfão sem
    /// removê-lo (remoção é decisão operacional, não automática).
    /// </summary>
    public class FileRetrainingLock : IRetrainingLock
    {
        private readonly ILogger<FileRetrainingLock> _logger;
        private readonly int _staleAfterHours;

        public FileRetrainingLock(
            ILogger<FileRetrainingLock> logger,
            IOptions<RetrainingOptions> options,
            IConfiguration configuration)
        {
            _logger = logger;

            var trainingDataPath = new StoragePaths(configuration).AiTrainingData;

            LockFilePath = !string.IsNullOrWhiteSpace(options.Value.LockFilePath)
                ? options.Value.LockFilePath!
                : Path.Combine(trainingDataPath, "retraining.lock");

            _staleAfterHours = options.Value.StaleLockAfterHours;
        }

        public string LockFilePath { get; }

        public bool IsHeld()
        {
            try
            {
                if (!File.Exists(LockFilePath))
                    return false;

                if (_staleAfterHours > 0)
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(LockFilePath);
                    if (age > TimeSpan.FromHours(_staleAfterHours))
                        _logger.LogWarning(
                            "retraining.lock em {Path} tem {Horas:F1}h — acima do teto de {Teto}h. " +
                            "Suspeita de lock órfão (treino morto sem limpar). Não removido automaticamente.",
                            LockFilePath, age.TotalHours, _staleAfterHours);
                }

                return true;
            }
            catch (Exception ex)
            {
                // Se não dá pra checar o lock, o lado seguro é assumir que NÃO está travado —
                // travar a síntese por um erro de I/O na checagem seria pior que o risco de
                // concorrência (que só existe se um treino estiver de fato rodando).
                _logger.LogWarning(ex, "Falha ao checar o retraining.lock em {Path} — assumindo não travado", LockFilePath);
                return false;
            }
        }
    }
}
