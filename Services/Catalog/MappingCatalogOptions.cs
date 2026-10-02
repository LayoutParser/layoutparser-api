using LayoutParserApi.Models.Catalog;

namespace LayoutParserApi.Services.Catalog
{
    /// <summary>Liga/desliga o SYNC de um adaptador (<c>MappingCatalog:Sources:{Neogrid|Own|ConnectUs}:Enabled</c>).</summary>
    public sealed class MappingCatalogSourceToggle
    {
        /// <summary>Default <c>false</c>: nada é sincronizado sem o dono ligar explicitamente (produção).</summary>
        public bool Enabled { get; set; }
    }

    /// <summary>Seção <c>MappingCatalog</c> do appsettings (issues #631/#632).</summary>
    public sealed class MappingCatalogOptions
    {
        public const int DefaultIntervalHours = 6;
        public const int DefaultInitialDelaySeconds = 120;

        public Dictionary<string, MappingCatalogSourceToggle> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public MappingCatalogSyncOptions Sync { get; set; } = new();

        public bool IsEnabled(SourceSystem system)
            => Sources.TryGetValue(system.ToString(), out var t) && t.Enabled;
    }

    public sealed class MappingCatalogSyncOptions
    {
        /// <summary>Intervalo entre syncs periódicos (horas); &lt;=0 usa o default de 6h.</summary>
        public int IntervalHours { get; set; } = MappingCatalogOptions.DefaultIntervalHours;

        /// <summary>Atraso inicial após o startup (segundos) — não compete com o boot.</summary>
        public int InitialDelaySeconds { get; set; } = MappingCatalogOptions.DefaultInitialDelaySeconds;
    }
}
