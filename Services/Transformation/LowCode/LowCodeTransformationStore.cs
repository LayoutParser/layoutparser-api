using LayoutParserApi.Services.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using LayoutParserApi.Models.Transformation;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Dono do store de transformações low-code: resolve o caminho (<c>ML:LowCodeTransformationsPath</c>),
    /// mantém o ÍNDICE DE LEITURA e serve como cache das execuções (spec §2).
    ///
    /// <para><b>Disco é a fonte da verdade.</b> Redis é acelerador OPCIONAL: pode não existir no
    /// servidor (a conexão é tentada uma única vez no startup do <c>Program.cs</c>) e qualquer falha
    /// dele é <c>LogWarning</c>, nunca quebra a operação. Todo o ganho tem de vir do disco.</para>
    ///
    /// <para><b>Nada aqui renomeia artefato.</b> Os arquivos <c>{sha}_{HHmmss}.*</c> continuam
    /// append-only (corpus de treino); o índice fica ao lado, em <c>index/</c>, e é sobrescrito a
    /// cada execução.</para>
    /// </summary>
    public class LowCodeTransformationStore
    {
        private readonly ILogger<LowCodeTransformationStore> _logger;
        private readonly IDatabase? _redis;
        private readonly bool _redisAvailable;
        private readonly string _storePath;
        private readonly string _indexPath;
        private readonly TimeSpan _cacheTtl;

        // Formato do ticket: "{sha256}.{layoutGuid}". Charset FIXO — o ticket vem do cliente e vira
        // nome de arquivo, então VALIDAMOS (recusa) em vez de sanitizar (que aceitaria entrada
        // hostil e tentaria consertá-la). Ver spec §2.5.
        private static readonly Regex TicketRegex = new(
            @"^[a-f0-9]{64}\.[A-Za-z0-9_\-]{1,64}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Mesmo charset da segunda metade do ticket: só emitimos ticket para layoutGuid que
        // sobrevive à validação de volta (senão o ticket emitido seria sempre recusado na leitura).
        private static readonly Regex LayoutGuidRegex = new(
            @"^[A-Za-z0-9_\-]{1,64}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // MapperGuid só compõe CHAVE DE REDIS (o caminho em disco vem do índice, não do cliente).
        // Fora deste charset, pulamos o Redis e usamos disco — degradar, não falhar.
        private static readonly Regex MapperGuidRegex = new(
            @"^[A-Za-z0-9_\-]{1,128}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // Mesmo encoder do resto do projeto — não escapa <, > e & (o índice não carrega XML hoje,
            // mas nomes de mapper podem trazer caracteres que o encoder default deformaria).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        public LowCodeTransformationStore(
            ILogger<LowCodeTransformationStore> logger,
            IConfiguration configuration,
            IOptions<LowCodeRunnerOptions> options,
            IConnectionMultiplexer? redis)
        {
            _logger = logger;

            _storePath = new StoragePaths(configuration).LowCodeTransformations;
            Directory.CreateDirectory(_storePath);
            _indexPath = Path.Combine(_storePath, "index");

            var horas = options.Value.TransformationCacheTtlHours;
            _cacheTtl = TimeSpan.FromHours(horas > 0 ? horas : 2);

            // ✅ Mesmo padrão de Redis opcional do MapperCacheService: sem Redis o serviço funciona,
            // só perde o atalho quente.
            if (redis != null && redis.IsConnected)
            {
                try
                {
                    _redis = redis.GetDatabase();
                    _redisAvailable = true;
                    _logger.LogInformation("LowCodeTransformationStore com Redis habilitado (TTL {TtlHours}h)", _cacheTtl.TotalHours);
                }
                catch (Exception ex)
                {
                    _redisAvailable = false;
                    _logger.LogWarning(ex, "Redis presente mas indisponivel para o store low-code — seguindo apenas por disco");
                }
            }
            else
            {
                _redisAvailable = false;
                _logger.LogInformation("LowCodeTransformationStore sem Redis — leitura/escrita apenas por disco");
            }
        }

        /// <summary>Raiz do store (onde ficam as pastas diárias de artefatos e o <c>index/</c>).</summary>
        public string StorePath => _storePath;

        /// <summary>Janela de frescor do cache-first (<c>LowCode:TransformationCacheTtlHours</c>, default 2h).</summary>
        public TimeSpan CacheTtl => _cacheTtl;

        // ─────────────────────────────── ticket ───────────────────────────────

        public static string ComputeSha256(string? input)
        {
            var bytes = Encoding.UTF8.GetBytes(input ?? "");
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        /// <summary>
        /// Monta o ticket <c>{sha256}.{layoutGuid}</c>. Retorna null quando o layoutGuid não cabe no
        /// charset do ticket — nesse caso não há ticket nem índice (degrada, não inventa nome).
        /// </summary>
        public static string? BuildTicket(string? sha256, string? layoutGuid)
        {
            if (string.IsNullOrWhiteSpace(sha256) || string.IsNullOrWhiteSpace(layoutGuid))
                return null;
            if (!LayoutGuidRegex.IsMatch(layoutGuid))
                return null;

            var ticket = $"{sha256}.{layoutGuid}";
            return TicketRegex.IsMatch(ticket) ? ticket : null;
        }

        public static string? BuildTicketFromContent(string? txtContent, string? layoutGuid)
            => BuildTicket(ComputeSha256(txtContent), layoutGuid);

        /// <summary>
        /// Valida o ticket por charset fixo e o quebra em (sha256, layoutGuid). <b>Não sanitiza</b>:
        /// qualquer coisa fora do formato é recusada inteira (<c>..</c>, separador de caminho, etc.).
        /// </summary>
        public static bool TryParseTicket(string? ticket, out string sha256, out string layoutGuid)
        {
            sha256 = "";
            layoutGuid = "";

            if (string.IsNullOrWhiteSpace(ticket) || !TicketRegex.IsMatch(ticket))
                return false;

            var separador = ticket.IndexOf('.');
            sha256 = ticket[..separador];
            layoutGuid = ticket[(separador + 1)..];
            return true;
        }

        // ─────────────────────────────── escrita ───────────────────────────────

        /// <summary>
        /// Marca a execução como em andamento (fase <c>"transforming"</c> — ver
        /// <see cref="LowCodeTransformationIndexEntry.TransformingStatus"/>, mesmo valor de fio de
        /// <see cref="LowCodeTransformationIndexEntry.ProcessingStatus"/>). Chamado só DEPOIS de
        /// haver mapper candidato — sem mapper não existe entrada (o ticket responde 404 e o parse
        /// já diz <c>not_applicable</c>). É o que permite ao front distinguir "ainda rodando" de
        /// "nunca existiu".
        /// </summary>
        public async Task WriteProcessingAsync(string sha256, string layoutGuid)
        {
            var entrada = new LowCodeTransformationIndexEntry
            {
                Status = LowCodeTransformationIndexEntry.TransformingStatus,
                CreatedAtUtc = DateTime.UtcNow,
                Sha256 = sha256,
                LayoutGuid = layoutGuid
            };

            await WriteEntryAsync(sha256, layoutGuid, entrada, bodies: null);
        }

        /// <summary>
        /// Grava a entrada final: disco primeiro (fonte da verdade), Redis depois (acelerador).
        /// <paramref name="bodies"/> mapeia MapperGuid → XML e alimenta o split do §2.4
        /// (manifesto leve numa chave, cada XML na sua) — no disco os XMLs já estão nos artefatos.
        /// </summary>
        /// <param name="falhouEstruturalmente">
        /// True quando NENHUM candidato do conjunto teve sucesso — fecha o índice como
        /// <see cref="LowCodeTransformationIndexEntry.FailedStatus"/> em vez de "completed" (spec
        /// §2, contrato aditivo 2026-08-27). Default false preserva o comportamento de sempre.
        /// </param>
        public async Task WriteCompletedAsync(
            string sha256,
            string layoutGuid,
            LowCodeTransformationIndexEntry entrada,
            IReadOnlyDictionary<string, string?>? bodies = null,
            bool falhouEstruturalmente = false)
        {
            entrada.Status = falhouEstruturalmente
                ? LowCodeTransformationIndexEntry.FailedStatus
                : LowCodeTransformationIndexEntry.CompletedStatus;
            entrada.CreatedAtUtc = DateTime.UtcNow;
            entrada.Sha256 = sha256;
            entrada.LayoutGuid = layoutGuid;

            await WriteEntryAsync(sha256, layoutGuid, entrada, bodies);
        }

        private async Task WriteEntryAsync(
            string sha256,
            string layoutGuid,
            LowCodeTransformationIndexEntry entrada,
            IReadOnlyDictionary<string, string?>? bodies)
        {
            if (!TryResolveIndexFile(sha256, layoutGuid, out var caminhoIndice))
            {
                _logger.LogWarning("Ticket invalido ao gravar indice low-code (layoutGuid={LayoutGuid}) — indice nao gravado", layoutGuid);
                return;
            }

            // ── Disco (obrigatório): falha aqui é Warning, mas a execução em si já aconteceu.
            try
            {
                Directory.CreateDirectory(_indexPath);
                var json = JsonSerializer.Serialize(entrada, JsonOptions);
                await File.WriteAllTextAsync(caminhoIndice, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao gravar indice de transformacao low-code (layoutGuid={LayoutGuid})", layoutGuid);
            }

            // ── Redis (opcional): write-through best effort.
            if (!_redisAvailable || _redis == null)
                return;

            try
            {
                var manifesto = JsonSerializer.Serialize(entrada, JsonOptions);
                await _redis.StringSetAsync(ManifestKey(sha256, layoutGuid), manifesto, _cacheTtl);

                if (bodies != null)
                {
                    foreach (var (mapperGuid, xml) in bodies)
                    {
                        if (xml == null || !MapperGuidRegex.IsMatch(mapperGuid))
                            continue;
                        await _redis.StringSetAsync(CandidateKey(sha256, layoutGuid, mapperGuid), xml, _cacheTtl);
                    }
                }
            }
            catch (Exception ex)
            {
                // Redis é acelerador: cair aqui não pode interferir em nada — o disco já tem tudo.
                _logger.LogWarning(ex, "Falha ao escrever cache Redis do store low-code (layoutGuid={LayoutGuid})", layoutGuid);
            }
        }

        // ─────────────────────────────── leitura ───────────────────────────────

        /// <summary>Lê a entrada do índice: Redis primeiro (quando houver), disco em seguida.</summary>
        public async Task<LowCodeTransformationIndexEntry?> ReadEntryAsync(string sha256, string layoutGuid)
        {
            if (!TryResolveIndexFile(sha256, layoutGuid, out var caminhoIndice))
                return null;

            if (_redisAvailable && _redis != null)
            {
                try
                {
                    var cached = await _redis.StringGetAsync(ManifestKey(sha256, layoutGuid));
                    if (cached.HasValue)
                    {
                        var doRedis = JsonSerializer.Deserialize<LowCodeTransformationIndexEntry>(cached.ToString(), JsonOptions);
                        if (doRedis != null)
                            return doRedis;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao ler manifesto no Redis — caindo para disco (layoutGuid={LayoutGuid})", layoutGuid);
                }
            }

            try
            {
                if (!File.Exists(caminhoIndice))
                    return null;

                // ✅ SCS0018 (issue #88): caminhoIndice já passou por TryResolveIndexFile
                // (BuildTicket valida sha256/layoutGuid por regex + TryResolveInsideStore
                // canonicaliza e confere prefixo contra _storePath) antes deste ponto — dupla
                // barreira que o SCS não reconhece como sanitizador.
#pragma warning disable SCS0018
                var json = await File.ReadAllTextAsync(caminhoIndice, Encoding.UTF8);
#pragma warning restore SCS0018
                return JsonSerializer.Deserialize<LowCodeTransformationIndexEntry>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler indice de transformacao low-code (layoutGuid={LayoutGuid})", layoutGuid);
                return null;
            }
        }

        /// <summary>Mesma leitura, a partir de um ticket já validado pelo chamador.</summary>
        public Task<LowCodeTransformationIndexEntry?> ReadEntryByTicketAsync(string ticket)
            => TryParseTicket(ticket, out var sha, out var layoutGuid)
                ? ReadEntryAsync(sha, layoutGuid)
                : Task.FromResult<LowCodeTransformationIndexEntry?>(null);

        /// <summary>
        /// Cache-first do <c>RunAsync</c>: devolve o resultado pronto de uma execução anterior do
        /// MESMO (documento, layout) — sem runner e sem SQL — ou null quando não há hit utilizável.
        ///
        /// <para>Não é hit quando: a execução ainda está rodando, terminou parcial (cancelada),
        /// não teve nenhum candidato bem-sucedido, passou da janela de frescor, ou algum XML
        /// referenciado sumiu do disco. Nesses casos vale mais rodar de novo do que servir metade.</para>
        /// </summary>
        public async Task<LowCodeAutoTransformResult?> TryGetCachedResultAsync(string sha256, string layoutGuid)
        {
            var entrada = await ReadEntryAsync(sha256, layoutGuid);
            if (entrada == null)
                return null;

            if (!string.Equals(entrada.Status, LowCodeTransformationIndexEntry.CompletedStatus, StringComparison.OrdinalIgnoreCase))
                return null;

            // Execução truncada por cancelamento não vira cache — senão o teto de 6s de UM upload
            // congelaria um resultado incompleto para todos os uploads idênticos seguintes.
            if (entrada.Partial)
                return null;

            if (entrada.Candidates.Count == 0 || !entrada.Candidates.Any(c => c.Success))
                return null;

            // Frescor: o artefato em disco não expira (é corpus de treino), mas PULAR O RUNNER com
            // base numa execução antiga é outra decisão — o catálogo de mappers pode ter mudado.
            if (DateTime.UtcNow - entrada.CreatedAtUtc > _cacheTtl)
                return null;

            var candidatos = new List<LowCodeCandidateResult>();
            foreach (var c in entrada.Candidates)
            {
                var resultado = new LowCodeCandidateResult
                {
                    MapperGuid = c.MapperGuid,
                    MapperName = c.MapperName,
                    TargetLayoutGuid = c.TargetLayoutGuid,
                    PackageGuid = c.PackageGuid,
                    Success = c.Success,
                    ErrorMessage = c.ErrorMessage,
                    OutputLength = c.OutputLength
                };

                if (c.Success)
                {
                    var xml = await ReadCandidateXmlAsync(entrada, sha256, layoutGuid, c);
                    if (xml == null)
                        return null; // artefato sumiu: melhor recalcular do que devolver conjunto quebrado

                    resultado.OutputXml = xml;
                    resultado.OutputLength = xml.Length;
                }

                candidatos.Add(resultado);
            }

            return new LowCodeAutoTransformResult
            {
                Applicable = true,
                MultiCandidate = candidatos.Count > 1,
                Candidates = candidatos
            };
        }

        /// <summary>Corpo (XML) de UM candidato: Redis primeiro, artefato em disco depois.</summary>
        public async Task<string?> ReadCandidateXmlAsync(
            LowCodeTransformationIndexEntry entrada,
            string sha256,
            string layoutGuid,
            LowCodeTransformationIndexCandidate candidato)
        {
            if (_redisAvailable && _redis != null && MapperGuidRegex.IsMatch(candidato.MapperGuid))
            {
                try
                {
                    var cached = await _redis.StringGetAsync(CandidateKey(sha256, layoutGuid, candidato.MapperGuid));
                    if (cached.HasValue)
                        return cached!;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao ler corpo de candidato no Redis — caindo para disco (mapper={MapperGuid})", candidato.MapperGuid);
                }
            }

            if (string.IsNullOrWhiteSpace(candidato.OutputFile) || string.IsNullOrWhiteSpace(entrada.DateFolder))
                return null;

            // Mesmo vindo do NOSSO índice, o caminho é canonicalizado e conferido contra a raiz do
            // store antes de abrir (defesa em profundidade: índice é arquivo, arquivo é editável).
            if (!TryResolveInsideStore(Path.Combine(entrada.DateFolder, candidato.OutputFile), out var caminho))
            {
                _logger.LogWarning("Artefato do candidato fora da raiz do store — leitura recusada (mapper={MapperGuid})", candidato.MapperGuid);
                return null;
            }

            try
            {
                return File.Exists(caminho) ? await File.ReadAllTextAsync(caminho, Encoding.UTF8) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler artefato do candidato (mapper={MapperGuid})", candidato.MapperGuid);
                return null;
            }
        }

        // ─────────────────────────────── caminhos e chaves ───────────────────────────────

        private bool TryResolveIndexFile(string sha256, string layoutGuid, out string caminho)
        {
            caminho = "";
            var ticket = BuildTicket(sha256, layoutGuid);
            if (ticket == null)
                return false;

            return TryResolveInsideStore(Path.Combine("index", $"{ticket}.json"), out caminho);
        }

        /// <summary>
        /// Canonicaliza e confere o prefixo contra a raiz do store. É a última barreira antes de
        /// qualquer <c>File.*</c>: entrada validada por charset já não deveria escapar, mas a
        /// verificação de prefixo é o que garante isso mesmo se a validação for afrouxada um dia.
        /// </summary>
        private bool TryResolveInsideStore(string caminhoRelativo, out string caminhoAbsoluto)
        {
            caminhoAbsoluto = "";
            try
            {
                var raiz = Path.GetFullPath(_storePath);
                if (!raiz.EndsWith(Path.DirectorySeparatorChar))
                    raiz += Path.DirectorySeparatorChar;

                var completo = Path.GetFullPath(Path.Combine(raiz, caminhoRelativo));
                if (!completo.StartsWith(raiz, StringComparison.OrdinalIgnoreCase))
                    return false;

                caminhoAbsoluto = completo;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Split do §2.4: manifesto (centenas de bytes, consultado sempre) separado do corpo de cada
        // candidato (KB a MB, consultado às vezes).
        private static string ManifestKey(string sha256, string layoutGuid)
            => $"lowcode:xform:{sha256}:{layoutGuid}";

        private static string CandidateKey(string sha256, string layoutGuid, string mapperGuid)
            => $"lowcode:xform:{sha256}:{layoutGuid}:cand:{mapperGuid}";
    }
}
