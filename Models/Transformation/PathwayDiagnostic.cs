namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Diagnóstico estruturado por pathway de <c>POST /api/transformation-execution/execute-candidates</c>
    /// (Issue LayoutParserReact #86). Campo ADITIVO — não substitui <see cref="TransformationExecutionCandidatesResponse.Warnings"/>,
    /// que continua populado exatamente como hoje por compatibilidade.
    ///
    /// <para>Ver desenho completo em
    /// docs/architecture/diagnostico-issue-86-diagnostico-estruturado-execute-candidates.md §4.
    /// Este arquivo só define a estrutura; a população dos valores por pathway (sysmiddle/tcl-xsl/
    /// ai-fallback) é feita por quem já monta <c>warnings</c>/<c>failureKinds</c> hoje.</para>
    /// </summary>
    public class PathwayDiagnostic
    {
        /// <summary>"sysmiddle" | "tcl-xsl" | "ai-fallback".</summary>
        public string Pathway { get; set; } = "";

        /// <summary>"candidate_generated" | "not_applicable" | "failed" (§4.2 do desenho).
        /// String, não enum exposto — permite adicionar valores sem quebrar o contrato.</summary>
        public string Status { get; set; } = "";

        /// <summary>Taxonomia estável: "no_mapper" | "map_not_found" | "xsl_not_found" |
        /// "configuration_error" | "runner_unavailable" | "execution_error" | "not_applicable";
        /// no pathway sysmiddle via runner HTTP (#641) também: "queue_full" | "mapper_not_found" |
        /// "transform_failed" | "timeout" | "invalid_request" | "runtime_error"
        /// (§4.3 do desenho). String, não enum exposto, pelo mesmo motivo de <see cref="Status"/>.</summary>
        public string Code { get; set; } = "";

        /// <summary>Mensagem legível para o front. SEMPRE passada por
        /// <see cref="LayoutParserApi.Services.Transformation.LowCode.LowCodeErrorSanitizer"/> antes de
        /// chegar aqui — nunca caminho de disco/detalhe interno cru (§5 do desenho).</summary>
        public string Message { get; set; } = "";
    }
}
