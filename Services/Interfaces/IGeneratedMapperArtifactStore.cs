namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Constantes de status do candidato gerado automaticamente para um mapper Sysmiddle
    /// (issue #438, ADR <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §5).
    /// </summary>
    public static class GeneratedMapperArtifactStatus
    {
        /// <summary>Nunca foi pedido/gerado para este mapper.</summary>
        public const string None = "none";

        /// <summary>Geração em andamento (disparada por este ou por outro request).</summary>
        public const string Generating = "generating";

        /// <summary>Candidato disponível e com o hash do <c>MapperVo</c> ainda batendo.</summary>
        public const string Ready = "ready";

        /// <summary>Existia um candidato, mas o mapper mudou de versão desde então (hash divergente).</summary>
        public const string Stale = "stale";
    }

    /// <summary>
    /// Linha persistida de <c>dbo.tbGeneratedMapperArtifact</c> — um candidato TCL/XSL/XSLT por
    /// <c>(MapperGuid, ProjectId)</c> (issue #635: <see cref="ProjectId"/> nulo = linha legada, anterior à
    /// migração; <c>mapperGuid</c> sozinho NÃO é único entre projetos). <see cref="Status"/> guarda só os estados de escrita
    /// (<c>none</c> nunca é persistido — ausência de linha já significa "none"; <c>stale</c> também
    /// não é persistido — é calculado em leitura comparando <see cref="MapperVoHash"/> com o hash
    /// atual do mapper, ver <see cref="Transformation.Ai.IGeneratedMapperArtifactService"/>).
    /// </summary>
    public sealed record GeneratedMapperArtifactRecord(
        string MapperGuid,
        string Status,
        string? Content,
        string? CoverageJson,
        string? ValidationBasis,
        string? MapperVoHash,
        string? CorrelationId,
        DateTimeOffset? GeneratedAt,
        DateTimeOffset UpdatedAt,
        string? ProjectId = null);

    /// <summary>
    /// Acesso a dado do candidato gerado automaticamente (issue #438). Mesmo padrão ADO.NET cru de
    /// <c>SqlFieldCorrectionStore</c> — tabela autossuficiente, sem FK (mapper vive em
    /// <c>tbMapper</c>, banco compartilhado somente-leitura, ver <c>.claude/rules/security.md</c>).
    /// </summary>
    public interface IGeneratedMapperArtifactStore
    {
        Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken cancellationToken);

        // ── Issue #635: overloads com ProjectId ────────────────────────────────────────────────
        // Implementações default (retrocompatíveis): quem não conhece projeto (fakes antigos, outros
        // armazenamentos) cai no comportamento por MapperGuid. O store SQL sobrescreve todas.

        /// <summary>
        /// Lê por <c>(MapperGuid, ProjectId)</c>. <paramref name="projectId"/> nulo = linha legada. Com
        /// projeto informado e sem linha exata, o store SQL cai na linha legada (ProjectId nulo) do mesmo
        /// <c>MapperGuid</c> — o hash do <c>MapperVo</c> continua sendo o guarda contra conteúdo de outro projeto.
        /// </summary>
        Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, string? projectId, CancellationToken cancellationToken)
            => GetAsync(mapperGuid, cancellationToken);

        /// <summary>Todas as linhas de um <c>MapperGuid</c> (todos os projetos) — base da desambiguação (issue #634).</summary>
        async Task<IReadOnlyList<GeneratedMapperArtifactRecord>> ListByMapperGuidAsync(string mapperGuid, CancellationToken cancellationToken)
        {
            var one = await GetAsync(mapperGuid, cancellationToken);
            return one is null ? Array.Empty<GeneratedMapperArtifactRecord>() : [one];
        }

        Task<bool> TryBeginGeneratingAsync(string mapperGuid, string? projectId, string correlationId, CancellationToken cancellationToken)
            => TryBeginGeneratingAsync(mapperGuid, correlationId, cancellationToken);

        Task CompleteAsync(
            string mapperGuid, string? projectId, string content, string coverageJson, string validationBasis,
            string mapperVoHash, string correlationId, CancellationToken cancellationToken)
            => CompleteAsync(mapperGuid, content, coverageJson, validationBasis, mapperVoHash, correlationId, cancellationToken);

        Task FailAsync(string mapperGuid, string? projectId, CancellationToken cancellationToken)
            => FailAsync(mapperGuid, cancellationToken);

        /// <summary>
        /// Listagem paginada por offset (unificação com <c>mapping-releases</c>, issue #438, ADR
        /// <c>adr-unificacao-generated-artifact-mapping-release.md</c> opção b). As linhas voltam SEM
        /// <see cref="GeneratedMapperArtifactRecord.Content"/> (<c>null</c>) — o conteúdo é pesado e a
        /// listagem só o expõe via link de detalhe. Ordem estável: mais recente primeiro
        /// (<c>GeneratedAtUtc</c>, senão <c>UpdatedAtUtc</c>), desempate por <c>MapperGuid</c>.
        /// <paramref name="status"/> filtra pelo status PERSISTIDO (<c>ready</c>/<c>generating</c>);
        /// <c>stale</c> é calculado só em leitura de detalhe, logo nunca casa aqui.
        /// </summary>
        Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(
            string? status, int skip, int take, CancellationToken cancellationToken);

        /// <summary>
        /// Tenta assumir a geração deste mapper de forma atômica (issue #438, item 5 — "concorrência
        /// baixa"): se não existir linha, insere já como <see cref="GeneratedMapperArtifactStatus.Generating"/>;
        /// se existir e o status atual permitir regeração (<c>none</c> nunca fica persistido, então na
        /// prática é qualquer linha existente que não esteja <c>generating</c>), faz UPDATE condicional.
        /// Devolve <c>false</c> se outra chamada concorrente já está gerando — o chamador NÃO deve
        /// disparar uma segunda geração nesse caso.
        /// </summary>
        Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken cancellationToken);

        /// <summary>Grava o candidato pronto e volta o status para <see cref="GeneratedMapperArtifactStatus.Ready"/>.</summary>
        Task CompleteAsync(
            string mapperGuid, string content, string coverageJson, string validationBasis,
            string mapperVoHash, string correlationId, CancellationToken cancellationToken);

        /// <summary>
        /// Reverte a geração malsucedida — remove a linha (equivalente a voltar para <c>none</c>,
        /// que nunca é persistido) para permitir nova tentativa no próximo GET.
        /// </summary>
        Task FailAsync(string mapperGuid, CancellationToken cancellationToken);
    }
}
