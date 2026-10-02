using LayoutParserApi.Services.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Orquestra o gatilho de retraining (F4.2/F4.3, issue #351): mantém o contador de exemplos
    /// novos alimentado por F3, avalia o gatilho (volume OU teto de agenda), escreve o
    /// arquivo-marcador que o cron da VM observa, e detecta a conclusão do treino (marcador
    /// removido + lock livre) pra zerar o contador.
    ///
    /// <para>Singleton — estado compartilhado (contador) entre o hook de F3 (escopo de request) e
    /// o <see cref="RetrainingSchedulerBackgroundService"/>. Toda a durabilidade fica no
    /// <see cref="IRetrainingStateStore"/> (arquivo). Best-effort ponta a ponta: nada aqui pode
    /// derrubar a síntese nem o host.</para>
    /// </summary>
    public interface IRetrainingCoordinator
    {
        /// <summary>Chamado por F3 quando uma convergência real é gravada no JSONL incremental.</summary>
        void RegisterCapturedExample();

        /// <summary>Avalia o gatilho e, se for o caso, escreve o marcador de disparo. Também detecta
        /// a conclusão de um treino anterior. Idempotente por tick.</summary>
        Task EvaluateAndMaybeTriggerAsync(CancellationToken cancellationToken);

        /// <summary>Snapshot do estado atual (leitura), pra observabilidade/testes.</summary>
        RetrainingState PeekState();
    }

    public class RetrainingCoordinator : IRetrainingCoordinator
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly ILogger<RetrainingCoordinator> _logger;
        private readonly IRetrainingStateStore _stateStore;
        private readonly IRetrainingLock _lock;
        private readonly RetrainingOptions _options;
        private readonly string _triggerFilePath;

        public RetrainingCoordinator(
            ILogger<RetrainingCoordinator> logger,
            IRetrainingStateStore stateStore,
            IRetrainingLock retrainingLock,
            IOptions<RetrainingOptions> options,
            IConfiguration configuration)
        {
            _logger = logger;
            _stateStore = stateStore;
            _lock = retrainingLock;
            _options = options.Value;

            var trainingDataPath = new StoragePaths(configuration).AiTrainingData;

            _triggerFilePath = !string.IsNullOrWhiteSpace(_options.TriggerFilePath)
                ? _options.TriggerFilePath!
                : Path.Combine(trainingDataPath, "retraining.trigger.json");
        }

        public void RegisterCapturedExample()
        {
            try
            {
                var state = _stateStore.Update(s => s.ExamplesSinceLastTraining++);
                _logger.LogDebug(
                    "Contador de retraining incrementado: {Contador} exemplo(s) novo(s) desde o último treino",
                    state.ExamplesSinceLastTraining);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao incrementar o contador de retraining (best-effort)");
            }
        }

        public RetrainingState PeekState()
        {
            try
            {
                return _stateStore.Load();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler o estado de retraining");
                return new RetrainingState { CreatedUtc = DateTime.UtcNow };
            }
        }

        public Task EvaluateAndMaybeTriggerAsync(CancellationToken cancellationToken)
        {
            try
            {
                var state = _stateStore.Load();
                var lockHeld = _lock.IsHeld();
                var triggerFileExists = File.Exists(_triggerFilePath);

                // 1. Detecção de conclusão: havia um disparo pendente, o marcador já foi removido
                //    pelo script da VM e o lock está livre → o treino terminou.
                if (state.TriggerPending && !triggerFileExists && !lockHeld)
                {
                    var completed = _stateStore.Update(s =>
                    {
                        // Só o tanto contado no disparo é zerado — o que F3 capturou durante o
                        // treino de ~40h fica pro próximo ciclo.
                        s.ExamplesSinceLastTraining = Math.Max(0, s.ExamplesSinceLastTraining - s.ExamplesCountedAtTrigger);
                        s.ExamplesCountedAtTrigger = 0;
                        s.TriggerPending = false;
                        s.LastTrainingCompletedUtc = DateTime.UtcNow;
                    });
                    _logger.LogInformation(
                        "Retraining detectado como concluído (marcador removido, lock livre). " +
                        "Contador reiniciado para {Contador} exemplo(s) acumulado(s) durante o treino.",
                        completed.ExamplesSinceLastTraining);
                    return Task.CompletedTask;
                }

                // 2. Enquanto o treino roda, não faz mais nada.
                if (lockHeld || state.TriggerPending)
                {
                    _logger.LogDebug(
                        "Avaliação de gatilho de retraining pulada: lockHeld={LockHeld}, triggerPending={Pending}",
                        lockHeld, state.TriggerPending);
                    return Task.CompletedTask;
                }

                // 3. Avalia o gatilho.
                var decision = RetrainingTriggerEvaluator.Evaluate(state, _options, DateTime.UtcNow);
                if (!decision.ShouldTrigger)
                {
                    _logger.LogDebug("Gatilho de retraining não disparou: {Explicacao}", decision.Explanation);
                    return Task.CompletedTask;
                }

                if (!_options.Enabled)
                {
                    _logger.LogInformation(
                        "Gatilho de retraining SATISFEITO ({Motivo}: {Explicacao}), mas XslSynth:Retraining:Enabled=false — " +
                        "marcador de disparo NÃO escrito. Fiação do lado da VM (cron/lock) é pré-requisito.",
                        decision.Reason, decision.Explanation);
                    return Task.CompletedTask;
                }

                WriteTriggerMarker(state, decision);
                var updated = _stateStore.Update(s =>
                {
                    s.TriggerPending = true;
                    s.LastTriggeredUtc = DateTime.UtcNow;
                    s.ExamplesCountedAtTrigger = s.ExamplesSinceLastTraining;
                    s.LastTriggerReason = decision.Reason.ToString();
                });

                _logger.LogWarning(
                    "Retraining DISPARADO ({Motivo}): {Explicacao}. Marcador escrito em {Path}. " +
                    "{Contador} exemplo(s) contabilizado(s) no disparo.",
                    decision.Reason, decision.Explanation, _triggerFilePath, updated.ExamplesCountedAtTrigger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha na avaliação do gatilho de retraining (best-effort, não afeta a síntese)");
            }

            return Task.CompletedTask;
        }

        private void WriteTriggerMarker(RetrainingState state, RetrainingTriggerDecision decision)
        {
            var threshold = _options.NewExampleThreshold > 0
                ? _options.NewExampleThreshold
                : RetrainingOptions.DefaultNewExampleThreshold;

            var request = new RetrainingTriggerRequest
            {
                RequestedUtc = DateTime.UtcNow.ToString("o"),
                Reason = decision.Reason.ToString(),
                Explanation = decision.Explanation,
                ExamplesSinceLastTraining = state.ExamplesSinceLastTraining,
                Threshold = threshold,
                LastTrainingCompletedUtc = state.LastTrainingCompletedUtc?.ToString("o"),
            };

            var dir = Path.GetDirectoryName(_triggerFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = _triggerFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(request, JsonOptions));
            File.Move(tmp, _triggerFilePath, overwrite: true);
        }
    }
}
