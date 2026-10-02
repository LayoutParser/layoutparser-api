using System.Threading.Channels;

using LayoutParserApi.Models.Catalog;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Catalog
{
    public enum CatalogSyncTriggerResult { Accepted, Disabled, QueueFull }

    /// <summary>Gatilho manual do sync (POST api/mapping-catalog/sync): enfileira e devolve na hora.</summary>
    public interface IMappingCatalogSyncTrigger
    {
        CatalogSyncTriggerResult Trigger(SourceSystem? only);
    }

    /// <summary>
    /// Sync periódico do catálogo (issue #632): intervalo configurável (default 6h) + gatilho manual via
    /// fila. Cada execução usa um scope novo; o sync por origem só roda com o flag ligado
    /// (<c>MappingCatalog:Sources:{Origem}:Enabled</c>, default false) e sob <c>sp_getapplock</c>.
    /// Try/catch total: nada aqui derruba a API.
    /// </summary>
    public sealed class MappingCatalogSyncBackgroundService : BackgroundService, IMappingCatalogSyncTrigger
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<MappingCatalogOptions> _options;
        private readonly ILogger<MappingCatalogSyncBackgroundService> _logger;
        private readonly Channel<SourceSystem?> _queue = Channel.CreateBounded<SourceSystem?>(
            new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

        public MappingCatalogSyncBackgroundService(IServiceScopeFactory scopeFactory,
            IOptionsMonitor<MappingCatalogOptions> options, ILogger<MappingCatalogSyncBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        public CatalogSyncTriggerResult Trigger(SourceSystem? only)
        {
            var opts = _options.CurrentValue;
            var anyEnabled = only == null
                ? System.Enum.GetValues<SourceSystem>().Any(opts.IsEnabled)
                : opts.IsEnabled(only.Value);
            if (!anyEnabled)
                return CatalogSyncTriggerResult.Disabled;
            return _queue.Writer.TryWrite(only) ? CatalogSyncTriggerResult.Accepted : CatalogSyncTriggerResult.QueueFull;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var sync = _options.CurrentValue.Sync;
                var delay = TimeSpan.FromSeconds(sync.InitialDelaySeconds > 0 ? sync.InitialDelaySeconds : MappingCatalogOptions.DefaultInitialDelaySeconds);
                await Task.Delay(delay, stoppingToken); // não compete com o boot

                var pending = (SourceSystem?)null;
                var run = true; // primeira rodada periódica logo após o delay inicial
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (run)
                        await RunAsync(pending, stoppingToken);

                    var hours = _options.CurrentValue.Sync.IntervalHours > 0 ? _options.CurrentValue.Sync.IntervalHours : MappingCatalogOptions.DefaultIntervalHours;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromHours(hours));
                    try
                    {
                        pending = await _queue.Reader.ReadAsync(cts.Token); // gatilho manual
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        pending = null; // estourou o intervalo: rodada periódica de todas as origens habilitadas
                    }
                    run = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // desligamento normal
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MappingCatalogSyncBackgroundService encerrou por erro inesperado; o sync periódico não rodará mais até o restart.");
            }
        }

        private async Task RunAsync(SourceSystem? only, CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IMappingCatalogSyncService>();
                await sync.SyncAsync(only, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rodada de sync do catálogo falhou; próxima tentativa no próximo gatilho/intervalo.");
            }
        }
    }
}
