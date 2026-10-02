using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação de <see cref="IFiscalSchemaInitializer"/> — chama, em ORDEM, o
    /// <c>EnsureSchemaAsync</c> (agora <c>internal</c>) de cada store fiscal/identidade, uma vez no
    /// startup, em vez de depender de qual store uma requisição real bate primeiro.
    /// </summary>
    /// <remarks>
    /// Grafo de dependência de FK mapeado (issue da instabilidade de deploy, PR #310) e atualizado
    /// após a migração de banco (PR seguinte a #312): <see cref="SqlFiscalPackageStore"/>,
    /// <see cref="SqlMappingDraftStore"/> e <see cref="SqlMappingReleaseStore"/> deixaram de usar o
    /// banco compartilhado <c>Database:*</c> (ConnectUS_Macgyver/Sysmiddle) e passaram a usar o banco
    /// DEDICADO do projeto (<c>IdentityDatabase:*</c>), o mesmo já usado por
    /// <see cref="SqlIdentityWorkspaceStore"/> e <see cref="SqlAiUserSessionStore"/>. Motivo: essas
    /// tabelas fiscais são exclusivas deste projeto e não deveriam depender do SQL Server
    /// compartilhado por ~231.890 times da NDD (ver <c>.claude/rules/security.md</c>) — a FK inválida
    /// corrigida na PR #312 (removida, não recriada) era sintoma dessa divergência de banco, não a
    /// causa raiz completa.
    /// <para>
    /// Com todos os 5 stores agora no MESMO servidor físico (<c>IdentityDatabase:*</c>), o
    /// initializer usa uma ÚNICA conexão para todo o schema, em ordem de dependência de FK:
    /// <list type="bullet">
    /// <item><see cref="SqlFiscalPackageStore"/> (tbFiscalProject, tbFiscalMappingPackage,
    ///   tbFiscalMappingPackageRevision, tbPackageArtifact) →</item>
    /// <item><see cref="SqlMappingDraftStore"/> (tbMappingDraft.PackageId/RevisionId referenciam as
    ///   tabelas acima) →</item>
    /// <item><see cref="SqlMappingReleaseStore"/> (tbMappingRelease.DraftId referencia tbMappingDraft) →</item>
    /// <item><see cref="SqlIdentityWorkspaceStore"/> e <see cref="SqlAiUserSessionStore"/>, cada um
    ///   autossuficiente (FKs só apontam para tabelas do próprio bloco) — ordem entre os dois (e em
    ///   relação aos três primeiros) é irrelevante, mantidos por último por não serem dependência de
    ///   ninguém.</item>
    /// </list>
    /// </para>
    /// Adicionalmente, as colunas <c>WorkspaceId</c> de <c>tbFiscalProject</c>/
    /// <c>tbFiscalMappingPackage</c>/<c>tbMappingDraft</c>/<c>tbMappingRelease</c> tinham
    /// <c>REFERENCES dbo.tbFiscalWorkspace(WorkspaceId)</c> — uma FK que NUNCA funcionou, em
    /// nenhuma ordem: <c>tbFiscalWorkspace</c> nunca existiu em banco nenhum (o workspace fiscal
    /// real é <c>tbLpFiscalWorkspace</c>). Essa FK foi removida das 4 tabelas (coluna preservada, sem
    /// constraint) antes mesmo desta migração; não foi recriada apontando para
    /// <c>tbLpFiscalWorkspace</c> mesmo agora que ambos moram no mesmo banco — ver comentário em
    /// <see cref="SqlFiscalPackageStore.SchemaDdl"/>.
    /// </remarks>
    public sealed class FiscalSchemaInitializer : IFiscalSchemaInitializer
    {
        private readonly ILogger<FiscalSchemaInitializer> _logger;
        private readonly string _identityConnectionString;

        public FiscalSchemaInitializer(ILogger<FiscalSchemaInitializer> logger, IConfiguration configuration)
        {
            _logger = logger;

            var identityServer = configuration["IdentityDatabase:Server"];
            var identityDatabase = configuration["IdentityDatabase:Database"];
            var identityUserId = configuration["IdentityDatabase:UserId"];
            var identityPassword = configuration["IdentityDatabase:Password"];
            _identityConnectionString =
                $"Server={identityServer};Database={identityDatabase};User Id={identityUserId};Password={identityPassword};TrustServerCertificate=True;";
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var connection = new SqlConnection(_identityConnectionString);
                await connection.OpenAsync(cancellationToken);

                // Ordem impositiva: FiscalPackage → MappingDraft → MappingRelease (FK entre eles) →
                // IdentityWorkspace/AiUserSession (autossuficientes, ordem irrelevante entre si).
                await SqlFiscalPackageStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlMappingDraftStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlMappingReleaseStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlIdentityWorkspaceStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlAiUserSessionStore.EnsureSchemaAsync(connection, cancellationToken);
                // ✅ Issue #345: tbFieldCorrectionContext/tbFieldCorrectionReport — autossuficientes
                // (sem FK para as tabelas acima, ver comentário em SqlFieldCorrectionStore.SchemaDdl).
                await SqlFieldCorrectionStore.EnsureSchemaAsync(connection, cancellationToken);
                // ✅ Issue #438: tbGeneratedMapperArtifact — também autossuficiente (sem FK para
                // tbMapper, que vive no banco compartilhado somente-leitura).
                await SqlGeneratedMapperArtifactStore.EnsureSchemaAsync(connection, cancellationToken);
                // ✅ Issue #423: tbTestSuite/tbTestSuiteFixture/tbTestSuiteRun — FK só entre si (sem FK
                // para tbMappingDraft/tbMappingRelease, ver comentário em SqlTestSuiteStore.SchemaDdl).
                await SqlTestSuiteStore.EnsureSchemaAsync(connection, cancellationToken);
                // ✅ Issue #422: tbMappingRuleAnswer — autossuficiente (sem FK; vínculo validado no controller).
                await SqlMappingRuleAnswerStore.EnsureSchemaAsync(connection, cancellationToken);
                // ✅ Issue #366: tbLpFiscalAnalysis/tbLpFiscalAnalysisFile — FK só entre si (cascade).
                await SqlFiscalAnalysisStore.EnsureSchemaAsync(connection, cancellationToken);
                // ✅ Issue #628: tbMappingCatalogSource/Folder/Item — catálogo unificado, autossuficiente (sem FK).
                await SqlMappingCatalogStore.EnsureSchemaAsync(connection, cancellationToken);

                _logger.LogInformation("Schema fiscal e de identidade (IdentityDatabase:*) inicializado com sucesso no startup, em ordem de dependência de FK.");
            }
            catch (Exception ex)
            {
                // Degrada graciosamente: cada store mantém seu próprio EnsureSchemaAsync lazy como
                // safety net por requisição — não derruba o startup da API se o SQL estiver fora do ar.
                _logger.LogWarning(ex, "Falha ao inicializar o schema fiscal/identidade (IdentityDatabase:*) no startup — cada store tentará novamente sob demanda.");
            }
        }
    }
}
