namespace LayoutParserApi.Services.StudioModel
{
    /// <summary>Fonte (SQL/cache de mappers) indisponível → controller responde 503.</summary>
    public sealed class StudioModelUnavailableException : Exception
    {
        public StudioModelUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary><c>engine</c> desconhecido → controller responde 400 <c>{error}</c>.</summary>
    public sealed class StudioModelInvalidEngineException : Exception
    {
        public StudioModelInvalidEngineException(string message) : base(message) { }
    }

    /// <summary>Engine conhecido mas sem adaptador nesta versão (xslt) → 501 <c>{error}</c>.</summary>
    public sealed class StudioModelEngineNotSupportedException : Exception
    {
        public StudioModelEngineNotSupportedException(string message) : base(message) { }
    }
}
