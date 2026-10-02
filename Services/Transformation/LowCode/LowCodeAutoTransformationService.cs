using System.Text;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Database;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Executa transformação low-code automaticamente (sem necessidade de chamada manual)
    /// usando o layoutGuid selecionado no front para identificar o MapperGuid no banco.
    /// </summary>
    public class LowCodeAutoTransformationService
    {
        private readonly ILogger<LowCodeAutoTransformationService> _logger;
        // ✅ Singleton não pode injetar serviço Scoped direto (quebra a validação de DI em Development).
        // Usamos IServiceScopeFactory e resolvemos o MapperDatabaseService dentro do escopo do background.
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LowCodeTransformationService _lowCode;
        private readonly LowCodeTransformationStore _store;
        private readonly LowCodeRunnerOptions _opt;
        private readonly string _storePath;

        public LowCodeAutoTransformationService(
            ILogger<LowCodeAutoTransformationService> logger,
            IServiceScopeFactory scopeFactory,
            LowCodeTransformationService lowCode,
            LowCodeTransformationStore store,
            IOptions<LowCodeRunnerOptions> options)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _lowCode = lowCode;
            _store = store;
            _opt = options.Value;
            // ✅ O store passou a ser o dono do caminho (ele também escreve o índice de leitura ao
            // lado dos artefatos) — aqui só reaproveitamos a mesma raiz, sem segunda resolução de
            // config que pudesse divergir.
            _storePath = store.StorePath;
        }

        public Task RunInBackgroundAsync(
            string layoutGuid,
            string layoutName,
            string txtContent,
            string detectedType,
            string originalFileName,
            LowCodePositionalMetadata? positionalMetadata = null)
        {
            // fire-and-forget (chamador não quer/precisa do resultado — usar RunAsync quando precisar)
            return Task.Run(async () =>
            {
                try
                {
                    await TransformAndPersistAsync(layoutGuid, layoutName, txtContent, detectedType, originalFileName, positionalMetadata, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha no auto-transform low-code");
                }
            });
        }

        /// <summary>
        /// Mesma operação de <see cref="RunInBackgroundAsync"/>, mas retornando o resultado (ao invés de
        /// fire-and-forget) para permitir entrega SÍNCRONA com teto de tempo por quem chama (ex.:
        /// <c>ParseController</c>, que aguarda até <c>LowCode:SyncDeliveryTimeoutSeconds</c>).
        ///
        /// <para><b>Semântica de cancelamento (mudou de propósito — spec §1.1).</b> Antes, estourar o
        /// teto síncrono não interrompia nada: "o trabalho não se perde mesmo se o chamador parar de
        /// esperar". O problema é que ele também não chegava a lugar nenhum — o store era write-only,
        /// nenhum cliente HTTP conseguia lê-lo — enquanto continuava segurando um dos
        /// <c>MaxConcurrentRunners</c> e atrasando o próximo upload. Agora o
        /// <paramref name="cancellationToken"/> do chamador interrompe a execução e devolve o slot; em
        /// troca, o que já ficou pronto é gravado no índice e passa a ser consultável por ticket
        /// (<c>GET /api/parse/transformations/{ticket}</c>), marcado como parcial.</para>
        ///
        /// <para>Falhas estruturais (ex.: banco fora do ar ao buscar candidatos) propagam como exceção — cabe ao
        /// chamador decidir como degradar (nunca deve virar 500 do endpoint principal de parse).</para>
        /// </summary>
        public Task<LowCodeAutoTransformResult> RunAsync(
            string layoutGuid,
            string layoutName,
            string txtContent,
            string detectedType,
            string originalFileName,
            LowCodePositionalMetadata? positionalMetadata = null,
            CancellationToken cancellationToken = default)
            => TransformAndPersistAsync(layoutGuid, layoutName, txtContent, detectedType, originalFileName, positionalMetadata, cancellationToken);

        private async Task<LowCodeAutoTransformResult> TransformAndPersistAsync(
            string layoutGuid,
            string layoutName,
            string txtContent,
            string detectedType,
            string originalFileName,
            LowCodePositionalMetadata? positionalMetadata,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid) || string.IsNullOrWhiteSpace(txtContent))
                return new LowCodeAutoTransformResult { Applicable = false };

            var sha = LowCodeTransformationStore.ComputeSha256(txtContent);

            // ✅ Cache-first (spec §1.2): ANTES de consultar mappers e ANTES de tocar no runner.
            // O mesmo documento chegava a rodar o runner duas vezes — uma no parse e outra no clique
            // de "Gerar Transformação XML" (execute-candidates) — multiplicando por 2 o custo de N
            // candidatos. Com hit, o clique é instantâneo: sem runner, sem SQL.
            var emCache = await _store.TryGetCachedResultAsync(sha, layoutGuid);
            if (emCache != null)
            {
                _logger.LogInformation(
                    "AutoTransform low-code servido do cache para layout={LayoutName} ({LayoutGuid}) — {Count} candidato(s), runner nao invocado",
                    layoutName, layoutGuid, emCache.Candidates.Count);
                return emCache;
            }

            // Selecionar candidatos (filtrado por ProjectId e AllowedPackageGuids)
            // ✅ Escopo próprio para o serviço Scoped dentro do fire-and-forget
            using var scope = _scopeFactory.CreateScope();
            var mapperDb = scope.ServiceProvider.GetRequiredService<MapperDatabaseService>();

            // ✅ Seleção multi-candidato: busca TODOS os candidatos plausíveis (mesma prioridade de
            // sempre — input match > target match > mais recente), já deduplicados por MapperGuid.
            var ranked = await mapperDb.GetRankedMapperCandidatesForLayoutGuidAsync(
                layoutGuid,
                _opt.ProjectId,
                _opt.AllowedPackageGuids);

            if (ranked.Count == 0 || string.IsNullOrWhiteSpace(ranked[0].MapperGuid))
            {
                _logger.LogWarning("Nenhum mapper encontrado para layoutGuid={LayoutGuid} nos pacotes permitidos", layoutGuid);
                return new LowCodeAutoTransformResult { Applicable = false };
            }

            // ✅ Só a partir daqui existe trabalho de verdade — e só a partir daqui a entrada de
            // índice faz sentido. Sem mapper, o ticket responde 404 e o parse já diz
            // "not_applicable": 404 e "não aplicável" contam a mesma história, sem estado órfão.
            await _store.WriteProcessingAsync(sha, layoutGuid);

            // N==1: comportamento EXATAMENTE igual ao de sempre (sem overhead, sem mudança de shape
            // dos artefatos persistidos). N>1 depois de deduplicar por MapperGuid = candidatos
            // genuinamente distintos (não apenas desempate de recência do mesmo mapper).
            if (ranked.Count == 1)
            {
                var mapper = ranked[0];
                try
                {
                    var single = await TransformSingleAndPersistAsync(
                        mapper, layoutGuid, layoutName, txtContent, detectedType, originalFileName, positionalMetadata, sha, cancellationToken);
                    return new LowCodeAutoTransformResult
                    {
                        Applicable = true,
                        MultiCandidate = false,
                        Candidates = new List<LowCodeCandidateResult> { single }
                    };
                }
                catch (Exception ex)
                {
                    // ✅ Mesmo tratamento de falha isolada por candidato do caminho multi (não propaga
                    // pro chamador síncrono como exceção não tratada) — mas SEM alterar a semântica de
                    // persistência em disco de sempre: se a transformação falhar aqui (ex.: timeout do
                    // runner), nenhum ARTEFATO é persistido, exatamente como antes desta mudança.
                    _logger.LogWarning(ex,
                        "Falha na transformação low-code (candidato único) mapper={MapperGuid} ({MapperName}) para layout={LayoutName} ({LayoutGuid})",
                        mapper.MapperGuid, mapper.Name, layoutName, layoutGuid);

                    var falha = new LowCodeCandidateResult
                    {
                        MapperGuid = mapper.MapperGuid,
                        MapperName = mapper.Name,
                        TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        PackageGuid = mapper.PackageGuid,
                        Success = false,
                        // Saneado: este texto sai no payload 200 do parse (spec §3.1).
                        ErrorMessage = LowCodeErrorSanitizer.ForWire(ex),
                        ErrorCode = LowCodeRunnerException.CodeOf(ex)
                    };

                    // O índice FECHA mesmo em falha: sem isto o "processing" gravado acima ficaria
                    // eterno e o front voltaria a ter um rótulo que nunca resolve.
                    await EscreverIndiceAsync(sha, layoutGuid, baseName: null, dateFolder: null,
                        new[] { (candidato: falha, outputFile: (string?)null) },
                        parcial: cancellationToken.IsCancellationRequested);

                    return new LowCodeAutoTransformResult
                    {
                        Applicable = true,
                        MultiCandidate = false,
                        Candidates = new List<LowCodeCandidateResult> { falha }
                    };
                }
            }

            var topN = ranked.Take(Math.Max(1, _opt.MultiCandidateTopN)).ToList();
            _logger.LogInformation(
                "AutoTransform low-code: {Count} candidatos genuinamente plausíveis para layout={LayoutName} ({LayoutGuid}) — capado em top-{TopN}",
                topN.Count, layoutName, layoutGuid, _opt.MultiCandidateTopN);

            var candidates = await TransformMultiCandidateAndPersistAsync(
                topN, layoutGuid, layoutName, txtContent, detectedType, originalFileName, positionalMetadata, sha, cancellationToken);
            return new LowCodeAutoTransformResult { Applicable = true, MultiCandidate = true, Candidates = candidates };
        }

        /// <summary>
        /// Caminho de hoje (N==1), inalterado na persistência: 1 mapper, 1 transformação, 1 conjunto de
        /// artefatos. Passou a retornar o <see cref="LowCodeCandidateResult"/> correspondente (além de
        /// persistir) para permitir entrega síncrona via <see cref="RunAsync"/> — se a transformação
        /// falhar, a exceção propaga (igual sempre fez) e quem persiste o tratamento é o chamador.
        /// </summary>
        private async Task<LowCodeCandidateResult> TransformSingleAndPersistAsync(
            Mapper mapper,
            string layoutGuid,
            string layoutName,
            string txtContent,
            string detectedType,
            string originalFileName,
            LowCodePositionalMetadata? positionalMetadata,
            string sha,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("AutoTransform low-code: layout={LayoutName} ({LayoutGuid}) mapper={MapperName} ({MapperGuid})",
                layoutName, layoutGuid, mapper.Name, mapper.MapperGuid);

            // Executar low-code
            var lowCodeXml = await _lowCode.TransformAsync(
                txtContent,
                mapperId: mapper.MapperGuid,
                mapperName: null,
                fileName: originalFileName,
                cancellationToken: cancellationToken);

            // Persistir para aprendizado contínuo
            var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
            var folder = Path.Combine(_storePath, dateFolder);
            Directory.CreateDirectory(folder);

            var baseName = $"{sha}_{DateTime.UtcNow:HHmmss}";
            var metaPath = Path.Combine(folder, $"{baseName}.meta.json");
            var inPath = Path.Combine(folder, $"{baseName}.input.txt");
            var outputFile = $"{baseName}.lowcode.xml";
            var outPath = Path.Combine(folder, outputFile);

            // ✅ SCS0018 (issue #88): inPath/outPath/metaPath (abaixo) são montados só com
            // _storePath + dateFolder (DateTime.UtcNow) + baseName (sha do conteúdo + hora) —
            // nenhum segmento vem cru da requisição.
#pragma warning disable SCS0018
            await File.WriteAllTextAsync(inPath, txtContent, Encoding.UTF8);
            await File.WriteAllTextAsync(outPath, lowCodeXml ?? "", Encoding.UTF8);
#pragma warning restore SCS0018

            var meta = LowCodeDatasetMetaBuilder.AddPositionalMetadata(new Dictionary<string, object?>
            {
                ["createdAtUtc"] = DateTime.UtcNow,
                ["layoutGuid"] = layoutGuid,
                ["layoutName"] = layoutName,
                ["detectedType"] = detectedType,
                ["originalFileName"] = originalFileName,
                ["mapperGuid"] = mapper.MapperGuid,
                ["mapperName"] = mapper.Name,
                ["packageGuid"] = mapper.PackageGuid,
                ["projectId"] = mapper.ProjectId,
                ["sha256"] = sha,
                ["inputLength"] = txtContent.Length,
                ["outputLength"] = (lowCodeXml ?? "").Length
            }, positionalMetadata);
            var json = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            // ✅ SCS0018 (issue #88): mesma justificativa de inPath/outPath acima — metaPath também
            // é montado só a partir de _storePath/dateFolder/baseName, internos.
#pragma warning disable SCS0018
            await File.WriteAllTextAsync(metaPath, json, Encoding.UTF8);
#pragma warning restore SCS0018

            var candidato = new LowCodeCandidateResult
            {
                MapperGuid = mapper.MapperGuid,
                MapperName = mapper.Name,
                TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                PackageGuid = mapper.PackageGuid,
                Success = true,
                OutputXml = lowCodeXml,
                OutputLength = (lowCodeXml ?? "").Length,
                // ✅ Issue #141/#138: reexpõe o mapper já decifrado (nenhuma consulta SQL nova) para o
                // controller compor fieldMappings/sectionMappings sem repetir
                // GetRankedMapperCandidatesForLayoutGuidAsync.
                DecryptedMapperContent = mapper.DecryptedContent
            };

            // ✅ Índice de leitura ao lado dos artefatos (spec §2.3): é o que torna o store
            // consultável. Nenhum artefato foi renomeado — o esquema append-only {sha}_{HHmmss}
            // continua sendo o que protege o histórico de treino.
            await EscreverIndiceAsync(sha, layoutGuid, baseName, dateFolder,
                new[] { (candidato, (string?)outputFile) },
                parcial: false);

            return candidato;
        }

        /// <summary>
        /// Caminho multi-candidato (N&gt;1 mapeadores genuinamente plausíveis): roda a transformação
        /// low-code contra CADA candidato em paralelo (Task.WhenAll) e persiste todos os N resultados,
        /// cada um tagueado com MapperGuid/Name, TargetLayoutGuid e indicador de sucesso/erro.
        /// Resiliência: uma falha de candidato individual é capturada e não derruba os demais.
        /// Retorna a lista de resultados (além de persistir) para permitir entrega síncrona via <see cref="RunAsync"/>.
        /// </summary>
        private async Task<List<LowCodeCandidateResult>> TransformMultiCandidateAndPersistAsync(
            List<Mapper> candidates,
            string layoutGuid,
            string layoutName,
            string txtContent,
            string detectedType,
            string originalFileName,
            LowCodePositionalMetadata? positionalMetadata,
            string sha,
            CancellationToken cancellationToken)
        {
            var tasks = candidates.Select(async mapper =>
            {
                try
                {
                    var xml = await _lowCode.TransformAsync(
                        txtContent,
                        mapperId: mapper.MapperGuid,
                        mapperName: null,
                        fileName: originalFileName,
                        cancellationToken: cancellationToken);

                    return new LowCodeCandidateResult
                    {
                        MapperGuid = mapper.MapperGuid,
                        MapperName = mapper.Name,
                        TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        PackageGuid = mapper.PackageGuid,
                        Success = true,
                        OutputXml = xml,
                        OutputLength = (xml ?? "").Length,
                        // ✅ Issue #141/#138: idem TransformSingleAndPersistAsync — mapper já decifrado,
                        // sem nova consulta SQL para compor fieldMappings/sectionMappings no controller.
                        DecryptedMapperContent = mapper.DecryptedContent
                    };
                }
                catch (Exception ex)
                {
                    // ✅ Falha de UM candidato não derruba os demais — capturada aqui, dentro da própria
                    // task, para que Task.WhenAll não propague a exceção e aborte o restante.
                    // Cancelamento entra por aqui também: o candidato interrompido vira falha, e o
                    // conjunto é marcado como parcial no índice (não vira cache).
                    _logger.LogWarning(ex,
                        "Falha na transformação low-code do candidato mapper={MapperGuid} ({MapperName}) para layout={LayoutName} ({LayoutGuid})",
                        mapper.MapperGuid, mapper.Name, layoutName, layoutGuid);

                    return new LowCodeCandidateResult
                    {
                        MapperGuid = mapper.MapperGuid,
                        MapperName = mapper.Name,
                        TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        PackageGuid = mapper.PackageGuid,
                        Success = false,
                        // Saneado: este texto sai no payload 200 do parse (spec §3.1).
                        ErrorMessage = LowCodeErrorSanitizer.ForWire(ex),
                        ErrorCode = LowCodeRunnerException.CodeOf(ex)
                    };
                }
            });

            var results = await Task.WhenAll(tasks);

            _logger.LogInformation(
                "AutoTransform low-code multi-candidato: layout={LayoutName} ({LayoutGuid}) {SuccessCount}/{Total} candidatos OK",
                layoutName, layoutGuid, results.Count(r => r.Success), results.Length);

            // Persistir para aprendizado contínuo: 1 input compartilhado + 1 meta.json com o array de
            // candidatos + 1 arquivo de saída por candidato bem-sucedido.
            var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
            var folder = Path.Combine(_storePath, dateFolder);
            Directory.CreateDirectory(folder);

            var baseName = $"{sha}_{DateTime.UtcNow:HHmmss}";
            var metaPath = Path.Combine(folder, $"{baseName}.meta.json");
            var inPath = Path.Combine(folder, $"{baseName}.input.txt");

            // ✅ SCS0018 (issue #88): mesma justificativa do caminho single-candidato acima —
            // inPath/metaPath (abaixo) só usam _storePath/dateFolder/baseName, internos.
#pragma warning disable SCS0018
            await File.WriteAllTextAsync(inPath, txtContent, Encoding.UTF8);
#pragma warning restore SCS0018

            var candidateMeta = new List<object>();
            var paraIndice = new List<(LowCodeCandidateResult candidato, string? outputFile)>();
            for (int i = 0; i < results.Length; i++)
            {
                var r = results[i];
                string? outputFile = null;
                if (r.Success)
                {
                    outputFile = $"{baseName}.cand{i}_{SanitizeForFileName(r.MapperGuid)}.lowcode.xml";
                    await File.WriteAllTextAsync(Path.Combine(folder, outputFile), r.OutputXml ?? "", Encoding.UTF8);
                }

                paraIndice.Add((r, outputFile));

                candidateMeta.Add(new
                {
                    mapperGuid = r.MapperGuid,
                    mapperName = r.MapperName,
                    targetLayoutGuid = r.TargetLayoutGuid,
                    packageGuid = r.PackageGuid,
                    success = r.Success,
                    outputFile,
                    outputLength = r.Success ? (r.OutputXml ?? "").Length : 0,
                    errorMessage = r.Success ? null : r.ErrorMessage
                });
            }

            var meta = LowCodeDatasetMetaBuilder.AddPositionalMetadata(new Dictionary<string, object?>
            {
                ["createdAtUtc"] = DateTime.UtcNow,
                ["layoutGuid"] = layoutGuid,
                ["layoutName"] = layoutName,
                ["detectedType"] = detectedType,
                ["originalFileName"] = originalFileName,
                ["sha256"] = sha,
                ["inputLength"] = txtContent.Length,
                ["multiCandidate"] = true,
                ["candidateCount"] = results.Length,
                ["successCount"] = results.Count(r => r.Success),
                ["candidates"] = candidateMeta
            }, positionalMetadata);
            var json = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            // ✅ SCS0018 (issue #88): metaPath idem — sem segmento controlável pelo request.
#pragma warning disable SCS0018
            await File.WriteAllTextAsync(metaPath, json, Encoding.UTF8);
#pragma warning restore SCS0018

            // ✅ Índice de leitura (spec §2.3). "parcial" quando o chamador cancelou: o que ficou
            // pronto continua consultável por ticket, mas um conjunto truncado NUNCA vira cache.
            await EscreverIndiceAsync(sha, layoutGuid, baseName, dateFolder, paraIndice,
                parcial: cancellationToken.IsCancellationRequested);

            return results.ToList();
        }

        /// <summary>
        /// Fecha o índice de leitura da execução (disco + Redis opcional). Nunca lança: falha de
        /// índice é degradação de leitura, não pode derrubar uma transformação que já aconteceu.
        ///
        /// <para>✅ Fecha como <see cref="LowCodeTransformationIndexEntry.FailedStatus"/> (em vez de
        /// "completed") quando existe ao menos um candidato e NENHUM teve sucesso — antes o front
        /// via "completed" com todo mundo em erro e tinha que inferir "deu tudo errado" varrendo o
        /// array de candidatos (spec §2, contrato aditivo 2026-08-27).</para>
        /// </summary>
        private async Task EscreverIndiceAsync(
            string sha,
            string layoutGuid,
            string? baseName,
            string? dateFolder,
            IEnumerable<(LowCodeCandidateResult candidato, string? outputFile)> candidatos,
            bool parcial)
        {
            try
            {
                var entrada = new LowCodeTransformationIndexEntry
                {
                    BaseName = baseName,
                    DateFolder = dateFolder,
                    Partial = parcial
                };

                var bodies = new Dictionary<string, string?>();
                var candidatosLista = candidatos as ICollection<(LowCodeCandidateResult candidato, string? outputFile)>
                    ?? candidatos.ToList();
                foreach (var (c, outputFile) in candidatosLista)
                {
                    entrada.Candidates.Add(new LowCodeTransformationIndexCandidate
                    {
                        MapperGuid = c.MapperGuid,
                        MapperName = c.MapperName,
                        TargetLayoutGuid = c.TargetLayoutGuid,
                        PackageGuid = c.PackageGuid,
                        Success = c.Success,
                        OutputFile = outputFile,
                        OutputLength = c.OutputLength,
                        ErrorMessage = c.ErrorMessage
                    });

                    if (c.Success && c.OutputXml != null && !string.IsNullOrWhiteSpace(c.MapperGuid))
                        bodies[c.MapperGuid] = c.OutputXml;
                }

                var falhouEstruturalmente = candidatosLista.Count > 0 && candidatosLista.All(x => !x.candidato.Success);

                await _store.WriteCompletedAsync(sha, layoutGuid, entrada, bodies, falhouEstruturalmente);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao fechar indice de transformacao low-code (layoutGuid={LayoutGuid})", layoutGuid);
            }
        }

        private static string SanitizeForFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(s.Where(c => !invalid.Contains(c)).ToArray());
        }
    }
}
