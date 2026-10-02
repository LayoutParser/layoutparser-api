using System.Text.Json.Serialization;

namespace LayoutParserApi.Models.Catalog
{
    /// <summary>
    /// Sistema de origem de um item do catálogo unificado de mapeadores (issue #627, desenho em
    /// <c>docs/architecture/mapping-catalog-design.md</c> D2/D5). Serializado em JSON como snake_case
    /// (<c>connect_us | neogrid | map4connect | own</c>) via <see cref="SourceSystemJsonConverter"/>.
    /// </summary>
    /// <remarks>
    /// Extensível: novo valor = novo membro com <see cref="JsonStringEnumMemberNameAttribute"/>. O nome
    /// de fio entra no cálculo do <c>catalogId</c> (ver <see cref="CatalogIdGenerator"/>), então NUNCA
    /// renomeie um valor existente — mudaria todos os ids já persistidos.
    /// </remarks>
    [JsonConverter(typeof(SourceSystemJsonConverter))]
    public enum SourceSystem
    {
        /// <summary>SQL ConnectUs (somente leitura; será abandonado).</summary>
        [JsonStringEnumMemberName("connect_us")] ConnectUs = 0,

        /// <summary>Exemplos de referência Neogrid (pares TCL/XSL em disco).</summary>
        [JsonStringEnumMemberName("neogrid")] Neogrid = 1,

        /// <summary>Map4Connect (fonte ainda a confirmar — risco R1 do desenho).</summary>
        [JsonStringEnumMemberName("map4connect")] Map4Connect = 2,

        /// <summary>Artefatos próprios (ex.: <c>tbGeneratedMapperArtifact</c>).</summary>
        [JsonStringEnumMemberName("own")] Own = 3,
    }

    /// <summary>Conversor JSON do <see cref="SourceSystem"/> usando os nomes snake_case explícitos.</summary>
    public sealed class SourceSystemJsonConverter : JsonStringEnumConverter<SourceSystem>
    {
    }

    /// <summary>Nome de fio (snake_case) do <see cref="SourceSystem"/>, usado no JSON, na coluna SQL e no catalogId.</summary>
    public static class SourceSystemExtensions
    {
        public static string ToWireName(this SourceSystem system) => system switch
        {
            SourceSystem.ConnectUs => "connect_us",
            SourceSystem.Neogrid => "neogrid",
            SourceSystem.Map4Connect => "map4connect",
            SourceSystem.Own => "own",
            _ => throw new ArgumentOutOfRangeException(nameof(system), system, "SourceSystem desconhecido."),
        };

        public static bool TryParseWireName(string? value, out SourceSystem system)
        {
            foreach (var candidate in System.Enum.GetValues<SourceSystem>())
            {
                if (string.Equals(candidate.ToWireName(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    system = candidate;
                    return true;
                }
            }
            system = default;
            return false;
        }
    }
}
