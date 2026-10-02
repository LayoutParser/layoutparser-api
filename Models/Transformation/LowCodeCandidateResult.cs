namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Resultado de UMA transformação low-code candidata, produzido quando o pathway LowCode-auto
    /// (<see cref="LayoutParserApi.Services.Transformation.LowCode.LowCodeAutoTransformationService"/>)
    /// encontra N&gt;1 mapeadores genuinamente plausíveis (MapperGuid distintos) para o mesmo layoutGuid.
    /// Quando N==1 (caso comum), este tipo não é usado — o comportamento permanece o de sempre.
    /// </summary>
    public class LowCodeCandidateResult
    {
        /// <summary>Identificador do mapeador (tbMapper.MapperGuid) usado nesta transformação.</summary>
        public string MapperGuid { get; set; } = "";

        /// <summary>Nome do mapeador (tbMapper.Name), quando disponível.</summary>
        public string? MapperName { get; set; }

        /// <summary>TargetLayoutGuid do mapeador (prioriza o valor extraído do XML descriptografado).</summary>
        public string? TargetLayoutGuid { get; set; }

        /// <summary>PackageGuid do mapeador, para rastreabilidade.</summary>
        public string? PackageGuid { get; set; }

        /// <summary>
        /// Indicador de validade desta transformação. Hoje reflete apenas se o runner low-code
        /// executou sem exceção — não há validação XSD cabeada neste pathway (ver AutomatedTransformationTestService,
        /// que é usado em outro loop). Não inventamos validação nova aqui.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>XML de saída da transformação, quando bem-sucedida.</summary>
        public string? OutputXml { get; set; }

        /// <summary>
        /// Tamanho do XML de saída em caracteres. Existe para o front saber o que vai buscar mesmo
        /// quando <see cref="OutputXml"/> é omitido do payload por exceder
        /// <c>LowCode:InlineXmlMaxChars</c> — sem isso, "campo ausente" seria indistinguível de
        /// "candidato sem saída".
        /// </summary>
        public int OutputLength { get; set; }

        /// <summary>
        /// Mensagem de erro, quando a transformação deste candidato específico falhou.
        /// <b>Já saneada</b> (<see cref="LayoutParserApi.Services.Transformation.LowCode.LowCodeErrorSanitizer"/>):
        /// este campo sai no payload 200 do parse, então não pode carregar caminho de disco do servidor.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Código estável do runner HTTP (queue_full, mapper_not_found, transform_failed, timeout,
        /// runner_unavailable, invalid_request, runtime_error); null para falhas fora do cliente HTTP.
        /// Não é persistido no índice em disco.</summary>
        public string? ErrorCode { get; set; }

        /// <summary>
        /// Conteúdo decifrado (XML) do mapeador usado nesta transformação. Exposto de volta ao chamador
        /// (<c>TransformationExecutionController.ExecuteSysmiddleCandidatesAsync</c>) para dois usos
        /// distintos que compartilham o mesmo dado: (1) issue #138 — o controller resolve
        /// <c>SectionMappings</c> depois do runner (<see cref="LayoutParserApi.Services.Transformation.LowCode.SysmiddleSectionMappingResolver"/>);
        /// (2) issue #141 — permite compor <c>fieldMappings</c> (<see cref="LayoutParserApi.Services.Transformation.StructuralResolution.FieldMappingCompositionService"/>)
        /// sem uma segunda consulta SQL ao mapper, já que o candidato já carrega o mesmo mapper decifrado
        /// que o runner low-code usou para gerar <see cref="OutputXml"/>. Deliberadamente NÃO entra nas
        /// projeções persistidas em disco (meta.json/índice) — nenhum dos pontos que escrevem em
        /// <c>_storePath</c> lê este campo, todos usam objetos anônimos explícitos. Nulo quando a
        /// transformação falhou antes de o mapper ser resolvido, ou quando o candidato é da entrada N==1
        /// legada (preenchido mesmo assim, ver <c>TransformSingleAndPersistAsync</c>).
        /// </summary>
        public string? DecryptedMapperContent { get; set; }
    }
}
