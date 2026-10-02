using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Adapter que executa a transformação pelo aplicativo low-code via runner x86 (processo externo).
    /// Motivo: as DLLs do SysMiddle possuem dependências/arquitetura (x86) incompatíveis com o processo do ASP.NET.
    /// </summary>
    public class LowCodeTransformationService
    {
        private readonly ILogger<LowCodeTransformationService> _logger;
        private readonly LowCodeRunnerOptions _opt;
        private readonly IConfiguration _configuration;

        // ✅ Registrado como Singleton no Program.cs → este semáforo vale para o PROCESSO INTEIRO da
        // API, não só para as N invocações paralelas de um único documento (Task.WhenAll do
        // multi-candidato). Se dois uploads diferentes chegarem ao mesmo tempo, ambos multi-candidato,
        // o limite de concorrência do runner ainda é respeitado no total. Ver LowCode:MaxConcurrentRunners.
        private readonly SemaphoreSlim _runnerSemaphore;
        private readonly LowCodeRunnerHttpClient? _httpRunner;

        public LowCodeTransformationService(
            ILogger<LowCodeTransformationService> logger,
            IOptions<LowCodeRunnerOptions> options,
            IConfiguration configuration,
            LowCodeRunnerHttpClient? httpRunner = null)
        {
            _httpRunner = httpRunner;
            _logger = logger;
            _opt = options.Value;
            _configuration = configuration;
            _runnerSemaphore = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrentRunners));
        }

        /// <summary>
        /// Executa a transformação no runner externo.
        ///
        /// <para><paramref name="cancellationToken"/> é levado a sério ponta a ponta: cancela a
        /// ESPERA POR SLOT (<c>SemaphoreSlim.WaitAsync</c>) e, se o processo já estiver rodando,
        /// mata o processo e libera o slot. Antes disso, desistir da espera (teto síncrono do
        /// <c>ParseController</c>) deixava o trabalho abandonado segurando um dos
        /// <c>MaxConcurrentRunners</c> — o gargalo é o slot, e nenhum cache resolve isso.</para>
        ///
        /// <para><c>virtual</c> de propósito: é o ponto de substituição dos testes que precisam
        /// exercitar o serviço chamador sem depender do <c>.exe</c> x86 do Sysmiddle.</para>
        /// </summary>
        public virtual async Task<string> TransformAsync(
            string inputContent,
            string? mapperId = null,
            string? mapperName = null,
            string? fileName = null,
            string? package = null,
            string? globalFolder = null,
            string? sysmiddleDir = null,
            CancellationToken cancellationToken = default)
        {
            // ✅ Usar CorrelationId da request (se existir) para rastreabilidade end-to-end
            var correlationId = LayoutParserApi.Services.Logging.CorrelationContext.CurrentId ?? Guid.NewGuid().ToString("N");
            package ??= _opt.Package;
            globalFolder ??= _opt.GlobalFolder;
            sysmiddleDir ??= _opt.SysmiddleDir;
            mapperName ??= _opt.DefaultMapperName;

            // ✅ Issue #641: BaseUrl configurada => runner HTTP (contrato v1), sem .exe nem arquivos temporários.
            if (LowCodeRunnerHttpClient.IsConfigured(_opt) && _httpRunner != null)
            {
                if (string.IsNullOrWhiteSpace(mapperId) && string.IsNullOrWhiteSpace(mapperName))
                    throw new InvalidOperationException("Informe mapperId ou mapperName (ou configure LowCode:DefaultMapperName)");
                // O contrato exige EXATAMENTE um: mapperId (MapperGuid) tem precedência.
                var idHttp = string.IsNullOrWhiteSpace(mapperId) ? null : mapperId;
                var nameHttp = idHttp == null ? mapperName : null;

                await _runnerSemaphore.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogInformation("Executando transformação low-code via HTTP: corr={CorrelationId} mapperId={MapperId}, mapperName={MapperName}",
                        LayoutParserApi.Services.Logging.LogMessageSanitizer.Sanitize(correlationId, maxLength: 200), LayoutParserApi.Services.Logging.LogMessageSanitizer.SanitizeOptional(idHttp, 200), LayoutParserApi.Services.Logging.LogMessageSanitizer.SanitizeOptional(nameHttp, 200));
                    var resp = await _httpRunner.TransformAsync(inputContent ?? "", fileName ?? "document.txt", idHttp, nameHttp, correlationId, cancellationToken);
                    if (resp.Warnings.Count > 0)
                        _logger.LogInformation("Runner low-code HTTP devolveu {WarningCount} warning(s) (corr={CorrelationId}, durationMs={DurationMs})",
                            resp.Warnings.Count, correlationId, resp.DurationMs);
                    return resp.Output;
                }
                finally
                {
                    _runnerSemaphore.Release();
                }
            }

            if (string.IsNullOrWhiteSpace(_opt.RunnerPath))
                throw new LowCodeRunnerException(LowCodeRunnerException.RunnerUnavailable, "runner indisponível neste host");
            if (string.IsNullOrWhiteSpace(sysmiddleDir))
                throw new InvalidOperationException("LowCode:SysmiddleDir não configurado");
            if (string.IsNullOrWhiteSpace(globalFolder))
                throw new InvalidOperationException("LowCode:GlobalFolder não configurado");
            if (string.IsNullOrWhiteSpace(mapperId) && string.IsNullOrWhiteSpace(mapperName))
                throw new InvalidOperationException("Informe mapperId ou mapperName (ou configure LowCode:DefaultMapperName)");

            var tempDir = Path.Combine(Path.GetTempPath(), "layoutparser-lowcode");
            Directory.CreateDirectory(tempDir);

            var inputPath = Path.Combine(tempDir, $"in_{Guid.NewGuid():N}.txt");
            var outputPath = Path.Combine(tempDir, $"out_{Guid.NewGuid():N}.xml");
            await File.WriteAllTextAsync(inputPath, inputContent ?? "", Encoding.UTF8);

            // ✅ Todos os logs na mesma pasta do API (Logging:File:Directory)
            var logsBase = _configuration["Logging:File:Directory"] ?? Path.Combine(tempDir, "runner-logs");
            Directory.CreateDirectory(logsBase);
            var runnerLogFile = Path.Combine(logsBase, "layoutparserlowcoderunner.log");

            var args = new List<string>
            {
                "--sysmiddleDir", sysmiddleDir,
                "--globalFolder", globalFolder,
                "--package", package ?? "",
                "--inputFile", inputPath,
                "--outputFile", outputPath,
                "--fileName", fileName ?? Path.GetFileName(inputPath),
                "--correlationId", correlationId,
                "--runnerLogFile", runnerLogFile
            };

            if (!string.IsNullOrWhiteSpace(mapperId))
            {
                args.Add("--mapperId");
                args.Add(mapperId);
            }
            else
            {
                args.Add("--mapperName");
                args.Add(mapperName!);
            }

            var psi = new ProcessStartInfo
            {
                FileName = _opt.RunnerPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // ✅ ArgumentList evita reinterpretação de shell (cs/command-line-injection) — cada item
            // vai como argumento literal pro processo filho, sem parsing/escaping manual de string.
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            _logger.LogInformation("Executando transformação low-code: corr={CorrelationId} mapperId={MapperId}, mapperName={MapperName}, runnerLog={RunnerLogFile}",
                correlationId, mapperId, mapperName, runnerLogFile);

            // ✅ O literal aqui só cobre config explicitamente inválida (0 ou negativa) — casada com o
            // default de LowCodeRunnerOptions.RunnerTimeoutSeconds de propósito: as duas eram 15s e as
            // duas eram inviáveis (a transformação real leva 48-137s medidos). Se divergirem, um
            // `RunnerTimeoutSeconds: 0` no appsettings volta a matar toda transformação no meio.
            var timeoutSeconds = _opt.RunnerTimeoutSeconds > 0 ? _opt.RunnerTimeoutSeconds : 180;

            LowCodeRunnerExecution execucao;

            // ✅ Limite de concorrência do runner (processo inteiro da API — ver comentário no campo).
            // Com token: quem desistiu da espera não entra na fila do semáforo nem toma um slot
            // depois — cancelar aqui é o que impede a fila de crescer sem ninguém observando.
            await _runnerSemaphore.WaitAsync(cancellationToken);
            try
            {
                execucao = await ExecuteRunnerProcessAsync(psi, timeoutSeconds, correlationId, mapperId, mapperName, cancellationToken);
            }
            finally
            {
                _runnerSemaphore.Release();
            }

            if (execucao.ExitCode != 0)
            {
                string runnerLog = "";
                try { if (File.Exists(runnerLogFile)) runnerLog = await File.ReadAllTextAsync(runnerLogFile, Encoding.UTF8); } catch { }
                _logger.LogError("Runner low-code falhou (corr={CorrelationId}, exit={ExitCode}). stderr={Stderr}\nrunnerLog:\n{RunnerLog}",
                    correlationId, execucao.ExitCode, execucao.StandardError, runnerLog);

                // ✅ stderr fica no log, NÃO na exceção: esta mensagem vira ErrorMessage do candidato
                // e sai no payload 200 do parse (spec §3.1) — stderr do runner carrega caminho de
                // disco do servidor. O correlationId é a ponte para o detalhe completo no log.
                throw new Exception($"Low-code runner falhou (exit={execucao.ExitCode}, corr={correlationId})");
            }

            if (!File.Exists(outputPath))
            {
                _logger.LogError("Runner low-code nao gerou o arquivo de saida (corr={CorrelationId}, outputPath={OutputPath}). stdout={Stdout}",
                    correlationId, outputPath, execucao.StandardOutput);

                // Mesmo motivo acima: o caminho absoluto do outputFile vazava para o cliente.
                throw new Exception($"Runner low-code nao gerou o arquivo de saida (corr={correlationId})");
            }

            var output = await File.ReadAllTextAsync(outputPath, Encoding.UTF8);

            // Best effort cleanup
            TryDelete(inputPath);
            TryDelete(outputPath);

            return output;
        }

        /// <summary>
        /// Ciclo de vida do processo externo, já dentro do slot do semáforo.
        ///
        /// <para><c>protected virtual</c> para que os testes possam exercitar a disciplina de slot e
        /// de cancelamento sem depender do <c>.exe</c> x86 (que não existe na máquina de teste). A
        /// lógica sob teste — semáforo, token, liberação do slot — continua sendo a real.</para>
        /// </summary>
        protected virtual async Task<LowCodeRunnerExecution> ExecuteRunnerProcessAsync(
            ProcessStartInfo psi,
            int timeoutSeconds,
            string correlationId,
            string? mapperId,
            string? mapperName,
            CancellationToken cancellationToken)
        {
            using var p = Process.Start(psi);
            if (p == null)
                throw new Exception("Falha ao iniciar processo do runner low-code");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            var exitTask = p.WaitForExitAsync();

            // ✅ Timeout cobre o ciclo de vida INTEIRO do processo (leitura de stdout/stderr + exit),
            // não só a chamada a WaitForExitAsync isoladamente: se só a espera de exit tivesse
            // CancellationToken, uma leitura de stream travada (processo não fecha os handles)
            // ainda escaparia do timeout — Task.WhenAll(stdout, stderr, exit) só resolve quando o
            // processo de fato morre/fecha os pipes, então corremos essa combinação contra um
            // Task.Delay e, se o delay vencer, matamos o processo nós mesmos.
            //
            // O delay agora corre também contra o token do chamador: cancelado, ele completa na
            // hora e caímos no MESMO caminho de kill — matar o processo é o que devolve o slot.
            var allTask = Task.WhenAll(stdoutTask, stderrTask, exitTask);
            using var esperaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var esperaTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), esperaCts.Token);

            var winner = await Task.WhenAny(allTask, esperaTask);

            if (winner != allTask)
            {
                var canceladoPeloChamador = cancellationToken.IsCancellationRequested;

                if (canceladoPeloChamador)
                {
                    _logger.LogWarning(
                        "Runner low-code cancelado pelo chamador (corr={CorrelationId}, mapperId={MapperId}, mapperName={MapperName}) — matando processo e liberando slot",
                        correlationId, mapperId, mapperName);
                }
                else
                {
                    _logger.LogError(
                        "Runner low-code excedeu o timeout de {TimeoutSeconds}s (corr={CorrelationId}, mapperId={MapperId}, mapperName={MapperName}) — matando processo",
                        timeoutSeconds, correlationId, mapperId, mapperName);
                }

                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception killEx) { _logger.LogWarning(killEx, "Falha ao matar processo do runner low-code (corr={CorrelationId})", correlationId); }

                // Best effort: dá uma janela curta pro kill liberar os streams antes de desistir,
                // só para não deixar as Tasks de leitura penduradas sem observação.
                try { await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(2))); } catch { }

                if (canceladoPeloChamador)
                    throw new OperationCanceledException($"Runner low-code cancelado (corr={correlationId})", cancellationToken);

                throw new TimeoutException($"Runner low-code excedeu o timeout de {timeoutSeconds}s (corr={correlationId}, mapperId={mapperId}, mapperName={mapperName})");
            }

            // Encerra o Task.Delay pendente (senão o timer fica vivo até o fim do timeout).
            esperaCts.Cancel();

            // allTask já concluída — propaga eventual exceção real de leitura/exit, se houver.
            await allTask;
            return new LowCodeRunnerExecution(p.ExitCode, stdoutTask.Result, stderrTask.Result);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>Saída bruta de UMA execução do runner externo (código de saída + streams).</summary>
    public readonly record struct LowCodeRunnerExecution(int ExitCode, string StandardOutput, string StandardError);
}


