using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Fiscal;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>Corpo do preflight: layout de entrada, documento posicional e (opcional) texto do mapper.</summary>
    public sealed record SysmiddlePreflightRequest(string LayoutXml, string? DocumentContent, string? MapperText);

    /// <summary>
    /// Preflight determinístico antes de gerar/testar um mapper: simula o parser do Sysmiddle sobre o
    /// documento (ordem dos registros) e confere as referências <c>I.LINHAxxx/Campo</c> do mapper contra
    /// o layout. Sem LLM, sem I/O externo, sem persistir nada. Regras em
    /// <c>docs/architecture/regras-sysmiddle-para-geracao-tcl.md</c>.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class SysmiddlePreflightController : ControllerBase
    {
        private const int MaxChars = 5_000_000;

        private readonly ILogger<SysmiddlePreflightController> _logger;

        public SysmiddlePreflightController(ILogger<SysmiddlePreflightController> logger)
        {
            _logger = logger;
        }

        /// <summary>Roda os checks de ordem do documento e de referências do mapper.</summary>
        /// <response code="200">Resultado: <c>parse</c> (quando há documento), <c>referenceIssues</c> (quando há mapper) e <c>ok</c>.</response>
        /// <response code="400">Layout ausente/inválido ou entrada acima do limite.</response>
        [HttpPost]
        [ServiceFilter(typeof(AuditActionFilter))]
        public IActionResult Run([FromBody] SysmiddlePreflightRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.LayoutXml))
                return BadRequest(new { error = "layoutXml é obrigatório." });

            if (request.LayoutXml.Length > MaxChars
                || (request.DocumentContent?.Length ?? 0) > MaxChars
                || (request.MapperText?.Length ?? 0) > MaxChars)
                return BadRequest(new { error = "Entrada acima do limite permitido." });

            try
            {
                SysmiddleParseSimulation? parse = null;
                if (!string.IsNullOrEmpty(request.DocumentContent))
                    parse = SysmiddleParseOrderChecker.Simulate(request.LayoutXml, request.DocumentContent);

                IReadOnlyList<TclReferenceIssue> issues = Array.Empty<TclReferenceIssue>();
                if (!string.IsNullOrWhiteSpace(request.MapperText))
                    issues = TclLayoutReferenceChecker.Check(request.LayoutXml, request.MapperText);

                var ok = (parse?.Consumed ?? true) && issues.Count == 0;
                var hint = parse is { Consumed: false }
                    ? "Sobrou conteúdo no parse: provável registro fora da ordem do layout/planilha. Corrija na origem do documento."
                    : null;

                // Conteúdo de documento/mapper nunca é logado (dado de cliente).
                _logger.LogInformation("Preflight Sysmiddle: ok={Ok}, parseConsumido={Consumed}, problemasDeReferencia={Issues}",
                    ok, parse?.Consumed, issues.Count);

                return Ok(new { ok, parse, referenceIssues = issues, hint });
            }
            catch (System.Xml.XmlException)
            {
                return BadRequest(new { error = "layoutXml não é um XML válido." });
            }
        }
    }
}
