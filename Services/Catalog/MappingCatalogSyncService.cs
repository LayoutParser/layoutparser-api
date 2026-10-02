using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Logging;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Catalog
{
    public enum CatalogSyncOutcome { Completed, Partial, Failed, Disabled, Locked }

    /// <summary>Resultado do sync de uma origem (sem detalhes de adaptador).</summary>
    public sealed record CatalogSyncResult(SourceSystem SourceSystem, CatalogSyncOutcome Outcome, int Folders, int Items, int Retired, string? Error = null);

    /// <summary>Sync do índice do catálogo (issue #632, design D7).</summary>
    public interface IMappingCatalogSyncService
    {
        /// <summary>Sincroniza as origens HABILITADAS (ou só <paramref name="only"/>). Nunca lança (exceto cancelamento).</summary>
        Task<IReadOnlyList<CatalogSyncResult>> SyncAsync(SourceSystem? only, CancellationToken cancellationToken);
    }

    public sealed class MappingCatalogSyncService : IMappingCatalogSyncService
    {
        private readonly IMappingCatalogStore _store;
        private readonly IEnumerable<IMappingCatalogSource> _sources;
        private readonly IOptionsMonitor<MappingCatalogOptions> _options;
        private readonly ILogger<MappingCatalogSyncService> _logger;

        public MappingCatalogSyncService(IMappingCatalogStore store, IEnumerable<IMappingCatalogSource> sources,
            IOptionsMonitor<MappingCatalogOptions> options, ILogger<MappingCatalogSyncService> logger)
        {
            _store = store;
            _sources = sources;
            _options = options;
            _logger = logger;
        }

        public async Task<IReadOnlyList<CatalogSyncResult>> SyncAsync(SourceSystem? only, CancellationToken cancellationToken)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            CorrelationContext.CurrentId = correlationId;
            using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

            var results = new List<CatalogSyncResult>();
            foreach (var source in _sources.Where(s => only == null || s.System == only))
            {
                if (!_options.CurrentValue.IsEnabled(source.System))
                {
                    _logger.LogInformation("Sync do catálogo: origem {SourceSystem} desligada na configuração; ignorando. CorrelationId={CorrelationId}", source.System.ToWireName(), correlationId);
                    results.Add(new CatalogSyncResult(source.System, CatalogSyncOutcome.Disabled, 0, 0, 0));
                    continue;
                }
                results.Add(await SyncSourceAsync(source, correlationId, cancellationToken));
            }
            return results;
        }

        private async Task<CatalogSyncResult> SyncSourceAsync(IMappingCatalogSource source, string correlationId, CancellationToken ct)
        {
            var system = source.System;
            var wire = system.ToWireName();
            try
            {
                await using var syncLock = await _store.TryAcquireSyncLockAsync(system, ct);
                if (syncLock == null)
                {
                    _logger.LogInformation("Sync do catálogo {SourceSystem} não executou (lock ocupado ou índice indisponível). CorrelationId={CorrelationId}", wire, correlationId);
                    return new CatalogSyncResult(system, CatalogSyncOutcome.Locked, 0, 0, 0);
                }

                // Corte do RetireUnseen no relógio do SERVIDOR SQL (o mesmo de LastSeenUtc): relógio da API
                // adiantado em relação ao SQL retiraria itens recém-vistos.
                var cutoff = await _store.GetServerUtcNowAsync(ct);
                if (cutoff == null)
                    return new CatalogSyncResult(system, CatalogSyncOutcome.Failed, 0, 0, 0, "IdentityDatabase indisponível");

                var previous = (await _store.GetSourceAsync(system, ct)).Value;
                _logger.LogInformation("Sync do catálogo {SourceSystem} iniciado. CorrelationId={CorrelationId}", wire, correlationId);

                MappingCatalogSnapshot snapshot;
                try
                {
                    snapshot = await source.ReadAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Sync do catálogo {SourceSystem}: leitura da origem falhou; nada será retirado. CorrelationId={CorrelationId}", wire, correlationId);
                    await MarkFailureAsync(system, previous, ex.GetType().Name, ct);
                    return new CatalogSyncResult(system, CatalogSyncOutcome.Failed, 0, 0, 0, ex.GetType().Name);
                }

                var failures = 0;
                foreach (var folder in snapshot.Folders)
                    if (!await _store.UpsertFolderAsync(folder, ct)) failures++;
                foreach (var item in snapshot.Items)
                    if (!await _store.UpsertItemAsync(item, ct)) failures++;

                // Só um sync COMPLETO, sem nenhuma falha de gravação e com ao menos 1 item pode retirar.
                var complete = snapshot.Complete && failures == 0 && snapshot.Items.Count > 0;
                var retired = complete ? await _store.RetireUnseenAsync(system, cutoff.Value, ct) : 0;

                if (snapshot.Complete && failures == 0)
                {
                    await _store.UpsertSourceAsync(new MappingCatalogSourceDto(system, true, DateTime.UtcNow, MappingCatalogSourceStatus.Ok, null), ct);
                    _logger.LogInformation("Sync do catálogo {SourceSystem} concluído: {Folders} pastas, {Items} itens, {Retired} retirados. CorrelationId={CorrelationId}",
                        wire, snapshot.Folders.Count, snapshot.Items.Count, retired, correlationId);
                    return new CatalogSyncResult(system, CatalogSyncOutcome.Completed, snapshot.Folders.Count, snapshot.Items.Count, retired);
                }

                var reason = failures > 0 ? $"{failures} falha(s) de gravação no índice" : "leitura incompleta da origem";
                await MarkFailureAsync(system, previous, "sync parcial: " + reason, ct);
                _logger.LogWarning("Sync do catálogo {SourceSystem} PARCIAL ({Reason}); nada foi retirado. CorrelationId={CorrelationId}", wire, reason, correlationId);
                return new CatalogSyncResult(system, CatalogSyncOutcome.Partial, snapshot.Folders.Count, snapshot.Items.Count, 0, reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Rede de segurança: sync jamais derruba a API nem o loop do BackgroundService.
                _logger.LogError(ex, "Sync do catálogo {SourceSystem} falhou inesperadamente. CorrelationId={CorrelationId}", wire, correlationId);
                return new CatalogSyncResult(system, CatalogSyncOutcome.Failed, 0, 0, 0, ex.GetType().Name);
            }
        }

        /// <summary>Com sync anterior bem-sucedido o dado antigo segue servido (<c>stale</c>); sem ele, <c>unavailable</c>.</summary>
        private Task<bool> MarkFailureAsync(SourceSystem system, MappingCatalogSourceDto? previous, string error, CancellationToken ct)
            => _store.UpsertSourceAsync(new MappingCatalogSourceDto(system, true, previous?.LastSyncUtc,
                previous?.LastSyncUtc != null ? MappingCatalogSourceStatus.Stale : MappingCatalogSourceStatus.Unavailable, error), ct);
    }
}
