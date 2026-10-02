using System.Collections.Concurrent;

using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Dispara em background a criação do mapeador (TCL/XSL/XSLT) que faltou quando o pathway tcl-xsl
    /// caiu em <c>map_not_found</c>/<c>xsl_not_found</c> (issue #642). Reaproveita
    /// <see cref="IGeneratedMapperArtifactService.GetOrTriggerAsync"/> — que já faz o lock por mapper
    /// (<c>TryBeginGeneratingAsync</c>), o hash/stale e a síntese em background. Este serviço só resolve
    /// layout → mapper e aplica um cooldown por layout para a rajada de requisições do mesmo layout não
    /// ir ao SQL do catálogo a cada chamada.
    /// </summary>
    public interface IMissingMapperGenerationTrigger
    {
        /// <summary>Fire-and-forget. NUNCA lança e NUNCA bloqueia o chamador.</summary>
        void TriggerForLayout(string? layoutName, string? errorCode);
    }

    public sealed class MissingMapperGenerationTrigger : IMissingMapperGenerationTrigger
    {
        /// <summary>Janela mínima entre dois disparos para o MESMO layout (idempotência barata, em memória).</summary>
        public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
        private const int MaxTrackedLayouts = 2000;
        /// <summary>Default conservador do cap global (config <c>Ai:MissingMapperTrigger:MaxPerMinute</c>).</summary>
        public const int DefaultMaxPerMinute = 10;

        private readonly int _maxPerMinute;
        private readonly object _capLock = new();
        private readonly Queue<DateTime> _recentTriggers = new();
        private int _droppedSinceLog;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MissingMapperGenerationTrigger> _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly ConcurrentDictionary<string, DateTime> _lastTriggerUtc = new(StringComparer.OrdinalIgnoreCase);

        public MissingMapperGenerationTrigger(
            IServiceScopeFactory scopeFactory,
            ILogger<MissingMapperGenerationTrigger> logger,
            Func<DateTime>? utcNow = null,
            IConfiguration? configuration = null)
        {
            var cfg = configuration?.GetValue<int?>("Ai:MissingMapperTrigger:MaxPerMinute");
            _maxPerMinute = cfg is > 0 ? cfg.Value : DefaultMaxPerMinute;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public void TriggerForLayout(string? layoutName, string? errorCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(layoutName)) return;
                layoutName = layoutName.Trim();
                if (!ShouldTrigger(layoutName) || !TryAcquireGlobalSlot())
                    return;

                var safeLayout = Logging.LogMessageSanitizer.Sanitize(layoutName);
                _logger.LogInformation(
                    "Mapeador ausente ({ErrorCode}) para o layout {LayoutName}: disparando criação automática em background.",
                    errorCode, safeLayout);

                _ = Task.Run(() => ResolveAndTriggerAsync(layoutName, safeLayout));
            }
            catch (Exception ex)
            {
                // Nunca quebra a resposta ao usuário (dotnet-standards.md).
                _logger.LogWarning(ex, "Falha ao agendar criação automática do mapeador ausente.");
            }
        }

        /// <summary>Cap global por janela de 1 min; excedentes são descartados (log limitado).</summary>
        private bool TryAcquireGlobalSlot()
        {
            var now = _utcNow();
            lock (_capLock)
            {
                while (_recentTriggers.Count > 0 && now - _recentTriggers.Peek() >= TimeSpan.FromMinutes(1))
                    _recentTriggers.Dequeue();
                if (_recentTriggers.Count >= _maxPerMinute)
                {
                    if (++_droppedSinceLog == 1 || _droppedSinceLog % 100 == 0)
                        _logger.LogWarning("Cap global de criação automática atingido ({Max}/min): disparo descartado ({Dropped} descartados desde o último aviso).",
                            _maxPerMinute, _droppedSinceLog);
                    return false;
                }
                _recentTriggers.Enqueue(now);
                _droppedSinceLog = 0;
                return true;
            }
        }

        /// <summary>true = este chamador "venceu" a janela de cooldown do layout (e a reivindicou).</summary>
        private bool ShouldTrigger(string layoutName)
        {
            var now = _utcNow();
            if (_lastTriggerUtc.Count > MaxTrackedLayouts)
            {
                foreach (var kv in _lastTriggerUtc.Where(kv => now - kv.Value > Cooldown).ToList())
                    _lastTriggerUtc.TryRemove(kv.Key, out _);
                // Ainda acima do teto: descarta os mais antigos (memória limitada).
                var excess = _lastTriggerUtc.Count - MaxTrackedLayouts;
                if (excess > 0)
                    foreach (var kv in _lastTriggerUtc.OrderBy(kv => kv.Value).Take(excess).ToList())
                        _lastTriggerUtc.TryRemove(kv.Key, out _);
            }

            var claimed = false;
            _lastTriggerUtc.AddOrUpdate(
                layoutName,
                _ => { claimed = true; return now; },
                (_, last) =>
                {
                    if (now - last < Cooldown) return last;
                    claimed = true;
                    return now;
                });
            return claimed;
        }

        private async Task ResolveAndTriggerAsync(string layoutName, string safeLayout)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var layoutDb = sp.GetRequiredService<ILayoutDatabaseService>();
                var mapperDb = sp.GetRequiredService<MapperDatabaseService>();
                var opts = sp.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value;
                var artifacts = sp.GetRequiredService<IGeneratedMapperArtifactService>();

                var search = await layoutDb.SearchLayoutsAsync(new global::LayoutParserApi.Models.Database.LayoutSearchRequest { SearchTerm = layoutName });
                var layout = search.Success
                    ? search.Layouts.FirstOrDefault(l => string.Equals(l.Name, layoutName, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (layout is null || layout.LayoutGuid == Guid.Empty)
                {
                    _logger.LogWarning("Criação automática: layout {LayoutName} não encontrado no catálogo — nada a gerar.", safeLayout);
                    return;
                }

                var ranked = await mapperDb.GetRankedMapperCandidatesForLayoutGuidAsync(
                    layout.LayoutGuid.ToString(), opts.ProjectId, opts.AllowedPackageGuids);
                var mapper = ranked.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.DecryptedContent));
                if (mapper is null)
                {
                    _logger.LogWarning("Criação automática: nenhum mapeador decifrável para o layout {LayoutName} — nada a gerar.", safeLayout);
                    return;
                }

                var response = await artifacts.GetOrTriggerAsync(mapper.MapperGuid, $"missing-{Guid.NewGuid():N}", CancellationToken.None);
                _logger.LogInformation(
                    "Criação automática para o layout {LayoutName}: mapper {MapperGuid} status={Status}.",
                    safeLayout, Logging.LogMessageSanitizer.Sanitize(mapper.MapperGuid), response?.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao disparar criação automática do mapeador para o layout {LayoutName}.", safeLayout);
            }
        }
    }
}
