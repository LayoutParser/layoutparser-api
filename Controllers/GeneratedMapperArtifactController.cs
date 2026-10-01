using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// <c>GET .../mappings/{mappingId}/generated-transformation</c> (issue #438, ADR
    /// <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §5 — escopo mínimo).
    /// Devolve o candidato TCL/XSL/XSLT gerado automaticamente para um mapeador Sysmiddle, com
    /// disparo LAZY da síntese quando ainda não existe (ou está desatualizado): o time de operações
    /// vê o gerado sem precisar de um dev rodar o CLI <c>ai/XslSynth</c> manualmente. Mesma
    /// convenção de rota/RBAC de <see cref="LayoutTreeController"/> — rota de LEITURA, sem
    /// <see cref="MappingEngineGuardFilter"/> (ler/gerar visualização de Sysmiddle é sempre permitido).
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mappings/{mappingId}")]
    public class GeneratedMapperArtifactController : ControllerBase
    {
        private readonly IGeneratedMapperArtifactService _service;
        private readonly ILogger<GeneratedMapperArtifactController> _logger;

        public GeneratedMapperArtifactController(
            IGeneratedMapperArtifactService service, ILogger<GeneratedMapperArtifactController> logger)
        {
            _service = service;
            _logger = logger;
        }

        /// <summary>
        /// RBAC: qualquer papel de membro (leitura, mesmo espírito de
        /// <see cref="LayoutTreeController.GetLayoutTree"/>). <c>mappingId</c> não resolvido no
        /// catálogo <c>tbMapper</c> → 404 (indistinguível de não-membro, mesmo padrão fail-closed).
        /// O segmento de rota <c>{mappingId}</c> é o GUID do mapeador (<c>mapperGuid</c> na resposta).
        /// </summary>
        /// <param name="workspaceId">Workspace da rota (membership conferida pelo filtro de RBAC).</param>
        /// <param name="mappingId">GUID do mapeador Sysmiddle no catálogo <c>tbMapper</c>.</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200">
        /// <c>{ mapperGuid, status, content?, coverageJson?, validationBasis?, generatedAt?, correlationId? }</c>
        /// (campos nulos são omitidos). <c>status</c> é <c>generating</c> (candidato ainda não
        /// existia, ou estava <c>stale</c> — o hash do DSL do mapeador OU a versão do gerador mudou
        /// desde a última geração —, e a geração foi disparada em background por ESTA chamada, ou já
        /// estava em andamento) ou <c>ready</c> (candidato disponível). <c>stale</c> é calculado só em
        /// LEITURA e nunca é devolvido como valor final: ao detectá-lo, este GET já dispara a
        /// regeneração e responde <c>generating</c>. Quando <c>ready</c>, <c>validationBasis</c> é
        /// sempre <c>"declared_dsl"</c> — a cobertura reportada em <c>coverageJson</c> é contra a regra
        /// DECLARADA no mapeador (MapperVo/DSL decifrado), NÃO contra execução real do motor Sysmiddle
        /// (bloqueio de licença do host FiatMQ, ver ADR §2). <c>none</c> só aparece no caso residual em
        /// que o MapeadorVO do catálogo não é XML bem-formado (nada a gerar); fora isso, se o candidato
        /// não existia, a própria chamada já disparou a geração.
        /// <para>
        /// <c>coverageJson</c> é uma STRING JSON com: <c>generatorVersion</c> (hoje <c>"2"</c>; entra
        /// no hash de <c>stale</c>), <c>shell { rootElement, namespace, attributes }</c> (casca do
        /// documento gerada), <c>limitations[]</c> (limites conhecidos do gerador, em texto),
        /// <c>compiles</c>/<c>compileError</c>, <c>linksCovered</c>/<c>linksTotal</c>/<c>linkPct</c>,
        /// <c>rulesCovered</c>/<c>rulesTotal</c>/<c>rulePct</c>, <c>provenanceEntries</c> e
        /// <c>linkMappingsSemFolha</c>. Atributos do elemento raiz (<c>versao</c>, <c>Id</c> etc.) saem
        /// como <c>xsl:attribute</c>; o namespace (<c>xmlns</c>) só é emitido quando é constante no
        /// próprio mapeador. Na tradução da DSL, <c>Concat</c>/<c>Substring</c>/<c>GetLength</c> viram
        /// XPath; <c>Trim</c>/<c>Replace</c> e demais funções NÃO são traduzidas.
        /// </para>
        /// </response>
        /// <response code="404">Mapper não encontrado no catálogo ou usuário não é membro do workspace.</response>
        /// <response code="503">Catálogo de mappers (tbMapper) indisponível no momento.</response>
        [HttpGet("generated-transformation")]
        [RequireWorkspaceRole(WorkspaceRoleLevel.Viewer)]
        public async Task<IActionResult> GetGeneratedTransformation(Guid workspaceId, string mappingId, CancellationToken cancellationToken)
        {
            var correlationId = HttpContext.TraceIdentifier;
            try
            {
                var result = await _service.GetOrTriggerAsync(mappingId, correlationId, cancellationToken);
                return result is null ? NotFound() : Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar/disparar geração automática para o mapper {MapperGuid}.", mappingId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o catálogo de mappers no momento." });
            }
        }
    }
}
