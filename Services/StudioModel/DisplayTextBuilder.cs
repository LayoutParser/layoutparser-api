namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>
    /// Texto exibido na árvore (design §2.1): <c>Nome    (±, mín, máx)</c> para nó com filhos/contêiner e
    /// <c>Nome    (±, Tipo)</c> para nó com valor. Separador = 4 espaços. <c>±</c> = <c>-</c> se não obrigatório,
    /// <c>+</c> se obrigatório (sentido do <c>+</c> NÃO CONFIRMADO — hipótese, ver design §9-3).
    /// Puro, sem I/O, engine-agnóstico.
    /// </summary>
    public static class DisplayTextBuilder
    {
        public const string Separator = "    ";

        /// <summary>Forma com ocorrências. Sem mín/máx (qualquer um ausente) omite o segmento: <c>Nome    (-)</c>.</summary>
        public static string ForContainer(string name, bool required, int? minOccurs, int? maxOccurs)
        {
            var sign = required ? "+" : "-";
            return minOccurs.HasValue && maxOccurs.HasValue
                ? $"{name}{Separator}({sign}, {minOccurs.Value}, {maxOccurs.Value})"
                : $"{name}{Separator}({sign})";
        }

        /// <summary>Forma com valor. Tipo sem catálogo (<c>null</c>/vazio) → <c>?</c>.</summary>
        public static string ForValue(string name, bool required, string? dataTypeName)
        {
            var sign = required ? "+" : "-";
            var type = string.IsNullOrWhiteSpace(dataTypeName) ? "?" : dataTypeName;
            return $"{name}{Separator}({sign}, {type})";
        }
    }
}
