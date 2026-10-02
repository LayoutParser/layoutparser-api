namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Falha do LayoutParserLowCodeRunner (HTTP v1) já traduzida para um código string estável
    /// (issue #641). Os códigos são contrato com o front/PathwayDiagnostic — não renomear.
    /// </summary>
    public sealed class LowCodeRunnerException : Exception
    {
        public const string InvalidRequest = "invalid_request";
        public const string MapperNotFound = "mapper_not_found";
        public const string TransformFailed = "transform_failed";
        public const string Timeout = "timeout";
        public const string QueueFull = "queue_full";
        public const string RunnerUnavailable = "runner_unavailable";
        public const string RuntimeError = "runtime_error";
        // Codigos adicionados pelo runner (campo aditivo "code" nos erros).
        public const string EmptyDocument = "empty_document";
        public const string InputNotFound = "input_not_found";
        public const string EmptyResult = "empty_result";
        public const string PackageNotConfigured = "package_not_configured";
        public const string PackageNotFound = "package_not_found";
        public const string RouteNotFound = "route_not_found";
        public const string ClientClosedRequest = "client_closed_request";
        /// <summary>PROPOSTO pelo runner (409), ainda nao confirmado: tratado como codigo conhecido generico.</summary>
        public const string AmbiguousMapper = "ambiguous_mapper";
        /// <summary>Somente em resultados de batch (status skipped).</summary>
        public const string NotExecuted = "not_executed";

        private static readonly HashSet<string> Conhecidos = new(StringComparer.Ordinal)
        {
            InvalidRequest, MapperNotFound, TransformFailed, Timeout, QueueFull, RunnerUnavailable, RuntimeError,
            EmptyDocument, InputNotFound, EmptyResult, PackageNotConfigured, PackageNotFound, RouteNotFound,
            ClientClosedRequest, AmbiguousMapper, NotExecuted
        };

        /// <summary>Devolve o codigo canonico se estiver no conjunto conhecido; null caso contrario
        /// (nunca propaga string arbitraria do runner).</summary>
        public static string? NormalizeKnown(string? code) =>
            !string.IsNullOrWhiteSpace(code) && Conhecidos.Contains(code.Trim()) ? code.Trim() : null;

        public string Code { get; }
        public int? HttpStatus { get; }
        public int? ExitCode { get; }

        public LowCodeRunnerException(string code, string message, int? httpStatus = null, int? exitCode = null, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            HttpStatus = httpStatus;
            ExitCode = exitCode;
        }

        /// <summary>Código estável da exceção, ou null quando ela não veio do cliente HTTP do runner.</summary>
        public static string? CodeOf(Exception? ex) => (ex as LowCodeRunnerException)?.Code;
    }
}
