using LayoutParserApi.Models.Catalog;
using LayoutParserApi.Services.Catalog;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Catalog
{
    public class MappingCatalogSyncServiceTests
    {
        private sealed class FakeSource : IMappingCatalogSource
        {
            public SourceSystem System => SourceSystem.Neogrid;
            public int Reads;
            public Func<MappingCatalogSnapshot> Snapshot = () => throw new InvalidOperationException();
            public Task<MappingCatalogSnapshot> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Snapshot()); }
            public Task<MappingContent?> GetContentAsync(MappingCatalogSourceRef r, CancellationToken ct) => Task.FromResult<MappingContent?>(null);
        }

        private static readonly Guid Folder = CatalogIdGenerator.ForFolder(SourceSystem.Neogrid, "nfe");

        private static MappingCatalogItemDto Item(string n)
            => new(CatalogIdGenerator.ForItem(SourceSystem.Neogrid, "nfe", n), Folder, SourceSystem.Neogrid, n, "tcl", n, null, null, null, null, false);

        private static MappingCatalogSnapshot Snap(bool complete, int items = 2)
            => new(complete, [new MappingCatalogFolderDto(Folder, SourceSystem.Neogrid, "nfe", null, "NFe", false)],
                Enumerable.Range(0, items).Select(i => Item("i" + i)).ToList());

        private static (MappingCatalogSyncService Sync, FakeMappingCatalogStore Store, FakeSource Src) Build(bool enabled = true)
        {
            var store = new FakeMappingCatalogStore();
            var src = new FakeSource();
            var opts = new MappingCatalogOptions();
            opts.Sources["Neogrid"] = new MappingCatalogSourceToggle { Enabled = enabled };
            return (new MappingCatalogSyncService(store, [src], new TestOptionsMonitor<MappingCatalogOptions>(opts), NullLogger<MappingCatalogSyncService>.Instance), store, src);
        }

        [Fact]
        public async Task Sync_completo_grava_retira_com_corte_do_servidor_sql_e_marca_ok()
        {
            var (sync, store, src) = Build();
            src.Snapshot = () => Snap(true);

            var r = Assert.Single(await sync.SyncAsync(null, default));

            Assert.Equal(CatalogSyncOutcome.Completed, r.Outcome);
            Assert.Equal(2, store.Items.Count);
            var call = Assert.Single(store.RetireCalls);
            Assert.Equal(store.ServerNow, call.Before); // relógio do SQL, não da API
            Assert.Equal("ok", store.Sources[SourceSystem.Neogrid].Status);
            Assert.Equal((1, 1), (store.LocksTaken, store.LocksReleased));
        }

        [Fact]
        public async Task Leitura_que_lanca_nao_retira_nada_e_vira_unavailable_sem_sync_previo()
        {
            var (sync, store, _) = Build();
            var r = Assert.Single(await sync.SyncAsync(null, default));
            Assert.Equal(CatalogSyncOutcome.Failed, r.Outcome);
            Assert.Empty(store.RetireCalls);
            Assert.Equal("unavailable", store.Sources[SourceSystem.Neogrid].Status);
            Assert.Equal(1, store.LocksReleased);
        }

        [Fact]
        public async Task Falha_com_sync_previo_vira_stale_e_preserva_lastSync()
        {
            var (sync, store, src) = Build();
            var prev = DateTime.UtcNow.AddHours(-3);
            store.Sources[SourceSystem.Neogrid] = new MappingCatalogSourceDto(SourceSystem.Neogrid, true, prev, "ok", null);
            await sync.SyncAsync(null, default);
            Assert.Equal("stale", store.Sources[SourceSystem.Neogrid].Status);
            Assert.Equal(prev, store.Sources[SourceSystem.Neogrid].LastSyncUtc);
        }

        [Fact]
        public async Task Snapshot_incompleto_grava_mas_nao_retira()
        {
            var (sync, store, src) = Build();
            src.Snapshot = () => Snap(false);
            var r = Assert.Single(await sync.SyncAsync(null, default));
            Assert.Equal(CatalogSyncOutcome.Partial, r.Outcome);
            Assert.Empty(store.RetireCalls);
            Assert.Equal(2, store.Items.Count); // o que foi lido fica gravado
            Assert.Equal("unavailable", store.Sources[SourceSystem.Neogrid].Status); // sem sync completo anterior
        }

        [Fact]
        public async Task Falha_de_gravacao_de_um_item_impede_o_retire()
        {
            var (sync, store, src) = Build();
            src.Snapshot = () => Snap(true);
            store.FailItemUpserts.Add(Item("i1").CatalogId);
            var r = Assert.Single(await sync.SyncAsync(null, default));
            Assert.Equal(CatalogSyncOutcome.Partial, r.Outcome);
            Assert.Empty(store.RetireCalls);
        }

        [Fact]
        public async Task Origem_vazia_nao_retira_catalogo_inteiro()
        {
            var (sync, store, src) = Build();
            src.Snapshot = () => Snap(true, items: 0);
            await sync.SyncAsync(null, default);
            Assert.Empty(store.RetireCalls);
        }

        [Fact]
        public async Task Flag_desligada_nao_le_nem_trava_nada()
        {
            var (sync, store, src) = Build(enabled: false);
            var r = Assert.Single(await sync.SyncAsync(null, default));
            Assert.Equal(CatalogSyncOutcome.Disabled, r.Outcome);
            Assert.Equal((0, 0), (src.Reads, store.LocksTaken));
        }

        [Fact]
        public async Task Lock_ocupado_pula_sem_ler_a_origem()
        {
            var (sync, store, src) = Build();
            store.LockBusy = true;
            var r = Assert.Single(await sync.SyncAsync(null, default));
            Assert.Equal(CatalogSyncOutcome.Locked, r.Outcome);
            Assert.Equal(0, src.Reads);
        }

        [Fact]
        public async Task Indice_fora_do_ar_nao_lanca()
        {
            var (sync, store, src) = Build();
            store.Down = true;
            src.Snapshot = () => Snap(true);
            var r = Assert.Single(await sync.SyncAsync(null, default));
            Assert.NotEqual(CatalogSyncOutcome.Completed, r.Outcome);
            Assert.Empty(store.RetireCalls);
        }

        [Fact]
        public void Trigger_respeita_flag_por_adaptador()
        {
            var opts = new MappingCatalogOptions();
            opts.Sources["Neogrid"] = new MappingCatalogSourceToggle { Enabled = true };
            var bg = new MappingCatalogSyncBackgroundService(null!, new TestOptionsMonitor<MappingCatalogOptions>(opts), NullLogger<MappingCatalogSyncBackgroundService>.Instance);
            Assert.Equal(CatalogSyncTriggerResult.Accepted, bg.Trigger(SourceSystem.Neogrid));
            Assert.Equal(CatalogSyncTriggerResult.Disabled, bg.Trigger(SourceSystem.Own));
            Assert.Equal(CatalogSyncTriggerResult.Accepted, bg.Trigger(null));
        }
    }
}
