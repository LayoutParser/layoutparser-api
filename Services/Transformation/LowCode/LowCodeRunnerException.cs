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
