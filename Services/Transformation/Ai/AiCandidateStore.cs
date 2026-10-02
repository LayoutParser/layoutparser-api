using LayoutParserApi.Services.Storage;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Persistência mínima do job do pathway IA por ticket (docs/architecture/pathway-ia-execute-candidates.md
    /// §4.2 — "não repetir o mesmo buraco" do Job 1 do pipeline de métricas, que não persistia nada).
    /// Cache em memória (rápido para <c>GetStatusAsync</c> em polling) com fonte de verdade em disco,
    /// mesmo espírito do <see cref="LowCode.LowCodeTransformationStore"/> mas sem a complexidade de
    /// índice/Redis — o volume de jobs IA é muito menor.
    /// </summary>
    /// <remarks>
    /// <b>Particionamento por usuário (issue #92):</b> antes desta mudança a chave era só o ticket —
    /// como o dicionário/arquivo é global ao processo, qualquer usuário que soubesse (ou adivinhasse)
    /// um ticket de outro conseguia ler o <c>ia-status</c> dele, inclusive XML fiscal em progresso. Com
    /// a issue #93 prestes a abrir esses endpoints além do papel "admin", isso deixaria de ser teórico.
    /// Toda operação agora exige <c>userId</c> (o <c>ICurrentUser.Name</c> resolvido no controller — a
    /// store em si é <c>Singleton</c> e não pode injetar <c>ICurrentUser</c> Scoped diretamente, por
    /// isso o chamador passa o valor já resolvido). Em disco, cada usuário tem sua própria subpasta
    /// (<c>_storePath/{usuário}/{ticket}.json</c>) — mantém a varredura de TTL do
    /// <see cref="AiCandidateStoreCleanupBackgroundService"/> igualmente eficiente (ainda é um
    /// <c>EnumerateFiles</c> recursivo simples) sem precisar codificar usuário+ticket num nome só.
    /// </remarks>
    public class AiCandidateStore
    {
        private readonly ILogger<AiCandidateStore> _logger;
        private readonly string _storePath;
        private readonly TimeSpan _ttl;
        private readonly int _maxStoredTickets;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ConcurrentDictionary<string, StoredEntry> _memory = new();

        // Serializa a poda por limite de tamanho: Set() concorrente de tickets diferentes não
        // precisa disputar I/O de enumeração/deleção ao mesmo tempo — best-effort, não é um lock
        // de correção (perder uma corrida aqui só adia a poda para o próximo Set()).
        private readonly object _sizeLimitGate = new();

        // Separador de controle (US, ) para compor a chave de memória userId+ticket sem
        // ambiguidade — não é digitável e não deve vir de um header HTTP/nome de usuário normal.
        private const char KeySeparator = '\u001F';

        // Usuário anônimo/ausente cai neste bucket fixo em vez de virar pasta vazia/raiz — mantém o
        // isolamento (nunca cai na pasta de outro usuário real) sem lançar por falta de identidade.
        private const string AnonymousBucket = "_anonimo";

        /// <summary>Ticket em memória + carimbo de quando foi escrito (base do TTL da issue #51).</summary>
        private readonly record struct StoredEntry(AiCandidateStatus Status, DateTime StoredAtUtc);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        /// <param name="clock">
        /// Relógio injetável (issue #51). Em produção fica nulo e cai em <c>DateTimeOffset.UtcNow</c>;
        /// nos testes permite envelhecer tickets sem <c>Task.Delay</c> real. É opcional de propósito
        /// para o registro no DI continuar sendo o <c>AddSingleton&lt;AiCandidateStore&gt;()</c> simples.
        /// </param>
        public AiCandidateStore(
            ILogger<AiCandidateStore> logger,
            IOptions<AiTransformationCandidateOptions> options,
            Func<DateTimeOffset>? clock = null,
            StoragePaths? storagePaths = null)
        {
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            var ttlHoras = options.Value.TicketTtlHours;
            _ttl = TimeSpan.FromHours(ttlHoras > 0 ? ttlHoras : AiTransformationCandidateOptions.DefaultTicketTtlHours);
            var maxTickets = options.Value.MaxStoredTickets;
            _maxStoredTickets = maxTickets > 0 ? maxTickets : AiTransformationCandidateOptions.DefaultMaxStoredTickets;
            _storePath = options.Value.StorePath
                ?? (storagePaths ?? new StoragePaths(new ConfigurationBuilder().Build())).AiCandidates;

            try
            {
                Directory.CreateDirectory(_storePath);
            }
            catch (Exception ex)
            {
                // Degrade: sem disco o job ainda funciona via memória (perde durabilidade entre restarts).
                _logger.LogWarning(ex, "Falha ao criar diretório da store do pathway IA em {StorePath}", _storePath);
            }
        }

        /// <summary>Janela de retenção efetiva do ticket (<c>AiTransformationCandidate:TicketTtlHours</c>).</summary>
        public TimeSpan Ttl => _ttl;

        public void Set(string userId, string ticket, AiCandidateStatus status)
        {
            var agoraUtc = _clock().UtcDateTime;
            var memoryKey = BuildMemoryKey(userId, ticket);

            // Grava em disco ANTES de publicar em memória: quem consulta GetStatusAsync via
            // polling só pode ver "pronto" depois que o arquivo já está completo e fechado.
            // Antes desta correção a ordem era invertida (memória primeiro) e sob contenção de
            // I/O um segundo Set() concorrente no mesmo ticket colidia no WriteAllText enquanto
            // o primeiro ainda escrevia — reproduzido pelo @lp-qa como IOException intermitente
            // ("being used by another process") ao rodar a suíte 2x seguidas. Preservada ao
            // particionar por usuário (issue #92): a ordem disco→memória não muda, só a chave.
            try
            {
                var path = ResolvePath(userId, ticket);
                if (path != null)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOptions), Encoding.UTF8);

                    // Idade do ticket em disco = LastWriteTime do arquivo. Carimbar explicitamente
                    // (em vez de deixar o do sistema) mantém disco e memória no MESMO relógio —
                    // inclusive o injetado nos testes — e evita ter que sujar o DTO de status, que
                    // é o corpo da resposta do endpoint, com um campo de controle interno.
                    // Try próprio: se só o carimbo falhar (antivírus/indexador segurando o handle),
                    // a persistência em si deu certo — logar "falha ao persistir" seria mentira, e
                    // o arquivo fica com o LastWriteTime do sistema, que em produção é o mesmo instante.
                    try
                    {
                        File.SetLastWriteTimeUtc(path, agoraUtc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Falha ao carimbar a data do ticket do pathway IA (ticket={Ticket})", ticket);
                    }
                }
            }
            catch (Exception ex)
            {
                // Degrade: mesmo sem durabilidade em disco, ainda publicamos em memória — perder o
                // job inteiro por uma falha de I/O transitória seria pior que perder durabilidade
                // entre restarts (mesmo espírito da falha de Directory.CreateDirectory no ctor).
                _logger.LogWarning(ex, "Falha ao persistir em disco o status do pathway IA (ticket={Ticket})", ticket);
            }

            _memory[memoryKey] = new StoredEntry(status, agoraUtc);

            EnforceSizeLimit();
        }

        /// <summary>
        /// Poda os tickets mais antigos quando a store estoura <see cref="_maxStoredTickets"/>
        /// (issue #51 — limite de tamanho, complementar ao TTL). Best-effort: qualquer falha de
        /// I/O aqui é uma limpeza perdida, não um erro de escrita — o ticket que acabou de ser
        /// gravado pelo <c>Set</c> já está persistido antes deste ponto.
        /// </summary>
        private void EnforceSizeLimit()
        {
            if (!Monitor.TryEnter(_sizeLimitGate))
                return; // Outra thread já está podando; não vale a pena esperar no caminho de escrita.

            try
            {
                if (!Directory.Exists(_storePath))
                    return;

                // Arquivo é a fonte de verdade da idade (mesmo critério do RemoveExpired): mais
                // barato que manter um segundo índice em memória só para isso.
                var arquivos = Directory.EnumerateFiles(_storePath, "*.json", SearchOption.AllDirectories)
                    .Select(caminho =>
                    {
                        try { return (Caminho: caminho, EscritoEmUtc: File.GetLastWriteTimeUtc(caminho)); }
                        catch { return (Caminho: caminho, EscritoEmUtc: DateTime.MaxValue); }
                    })
                    .OrderBy(item => item.EscritoEmUtc)
                    .ToList();

                var excedente = arquivos.Count - _maxStoredTickets;
                if (excedente <= 0)
                    return;

                foreach (var (caminho, _) in arquivos.Take(excedente))
                {
                    try
                    {
                        // userId/ticket vêm da própria estrutura de pasta (ResolvePath): subpasta =
                        // usuário, nome do arquivo sem extensão = ticket — reconstrói a chave de
                        // memória sem precisar guardar um índice à parte.
                        var ticket = Path.GetFileNameWithoutExtension(caminho);
                        var userId = Path.GetFileName(Path.GetDirectoryName(caminho)) ?? AnonymousBucket;

                        File.Delete(caminho);
                        _memory.TryRemove(BuildMemoryKey(userId, ticket), out _);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Falha ao podar ticket excedente do pathway IA (limite={Limite}): {Arquivo}",
                            _maxStoredTickets, caminho);
                    }
                }

                _logger.LogInformation(
                    "Limite de tamanho da store do pathway IA excedido — {Removidos} ticket(s) mais antigo(s) removido(s) (limite={Limite})",
                    excedente, _maxStoredTickets);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao aplicar o limite de tamanho da store do pathway IA");
            }
            finally
            {
                Monitor.Exit(_sizeLimitGate);
            }
        }

        public AiCandidateStatus? Get(string userId, string ticket)
        {
            var limiteUtc = ExpiracaoUtc();
            var memoryKey = BuildMemoryKey(userId, ticket);

            if (_memory.TryGetValue(memoryKey, out var cached))
            {
                if (cached.StoredAtUtc > limiteUtc)
                    return cached.Status;

                // Ticket vencido: sai da memória na hora, custo O(1). Nada de varrer diretório
                // aqui — a limpeza em disco é do BackgroundService, o caminho de request (polling
                // de GetStatusAsync) não pode pagar por I/O de manutenção.
                _memory.TryRemove(memoryKey, out _);
                return null;
            }

            try
            {
                var path = ResolvePath(userId, ticket);
                if (path == null || !File.Exists(path))
                    return null;

                // Ticket lido do disco (ex.: depois de um restart da API) não ganha vida nova: o
                // TTL é absoluto a partir da última escrita, não deslizante por leitura.
                var escritoEmUtc = File.GetLastWriteTimeUtc(path);
                if (escritoEmUtc <= limiteUtc)
                    return null;

                var json = File.ReadAllText(path, Encoding.UTF8);
                var status = JsonSerializer.Deserialize<AiCandidateStatus>(json, JsonOptions);
                if (status != null)
                    _memory[memoryKey] = new StoredEntry(status, escritoEmUtc);
                return status;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler do disco o status do pathway IA (ticket={Ticket})", ticket);
                return null;
            }
        }

        /// <summary>
        /// Remove tickets vencidos das DUAS camadas — memória e disco (issue #51). Chamado pelo
        /// <see cref="AiCandidateStoreCleanupBackgroundService"/>, fora do caminho de request.
        /// Best-effort ponta a ponta: nenhuma falha de I/O aborta a varredura nem sobe para o host.
        /// Varre TODOS os usuários (issue #92) — não sabe nem precisa saber de quem é cada ticket,
        /// só da idade do arquivo/entrada em memória.
        /// </summary>
        /// <remarks>
        /// Síncrono de propósito: é I/O de arquivo (<c>EnumerateFiles</c>/<c>Delete</c>), que não
        /// tem contrapartida async real no BCL — envolver em <c>Task.Run</c> só criaria indireção.
        /// Roda numa thread de background, nunca numa request.
        /// </remarks>
        public AiCandidateStoreCleanupResult RemoveExpired()
        {
            var limiteUtc = ExpiracaoUtc();
            var memoriaRemovida = 0;
            var arquivosRemovidos = 0;

            foreach (var entrada in _memory)
            {
                if (entrada.Value.StoredAtUtc > limiteUtc)
                    continue;

                // Remoção condicional (par chave+valor): se um Set() concorrente republicou o
                // ticket entre a leitura e a remoção, a entrada nova não é derrubada por engano.
                if (_memory.TryRemove(entrada))
                    memoriaRemovida++;
            }

            try
            {
                if (Directory.Exists(_storePath))
                {
                    // AllDirectories: cada usuário tem sua subpasta (ResolvePath) — a varredura
                    // continua um único EnumerateFiles recursivo, sem precisar listar usuários antes.
                    foreach (var arquivo in Directory.EnumerateFiles(_storePath, "*.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(arquivo) > limiteUtc)
                                continue;

                            File.Delete(arquivo);
                            arquivosRemovidos++;
                        }
                        catch (Exception ex)
                        {
                            // Best-effort por arquivo (mesmo espírito do CleanupOldRuns do Job 1 de
                            // métricas): um arquivo travado/em uso não pode abortar a varredura toda.
                            _logger.LogDebug(ex, "Falha ao remover ticket vencido do pathway IA: {Arquivo}", arquivo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao varrer o diretório da store do pathway IA em {StorePath}", _storePath);
            }

            return new AiCandidateStoreCleanupResult(memoriaRemovida, arquivosRemovidos);
        }

        /// <summary>Instante-limite: o que foi escrito até aqui está vencido.</summary>
        private DateTime ExpiracaoUtc() => _clock().UtcDateTime - _ttl;

        // Chave de memória userId+ticket — separador de controle evita colisão tipo
        // userId="a", ticket="b:c" vs userId="a:b", ticket="c".
        private static string BuildMemoryKey(string userId, string ticket)
            => $"{SafeUserBucket(userId)}{KeySeparator}{ticket}";

        private static string SafeUserBucket(string? userId)
            => string.IsNullOrWhiteSpace(userId) ? AnonymousBucket : userId;

        // Ticket já vem validado por LowCodeTransformationStore.TryParseTicket no controller — aqui
        // só canonicalizamos e conferimos o prefixo, mesma defesa em profundidade do store low-code.
        // userId sanitizado do mesmo jeito (whitelist alfanumérico + separadores comuns) porque pode
        // vir de um header confiado (domínio\usuário, e-mail) e vira nome de subpasta em disco.
        private string? ResolvePath(string userId, string ticket)
        {
            try
            {
                var safeUser = SanitizeForFileSystem(SafeUserBucket(userId));
                var safeTicket = SanitizeForFileSystem(ticket);
                if (string.IsNullOrWhiteSpace(safeUser) || string.IsNullOrWhiteSpace(safeTicket))
                    return null;

                var root = Path.GetFullPath(_storePath);
                if (!root.EndsWith(Path.DirectorySeparatorChar))
                    root += Path.DirectorySeparatorChar;

                var full = Path.GetFullPath(Path.Combine(root, safeUser, $"{safeTicket}.json"));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeForFileSystem(string value)
            => string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@'));
    }

    /// <summary>Resultado de uma varredura de limpeza da store do pathway IA (issue #51).</summary>
    /// <param name="TicketsEmMemoria">Tickets vencidos removidos do cache em memória.</param>
    /// <param name="ArquivosEmDisco">Arquivos de ticket vencidos removidos do disco.</param>
    public record AiCandidateStoreCleanupResult(int TicketsEmMemoria, int ArquivosEmDisco)
    {
        public int Total => TicketsEmMemoria + ArquivosEmDisco;
    }
}
