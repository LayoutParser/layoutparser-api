using System.Security.Cryptography;
using System.Text.RegularExpressions;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Security;
using LayoutParserApi.Services.Validation;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Implementação de <see cref="IFiscalAnalysisService"/> — issue #366 (ADR
    /// <c>docs/architecture/adr-historico-analises-fiscais-366.md</c>). Mesmo padrão de storage de
    /// <see cref="FiscalPackageService"/> (metadado em SQL + blob em disco por workspace), porém em
    /// raiz PRÓPRIA (<c>ML:FiscalAnalysesPath</c>) — nunca a pasta de exemplos de aprendizado
    /// (<c>TransformationPipeline:ExamplesPath</c>): histórico do usuário não alimenta dataset de IA.
    /// </summary>
    /// <remarks>
    /// Ordem de escrita: disco → SQL (transação). Falha do SQL ⇒ apaga o diretório recém-gravado.
    /// Ordem de remoção: SQL → disco (sobra de disco é órfã inofensiva e é varrida pela purga; o
    /// inverso deixaria linha apontando para arquivo inexistente). Nunca loga nome/conteúdo de arquivo.
    /// </remarks>
    public sealed class FiscalAnalysisService : IFiscalAnalysisService
    {
        // Limites das colunas de tbLpFiscalAnalysis/tbLpFiscalAnalysisFile (ver DDL em SqlFiscalAnalysisStore):
        // valor maior estoura SqlException 2628 e a análise ficaria sem histórico.
        private const int MaxLayoutGuidLength = 64;
        private const int MaxLayoutNameLength = 256;
        private const int MaxDetectedTypeLength = 32;
        private const int MaxOriginalFileNameLength = 260;
        private const int MaxStoragePathLength = 512;

        private const int PurgeBatchSize = 100;
        private const int MaxPurgeBatchesPerCycle = 50;

        // Diretório recém-criado pode pertencer a um registro em andamento (disco grava antes do SQL):
        // a varredura de órfãos só toca diretórios mais velhos que isto.
        private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(1);

        private static readonly Regex ValidLayoutGuid = new(@"^[A-Za-z0-9_{}.-]+$", RegexOptions.Compiled);

        private static readonly Regex InvalidFileNameChars = new(@"[^a-zA-Z0-9._-]", RegexOptions.Compiled);

        private readonly IFiscalAnalysisStore _store;
        private readonly IIdentityWorkspaceStore _workspaceStore;
        private readonly FiscalAnalysisHistoryOptions _options;
        private readonly ILogger<FiscalAnalysisService> _logger;
        private readonly TimeProvider _clock;
        private readonly string _rootPath;

        public FiscalAnalysisService(
            IFiscalAnalysisStore store,
            IIdentityWorkspaceStore workspaceStore,
            IOptions<FiscalAnalysisHistoryOptions> options,
            ILogger<FiscalAnalysisService> logger,
            IConfiguration configuration,
            TimeProvider? clock = null)
        {
            _store = store;
            _workspaceStore = workspaceStore;
            _options = options.Value;
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
            _rootPath = configuration["ML:FiscalAnalysesPath"]
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLData", "FiscalAnalyses");
        }

        public async Task<Guid?> RegisterAsync(FiscalAnalysisRegistration registration, TimeSpan timeout)
        {
            var analysisId = Guid.NewGuid();
            // CTS PRÓPRIA (não RequestAborted): o histórico tem teto de tempo independente do cliente.
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var registered = await RegisterCoreAsync(analysisId, registration, cts.Token);
                if (!registered)
                    DeleteDirectoryQuietly(GetAnalysisDirectory(registration.WorkspaceId, analysisId));
                return registered ? analysisId : null;
            }
            catch (Exception ex)
            {
                // Degrada: análise nunca cai por causa do histórico. Compensa o que já foi para o disco.
                _logger.LogWarning(ex,
                    "Falha ao registrar histórico de análise fiscal (workspace={WorkspaceId}, analysis={AnalysisId}, timeout={TimedOut}) — análise segue sem AnalysisId.",
                    registration.WorkspaceId, analysisId, cts.IsCancellationRequested);
                DeleteDirectoryQuietly(GetAnalysisDirectory(registration.WorkspaceId, analysisId));
                return null;
            }
        }

        private async Task<bool> RegisterCoreAsync(Guid analysisId, FiscalAnalysisRegistration registration, CancellationToken cancellationToken)
        {
            if (registration.Files.Count == 0)
                return false;

            // Membership: quem não é membro do workspace nunca registra (segue sem histórico, não é erro).
            var workspace = await _workspaceStore.GetWorkspaceIfMemberAsync(registration.WorkspaceId, registration.OwnerUserId, cancellationToken);
            if (workspace == null)
            {
                _logger.LogInformation(
                    "Histórico de análise ignorado: usuário não é membro do workspace {WorkspaceId}.", registration.WorkspaceId);
                return false;
            }

            foreach (var file in registration.Files)
            {
                if (file.Content.Length == 0 || file.Content.Length > MultipartUploadValidator.MaxArtifactSizeBytes)
                {
                    _logger.LogWarning(
                        "Histórico de análise ignorado: arquivo com tamanho fora do limite ({SizeBytes} bytes, máx {MaxBytes}).",
                        file.Content.Length, MultipartUploadValidator.MaxArtifactSizeBytes);
                    return false;
                }
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var records = new List<FiscalAnalysisFileRecord>();

            foreach (var file in registration.Files)
            {
                var fileId = Guid.NewGuid();
                var safeName = SanitizeFileName(file.OriginalFileName);
                var relativePath = Path.Combine(registration.WorkspaceId.ToString(), analysisId.ToString(), $"{fileId}_{safeName}");
                // Falha cedo (antes de gravar disco) em vez de estourar no SQL: o caminho relativo é
                // guid/guid/guid_nome (~230 chars) e o nome já é sanitizado, então isto é só uma guarda.
                if (relativePath.Length > MaxStoragePathLength || safeName.Length > MaxOriginalFileNameLength)
                    throw new InvalidOperationException(
                        $"Caminho/nome do arquivo do histórico excede o limite da coluna ({relativePath.Length}/{MaxStoragePathLength}).");
                var absolutePath = Path.Combine(_rootPath, relativePath);

                if (!SafePathResolver.IsInsideBase(_rootPath, absolutePath))
                    throw new InvalidOperationException("Caminho de armazenamento do histórico escapa da raiz configurada.");

                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                await File.WriteAllBytesAsync(absolutePath, file.Content, cancellationToken);

                records.Add(new FiscalAnalysisFileRecord(
                    fileId, analysisId, file.Role, safeName, file.Content.Length,
                    ComputeSha256(file.Content), null, relativePath));
            }

            var layoutGuid = NormalizeLayoutGuid(registration.LayoutGuid);
            var analysis = new FiscalAnalysisRecord(
                analysisId, registration.WorkspaceId, registration.OwnerUserId, now,
                now.AddDays(_options.EffectiveRetentionDays),
                registration.Source, registration.LayoutMode,
                layoutGuid, Truncate(registration.LayoutName, MaxLayoutNameLength), Truncate(registration.DetectedType, MaxDetectedTypeLength));

            await _store.CreateAsync(analysis, records, cancellationToken);

            _logger.LogInformation(
                "Análise fiscal registrada no histórico (analysis={AnalysisId}, workspace={WorkspaceId}, arquivos={FileCount}, bytes={TotalBytes}).",
                analysisId, registration.WorkspaceId, records.Count, records.Sum(r => r.SizeBytes));
            return true;
        }

        public Task<FiscalAnalysisPage> ListAsync(Guid workspaceId, Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken)
            => _store.ListAsync(workspaceId, ownerUserId, page, pageSize, _clock.GetUtcNow().UtcDateTime, cancellationToken);

        public Task<FiscalAnalysisDetail?> GetAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken cancellationToken)
            => _store.GetAsync(workspaceId, ownerUserId, analysisId, _clock.GetUtcNow().UtcDateTime, cancellationToken);

        public async Task<FiscalAnalysisFileContent> OpenFileAsync(
            Guid workspaceId, Guid ownerUserId, Guid analysisId, Guid fileId, CancellationToken cancellationToken)
        {
            var detail = await GetAsync(workspaceId, ownerUserId, analysisId, cancellationToken);
            var file = detail?.Files.FirstOrDefault(f => f.AnalysisFileId == fileId);
            if (file == null)
                return new FiscalAnalysisFileContent(FiscalAnalysisFileStatus.NotFound, null, null);

            var absolutePath = Path.Combine(_rootPath, file.StoragePath);
            if (!SafePathResolver.IsInsideBase(_rootPath, absolutePath) || !File.Exists(absolutePath))
            {
                _logger.LogWarning("Arquivo do histórico ausente em disco (analysis={AnalysisId}, file={FileId}).", analysisId, fileId);
                return new FiscalAnalysisFileContent(FiscalAnalysisFileStatus.NotFound, null, null);
            }

            var content = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
            if (!string.Equals(ComputeSha256(content), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Integridade quebrada: não serve o conteúdo (adulterado/corrompido).
                _logger.LogError("Divergência de sha256 no arquivo do histórico (analysis={AnalysisId}, file={FileId}) — não servido.", analysisId, fileId);
                return new FiscalAnalysisFileContent(FiscalAnalysisFileStatus.IntegrityFailure, null, null);
            }

            return new FiscalAnalysisFileContent(FiscalAnalysisFileStatus.Ok, file.OriginalFileName, content);
        }

        public async Task<bool> DeleteAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken cancellationToken)
        {
            var deleted = await _store.DeleteAsync(workspaceId, ownerUserId, analysisId, cancellationToken);
            if (!deleted)
                return false;

            // SQL primeiro; disco best-effort (sobra vira órfã e é varrida pela purga).
            DeleteDirectoryQuietly(GetAnalysisDirectory(workspaceId, analysisId));
            _logger.LogInformation("Análise fiscal {AnalysisId} removida pelo dono (workspace={WorkspaceId}).", analysisId, workspaceId);
            return true;
        }

        public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
        {
            var purged = 0;

            for (var batch = 0; batch < MaxPurgeBatchesPerCycle; batch++)
            {
                var expired = await _store.ListExpiredAsync(_clock.GetUtcNow().UtcDateTime, PurgeBatchSize, cancellationToken);
                if (expired.Count == 0)
                    break;

                var purgedInBatch = 0;
                foreach (var item in expired)
                {
                    try
                    {
                        await _store.DeleteByIdAsync(item.AnalysisId, cancellationToken);
                        DeleteDirectoryQuietly(GetAnalysisDirectory(item.WorkspaceId, item.AnalysisId));
                        purgedInBatch++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Falha ao purgar análise expirada {AnalysisId}.", item.AnalysisId);
                    }
                }

                purged += purgedInBatch;
                // Nada apagado neste lote = falha persistente; evita girar sobre os mesmos registros.
                if (purgedInBatch == 0 || expired.Count < PurgeBatchSize)
                    break;
            }

            // Órfãos de disco não contam como "análises purgadas" (não tinham linha).
            await SweepOrphanDirectoriesAsync(cancellationToken);
            return purged;
        }

        /// <summary>Apaga diretórios de análise em disco sem linha no SQL (mais velhos que a carência).</summary>
        private async Task<int> SweepOrphanDirectoriesAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_rootPath))
                return 0;

            try
            {
                var candidates = new List<(Guid AnalysisId, string Path)>();
                foreach (var workspaceDir in Directory.EnumerateDirectories(_rootPath))
                {
                    foreach (var analysisDir in Directory.EnumerateDirectories(workspaceDir))
                    {
                        if (!Guid.TryParse(Path.GetFileName(analysisDir), out var analysisId))
                            continue;
                        // Relógio do filesystem é o real (LastWriteTime), independente do TimeProvider.
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(analysisDir) < OrphanGracePeriod)
                            continue;
                        candidates.Add((analysisId, analysisDir));
                    }
                }

                if (candidates.Count == 0)
                    return 0;

                var existing = await _store.GetExistingIdsAsync(candidates.Select(c => c.AnalysisId).ToList(), cancellationToken);
                var removed = 0;
                foreach (var (analysisId, path) in candidates.Where(c => !existing.Contains(c.AnalysisId)))
                {
                    DeleteDirectoryQuietly(path);
                    removed++;
                }

                if (removed > 0)
                    _logger.LogInformation("Varredura removeu {OrphanCount} diretório(s) órfão(s) do histórico de análises.", removed);
                return removed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Falha na varredura de diretórios órfãos do histórico de análises.");
                return 0;
            }
        }

        private string GetAnalysisDirectory(Guid workspaceId, Guid analysisId)
            => Path.Combine(_rootPath, workspaceId.ToString(), analysisId.ToString());

        private void DeleteDirectoryQuietly(string path)
        {
            try
            {
                if (SafePathResolver.IsInsideBase(_rootPath, path) && Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Não foi possível apagar o diretório do histórico de análises (será varrido depois).");
            }
        }

        private static string ComputeSha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        /// <summary>Trunca no limite da coluna sem partir um par substituto UTF-16 ao meio.</summary>
        private static string? Truncate(string? value, int max)
        {
            if (value is not { Length: > 0 } || value.Length <= max)
                return value;
            var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
            return value[..cut];
        }

        /// <summary>
        /// LayoutGuid fora do padrão (vazio, caracteres estranhos ou acima do limite) não trunca —
        /// truncar geraria identificador falso. Vira null e o histórico segue com o LayoutName.
        /// </summary>
        private string? NormalizeLayoutGuid(string? layoutGuid)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid))
                return null;
            var trimmed = layoutGuid.Trim();
            if (trimmed.Length <= MaxLayoutGuidLength && ValidLayoutGuid.IsMatch(trimmed))
                return trimmed;
            _logger.LogWarning(
                "LayoutGuid inválido ignorado no histórico de análise (tamanho={Length}, máx {Max}).", trimmed.Length, MaxLayoutGuidLength);
            return null;
        }

        private static string SanitizeFileName(string originalFileName)
        {
            // Normaliza '\' para '/': em Linux Path.GetFileName NÃO trata a barra invertida como separador, então um nome
            // enviado por cliente Windows ("..\..\evil.txt") manteria o traversal no nome. Vale nas duas plataformas.
            var name = Path.GetFileName((originalFileName ?? string.Empty).Replace('\\', '/')); // remove componente de diretório/traversal.
            var extension = Path.GetExtension(name);
            var baseName = Path.GetFileNameWithoutExtension(name);
            var safeBase = InvalidFileNameChars.Replace(baseName, "_");
            if (safeBase.Length > 100) safeBase = safeBase[..100];
            var safeExt = InvalidFileNameChars.Replace(extension, "");
            if (safeExt.Length > 20) safeExt = safeExt[..20];
            return string.IsNullOrWhiteSpace(safeBase) ? $"arquivo{safeExt}" : $"{safeBase}{safeExt}";
        }
    }
}
