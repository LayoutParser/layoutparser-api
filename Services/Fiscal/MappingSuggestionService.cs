using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Llm;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Implementação de <see cref="IMappingSuggestionService"/> — Slice 3 (issue #230). Prompt novo,
    /// upstream do <c>RepairOrchestrator</c> (que sintetiza XSLT executável — não se aplica aqui).
    /// ✅ Issue #340 (F1): consome <see cref="ILlmProvider"/> via <see cref="LlmProviderResolver"/>
    /// em vez de <see cref="HttpClient"/>/<see cref="XmlAnalysis.OllamaOptions"/> diretos (mesmo
    /// motor Ollama por trás — ver ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md).
    /// Nunca nuvem (Gemini/OpenAI decomissionados — dado fiscal sensível, ver <c>security.md</c>);
    /// ✅ Issue #341 (F2): a sensibilidade NÃO é mais hardcoded incondicional — é resolvida a partir
    /// da <see cref="ArtifactProvenance"/> declarada em cada artefato via
    /// <see cref="ArtifactProvenance.ResolveSensitivity"/> (fail-closed: ausência/valor inválido
    /// resolve para <see cref="DataSensitivity.RealFiscalDocument"/>, nunca para
    /// <see cref="DataSensitivity.SyntheticOrAnonymized"/> por omissão — ADR §2.3).
    /// </summary>
    public sealed class MappingSuggestionService : IMappingSuggestionService
    {
        private readonly ILogger<MappingSuggestionService> _logger;
        private readonly LlmProviderResolver _providerResolver;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _artifactStorePath;

        // Estado do job em memória, mesmo espírito de AiCandidateStore — observável via GetStatusAsync,
        // sem persistir em SQL (não é dado de negócio, é telemetria de execução do job).
        private static readonly ConcurrentDictionary<Guid, SuggestionJobState> Jobs = new();
        private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> JobCancellations = new();

        // Idempotência por (draftId, hash do conteúdo dos artefatos): reenviar a mesma revisão não
        // duplica um job já em execução/concluído para o mesmo conteúdo-fonte.
        private static readonly ConcurrentDictionary<(Guid DraftId, string ArtifactsHash), Guid> IdempotencyIndex = new();

        /// <summary>Confiança mínima para nascer "proposed" — abaixo disso, a regra vira needs_input (spec §8: nunca inventar mapping silenciosamente).</summary>
        private const string MinimumConfidenceForProposed = "medium";

        public MappingSuggestionService(
            ILogger<MappingSuggestionService> logger,
            LlmProviderResolver providerResolver,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _providerResolver = providerResolver;
            _scopeFactory = scopeFactory;
            _artifactStorePath = configuration["ML:FiscalMappingPackagesPath"]
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLData", "FiscalMappingPackages");
        }

        public async Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken)
        {
            IReadOnlyList<ArtifactFileRef> artifacts;
            using (var scope = _scopeFactory.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IMappingDraftStore>();
                artifacts = await store.GetArtifactFilesForRevisionAsync(revisionId, cancellationToken);
            }

            var artifactsHash = ComputeArtifactsHash(artifacts);
            var idempotencyKey = (draftId, artifactsHash);

            if (IdempotencyIndex.TryGetValue(idempotencyKey, out var existingJobId) &&
                Jobs.TryGetValue(existingJobId, out var existingState) &&
                existingState.Status is SuggestionJobStatus.Queued or SuggestionJobStatus.Running)
            {
                _logger.LogInformation("Job de sugestão idempotente: reusando job {JobId} em execução para o draft {DraftId}.", existingJobId, draftId);
                return existingJobId;
            }

            var jobId = Guid.NewGuid();
            var state = new SuggestionJobState { JobId = jobId, Status = SuggestionJobStatus.Queued };
            Jobs[jobId] = state;
            IdempotencyIndex[idempotencyKey] = jobId;

            var jobCts = new CancellationTokenSource();
            JobCancellations[jobId] = jobCts;

            // ✅ Fire-and-forget real (dotnet-standards.md §Background work): nunca propaga exceção
            // para o chamador do POST .../suggestions, que já retornou 202 antes deste ponto.
            _ = Task.Run(async () =>
            {
                state.Status = SuggestionJobStatus.Running;
                try
                {
                    var proposals = await GenerateProposalsAsync(artifacts, jobCts.Token);

                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<IMappingDraftStore>();
                    await store.InsertProposedRulesAsync(draftId, jobId, proposals, jobCts.Token);

                    state.RulesCreated = proposals.Count;
                    state.Status = SuggestionJobStatus.Completed;
                }
                catch (OperationCanceledException)
                {
                    state.Status = SuggestionJobStatus.Canceled;
                    _logger.LogInformation("Job de sugestão {JobId} cancelado (draft={DraftId}).", jobId, draftId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha não tratada no job de sugestão {JobId} (draft={DraftId}).", jobId, draftId);
                    state.Status = SuggestionJobStatus.Failed;
                    state.Error = "Falha interna ao gerar sugestões";
                }
                finally
                {
                    JobCancellations.TryRemove(jobId, out _);
                }
            }, CancellationToken.None);

            return jobId;
        }

        public Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
            => Task.FromResult(Jobs.TryGetValue(jobId, out var state) ? state : null);

        public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
        {
            if (!JobCancellations.TryGetValue(jobId, out var cts))
                return Task.FromResult(false);

            cts.Cancel();
            return Task.FromResult(true);
        }

        /// <summary>
        /// Lê os artefatos (spec/xsd/sample — Kind já modelado no Slice 2) e chama o Ollama para
        /// propor regras estruturadas. Regra sem evidência suficiente nasce <c>needs_input</c>, nunca
        /// <c>proposed</c> com confiança fabricada (spec §8).
        /// </summary>
        private async Task<IReadOnlyList<MappingDraftRuleProposal>> GenerateProposalsAsync(
            IReadOnlyList<ArtifactFileRef> artifacts, CancellationToken cancellationToken)
        {
            var relevant = artifacts.Where(a => a.Kind is ArtifactKind.Spec or ArtifactKind.Xsd or ArtifactKind.Sample).ToList();
            if (relevant.Count == 0)
            {
                _logger.LogWarning("Job de sugestão sem artefatos spec/xsd/sample — nenhuma regra pode ser proposta com evidência real.");
                return Array.Empty<MappingDraftRuleProposal>();
            }

            var prompt = await BuildPromptAsync(relevant, cancellationToken);

            // ✅ issue #341: se QUALQUER artefato relevante não tiver proveniência explícita
            // "synthetic", o lote inteiro é tratado como RealFiscalDocument — um artefato real
            // misturado no prompt contamina a sensibilidade de tudo que foi enviado junto ao modelo.
            var sensitivity = relevant.Any(a => Models.Entities.Fiscal.ArtifactProvenance.ResolveSensitivity(a.Provenance) == DataSensitivity.RealFiscalDocument)
                ? DataSensitivity.RealFiscalDocument
                : DataSensitivity.SyntheticOrAnonymized;

            var llmRequest = new LlmRequest(prompt, sensitivity, JsonSchema: "\"json\"", Temperature: 0.0);
            var provider = _providerResolver.Resolve(sensitivity);

            LlmResponse response;
            try
            {
                response = await provider.GenerateAsync(llmRequest, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Degrada graciosamente (dotnet-standards.md §Resiliência): Ollama indisponível não
                // derruba o job — devolve zero propostas, o job termina "completed" com 0 regras.
                _logger.LogWarning(ex, "Ollama indisponível/timeout ao gerar sugestões de mapeamento.");
                return Array.Empty<MappingDraftRuleProposal>();
            }

            if (!response.Success)
            {
                _logger.LogWarning("Falha ao gerar sugestões de mapeamento via {Provider}: {Error}", provider.Name, response.ErrorMessage);
                return Array.Empty<MappingDraftRuleProposal>();
            }

            return ParseProposals(response.Text);
        }

        private async Task<string> BuildPromptAsync(IReadOnlyList<ArtifactFileRef> artifacts, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Você é um especialista em mapeamento fiscal (NFe/CTe) do ecossistema Sysmiddle.");
            sb.AppendLine("A partir dos artefatos abaixo (planilha de especificação, XSD do destino, amostra");
            sb.AppendLine("de origem), proponha regras de mapeamento ESTRUTURADAS — NUNCA escreva código");
            sb.AppendLine("XSLT/TCL executável, apenas a representação intermediária. Responda SOMENTE com");
            sb.AppendLine("um array JSON de objetos com os campos: sourceRefs (array), targetRefs (array),");
            sb.AppendLine("operation (copy|concat|lookup|conditional|constant), conditions (array), ");
            sb.AppendLine("transformations (array), cardinality (\"1:1\"|\"1:N\"|\"N:1\"), evidence (array de");
            sb.AppendLine("{kind, reference}), confidence (\"high\"|\"medium\"|\"low\"), questions (array de");
            sb.AppendLine("string — perguntas abertas quando a evidência for insuficiente). Se não houver");
            sb.AppendLine("evidência suficiente para uma regra, ainda assim devolva o objeto com confidence");
            sb.AppendLine("\"low\" e questions preenchido — NUNCA invente uma regra com confiança alta sem");
            sb.AppendLine("evidência real nos artefatos abaixo.");
            sb.AppendLine();
            sb.AppendLine(SysmiddleGenerationRules.PromptSection);

            foreach (var artifact in artifacts)
            {
                sb.AppendLine();
                sb.AppendLine($"ARTEFATO ({artifact.Kind} — {artifact.OriginalFileName}):");
                var text = await TryReadArtifactTextAsync(artifact, cancellationToken);
                sb.AppendLine(Truncate(text ?? "(conteúdo binário ou ilegível como texto)", 4000));
            }

            return sb.ToString();
        }

        private async Task<string?> TryReadArtifactTextAsync(ArtifactFileRef artifact, CancellationToken cancellationToken)
        {
            try
            {
                var absolutePath = Path.Combine(_artifactStorePath, artifact.StoragePath);
                if (!File.Exists(absolutePath))
                    return null;

                // XLSX é binário — não tenta ler como texto puro; XSD/sample são texto.
                if (artifact.Kind == ArtifactKind.Spec)
                    return $"(planilha binária, {new FileInfo(absolutePath).Length} bytes — conteúdo não extraído neste slice)";

                return await File.ReadAllTextAsync(absolutePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao ler artefato {ArtifactId} para o prompt de sugestão — tratado como ilegível.", artifact.ArtifactId);
                return null;
            }
        }

        /// <summary>
        /// Parseia a resposta do modelo. Qualquer regra sem evidência (array vazio) ou confiança abaixo
        /// de <see cref="MinimumConfidenceForProposed"/> nasce <c>needs_input</c>, nunca <c>proposed</c>
        /// — regra obrigatória do spec §8, aplicada aqui independentemente do que o modelo alegou.
        /// </summary>
        private static IReadOnlyList<MappingDraftRuleProposal> ParseProposals(string modelText)
        {
            if (string.IsNullOrWhiteSpace(modelText))
                return Array.Empty<MappingDraftRuleProposal>();

            var text = modelText.Trim().Replace("```json", "").Replace("```", "").Trim();
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start < 0 || end < 0 || end <= start)
                return Array.Empty<MappingDraftRuleProposal>();

            text = text.Substring(start, end - start + 1);

            List<RawProposal>? raw;
            try
            {
                raw = JsonSerializer.Deserialize<List<RawProposal>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                return Array.Empty<MappingDraftRuleProposal>();
            }

            if (raw is null)
                return Array.Empty<MappingDraftRuleProposal>();

            var result = new List<MappingDraftRuleProposal>();
            foreach (var item in raw)
            {
                var evidence = (item.Evidence ?? new())
                    .Select(e => new MappingDraftRuleEvidence(e.Kind ?? "unknown", e.Reference ?? ""))
                    .ToList();

                var confidence = string.IsNullOrWhiteSpace(item.Confidence) ? "low" : item.Confidence!.ToLowerInvariant();
                var hasEnoughEvidence = evidence.Count > 0 && confidence != "low";
                var status = hasEnoughEvidence ? MappingDraftRuleStatus.Proposed : MappingDraftRuleStatus.NeedsInput;

                var questions = item.Questions ?? new List<string>();
                if (status == MappingDraftRuleStatus.NeedsInput && questions.Count == 0)
                    questions = new List<string> { "Evidência insuficiente nos artefatos fornecidos — confirmar origem/destino manualmente." };

                result.Add(new MappingDraftRuleProposal(
                    item.SourceRefs ?? new List<string>(),
                    item.TargetRefs ?? new List<string>(),
                    item.Operation ?? "copy",
                    JsonSerializer.Serialize(item.Conditions ?? new List<object>()),
                    JsonSerializer.Serialize(item.Transformations ?? new List<object>()),
                    string.IsNullOrWhiteSpace(item.Cardinality) ? "1:1" : item.Cardinality!,
                    evidence,
                    confidence,
                    status,
                    questions));
            }

            return result;
        }

        private static string ComputeArtifactsHash(IReadOnlyList<ArtifactFileRef> artifacts)
        {
            var joined = string.Join('|', artifacts.Select(a => a.ArtifactId).OrderBy(id => id));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        }

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value.Substring(0, maxLength);

        private sealed class RawProposal
        {
            public List<string>? SourceRefs { get; set; }
            public List<string>? TargetRefs { get; set; }
            public string? Operation { get; set; }
            public List<object>? Conditions { get; set; }
            public List<object>? Transformations { get; set; }
            public string? Cardinality { get; set; }
            public List<RawEvidence>? Evidence { get; set; }
            public string? Confidence { get; set; }
            public List<string>? Questions { get; set; }
        }

        private sealed class RawEvidence
        {
            public string? Kind { get; set; }
            public string? Reference { get; set; }
        }
    }
}
