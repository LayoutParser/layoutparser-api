using LayoutParserApi.Services.Storage;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Validation;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Implementação de <see cref="IFiscalPackageService"/> — Slice 2 (issue #229). Segue o padrão de
    /// storage físico já usado pelo projeto (<c>MLData/LowCodeTransformations/</c>): filesystem local
    /// + SQL de metadado, sem blob storage distribuído (design-slice2 §5).
    /// </summary>
    public sealed class FiscalPackageService : IFiscalPackageService
    {
        private readonly IFiscalPackageStore _store;
        private readonly IAntivirusScanner _antivirusScanner;
        private readonly IFiscalMappingRuleExtractor _ruleExtractor;
        private readonly ILogger<FiscalPackageService> _logger;
        private readonly string _storePath;
        private readonly IReadOnlyList<string> _requiredRuleSheetColumns;
        private readonly MultipartUploadValidator _validator = new();

        // Sanitização de nome de arquivo: só o basename, sem separador de caminho/traversal.
        private static readonly Regex InvalidFileNameChars = new(@"[^a-zA-Z0-9._-]", RegexOptions.Compiled);

        public FiscalPackageService(
            IFiscalPackageStore store,
            IAntivirusScanner antivirusScanner,
            IFiscalMappingRuleExtractor ruleExtractor,
            ILogger<FiscalPackageService> logger,
            IConfiguration configuration)
        {
            _store = store;
            _antivirusScanner = antivirusScanner;
            _ruleExtractor = ruleExtractor;
            _logger = logger;
            _storePath = new StoragePaths(configuration).FiscalMappingPackages;
            // ✅ issue #424: lista configurável de colunas obrigatórias (default vazio — ver FiscalSpecQualityAnalyzer).
            _requiredRuleSheetColumns = configuration.GetSection(FiscalSpecQualityAnalyzer.RequiredColumnsConfigKey)
                .Get<string[]>()?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
                ?? new List<string>();
        }

        public async Task<CreatePackageOutcome> CreatePackageAsync(
            Guid workspaceId,
            Guid projectId,
            Guid userId,
            string packageName,
            string? idempotencyKey,
            IReadOnlyList<UploadedArtifactInput> artifacts,
            CancellationToken cancellationToken)
        {
            if (artifacts.Count == 0)
                return new CreatePackageOutcome(false, "Nenhum artefato enviado.", null);

            // 1. Valida CADA artefato antes de tocar em disco/SQL — 422 sem inferência silenciosa
            //    (aceite explícito da issue: divergência de MIME/tamanho/tipo obrigatório = rejeita).
            var (validationOk, validationError, validated) = ValidateArtifacts(artifacts, workspaceId);
            if (!validationOk)
                return new CreatePackageOutcome(false, validationError, null);

            // Chave de idempotência: header explícito OU hash determinístico do conjunto de hashes
            // (ordenado, para não depender da ordem de envio no multipart).
            var effectiveKey = !string.IsNullOrWhiteSpace(idempotencyKey)
                ? idempotencyKey
                : ComputeSha256(System.Text.Encoding.UTF8.GetBytes(string.Join('|', validated!.Select(v => v.Sha256).OrderBy(s => s, StringComparer.Ordinal))));

            var existing = await _store.FindPackageByIdempotencyKeyAsync(workspaceId, projectId, effectiveKey, cancellationToken);
            if (existing != null)
            {
                _logger.LogInformation(
                    "Upload idempotente: pacote {PackageId} já existe para a chave informada — não duplicando.", existing.PackageId);
                return new CreatePackageOutcome(true, null, existing);
            }

            // 2. Garante o FiscalProject mínimo (Slice 2 não expõe CRUD de projeto — cria sob demanda).
            await EnsureProjectAsync(workspaceId, projectId, cancellationToken);

            // 3. Grava em filesystem + monta entidades de metadado.
            var packageId = Guid.NewGuid();
            var revisionId = Guid.NewGuid(); // usado só para o path físico — a store gera o RevisionId real.
            var entities = new List<PackageArtifact>();
            var writtenPaths = new List<string>();

            try
            {
                foreach (var (input, result, sha256) in validated!)
                {
                    var safeName = SanitizeFileName(input.OriginalFileName);
                    var artifactId = Guid.NewGuid();
                    var relativePath = Path.Combine(
                        workspaceId.ToString(), packageId.ToString(), revisionId.ToString(), $"{artifactId}_{safeName}");
                    var absolutePath = Path.Combine(_storePath, relativePath);

                    Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                    await File.WriteAllBytesAsync(absolutePath, input.Content, cancellationToken);
                    writtenPaths.Add(absolutePath);

                    entities.Add(new PackageArtifact
                    {
                        ArtifactId = artifactId,
                        Kind = input.Kind,
                        Sha256 = sha256,
                        SizeBytes = input.Content.Length,
                        OriginalFileName = safeName,
                        MimeDeclared = input.ContentType ?? "unknown",
                        MimeSniffed = result.MimeSniffed,
                        UploadedByUserId = userId,
                        UploadedAt = DateTimeOffset.UtcNow,
                        InspectionStatus = Models.Entities.Fiscal.InspectionStatus.Pending,
                        StoragePath = relativePath,
                        // ✅ issue #341: só grava a proveniência se for um valor explícito e válido —
                        // valor ausente/inválido vira null, que a leitura trata como amostra real (fail-closed).
                        Provenance = Models.Entities.Fiscal.ArtifactProvenance.IsValid(input.Provenance) ? input.Provenance : null,
                    });
                }

                // 4. Metadado no SQL — fonte da verdade (filesystem é blob store sem lógica).
                var packageDetail = await _store.CreatePackageAsync(workspaceId, projectId, userId, packageName, effectiveKey, entities, cancellationToken);

                // 5. Antivírus assíncrono, fire-and-forget — nunca bloqueia a resposta do upload.
                foreach (var (entity, absolutePath) in entities.Zip(writtenPaths))
                    DispatchAntivirusScan(entity.ArtifactId, absolutePath, _antivirusScanner, _store, _logger);

                return new CreatePackageOutcome(true, null, packageDetail);
            }
            catch (Exception ex)
            {
                // Falha após já ter escrito algum arquivo em disco: loga (sem conteúdo) e propaga —
                // o controller decide o status HTTP. Não tenta rollback de filesystem (best-effort,
                // arquivos órfãos não vazam dado porque não têm metadado associado).
                _logger.LogError(ex, "Falha ao persistir pacote de mapeamento fiscal (workspace={WorkspaceId}, project={ProjectId}).", workspaceId, projectId);
                throw;
            }
        }

        public Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken)
            => _store.GetPackageIfMemberAsync(packageId, userId, cancellationToken);

        public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
            => _store.ListProjectsForMemberAsync(workspaceId, userId, cancellationToken);

        /// <summary>
        /// Cria uma nova revisão de um pacote existente (Gap 2 — issue #201): mesma validação/gravação
        /// em disco/antivírus assíncrono de <see cref="CreatePackageAsync"/>, sem idempotência (uma
        /// revisão nova é sempre uma intenção explícita de correção, não um reenvio de rede).
        /// </summary>
        public async Task<CreateRevisionOutcome> CreateRevisionAsync(
            Guid workspaceId,
            Guid packageId,
            Guid userId,
            IReadOnlyList<UploadedArtifactInput> artifacts,
            CancellationToken cancellationToken)
        {
            if (artifacts.Count == 0)
                return new CreateRevisionOutcome(false, "Nenhum artefato enviado.", false, null);

            // Confirma existência + membership ANTES de tocar em disco — "não existe" e "não é seu"
            // respondem o mesmo 404, mesmo padrão do GET.
            var existingPackage = await _store.GetPackageIfMemberAsync(packageId, userId, cancellationToken);
            if (existingPackage == null || existingPackage.WorkspaceId != workspaceId)
                return new CreateRevisionOutcome(false, null, true, null);

            var (validationOk, validationError, validated) = ValidateArtifacts(artifacts, workspaceId);
            if (!validationOk)
                return new CreateRevisionOutcome(false, validationError, false, null);

            var revisionId = Guid.NewGuid(); // usado só para o path físico — a store gera o RevisionId real.
            var entities = new List<PackageArtifact>();
            var writtenPaths = new List<string>();

            try
            {
                foreach (var (input, result, sha256) in validated!)
                {
                    var safeName = SanitizeFileName(input.OriginalFileName);
                    var artifactId = Guid.NewGuid();
                    var relativePath = Path.Combine(
                        workspaceId.ToString(), packageId.ToString(), revisionId.ToString(), $"{artifactId}_{safeName}");
                    var absolutePath = Path.Combine(_storePath, relativePath);

                    Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                    await File.WriteAllBytesAsync(absolutePath, input.Content, cancellationToken);
                    writtenPaths.Add(absolutePath);

                    entities.Add(new PackageArtifact
                    {
                        ArtifactId = artifactId,
                        Kind = input.Kind,
                        Sha256 = sha256,
                        SizeBytes = input.Content.Length,
                        OriginalFileName = safeName,
                        MimeDeclared = input.ContentType ?? "unknown",
                        MimeSniffed = result.MimeSniffed,
                        UploadedByUserId = userId,
                        UploadedAt = DateTimeOffset.UtcNow,
                        InspectionStatus = Models.Entities.Fiscal.InspectionStatus.Pending,
                        StoragePath = relativePath,
                        // ✅ issue #341: só grava a proveniência se for um valor explícito e válido —
                        // valor ausente/inválido vira null, que a leitura trata como amostra real (fail-closed).
                        Provenance = Models.Entities.Fiscal.ArtifactProvenance.IsValid(input.Provenance) ? input.Provenance : null,
                    });
                }

                var packageDetail = await _store.CreateRevisionAsync(packageId, userId, entities, cancellationToken);

                foreach (var (entity, absolutePath) in entities.Zip(writtenPaths))
                    DispatchAntivirusScan(entity.ArtifactId, absolutePath, _antivirusScanner, _store, _logger);

                return new CreateRevisionOutcome(true, null, false, packageDetail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar nova revisão do pacote de mapeamento fiscal {PackageId}.", packageId);
                throw;
            }
        }

        /// <summary>
        /// Inventário de estrutura (abas/colunas/linhas) de um artefato <c>spec</c> (XLSX) — reusa
        /// <see cref="IFiscalMappingRuleExtractor"/> (issue #103), sem parser de Excel novo. Nunca
        /// devolve conteúdo bruto da planilha, só a estrutura reconhecida.
        /// </summary>
        public async Task<ExcelInventoryOutcome> GetExcelInventoryAsync(
            Guid workspaceId,
            Guid packageId,
            Guid artifactId,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var package = await _store.GetPackageIfMemberAsync(packageId, userId, cancellationToken);
            if (package == null || package.WorkspaceId != workspaceId)
                return new ExcelInventoryOutcome(false, null, true, null);

            var artifact = package.LatestRevision.Artifacts.FirstOrDefault(a => a.ArtifactId == artifactId);
            if (artifact == null)
                return new ExcelInventoryOutcome(false, null, true, null);

            if (artifact.Kind != ArtifactKind.Spec)
                return new ExcelInventoryOutcome(false, $"Inventário de estrutura só está disponível para artefatos do tipo \"{ArtifactKind.Spec}\" — este é \"{artifact.Kind}\".", false, null);

            var relativePath = await _store.GetArtifactStoragePathAsync(artifactId, cancellationToken);
            if (relativePath == null)
                return new ExcelInventoryOutcome(false, null, true, null);

            var absolutePath = Path.Combine(_storePath, relativePath);
            try
            {
                using var stream = File.OpenRead(absolutePath);
                var extraction = _ruleExtractor.Extract(stream);

                var decisionSheets = extraction.Rules
                    .GroupBy(r => r.SheetName)
                    .Select(g => new ExcelSheetInventory(
                        g.Key,
                        g.SelectMany(r => r.Conditions.Select(c => c.Field)).Distinct().ToList(),
                        g.Count()))
                    .ToList();

                return new ExcelInventoryOutcome(true, null, false, new ExcelInventoryResult(decisionSheets, extraction.SkippedSheets));
            }
            catch (Exception ex)
            {
                // Nunca logar o conteúdo do Excel — só metadado (artifactId/packageId).
                _logger.LogError(ex, "Falha ao gerar inventário do artefato {ArtifactId} do pacote {PackageId}.", artifactId, packageId);
                return new ExcelInventoryOutcome(false, "Não foi possível ler a estrutura do arquivo Excel — pode estar corrompido.", false, null);
            }
        }

        /// <summary>
        /// Sinais de qualidade dos artefatos <c>spec</c> da revisão mais recente (issue #424). Síncrono
        /// (leitura de cabeçalhos + contagem, mesma passada do inventário), sem persistência. Falha em
        /// um artefato degrada só aquele artefato para <c>failed</c> — nunca derruba o GET da revisão.
        /// </summary>
        public async Task<IReadOnlyDictionary<Guid, SpecQualityResult>> GetSpecQualityAsync(PackageDetail package, CancellationToken cancellationToken)
        {
            var results = new Dictionary<Guid, SpecQualityResult>();
            foreach (var artifact in package.LatestRevision.Artifacts.Where(a => a.Kind == ArtifactKind.Spec))
            {
                try
                {
                    var relativePath = await _store.GetArtifactStoragePathAsync(artifact.ArtifactId, cancellationToken);
                    if (relativePath == null)
                    {
                        results[artifact.ArtifactId] = new SpecQualityResult(QualityStatus.Failed, null, "Arquivo do artefato não localizado para análise de qualidade.");
                        continue;
                    }

                    using var stream = File.OpenRead(Path.Combine(_storePath, relativePath));
                    var extraction = _ruleExtractor.Extract(stream);
                    var signals = FiscalSpecQualityAnalyzer.Analyze(extraction, _requiredRuleSheetColumns);
                    results[artifact.ArtifactId] = new SpecQualityResult(QualityStatus.Complete, signals, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Nunca logar conteúdo da planilha — só ids. A mensagem devolvida também é genérica.
                    _logger.LogWarning(ex, "Falha na análise de qualidade do artefato {ArtifactId} do pacote {PackageId}.", artifact.ArtifactId, package.PackageId);
                    results[artifact.ArtifactId] = new SpecQualityResult(QualityStatus.Failed, null, "Não foi possível ler a planilha para análise de qualidade — pode estar corrompida.");
                }
            }

            return results;
        }

        /// <summary>
        /// Valida cada artefato do upload (compartilhado por <see cref="CreatePackageAsync"/> e
        /// <see cref="CreateRevisionAsync"/>) — para na primeira falha, 422 sem inferência silenciosa.
        /// </summary>
        private (bool Success, string? Error, List<(UploadedArtifactInput Input, UploadValidationResult Result, string Sha256)>? Validated) ValidateArtifacts(
            IReadOnlyList<UploadedArtifactInput> artifacts, Guid workspaceId)
        {
            var validated = new List<(UploadedArtifactInput Input, UploadValidationResult Result, string Sha256)>();
            foreach (var artifact in artifacts)
            {
                var result = _validator.Validate(artifact.Content, artifact.OriginalFileName, artifact.Kind);
                if (!result.IsValid)
                {
                    // Nunca logar o conteúdo — só metadado (nome sanitizado, kind, tamanho).
                    _logger.LogWarning(
                        "Upload rejeitado (workspace={WorkspaceId}, kind={Kind}, tamanho={Size}): {Error}",
                        workspaceId, artifact.Kind, artifact.Content.Length, result.Error);
                    return (false, $"Artefato \"{artifact.Kind}\": {result.Error}", null);
                }

                var sha256 = ComputeSha256(artifact.Content);
                validated.Add((artifact, result, sha256));
            }

            return (true, null, validated);
        }

        private Task EnsureProjectAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken)
            // Projeto mínimo criado sob demanda — Slice 2 não expõe CRUD de projeto. A store garante a
            // linha (idempotente) porque tbFiscalMappingPackage tem FK para tbFiscalProject.
            => _store.EnsureProjectExistsAsync(workspaceId, projectId, cancellationToken);

        private static string ComputeSha256(byte[] content)
            => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        /// <summary>
        /// Dispara o scan em background (fire-and-forget, padrão já usado pelo projeto para
        /// learning/transformação). <paramref name="store"/> e <paramref name="scanner"/> são
        /// capturados por fechamento — nenhum dos dois depende de estado por-requisição (o scanner só
        /// usa <c>ILogger</c>; a store abre sua própria <c>SqlConnection</c> por chamada), então é
        /// seguro reusá-los fora do escopo do request original.
        /// </summary>
        private static void DispatchAntivirusScan(
            Guid artifactId,
            string filePath,
            IAntivirusScanner scanner,
            IFiscalPackageStore store,
            ILogger logger)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var clean = await scanner.ScanAsync(filePath, CancellationToken.None);
                    var status = clean switch
                    {
                        true => Models.Entities.Fiscal.InspectionStatus.Clean,
                        false => Models.Entities.Fiscal.InspectionStatus.Rejected,
                        null => Models.Entities.Fiscal.InspectionStatus.Pending, // mecanismo indisponível — fica Pending indefinidamente, sem travar.
                    };

                    if (status != Models.Entities.Fiscal.InspectionStatus.Pending)
                        await store.UpdateInspectionStatusAsync(artifactId, status, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Nunca propaga — best-effort, artefato fica Pending.
                    logger.LogWarning(ex, "Falha no scan de antivírus em background para o artefato {ArtifactId}.", artifactId);
                }
            });
        }

        private static string SanitizeFileName(string originalFileName)
        {
            var name = Path.GetFileName(originalFileName); // remove qualquer componente de diretório/traversal.
            var extension = Path.GetExtension(name);
            var baseName = Path.GetFileNameWithoutExtension(name);
            var safeBase = InvalidFileNameChars.Replace(baseName, "_");
            var safeExt = InvalidFileNameChars.Replace(extension, "");
            return string.IsNullOrWhiteSpace(safeBase) ? $"artefato{safeExt}" : $"{safeBase}{safeExt}";
        }
    }
}
