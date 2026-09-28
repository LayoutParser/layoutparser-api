using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Health;
using LayoutParserApi.Services.Generation;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Security;
using LayoutParserApi.Services.Testing;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Validation;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.IO.Compression;

using Serilog;
using Serilog.Events;
using Serilog.Context;
using LayoutParserApi.Services.Logging;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

// ✅ Serviço Windows nativo: sob o SCM o processo inicia com CWD = System32, e o código
// resolve caminhos relativos via Directory.GetCurrentDirectory() em vários pontos
// (ex.: DocumentController, diretório de logs). Fixar o CWD na pasta do executável como
// PRIMEIRA instrução garante que o serviço se comporte igual à execução manual.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Bootstrap logger for errors before Serilog is configured
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ✅ Permite rodar como serviço Windows nativo (substitui o NSSM); integra o host ao
    // ciclo de vida do SCM. Fora do SCM (dotnet run / console) é no-op — zero impacto.
    builder.Host.UseWindowsService();

    // ✅ Hardening da senha do SQL em repouso (ver docs/architecture/runbook-hardening-senha-sql-em-repouso.md).
    // Por padrão, o ASP.NET Core só carrega user-secrets quando IsDevelopment() é true — o que
    // deixaria o mecanismo (DPAPI via user-secrets, escopo CurrentUser do usuário do serviço)
    // inútil em produção. Habilitamos aqui fora do ambiente Development. CreateBuilder(args) já
    // registrou appsettings.json/appsettings.{env}.json e env vars antes deste ponto; como
    // AddUserSecrets é adicionado por último, ele passa a ter a maior precedência — é o
    // comportamento desejado: uma vez migrada (passo 4 do runbook), a senha em user-secrets
    // é a fonte da verdade e a env var do serviço Windows pode ser removida. optional:true evita
    // erro caso o secrets.json ainda não exista no host.
    if (!builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddUserSecrets<Program>(optional: true);
    }

    // Configure Serilog for file and console logging
    var logDirectory = builder.Configuration["Logging:File:Directory"] ?? "Logs";
    // Nome base do arquivo (conforme requisito)
    var logFileName = builder.Configuration["Logging:File:FileName"] ?? "layoutparserapi.log";
    // ✅ Default subiu de 10 para 30: com RollingInterval.Day a unidade de retenção passa a ser
    // "dias" (antes eram ~10 arquivos de fileSizeLimitBytes cada); 30 dias é retenção razoável
    // e barata em disco pro volume de log desta API.
    var retainedFileCountLimit = int.TryParse(builder.Configuration["Logging:File:RetainedFileCountLimit"], out var r) ? r : 30;
    var fileSizeLimitKb = int.TryParse(builder.Configuration["Logging:File:FileSizeLimitKB"], out var kb) ? kb : 2049;
    var fileSizeLimitBytes = (long)fileSizeLimitKb * 1024L;

    // Ensure log directory exists and is writable
    try
    {
        Console.WriteLine($"[BOOTSTRAP] Configuring log directory: {logDirectory}");
        Console.WriteLine($"[BOOTSTRAP] Current working directory: {Directory.GetCurrentDirectory()}");

        if (string.IsNullOrEmpty(logDirectory))
        {
            logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Console.WriteLine($"[BOOTSTRAP] Log directory was empty, using default: {logDirectory}");
        }

        if (!Directory.Exists(logDirectory))
        {
            Console.WriteLine($"[BOOTSTRAP] Creating log directory: {logDirectory}");
            Directory.CreateDirectory(logDirectory);
            Console.WriteLine($"[BOOTSTRAP] Log directory created successfully");
        }
        else
        {
            Console.WriteLine($"[BOOTSTRAP] Log directory already exists: {logDirectory}");
        }

        // Test write permissions
        var testFile = Path.Combine(logDirectory, "test-write-permissions.tmp");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            Console.WriteLine($"[BOOTSTRAP] Log directory is writable: {logDirectory}");
        }
        catch (Exception writeEx)
        {
            Console.WriteLine($"[BOOTSTRAP] WARNING: Cannot write to log directory '{logDirectory}': {writeEx.Message}");
            // Fallback to current directory
            logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);
            
            Console.WriteLine($"[BOOTSTRAP] Using fallback log directory: {logDirectory}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BOOTSTRAP] ERROR: Failed to setup log directory '{logDirectory}': {ex.Message}");
        Console.WriteLine($"[BOOTSTRAP] Stack trace: {ex.StackTrace}");
        // Fallback to current directory
        logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        try
        {
            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);
            
            Console.WriteLine($"[BOOTSTRAP] Using fallback log directory: {logDirectory}");
        }
        catch
        {
            Console.WriteLine($"[BOOTSTRAP] CRITICAL: Cannot create fallback log directory. Logging may not work.");
        }
    }

    // Configure Serilog with error handling
    try
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // ✅ Origem padrão de toda entrada é "Backend"; o endpoint de ingestão de log do
            // cliente (LogsController.PostClientLog) sobrescreve para "Frontend" só no escopo
            // da própria requisição via LogContext.PushProperty (mesmo padrão do CorrelationId).
            .Enrich.WithProperty("Source", "Backend")
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, logFileName),
                // ✅ FIX (diagnóstico Aria): antes era RollingInterval.Infinite + rollOnFileSizeLimit,
                // que rotaciona só por tamanho e gera sufixo numérico sequencial (ex.: "..._150.log"),
                // sem nenhum timestamp no nome — o que estava sendo confundido com data errada.
                // RollingInterval.Day nomeia o arquivo com a data (ex.: "layoutparserapi20260819.log")
                // nativamente, e continua combinável com rollOnFileSizeLimit (protege contra arquivo
                // gigante dentro do mesmo dia). UnifiedLogReaderService já lê por glob de prefixo,
                // então tolera múltiplos arquivos rotacionados por data sem alteração.
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFileCountLimit,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();

        builder.Host.UseSerilog();
        Log.Information("Serilog configured successfully. Log directory: {LogDirectory}", logDirectory);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BOOTSTRAP] ERROR: Failed to configure Serilog: {ex.Message}");
        Console.WriteLine($"[BOOTSTRAP] Stack trace: {ex.StackTrace}");
        // Continue with default logging
    }

    Log.Information("Starting LayoutParserApi initialization...");

    // Add services to the container.
    // Configurar opções JSON para não escapar caracteres XML/HTML
    // Isso preserva o XML intacto no JSON (não converte < para \u003C, etc.)
    // ✅ Issue #32: enforcement por papel em cima da identidade que o TrustedIdentityMiddleware já
    // populava em HttpContext.User (pronto desde o P2, mas sem consumidor até agora). O esquema
    // "TrustedHeader" não autentica nada por conta própria — só formaliza o que o middleware
    // preencheu para o AuthorizationMiddleware conseguir desafiar (401)/negar (403) via
    // [Authorize(Roles = "...")] nos controllers marcados.
    // ✅ ADR M2M (docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md), Parte 1:
    // segundo AuthenticationScheme, PARALELO ao TrustedHeader acima — não o estende. TrustedHeader
    // confia por REDE (loopback = BFF); ServiceClient confia por CRIPTOGRAFIA (assinatura do JWT
    // contra o tenant Entra). São dois modelos de confiança diferentes, por desenho.
    //
    // Esquema "SmartAuth" (AddPolicyScheme) decide por requisição qual dos dois autentica, sem
    // exigir AuthenticationSchemes= explícito em cada [Authorize] existente (nenhum atributo foi
    // alterado). Authority/Audience só existem quando o App Registration "de serviço" for criado
    // no Entra (pré-requisito externo, fora do alcance de qualquer agente) — até lá,
    // serviceClientOptions.IsConfigured é false e o SmartAuthSchemeSelector sempre cai em
    // TrustedHeader, ou seja, o comportamento de hoje não muda.
    var serviceClientOptions = builder.Configuration
        .GetSection(ServiceClientAuthenticationOptions.SectionName)
        .Get<ServiceClientAuthenticationOptions>() ?? new ServiceClientAuthenticationOptions();
    var serviceClientConfigured = serviceClientOptions.IsConfigured;

    var authenticationBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = SmartAuthenticationDefaults.SchemeName;
            options.DefaultAuthenticateScheme = SmartAuthenticationDefaults.SchemeName;
            options.DefaultChallengeScheme = SmartAuthenticationDefaults.SchemeName;
        })
        .AddPolicyScheme(SmartAuthenticationDefaults.SchemeName, "TrustedHeader ou ServiceClient (M2M)", options =>
        {
            options.ForwardDefaultSelector = context =>
                SmartAuthSchemeSelector.Select(context.Request.Headers.Authorization.ToString(), serviceClientConfigured);
        })
        .AddScheme<AuthenticationSchemeOptions, TrustedHeaderAuthenticationHandler>(
            TrustedHeaderAuthenticationHandler.SchemeName, options => { });

    if (serviceClientConfigured)
    {
        authenticationBuilder.AddJwtBearer(ServiceClientAuthenticationDefaults.SchemeName, options =>
        {
            options.Authority = serviceClientOptions.Authority;
            options.Audience = serviceClientOptions.Audience;
            options.Events = new JwtBearerEvents
            {
                // Mapeia a App Role "Service.E2E" do Entra para ClaimTypes.Role "servico-e2e" —
                // mesmo formato de role que [Authorize(Roles = "...")] já entende em todo o
                // pipeline. Escopo mínimo por desenho: ver ServiceClientRoleMapper.
                OnTokenValidated = tokenContext =>
                {
                    ServiceClientRoleMapper.MapAppRolesToInternalRoles(tokenContext.Principal);
                    return Task.CompletedTask;
                },
                // Resiliência: falha de validação (token expirado, JWKS indisponível, etc.) nunca
                // derruba o request — só nega a autenticação por este esquema (o SmartAuth já
                // trata isso como "não autenticado", 401/403 seguem o fluxo normal do ASP.NET).
                OnAuthenticationFailed = failedContext =>
                {
                    var failureLogger = failedContext.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()?.CreateLogger("ServiceClientAuthentication");
                    failureLogger?.LogWarning(failedContext.Exception,
                        "Falha ao validar token ServiceClient (M2M)");
                    return Task.CompletedTask;
                }
            };
        });
    }
    else
    {
        Log.Warning("Authentication:ServiceClient não configurado (Authority/Audience vazios) — " +
            "esquema M2M (JWT Bearer) desabilitado nesta instância; endpoints [Authorize] continuam " +
            "aceitando só a identidade do BFF (TrustedHeader). Ver ADR autenticacao-m2m-e2e-cypress.");
    }

    builder.Services.AddAuthorization();

    // ✅ ADR M2M, Parte 2 (Honeypots/Canary Tokens) — camada de DETECÇÃO complementar aos
    // controles de auth reais acima; NÃO os substitui. Scoped por padrão (só usa ILogger<T>,
    // sem estado compartilhado).
    builder.Services.AddScoped<LayoutParserApi.Services.Security.ICanaryAlertService,
        LayoutParserApi.Services.Security.CanaryAlertService>();

    builder.Services.AddControllers(options =>
        {
            // A porta de entrada da API é a REDE (a API só aceita o BFF, trava do @lp-devops) somada à
            // identidade injetada pelo BFF (TrustedIdentityMiddleware, sob guarda de loopback). O antigo
            // ApiKeyGateFilter (chave compartilhada, fail-open) foi removido: rede + identidade venceram
            // e um filtro de meia-segurança só confundia quem lê. Ver docs/architecture/rollout-p2-autenticacao.md.
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.WriteIndented = false;
            // ✅ Defesa em profundidade (QA/Quinn, Gap 3 — métricas de IA): normalmente NaN/Infinity
            // já são saneados na origem (AiMetricsReaderService.ParseDouble), mas caso algum outro
            // endpoint futuro exponha um double não-saneado, isso evita o 500 cru de
            // System.Text.Json ao serializar (em vez disso escreve o literal "NaN"/"Infinity").
            options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        // ✅ Descrição geral da API no /swagger — objetivo: qualquer pessoa/agente/dev de
        // front-end entender e usar cada endpoint só olhando a UI, sem ler código-fonte.
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "LayoutParser API",
            Version = "v1",
            Description = "API ASP.NET Core que parseia documentos posicionais (TXT / MQSeries / IDOC) " +
                "contra um layout XML (low-code Sysmiddle), com camada de IA/ML que aprende a gerar " +
                "transformações (XSLT/TCL). É o hub de orquestração de parse, cache, IA e transformação " +
                "do ecossistema LayoutParser (Api + Lib de criptografia + Decrypt.exe + front-end React). " +
                "Endpoints agrupados por área via tag (Parsing, Transformation, Catalog, Learning, " +
                "Validation, Metrics, Logs, Testing)."
        });

        // Inclui os comentários XML (<summary>/<param>/<returns>/<response>) gerados pelo
        // GenerateDocumentationFile no .csproj — sem isso o Swashbuckle ignora os doc comments.
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (System.IO.File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
        }

        // ✅ Agrupamento por área de domínio em vez de lista plana de 17 controllers — mais
        // fácil de navegar no /swagger. Mapeamento simples por nome de controller.
        options.TagActionsBy(apiDesc =>
        {
            var controllerName = apiDesc.ActionDescriptor.RouteValues.TryGetValue("controller", out var c) ? c : null;
            var tag = controllerName switch
            {
                "Parse" => "Parsing",
                "Document" => "Parsing",
                "XmlAnalysis" => "Parsing",
                "AutoTransformation" => "Transformation",
                "TransformationExecution" => "Transformation",
                "LayoutDatabase" => "Catalog",
                "MapperDatabase" => "Catalog",
                "Learning" => "Learning",
                "RAG" => "Learning",
                "DataGeneration" => "Learning",
                "ValidationDiagnostic" => "Validation",
                "Testing" => "Validation",
                "Test" => "Validation",
                "AiMetrics" => "Metrics",
                "Metrics" => "Metrics",
                "Monitoring" => "Metrics",
                "Logs" => "Logs",
                _ => controllerName ?? "Outros"
            };
            return new[] { tag };
        });
        options.DocInclusionPredicate((docName, apiDesc) => true);
    });

    // ✅ Compressão de resposta. Este payload é o caso de uso ideal: a API devolve XML e JSON —
    // documento fiscal transformado, catálogo de layouts, páginas de log — e texto marcado comprime
    // na casa de 80-90%. O XML da transformação sozinho vai inline até 256 KB
    // (LowCode:InlineXmlMaxChars) e o front está do outro lado de uma rede corporativa.
    //
    // EnableForHttps fica no default (false) DE PROPÓSITO: comprimir sobre TLS reabre a classe de
    // ataque BREACH. Hoje a API é HTTP puro, então a compressão vale; quando o TLS entrar (§2.2 do
    // plano de arquitetura), reavaliar aqui em vez de ligar por reflexo.
    builder.Services.AddResponseCompression(options =>
    {
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        // O default cobre application/json; XML precisa ser declarado, e é justamente o payload
        // grande deste projeto.
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
        {
            "application/xml",
            "text/xml",
            "application/problem+json"
        });
    });
    // ⚠️ Os níveis NÃO são simétricos, e isso foi medido — não é preferência.
    // Benchmark em GET /api/monitoring/layouts-analysis (238.591 bytes crus, máquina de dev):
    //
    //   sem compressão      238.591 bytes   3602 ms
    //   brotli Fastest       77.865 bytes   3409 ms   <- pior que gzip nos DOIS eixos
    //   gzip   Fastest       69.119 bytes   2314 ms
    //   brotli Optimal       15.996 bytes   2200 ms   <- 93,3% de redução E o mais rápido
    //
    // Brotli em Fastest usa quality 1 e perde para gzip; só a partir de quality ~4 ele mostra o
    // que sabe fazer. Em Optimal comprime 4,3x melhor que o gzip e ainda termina ANTES da resposta
    // não comprimida — mandar 16 KB em vez de 238 KB paga a CPU com folga, mesmo no i7-4790 sem
    // GPU do servidor de produção.
    //
    // O gzip fica em Fastest de propósito: é só o fallback para cliente que não anuncia brotli
    // (Accept-Encoding), e nesse caminho o que importa é não custar caro.
    //
    // Caso pequeno medido: resposta de 32 bytes vira 37 com brotli — comprimir payload minúsculo
    // aumenta o tamanho. O middleware do ASP.NET não tem limiar mínimo, e não vale construir um:
    // o overhead é de bytes isolados contra uma economia de ~222 KB no caso que importa.
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

    // ✅ Teto explícito para upload de documento. O Kestrel já limita o corpo da request em ~30 MB
    // por default, mas o limite de multipart do ASP.NET é bem maior — e é multipart que o
    // ParseController recebe. Sem um teto declarado, o tamanho aceito passa a depender de qual
    // camada estoura primeiro, o que não é um limite, é um acidente.
    // 100 MB: os documentos posicionais reais medidos ficam na casa de dezenas de KB (a amostra do
    // gabarito tem 35 KB), então isto é folga de três ordens de grandeza — serve como barreira
    // contra corpo absurdo, não como restrição de uso.
    builder.Services.Configure<FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = 104_857_600;
    });

    // CORS Configuration
    // ✅ Origens configuráveis: env var CORS_ALLOWED_ORIGINS tem prioridade sobre
    // Cors:AllowedOrigins (appsettings). Fallback cobre os cenários de dev conhecidos
    // (front antigo em 80/81/8080 + LayoutParserReact build estático no IIS em 8081/3000).
    var corsAllowedOrigins = (Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")
        ?? builder.Configuration["Cors:AllowedOrigins"]
        ?? "http://172.25.32.42:81,http://localhost:81,http://localhost:8080,http://172.25.32.42:80,http://localhost:80,http://127.0.0.1:81,http://127.0.0.1:8080,http://localhost:8081,http://localhost:3000")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    Log.Information("Configuring CORS with allowed origins: {AllowedOrigins}", string.Join(", ", corsAllowedOrigins));

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            // ✅ Métodos restritos ao que a API de fato expõe. Levantamento nos 18 controllers:
            // 36 endpoints [HttpPost] e 28 [HttpGet] — zero PUT, DELETE ou PATCH. AllowAnyMethod
            // autorizava um conjunto que não existe; declarar os três reais (OPTIONS é o preflight)
            // não muda comportamento nenhum do front e fecha a porta antes de alguém adicionar um
            // DELETE sem pensar no lado do CORS.
            //
            // AllowAnyHeader FICA: apertar header de request é o que mais quebra front, o ganho de
            // segurança é pequeno (header não é credencial por si) e a lista real varia com o que o
            // navegador manda. Não é descuido — é onde a relação custo/benefício inverte.
            //
            // WithExposedHeaders passou de "*" para a lista real. O wildcard entrega ao JS de
            // origem cruzada todo header de resposta, inclusive os que a app venha a adicionar
            // depois sem revisar CORS. X-Correlation-ID é o único header próprio que a API emite
            // (middleware logo abaixo) e o único que o front tem motivo para ler.
            policy.WithOrigins(corsAllowedOrigins)
                  .WithMethods("GET", "POST", "OPTIONS")
                  .AllowAnyHeader()
                  .WithExposedHeaders("X-Correlation-ID");
        });
    });

    // Redis Configuration - Make it optional to prevent startup failure
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
    Log.Information("Configuring Redis connection at {RedisConnection}", redisConnectionString);

    // Try to connect to Redis, but don't fail if it's not available
    IConnectionMultiplexer? redisConnection = null;
    try
    {
        redisConnection = ConnectionMultiplexer.Connect(redisConnectionString);
        Log.Information("Successfully connected to Redis at {RedisConnection}", redisConnectionString);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to connect to Redis at {RedisConnection}. The application will start but caching features will be disabled.", redisConnectionString);
        redisConnection = null;
    }

    // Register Redis connection - make it optional for services
    if (redisConnection != null)
    {
        builder.Services.AddSingleton<IConnectionMultiplexer>(redisConnection);
        Log.Information("Redis service registered successfully");
    }
    else
        Log.Warning("Redis is not available. Cache services will operate without Redis.");
    

    // Cache Services - will handle null Redis connection gracefully
    builder.Services.AddScoped<ILayoutCacheService>(sp =>
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var logger = sp.GetRequiredService<ILogger<LayoutCacheService>>();
        return new LayoutCacheService(redis, logger);
    });

    // Database Services
    builder.Services.AddScoped<ILayoutDatabaseService, LayoutDatabaseService>();

    // ✅ ADR segregação Decrypt/LowCodeRunner (2026-09-25): HttpClient tipado apontando pro serviço
    // LayoutParserDecrypt (substitui Process.Start do executável legado local). BaseUrl vazia ⇒
    // IDecryptionService.IsDecryptorAvailable fica false (mesma semântica do "exe não encontrado"
    // de antes) — sem BaseAddress, o client nunca tenta conectar. Retry + circuit breaker via Polly
    // (Microsoft.Extensions.Http.Polly) — descriptografia é etapa crítica do pipeline, não dependência
    // opcional tipo Redis, então o objetivo do circuito é falhar rápido, não desabilitar o fluxo.
    var decryptBaseUrl = builder.Configuration["LayoutParserDecrypt:BaseUrl"];
    var decryptHttpClientBuilder = builder.Services.AddHttpClient<IDecryptionService, DecryptionService>(client =>
    {
        if (!string.IsNullOrWhiteSpace(decryptBaseUrl))
            client.BaseAddress = new Uri(decryptBaseUrl, UriKind.Absolute);

        // Timeout do HttpClient desligado — o timeout defensivo real é o CancellationTokenSource
        // dedicado dentro de DecryptionService (30s), para distinguir claramente "timeout de
        // descriptografia" de "timeout genérico de HttpClient" nos logs/exceções.
        client.Timeout = Timeout.InfiniteTimeSpan;
    });
    decryptHttpClientBuilder
        .AddPolicyHandler(LayoutParserApi.Services.Database.DecryptionResiliencePolicies.GetRetryPolicy())
        .AddPolicyHandler(LayoutParserApi.Services.Database.DecryptionResiliencePolicies.GetCircuitBreakerPolicy());

    builder.Services.AddScoped<MapperDatabaseService>();
    builder.Services.AddScoped<ICachedLayoutService, CachedLayoutService>();
    // ✅ Slice 1 (issue #225/#228): identidade externa → UserId interno + workspace fiscal isolado.
    // Banco DEDICADO (config "IdentityDatabase:*", SQL Server local à máquina da API) — NÃO é mais o
    // ConnectUS_Macgyver do Sysmiddle (reuso causava erro de FK em produção contra tbUser legada,
    // schema incompatível). Scoped por padrão do grupo Database.
    builder.Services.AddScoped<IIdentityWorkspaceStore, SqlIdentityWorkspaceStore>();
    // Histórico de longo prazo do pathway de IA por usuário (issue #102) — mesmo banco IdentityDatabase.
    builder.Services.AddScoped<LayoutParserApi.Services.Database.SqlAiUserSessionStore>();
    // ✅ Issue #97 (gap de TTL/retenção): sem isso, tbLpAiUserSessionHistoryEntry crescia
    // indefinidamente — mesmo espírito do TTL do AiCandidateStore (issue #51). BackgroundService
    // (Singleton por natureza do IHostedService) resolve SqlAiUserSessionStore via DI a cada ciclo
    // — o store em si continua Scoped/stateless por chamada (cria SqlConnection por operação).
    builder.Services.Configure<LayoutParserApi.Services.Database.AiUserSessionHistoryOptions>(
        builder.Configuration.GetSection("AiUserSessionHistory"));
    builder.Services.AddHostedService<LayoutParserApi.Services.Database.AiUserSessionHistoryCleanupBackgroundService>();
    builder.Services.AddScoped<IIdentityWorkspaceService, LayoutParserApi.Services.Identity.IdentityWorkspaceService>();
    // ✅ Slice 2 (issue #229): FiscalMappingPackage/Revision/Artifact — mesmo banco, mesmo padrão.
    // WindowsDefenderAntivirusScanner só usa ILogger (sem estado por-requisição) — Scoped por
    // consistência com o grupo, mas seria seguro como Singleton também.
    builder.Services.AddScoped<IFiscalPackageStore, SqlFiscalPackageStore>();
    builder.Services.AddScoped<IAntivirusScanner, LayoutParserApi.Services.Fiscal.WindowsDefenderAntivirusScanner>();
    builder.Services.AddScoped<IFiscalPackageService, LayoutParserApi.Services.Fiscal.FiscalPackageService>();
    // ✅ Issue #366 (ADR adr-historico-analises-fiscais-366): histórico de análises fiscais com
    // AnalysisId durável — metadado no IdentityDatabase, arquivos em disco (ML:FiscalAnalysesPath),
    // TTL 90 dias com purga em background. Registrado dentro de /parse/upload e /parse/auto.
    builder.Services.AddScoped<IFiscalAnalysisStore, SqlFiscalAnalysisStore>();
    builder.Services.Configure<LayoutParserApi.Services.Fiscal.FiscalAnalysisHistoryOptions>(
        builder.Configuration.GetSection("FiscalAnalysisHistory"));
    builder.Services.AddScoped<IFiscalAnalysisService, LayoutParserApi.Services.Fiscal.FiscalAnalysisService>();
    builder.Services.AddHostedService<LayoutParserApi.Services.Fiscal.FiscalAnalysisPurgeBackgroundService>();
    // ✅ Slice 3 (issue #230): MappingDraft human-in-the-loop — mesmo banco/padrão ADO.NET.
    // MappingSuggestionService consome ILlmProvider (issue #340/F1) — sem HttpClient direto aqui;
    // o pooling de conexão HTTP fica encapsulado dentro do registro do OllamaLlmProvider (grupo Llm, abaixo).
    builder.Services.AddScoped<IMappingDraftStore, SqlMappingDraftStore>();
    builder.Services.AddScoped<IMappingSuggestionService, LayoutParserApi.Services.Fiscal.MappingSuggestionService>();
    builder.Services.AddScoped<LayoutParserApi.Services.Filters.MappingEngineGuardFilter>();
    // ✅ Slice 4 (issue #226/#227): MappingExplanation — 3 adapters determinísticos (sem LLM),
    // resolvidos por Engine no controller via IEnumerable<IMappingExplanationAdapter>.
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.IMappingExplanationAdapter, LayoutParserApi.Services.Fiscal.SysmiddleExplanationAdapter>();
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.IMappingExplanationAdapter, LayoutParserApi.Services.Fiscal.TclExplanationAdapter>();
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.IMappingExplanationAdapter, LayoutParserApi.Services.Fiscal.XsltExplanationAdapter>();
    // ✅ Issue #425 (ADR "adr-layout-tree-endpoint-425"): árvore dupla origem/destino + regras de um
    // mapper Sysmiddle real — generaliza GuidXPathCatalog (ai/XslSynth.Contracts), reaproveita
    // ICachedMapperService/ICachedLayoutService já registrados acima.
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.ILayoutTreeService, LayoutParserApi.Services.Fiscal.LayoutTreeService>();
    // ✅ Slice 5 (issue #231): compilação determinística MappingDraftRule[] → XSLT/TCL + Fiscal Test
    // Lab. Mesmo banco/padrão ADO.NET; compile/test-run reaproveitam CanonicalDiffer/XsdValidationService
    // (já registrados/disponíveis via DI) sem I/O externo/Ollama.
    builder.Services.AddScoped<IMappingReleaseStore, SqlMappingReleaseStore>();
    // ✅ Issue #379 (ADR docs/architecture/adr-perfil-fiscal-draft-release-2026-09-10.md): cruza
    // FiscalProfile com XsdValidation:DocumentTypes (config em memória, sem I/O de banco) — Scoped
    // por consistência com o grupo, sem estado por-requisição real.
    builder.Services.AddScoped<LayoutParserApi.Services.Fiscal.IFiscalProfileResolver, LayoutParserApi.Services.Fiscal.FiscalProfileResolver>();
    // ✅ Issue #380 (#198.5): cobertura estática de destinos obrigatórios do XSD alvo — sem
    // estado por-requisição, Scoped por consistência com o grupo Fiscal.
    builder.Services.AddScoped<LayoutParserApi.Services.Fiscal.IRequiredCoverageCalculator, LayoutParserApi.Services.Fiscal.RequiredCoverageCalculator>();
    // Catálogo de exemplos reais de transformação TCL/XSL da Neogrid (corpus de referência/oráculo,
    // não releases compilados pelo pipeline) — leitura de disco via ReferenceExamples:BasePath
    // (opcional; ausente => catálogo vazio, degrada gracioso). Sem estado por-requisição.
    builder.Services.AddScoped<LayoutParserApi.Services.Fiscal.IReferenceExampleCatalogService, LayoutParserApi.Services.Fiscal.ReferenceExampleCatalogService>();
    // ✅ Issue #345 (ADR docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md):
    // contexto de documento + reporte de correção humana — mesmo banco/padrão ADO.NET.
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.IFieldCorrectionStore, LayoutParserApi.Services.Database.SqlFieldCorrectionStore>();
    // ✅ Issue #422: resposta livre do revisor às perguntas em aberto da IA — mesmo banco/padrão ADO.NET.
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.IMappingRuleAnswerStore, LayoutParserApi.Services.Database.SqlMappingRuleAnswerStore>();
    // ✅ Issue #438 (ADR docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md §5): geração
    // automática lazy de TCL/XSL/XSLT para um mapper Sysmiddle — mesmo banco/padrão ADO.NET, tabela
    // autossuficiente (sem FK). Reaproveita o loop determinístico de ai/XslSynth.Core in-process.
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.IGeneratedMapperArtifactStore, LayoutParserApi.Services.Database.SqlGeneratedMapperArtifactStore>();
    // ✅ Issue #473 (fase 2 do trigger lazy #438, ADR §3/§6): config do job periódico + limite de
    // concorrência ÚNICO, compartilhado entre o trigger lazy e o job periódico (Singleton — um só
    // SemaphoreSlim no processo, nunca dois limites independentes).
    builder.Services.Configure<LayoutParserApi.Services.Transformation.Ai.GeneratedMapperSweepOptions>(
        builder.Configuration.GetSection("GeneratedMapperSweep"));
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.GeneratedMapperGenerationLimiter>();
    builder.Services.AddScoped<LayoutParserApi.Services.Transformation.Ai.IGeneratedMapperArtifactService, LayoutParserApi.Services.Transformation.Ai.GeneratedMapperArtifactService>();
    builder.Services.AddScoped<LayoutParserApi.Services.Transformation.Ai.IGeneratedMapperListService, LayoutParserApi.Services.Transformation.Ai.GeneratedMapperListService>();
    // Job periódico (issue #473): varre tbMapper e dispara geração para quem não tem candidato ou está
    // stale, reaproveitando GetOrTriggerAsync acima — não bloqueia o startup (delay inicial de 2min).
    builder.Services.AddHostedService<LayoutParserApi.Services.Transformation.Ai.GeneratedMapperArtifactSweepService>();
    // ✅ Investigação PR #310 (2026-09-05): schema fiscal criado em ordem de dependência de FK no
    // startup, em vez de depender de qual store acima uma requisição real exercita primeiro. Ver
    // <see cref="LayoutParserApi.Services.Database.FiscalSchemaInitializer"/> para o grafo completo.
    builder.Services.AddScoped<IFiscalSchemaInitializer, LayoutParserApi.Services.Database.FiscalSchemaInitializer>();
    // Roda uma vez em background, após o app subir — não bloqueia o startup se o SQL estiver lento/fora do ar.
    builder.Services.AddHostedService<LayoutParserApi.Services.Database.FiscalSchemaInitializerBackgroundService>();
    builder.Services.AddScoped<IMappingCompileService, LayoutParserApi.Services.Fiscal.MappingCompileService>();
    builder.Services.AddScoped<IMappingTestRunService, LayoutParserApi.Services.Fiscal.MappingTestRunService>();
    // ✅ Issue #423: suíte de teste versionada do Fiscal Test Lab (múltiplas fixtures agrupadas +
    // histórico de execução) — mesmo banco/padrão ADO.NET, reaproveita IMappingTestRunService.
    // EvaluateFixtureAsync fixture-a-fixture (sem duplicar diff/XSD).
    builder.Services.AddScoped<LayoutParserApi.Services.Interfaces.ITestSuiteStore, LayoutParserApi.Services.Database.SqlTestSuiteStore>();
    builder.Services.AddScoped<LayoutParserApi.Services.Fiscal.ITestSuiteRunService, LayoutParserApi.Services.Fiscal.TestSuiteRunService>();
    // ✅ Issue #103 Passo 1: extração determinística (sem LLM) de tabelas de decisão fiscal a
    // partir de Excel real do dono. Sem estado por-requisição, poderia ser Singleton — Scoped
    // por consistência com o resto do grupo Fiscal.
    builder.Services.AddScoped<LayoutParserApi.Services.Fiscal.IFiscalMappingRuleExtractor, LayoutParserApi.Services.Fiscal.FiscalMappingRuleExtractor>();
    // ✅ Estado do warm-up do catálogo (P1.3), lido pela sonda de readiness. Singleton porque é
    // preenchido pelo IHostedService de warm-up e lido pelo health check — estado compartilhado.
    builder.Services.AddSingleton<CatalogWarmupState>();
    // ✅ Warm-up do cache permanente (layouts/mapeadores) roda em background APÓS app.Run(),
    // não bloqueia mais o startup / report ao Service Control Manager do Windows.
    builder.Services.AddHostedService<CachePermanentWarmupBackgroundService>();

    // ✅ P1.3 — Health checks REAIS. O health anterior (TestController) devolvia 200 sem tocar em
    // dependência: SQL/Redis/decryptor/runner fora → processo sobe, SCM diz Running, smoke test do
    // CI fica verde e o deploy declara sucesso publicando uma versão inoperante. Cada sonda testa
    // UMA dependência; a tag "ready" as agrupa em /health/ready (readiness). Registro por factory
    // (não AddCheck<T>) porque Redis é OPCIONAL — resolvido por GetService (nullable), igual ao resto.
    builder.Services.AddHealthChecks()
        .Add(new HealthCheckRegistration("sql",
            sp => new SqlServerHealthCheck(sp.GetRequiredService<IConfiguration>()),
            HealthStatus.Unhealthy, new[] { "ready" }))
        .Add(new HealthCheckRegistration("redis",
            sp => new RedisHealthCheck(sp.GetService<IConnectionMultiplexer>()),
            HealthStatus.Unhealthy, new[] { "ready" }))
        .Add(new HealthCheckRegistration("decryptor",
            sp => new DecryptorHealthCheck(sp.GetRequiredService<IDecryptionService>()),
            HealthStatus.Unhealthy, new[] { "ready" }))
        .Add(new HealthCheckRegistration("lowcode-runner",
            sp => new LowCodeRunnerHealthCheck(sp.GetRequiredService<IOptions<LowCodeRunnerOptions>>()),
            HealthStatus.Unhealthy, new[] { "ready" }))
        // ✅ A2 (gate #107/#108): Ollama:Url ausente/localhost é config órfã neste projeto — o Ollama
        // real roda numa VM Linux separada. Degraded (200): diagnóstico via IA é opcional.
        .Add(new HealthCheckRegistration("ollama-config",
            sp => new OllamaConfigHealthCheck(sp.GetRequiredService<IOptions<OllamaOptions>>()),
            HealthStatus.Unhealthy, new[] { "ready" }))
        .Add(new HealthCheckRegistration("catalog",
            sp => new CatalogHealthCheck(sp.GetRequiredService<CatalogWarmupState>()),
            HealthStatus.Unhealthy, new[] { "ready" }))
        // ✅ Issue #90: gate de capacidade dos 3 adapters de explicação de mapeamento (sysmiddle/
        // tcl/xslt) — a implementação nunca reporta Unhealthy (ver classe), então o failureStatus
        // aqui só é o piso de segurança caso o contrato seja violado no futuro.
        .Add(new HealthCheckRegistration("mapping-explanation-capabilities",
            sp => new MappingExplanationCapabilityHealthCheck(
                sp.GetRequiredService<IEnumerable<LayoutParserApi.Services.Interfaces.IMappingExplanationAdapter>>(),
                sp.GetRequiredService<ILogger<MappingExplanationCapabilityHealthCheck>>()),
            HealthStatus.Degraded, new[] { "ready" }));

    // XML Analysis Services
    builder.Services.AddScoped<XmlAnalysisService>();
    builder.Services.AddScoped<XsdValidationService>();
    builder.Services.AddScoped<XmlDocumentTypeDetector>();
    // Leitura de PDF de orientações XSD (issue #172) — sem estado, seguro como Scoped junto do resto.
    builder.Services.AddScoped<LayoutParserApi.Services.XmlAnalysis.PdfOrientationReader>();
    // Reconstrução reversa best-effort XML->TXT (issue #151, Fase 4) — sem estado, mesmo grupo.
    builder.Services.AddScoped<LayoutParserApi.Services.XmlAnalysis.ReverseReconstructionService>();
    builder.Services.AddScoped<MqSeriesToXmlTransformer>();
    builder.Services.AddScoped<TransformationPipelineService>();
    builder.Services.AddScoped<TclGeneratorService>();
    builder.Services.AddScoped<XslGeneratorService>();
    builder.Services.AddScoped<AutoTransformationGeneratorService>();
    // ✅ Cano de ligação com XslSynth.Contracts (núcleo determinístico extraído — parser DSL→JSON,
    // catálogo de funções) para o resto da API usar, sem trazer Ollama/RAG (ver
    // docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md §1).
    builder.Services.AddScoped<LayoutParserApi.Services.Transformation.MappingStructureService>();

    // ✅ Issue #140 (itens 2/6-9): wiring do motor de resolução estrutural TXT↔XML já implementado
    // (ai/XslSynth.Contracts/Core/StructuralResolution/) ao pipeline real — cache do catálogo XML
    // (NF-e via XSD) + serviço de composição que junta Layout/ParsedField reais + MapperVo real
    // (RealMapperParser, Parser B canônico da #139). Endpoint consumidor:
    // POST api/TransformationExecution/field-mappings.
    builder.Services.AddMemoryCache();
    builder.Services.Configure<LayoutParserApi.Services.Transformation.StructuralResolution.StructuralResolutionOptions>(
        builder.Configuration.GetSection("StructuralResolution"));
    // Singleton: só depende de IMemoryCache (singleton) e IOptions — o catálogo XML compilado é
    // caro (XmlSchemaSet) e estático por processo, mesmo racional de LowCodeTransformationService.
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.StructuralResolution.StructuralXmlCatalogCacheService>();
    builder.Services.AddScoped<LayoutParserApi.Services.Transformation.StructuralResolution.FieldMappingCompositionService>();

    // ✅ Diagnóstico de erro de validação via Ollama (LLM local) — Gap 2 do contrato
    // docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md. Não usa GeminiAIService
    // (decomissionado, sem registro no DI — ver generation-services-unregistered-di.md).
    builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));

    // ✅ Issue #340 (F1, ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md): abstração
    // ILlmProvider/LlmProviderResolver — único provider registrado nesta fase é o Ollama local.
    // Desliga o HttpClient.Timeout padrão (100s) do client tipado: cada serviço consumidor controla
    // seu próprio timeout via CancellationToken (ex.: Ollama:DiagnosisTimeoutSeconds dentro de
    // OllamaValidationDiagnosticService). Descoberto em teste manual (Gap 2): sem isso, o timeout de
    // 100s do HttpClient dispara ANTES do timeout dedicado e lança TaskCanceledException/
    // TimeoutException que o catch dedicado de timeout não reconhecia — caía no 500 genérico em
    // vez do 504 esperado pelo contrato.
    builder.Services.AddHttpClient<LayoutParserApi.Services.Llm.ILlmProvider, LayoutParserApi.Services.Llm.OllamaLlmProvider>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    });
    builder.Services.AddScoped<LayoutParserApi.Services.Llm.LlmProviderResolver>();

    // ✅ Pathway IA de execute-candidates (Issue #40) — loop gerar → validar XSD → comparar com o
    // gabarito sysmiddle → corrigir, assíncrono/desacoplado do ciclo síncrono do endpoint (ver
    // docs/architecture/pathway-ia-execute-candidates.md). Timeout infinito no client: o teto real
    // é o próprio CancellationTokenSource de sanidade dentro do serviço (AiTransformationCandidateOptions).
    builder.Services.Configure<LayoutParserApi.Services.Transformation.Ai.AiTransformationCandidateOptions>(
        builder.Configuration.GetSection("AiTransformationCandidate"));
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.AiCandidateStore>();
    // ✅ Issue #98: prompt customizado complementar, fallback mínimo por partição de usuário
    // (mesma partição da issue #92) — anexado APÓS o prompt padrão em BuildPrompt, nunca o substitui.
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.AiUserInstructionStore>();
    // ✅ Fallback automático de IA (design-fallback-ia-automatico-2026-08-16.md §5) — circuito de
    // proteção cross-usuário por LayoutGuid, estado em memória (Singleton, mesmo padrão de
    // LowCodeTransformationService/IConnectionMultiplexer em dotnet-standards.md).
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.IAiFallbackSuppressionGate,
        LayoutParserApi.Services.Transformation.Ai.AiFallbackSuppressionGate>();
    // ✅ Issue #51: a store não tinha expiração — cada ticket ficava para sempre em memória e em
    // disco. A poda roda em BackgroundService (previsível e independente de tráfego) justamente
    // para o polling de status não pagar por varredura de diretório no caminho de request.
    builder.Services.AddHostedService<LayoutParserApi.Services.Transformation.Ai.AiCandidateStoreCleanupBackgroundService>();
    builder.Services.AddHttpClient<LayoutParserApi.Services.Transformation.Ai.IAiTransformationCandidateService,
        LayoutParserApi.Services.Transformation.Ai.AiTransformationCandidateService>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

    // ✅ Síntese de XSLT real via RepairOrchestrator (ai/XslSynth.Core), in-process — substitui o
    // caminho "IA gera XML final direto" dentro de AiTransformationCandidateService.
    // docs/architecture/design-integracao-repairorchestrator-runtime-2026-08-21.md (Opção B).
    builder.Services.AddScoped<LayoutParserApi.Services.Transformation.Ai.IXslSynthesizerService,
        LayoutParserApi.Services.Transformation.Ai.RepairOrchestratorXslSynthesizerService>();

    // ✅ Issue #338 (F3): captura best-effort de cada convergência real como exemplo do dataset
    // de treino incremental (ai/XslSynth/training-data/*.jsonl).
    builder.Services.AddScoped<LayoutParserApi.Services.Transformation.Ai.TrainingDataCaptureService>();

    // ✅ Issue #351 (F4): retraining automatizado do modelo fine-tuned.
    //  - F4.1: telemetria de duração/iterações do RepairOrchestrator em runtime (Source=AiMetrics).
    //  - F4.2: contador de exemplos novos (alimentado por F3) + gatilho por volume OU teto de 90d.
    //  - F4.3: exclusão mútua Ollama-inferência × treino (retraining.lock) + validação pós-treino.
    // Estado durável em arquivo na árvore de XslSynth:TrainingDataPath (mesmo padrão de F3) — nunca
    // no SQL compartilhado 172.31.249.51 (read-only, ver .claude/rules/security.md). Singletons:
    // contador compartilhado entre o hook de F3 (escopo de request) e o background service.
    builder.Services.Configure<LayoutParserApi.Services.Transformation.Ai.Retraining.RetrainingOptions>(
        builder.Configuration.GetSection(
            LayoutParserApi.Services.Transformation.Ai.Retraining.RetrainingOptions.SectionName));
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.Retraining.IRetrainingStateStore,
        LayoutParserApi.Services.Transformation.Ai.Retraining.FileRetrainingStateStore>();
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.Retraining.IRetrainingLock,
        LayoutParserApi.Services.Transformation.Ai.Retraining.FileRetrainingLock>();
    builder.Services.AddSingleton<LayoutParserApi.Services.Transformation.Ai.Retraining.IRetrainingCoordinator,
        LayoutParserApi.Services.Transformation.Ai.Retraining.RetrainingCoordinator>();
    builder.Services.AddHostedService<LayoutParserApi.Services.Transformation.Ai.Retraining.RetrainingSchedulerBackgroundService>();

    // Transformation Services (ML)
    builder.Services.AddScoped<TransformationLearningService>();
    builder.Services.AddScoped<PatternComparisonService>();
    builder.Services.AddScoped<ImprovedTclGeneratorService>();
    builder.Services.AddScoped<ImprovedXslGeneratorService>();
    builder.Services.AddScoped<TransformationValidatorService>();

    // Parsing Services
    builder.Services.AddScoped<ILineSplitter, LineSplitter>();
    builder.Services.AddScoped<ILayoutValidator, LayoutValidator>();
    builder.Services.AddScoped<ILayoutNormalizer, LayoutNormalizer>();
    builder.Services.AddScoped<ILayoutDetector, LayoutDetector>();
    builder.Services.AddScoped<IAutomaticLayoutDetectionService, AutomaticLayoutDetectionService>();
    builder.Services.AddScoped<ILayoutParserService, LayoutParserService>();

    // Mapper Cache Services
    builder.Services.AddScoped<IMapperCacheService>(sp =>
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var logger = sp.GetRequiredService<ILogger<MapperCacheService>>();
        return new MapperCacheService(redis, logger);
    });
    builder.Services.AddScoped<ICachedMapperService, CachedMapperService>();

    // Learning Services
    builder.Services.AddScoped<ExampleLearningService>();
    builder.Services.AddScoped<LayoutLearningService>();
    builder.Services.AddScoped<XmlFormatterService>();
    builder.Services.AddScoped<FileStorageService>();
    // ✅ Detector de anomalia por z-score sobre MLData/DocumentPatterns (P2 do roadmap)
    builder.Services.AddScoped<IDocumentAnomalyDetector, DocumentAnomalyDetectorService>();
    // ✅ Item 4.1/4.2 do roadmap de IA: cenários fiscais sintéticos rotulados (Bogus,
    // pt_BR, CPF/CNPJ válidos) para o índice RAG da IA fiscal - NÃO fixture de teste,
    // NÃO treino (sem GPU em produção). Depende de CfopOperationCatalogService (6.1).
    builder.Services.AddScoped<SyntheticFiscalScenarioGenerator>();
    // ✅ Item 4.4 do roadmap de IA: checagem de near-duplicate obrigatória para QUALQUER
    // saída "sintética" antes de indexar - este projeto é material de TCC.
    builder.Services.AddScoped<NearDuplicateChecker>();

    // Generation Services (geração sintética / TXT — DataGenerationController)
    // ✅ Issue #33: o grupo não existia e QUALQUER chamada ao controller quebrava na resolução de
    // DI (build passa, o erro só aparece em runtime). Foi restaurado depois de ser apagado como
    // dano colateral na resolução de conflito do merge que removeu o Pathway 1 — por isso a
    // composição mora em AddGenerationServices (Services/Generation), chamada aqui e exercitada
    // pelo teste DataGenerationControllerDiTests: uma composição só, sem réplica que diverge.
    builder.Services.AddGenerationServices();

    // RAG Services (retrieval de exemplos para geração sintética)
    // ✅ Fix incidental (item 1.4 do roadmap de IA, 2026-07-21): RAGService nunca esteve
    // registrado aqui, o que derruba o RAGController em runtime (falha de resolução de DI
    // ao primeiro request). RAGService é retrieval puro sobre arquivos locais - sem
    // dependência de nuvem (Gemini/OpenAI) - não relacionado à decisão de decommission.
    builder.Services.AddScoped<RAGService>();

    // Validation Services
    builder.Services.AddScoped<LayoutValidationService>();
    builder.Services.AddScoped<DocumentValidationService>();
    builder.Services.AddScoped<DocumentMLValidationService>();
    // ✅ Item 3.1 do roadmap de IA: validação determinística de campo de input
    // (tamanho/formato/checksum contra o LengthField do Layout XML) - sem IA.
    builder.Services.AddScoped<FieldContentValidationService>();
    // ✅ Item 3.2 do roadmap de IA: classificador determinístico de erro XSD
    // "esperado" (ex.: assinatura digital ausente) vs. defeito real - sem IA.
    builder.Services.AddScoped<XsdErrorClassifierService>();
    // ✅ Item 6.1 do roadmap de IA: base indexada CFOP x tipo de operação (lookup
    // determinístico puro, tabela pública CONFAZ/SEFAZ/Receita Federal) - sem IA.
    // Ainda sem controller consumidor (isso é o item 6.2, Lia+Dex) - registrado desde
    // já seguindo o mesmo padrão dos itens 3.1/3.2 acima.
    builder.Services.AddScoped<CfopOperationCatalogService>();
    // ✅ A1 (gate #107/#108): valida no ARRANQUE, não deixa o silêncio do binding chegar a produção.
    // Causa raiz: `IOptions<LowCodeRunnerOptions>` cai nos defaults do C# sem erro nenhum quando a
    // seção LowCode falta — AllowedPackageGuids vira lista vazia e a query SQL de mapper vira
    // `IN (NULL)`, nunca batendo com nada. A regra é restrita de propósito: só falha quando a seção
    // LowCode EXISTE e AllowedPackageGuids está vazia — um host sem low-code configurado (seção
    // ausente) continua sendo um cenário válido (parse/catálogo servem sem transformação).
    // ProjectId NÃO entra na validação: o diagnóstico mostrou que o default C# (2) coincide por
    // acaso com o valor real do banco, então travar nele criaria uma regra rígida em cima de uma
    // coincidência, não de um invariante real.
    builder.Services.AddOptions<LowCodeRunnerOptions>()
        .Bind(builder.Configuration.GetSection("LowCode"))
        .Validate(opt =>
        {
            var lowCodeSection = builder.Configuration.GetSection("LowCode");
            if (!lowCodeSection.Exists())
                return true; // seção ausente: host sem low-code, cenário válido

            return opt.AllowedPackageGuids is { Count: > 0 };
        }, "LowCode:AllowedPackageGuids esta vazio, mas a secao LowCode esta presente na configuracao. " +
           "Isso faz TODA busca de mapper falhar silenciosamente (query SQL vira IN (NULL)) sem erro " +
           "visivel ate o usuario tentar transformar um documento. Configure a variavel de ambiente " +
           "LowCode__AllowedPackageGuids__0 (e demais indices) com os PackageGuid permitidos deste host, " +
           "ou remova a secao LowCode inteira se este host nao usa low-code.")
        .ValidateOnStart();
    builder.Services.AddSingleton<LowCodeTransformationService>();
    // ✅ Store/índice das transformações low-code: Singleton porque é consumido pelos Singletons do
    // pathway (auto-transform) e não guarda estado por request. Redis é OPCIONAL — resolvido por
    // GetService (nullable), mesmo padrão dos caches de catálogo: sem Redis, tudo funciona por disco.
    builder.Services.AddSingleton<LowCodeTransformationStore>(sp => new LowCodeTransformationStore(
        sp.GetRequiredService<ILogger<LowCodeTransformationStore>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<IOptions<LowCodeRunnerOptions>>(),
        sp.GetService<IConnectionMultiplexer>()));
    builder.Services.AddSingleton<LowCodeAutoTransformationService>();
    builder.Services.AddHostedService<LayoutValidationBackgroundService>();

    // Testing Services
    builder.Services.AddScoped<AutomatedTransformationTestService>();

    // Security / Identidade
    // ✅ Consumo da identidade que o BFF injeta (x-iis-user/x-iis-roles). Nomes dos headers e a
    // guarda de loopback são configuráveis pela seção Security (env Security__...), com defaults
    // seguros — ver TrustedIdentityOptions. ICurrentUser é Scoped (uma identidade por requisição),
    // preenchido pelo TrustedIdentityMiddleware sob a guarda de loopback e lido por AuditActionFilter.
    builder.Services.Configure<TrustedIdentityOptions>(
        builder.Configuration.GetSection(TrustedIdentityOptions.SectionName));
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();

    // Audit and Logging Services
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    builder.Services.AddScoped<ITechLogger, TechLogger>();
    builder.Services.AddScoped<AuditActionFilter>();
    // ✅ Leitura unificada dos 3 arquivos de log do ecossistema (Api/Lib/Decrypt) para
    // GET api/logs (LogsController) — desenho de logging unificado fechado pela arquiteta.
    builder.Services.AddScoped<IUnifiedLogReaderService, UnifiedLogReaderService>();

    // ✅ Gap 3 (painel de métricas de IA): parseia por cima do mesmo log unificado acima
    // (Source=AiMetrics), sem leitura/rotação de arquivo própria.
    builder.Services.AddScoped<IAiMetricsReaderService, AiMetricsReaderService>();

    // ✅ Contraparte de escrita do leitor acima: recebe as gerações do job que roda na VM Linux
    // (POST api/ai-metrics/generations/ingest) e as grava no MESMO log que o leitor consome —
    // sem isso o painel lê um diretório que nunca recebe geração nenhuma.
    builder.Services.AddScoped<IAiMetricsIngestService, AiMetricsIngestService>();

    // ✅ Chave compartilhada dos DOIS endpoints de escrita de métricas de IA (mesmo padrão de
    // registro do AuditActionFilter acima, aplicado via [ServiceFilter] no controller). Fail-closed:
    // sem AiMetrics__IngestApiKey no ambiente, a escrita é recusada — ver AiMetricsIngestKeyFilter.
    builder.Services.AddScoped<AiMetricsIngestKeyFilter>();

    var app = builder.Build();

    Log.Information("Application built successfully. Environment: {Environment}", app.Environment.EnvironmentName);

    // ✅ Postura da guarda de identidade, dita UMA vez no arranque (o middleware não loga por request —
    // o log é relido pelo painel). A guarda de loopback é o que impede um host remoto de forjar
    // x-iis-user mesmo com a API em 0.0.0.0; desligá-la só é seguro se a rede já garantir a origem.
    var identityOpt = app.Services.GetRequiredService<IOptions<TrustedIdentityOptions>>().Value;
    if (identityOpt.TrustIdentityFromLoopbackOnly)
    {
        Log.Information("Identidade do BFF ATIVA com guarda de loopback (headers {UserHeader}/{RolesHeader}). " +
            "Headers de identidade só são confiados em conexão loopback (127.0.0.1/::1); origem remota é ignorada.",
            identityOpt.TrustedUserHeader, identityOpt.TrustedRolesHeader);
    }
    else
    {
        Log.Warning("Identidade do BFF ATIVA SEM guarda de loopback (Security__TrustIdentityFromLoopbackOnly=false): " +
            "os headers {UserHeader}/{RolesHeader} são confiados de QUALQUER origem. Só é seguro se a rede já " +
            "restringir a origem à do BFF (bind em 127.0.0.1 ou firewall). Reavalie com o @lp-devops.",
            identityOpt.TrustedUserHeader, identityOpt.TrustedRolesHeader);
    }

    // ✅ Diagnóstico de arranque da config low-code. Mesmo princípio do Redis acima: AVISA e segue,
    // nunca derruba a app — parse e catálogo funcionam sem transformação.
    //
    // POR QUE ISTO EXISTE: o deploy PRESERVA o appsettings.json do destino (ci-dev.yml/deploy.yml
    // copiam com `-Exclude appsettings.json`), então chave nova adicionada ao appsettings.json do
    // REPO nunca chega ao servidor — o valor efetivo vem de variável de ambiente `Section__Key`.
    // Quando esse canal falha, a falha aparecia longe da causa: o runner saía com exit 9 e o usuário
    // via "transformação falhou", uma requisição por vez, depois de pagar o custo de subir um
    // processo x86. Ler os valores EFETIVOS aqui (pós-binding, já com env vars aplicadas) transforma
    // um mistério de runtime numa linha de log no arranque.
    try
    {
        var lowCodeOpt = app.Services.GetRequiredService<IOptions<LowCodeRunnerOptions>>().Value;

        if (string.IsNullOrWhiteSpace(lowCodeOpt.Package))
            Log.Warning("LowCode:Package VAZIO — toda transformação low-code vai falhar (runner sai com exit 9). " +
                        "Sem o appConnector o package não tem mais fallback. Configure a variável de ambiente LowCode__Package " +
                        "com o <PackageMappers> do config.xml da instância Sysmiddle.");
        else
            Log.Information("LowCode:Package configurado: {Package}", lowCodeOpt.Package);

        if (string.IsNullOrWhiteSpace(lowCodeOpt.RunnerPath))
            Log.Warning("LowCode:RunnerPath não configurado — nenhuma transformação low-code é possível.");
        else if (!File.Exists(lowCodeOpt.RunnerPath))
            Log.Warning("LowCode:RunnerPath aponta para um arquivo que NÃO EXISTE: {RunnerPath}. " +
                        "Toda transformação low-code vai falhar até o runner ser publicado neste host.", lowCodeOpt.RunnerPath);

        foreach (var (chave, valor) in new[]
                 {
                     ("LowCode:SysmiddleDir", lowCodeOpt.SysmiddleDir),
                     ("LowCode:GlobalFolder", lowCodeOpt.GlobalFolder)
                 })
        {
            if (string.IsNullOrWhiteSpace(valor))
                Log.Warning("{Chave} não configurado — a transformação low-code falha na validação de entrada.", chave);
        }

        // 60s é o piso empírico, não um número redondo: a medição A/B por fase mostrou a
        // transformação completa entre 48s e 137s (só o ExecuteMapper leva 38-73s). Abaixo disso o
        // processo é morto no meio de trabalho bom — a falha mais cara que este pathway tem, porque
        // consome o slot inteiro e não deixa resultado.
        if (lowCodeOpt.RunnerTimeoutSeconds > 0 && lowCodeOpt.RunnerTimeoutSeconds < 60)
            Log.Warning("LowCode:RunnerTimeoutSeconds = {Timeout}s é INVIÁVEL — a transformação real leva 48-137s medidos. " +
                        "O runner será morto no meio da execução. Configure LowCode__RunnerTimeoutSeconds (referência: 180).",
                        lowCodeOpt.RunnerTimeoutSeconds);
    }
    catch (Exception ex)
    {
        // Diagnóstico nunca pode ser o motivo de a app não subir.
        Log.Warning(ex, "Falha ao inspecionar a configuração low-code no arranque (diagnóstico ignorado).");
    }

    // ✅ Compressão primeiro no pipeline: precisa envolver tudo que escreve no corpo da resposta,
    // inclusive o handler de exceção mais abaixo. Depois que outro middleware começou a escrever,
    // é tarde para comprimir.
    app.UseResponseCompression();

    // ✅ Cabeçalhos de segurança na resposta. Baratos, sem efeito sobre o contrato da API, e
    // fecham três vetores que hoje estão abertos:
    //   nosniff        — impede o navegador de "adivinhar" que um XML de documento é HTML e
    //                    executá-lo; relevante aqui porque a API devolve markup de terceiro.
    //   DENY (frame)   — a API não tem UI própria; nada dela deve ser embutido em iframe.
    //   no-referrer    — evita vazar a URL (que carrega nome de layout e ticket) para terceiros.
    //
    // HSTS deliberadamente AUSENTE: só faz sentido sobre TLS, e a app serve HTTP puro hoje
    // (UseHttpsRedirection segue comentado abaixo). Enviá-lo agora não protegeria nada e poderia
    // travar o acesso ao host no navegador de quem testa. Entra junto com o TLS, não antes.
    app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        await next();
    });

    // Configure the HTTP request pipeline.
    // CORS must be early in the pipeline - before routing and other middleware
    // This ensures CORS headers are sent even when errors occur
    app.UseCors();

    // ✅ CorrelationId (GUID) por request/arquivo processado
    // - Aceita header X-Correlation-ID vindo do front (se existir)
    // - Caso não exista, gera um novo GUID
    // - Devolve no response header X-Correlation-ID
    // - Injeta no Serilog LogContext, aparecendo em todos os logs como Corr:{CorrelationId}
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString();
        else
            // ✅ CodeQL cs/log-forging: X-Correlation-ID vem do cliente e é ecoado em TODO log da
            // request (LogContext abaixo) — saneia aqui uma única vez em vez de em cada call site.
            correlationId = LayoutParserApi.Services.Logging.LogMessageSanitizer.Sanitize(correlationId, maxLength: 200);

        context.Response.Headers["X-Correlation-ID"] = correlationId;
        CorrelationContext.CurrentId = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next();
        }
    });

    // ✅ ADR M2M, Parte 2 (Honeypots/Canary Tokens): detecção da credencial-isca "aposentada"
    // (X-Service-Credential). Roda ANTES de qualquer middleware de auth real — inclusive antes do
    // TrustedIdentityMiddleware abaixo — para garantir que o alarme dispara mesmo que o valor
    // canary por acaso colida com alguma validação futura. Nunca autentica, nunca bloqueia por
    // conta própria: só loga Critical e deixa o pipeline seguir. Ver CanaryCredentialDetectionMiddleware.
    app.UseMiddleware<LayoutParserApi.Services.Security.CanaryCredentialDetectionMiddleware>();

    // ✅ Identidade injetada pelo BFF (x-iis-user/x-iis-roles), sob a guarda de loopback. Vem DEPOIS do
    // CorrelationId (para o log de arranque/diagnóstico compartilhar o contexto) e ANTES dos endpoints,
    // para que ICurrentUser já esteja preenchido quando controllers e AuditActionFilter rodam. Fora de
    // loopback ou sem header → identidade anônima, nunca exceção. Ver TrustedIdentityMiddleware.
    app.UseMiddleware<TrustedIdentityMiddleware>();

    // ✅ Swagger habilitado em todo ambiente (não só Development) — pedido explícito do dono
    // para testar endpoints via Postman/Swagger UI direto em produção. Servido sob /api/swagger
    // (não /swagger) de propósito: em produção o BFF/reverse proxy (LayoutParserReact/server)
    // só encaminha /api/* para esta API — qualquer rota fora desse prefixo cai no catch-all do
    // SPA React e devolve a página de erro 404 do front, não o Swagger.
    app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.json");
    app.UseSwaggerUI(c =>
    {
        c.RoutePrefix = "api/swagger";
        c.SwaggerEndpoint("/api/swagger/v1/swagger.json", "LayoutParserApi v1");
    });

    // Enable detailed error pages in development
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        Log.Information("Swagger UI enabled for development");
    }
    else
    {
        // Configure exception handler with a custom error handler
        // This ensures CORS headers are sent even when errors occur
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";

                var response = new
                {
                    error = "An error occurred while processing your request.",
                    message = app.Environment.IsDevelopment() ? exception?.Message : null
                };

                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response));
            });
        });
    }

    // Only use HTTPS redirection if actually using HTTPS
    // app.UseHttpsRedirection();

    // Issue #32: reativado. TrustedIdentityMiddleware (linha acima) já populou HttpContext.User;
    // UseAuthorization roda depois de UseCors (evita interferir no preflight) e habilita os
    // [Authorize(Roles = "...")] marcados nos controllers sensíveis.
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    // ✅ P1.3 — endpoints de health.
    //   /health        liveness  — 200 se o PROCESSO responde (não roda sonda de dependência).
    //   /health/ready  readiness — roda as sondas "ready"; 503 quando dependência ESSENCIAL está
    //                              fora (SQL, catálogo vazio). Redis/runner ausentes são Degraded → 200.
    // O corpo detalha cada dependência (status + motivo), para o operador ver O QUE está fora sem
    // precisar do log. NOTA p/ @lp-devops: o smoke test do CI deve migrar para /health/ready estrito.
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate = _ => false
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = async (httpContext, report) =>
        {
            httpContext.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                dependencies = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    error = e.Value.Exception?.Message,
                    // "data" carrega a classificação estruturada da sonda (o catálogo publica
                    // estado/transitorio aqui). Sem isso, "aquecendo" e "vazio definitivo" —
                    // que respondem o MESMO 503 — só se distinguiriam lendo o texto da descrição.
                    data = e.Value.Data.Count > 0 ? e.Value.Data : null
                })
            };
            await httpContext.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(payload));
        }
    });

    // Nota: a população do cache permanente de layouts/mapeadores foi movida para
    // CachePermanentWarmupBackgroundService (Services/Database), que roda como IHostedService
    // em segundo plano após app.Run(). Isso evita bloquear o startup (e o report ao Service
    // Control Manager do Windows) enquanto aguarda o SQL Server responder.

    var kestrelUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://127.0.0.1:5000";
    Log.Information("LayoutParserApi started successfully. Listening on: {Url}", kestrelUrl);
    Log.Information("CORS enabled for frontend origins");
    Log.Information("Log files are being written to: {LogDirectory}", logDirectory);

    Console.WriteLine("=".PadRight(80, '='));
    Console.WriteLine($"LayoutParserApi is starting...");
    Console.WriteLine($"Listening on: {kestrelUrl}");
    Console.WriteLine($"Log directory: {logDirectory}");
    Console.WriteLine("=".PadRight(80, '='));

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"[FATAL ERROR] Application start-up failed: {ex.Message}");
    Console.WriteLine($"[FATAL ERROR] Stack trace: {ex.StackTrace}");

    try
    {
        Log.Fatal(ex, "Application start-up failed");
    }
    catch
    {
        // If logging fails, at least we have console output
    }

    throw;
}
finally
{
    try
    {
        Log.CloseAndFlush();
    }
    catch
    {
        // Ignore errors during shutdown
    }
}

// ✅ Issue #90: expõe a classe `Program` gerada implicitamente pelos top-level statements como
// `public partial`, sem mudar comportamento algum em produção — é só uma declaração de tipo
// adicional para o compilador mesclar com a gerada automaticamente (que não tem modificador
// explícito, então herda o `public` daqui em vez de gerar conflito de acessibilidade).
// Necessário para `WebApplicationFactory<Program>` no projeto de testes, que está em outro
// assembly e não enxergaria a classe se ela ficasse `internal` (default do gerado).
public partial class Program;
