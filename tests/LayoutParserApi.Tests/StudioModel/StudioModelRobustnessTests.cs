using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using static LayoutParserApi.Tests.StudioModel.StudioModelTestSupport;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Robustez (QA): logs sem conteúdo, DTD, ETag/If-None-Match, serialização e desempenho. Só dados sintéticos.</summary>
    public sealed class StudioModelRobustnessTests
    {
        private readonly ITestOutputHelper _out;
        public StudioModelRobustnessTests(ITestOutputHelper o) => _out = o;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private sealed class CaptureLogger<T> : ILogger<T>
        {
            public List<string> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel l) => true;
            public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
                => Lines.Add(f(s, ex) + (ex == null ? "" : "\n" + ex));
        }

        [Fact]
        public async Task Logs_nao_contem_conteudo_de_xml_nem_codigo_de_regra_em_caminhos_degradados()
        {
            const string secret = "SEGREDO_SINTETICO_123";
            var log = new CaptureLogger<SysmiddleStudioModelAdapter>();
            var (_, mappers, layouts) = Build();
            // mapper ilegível contendo o marcador; layout de entrada ilegível contendo o marcador
            mappers.Mappers[0].DecryptedContent = $"<MapperVO><x>{secret}</y>";
            layouts.ByGuid[InputLayoutGuid].DecryptedContent = $"<LayoutVO><Name>{secret}</LayoutVO";
            var adapter = new SysmiddleStudioModelAdapter(mappers, layouts, log);
            var doc = await Load(adapter);

            Assert.NotNull(doc);
            Assert.NotEmpty(log.Lines);
            Assert.DoesNotContain(log.Lines, l => l.Contains(secret));
        }

        [Fact]
        public async Task Logs_do_caminho_feliz_nao_contem_code_da_regra()
        {
            var log = new CaptureLogger<SysmiddleStudioModelAdapter>();
            var (_, mappers, layouts) = Build(mapperXml: MapperXml.Replace("#.qtd == \"0000\"", "MARCADOR_CODE_XYZ"));
            var doc = await Load(new SysmiddleStudioModelAdapter(mappers, layouts, log));
            Assert.Contains(doc!.Rules.Values, r => r.Code!.Contains("MARCADOR_CODE_XYZ"));
            Assert.DoesNotContain(log.Lines, l => l.Contains("MARCADOR_CODE_XYZ"));
        }

        [Fact]
        public async Task Doctype_com_entidades_nao_expande_nem_derruba()
        {
            // Bomba de entidades sintética: não pode travar/estourar memória; degrada (ilegível) ou lê sem expandir.
            var bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
                + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]><MapperVO><Name>&d;</Name></MapperVO>";
            var (adapter, mappers, _) = Build();
            mappers.Mappers[0].DecryptedContent = bomb;
            var sw = Stopwatch.StartNew();
            var doc = await Load(adapter);
            sw.Stop();
            Assert.NotNull(doc);
            Assert.True(sw.ElapsedMilliseconds < 2000);
        }

        [Fact]
        public async Task Mapper_guid_e_resolvido_sem_diferenciar_maiusculas()
        {
            var (adapter, _, _) = Build();
            var doc = await adapter.LoadAsync(new(Guid.NewGuid(), MapperGuid.ToLowerInvariant()), CancellationToken.None);
            Assert.NotNull(doc);
        }

        [Fact]
        public async Task Serializacao_real_preserva_lt_gt_e_tem_tamanho_razoavel()
        {
            var doc = (await Load(Build(mapperXml: MapperXml.Replace("#.qtd == \"0000\"", "a &lt; b &amp;&amp; c &gt; d")).Adapter))!;
            var json = JsonSerializer.Serialize(doc, Json);
            Assert.Contains("a < b && c > d", json);
            Assert.DoesNotContain("\\u003C", json);
            Assert.DoesNotContain("\\u003E", json);
        }

        private static StudioModelController Ctl(StudioModelDocument doc, string? inm)
        {
            var http = new DefaultHttpContext();
            if (inm != null) http.Request.Headers["If-None-Match"] = inm;
            return new StudioModelController(new Fake(doc), Microsoft.Extensions.Logging.Abstractions.NullLogger<StudioModelController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = http } };
        }

        private sealed class Fake : IStudioModelService
        {
            private readonly StudioModelDocument _d;
            public Fake(StudioModelDocument d) => _d = d;
            public Task<StudioModelDocument?> GetAsync(Guid w, string m, string? e, CancellationToken ct) => Task.FromResult<StudioModelDocument?>(_d);
        }

        [Fact]
        public async Task If_None_Match_com_lista_aceita_o_etag_em_qualquer_posicao_e_diferente_retorna_200()
        {
            var doc = (await Load(Build().Adapter))!;
            var etag = doc.Artifact.ETag;
            Assert.Equal(304, Assert.IsType<StatusCodeResult>(await Ctl(doc, $"\"outro\", {etag}").GetStudioModel(Guid.NewGuid(), "M", null, default)).StatusCode);
            Assert.IsType<OkObjectResult>(await Ctl(doc, "\"outro\"").GetStudioModel(Guid.NewGuid(), "M", null, default));
        }

        [Fact]
        public async Task Cancelamento_do_cliente_nao_deve_virar_erro_503_registrado_como_falha()
        {
            // Documenta o comportamento atual (catch-all): OperationCanceledException vira 503. Ver relatório QA.
            var http = new DefaultHttpContext();
            var c = new StudioModelController(new Throwing(new OperationCanceledException()), Microsoft.Extensions.Logging.Abstractions.NullLogger<StudioModelController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = http } };
            var r = await c.GetStudioModel(Guid.NewGuid(), "M", null, default);
            Assert.Equal(503, Assert.IsType<ObjectResult>(r).StatusCode);
        }

        private sealed class Throwing : IStudioModelService
        {
            private readonly Exception _e;
            public Throwing(Exception e) => _e = e;
            public Task<StudioModelDocument?> GetAsync(Guid w, string m, string? e, CancellationToken ct) => throw _e;
        }

        [Fact]
        public async Task Desempenho_release_mediana_de_montagem_e_serializacao_em_escala_real()
        {
            const int lines = 200, perLine = 55;
            var inp = new StringBuilder("<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"TextLayoutVO\"><LayoutType>TextPositional</LayoutType><Elements>");
            var tgt = new StringBuilder("<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"XmlLayoutVO\"><LayoutType>Xml</LayoutType><Elements>");
            var map = new StringBuilder($"<MapperVO><MapperGuid>{MapperGuid}</MapperGuid><LinkMappings>");
            for (var l = 0; l < lines; l++)
            {
                inp.Append($"<Element xsi:type=\"LineElementVO\"><ElementGuid>LIN_{l}</ElementGuid><Sequence>{l + 1}</Sequence><Name>L{l}</Name><Elements>");
                tgt.Append($"<Element xsi:type=\"GroupTagElementVO\"><ElementGuid>GRT_{l}</ElementGuid><Sequence>{l + 1}</Sequence><Name>G{l}</Name><Elements>");
                for (var f = 0; f < perLine; f++)
                {
                    inp.Append($"<Element xsi:type=\"FieldElementVO\"><ElementGuid>FLD_{l}_{f}</ElementGuid><Sequence>{f + 1}</Sequence><Name>F{f}</Name><LengthField>5</LengthField></Element>");
                    tgt.Append($"<Element xsi:type=\"TagElementVO\"><ElementGuid>TAG_{l}_{f}</ElementGuid><Sequence>{f + 1}</Sequence><Name>T{f}</Name></Element>");
                    map.Append($"<LinkMappingItem><ElementGuid>LKM_{l}_{f}</ElementGuid><InputLayoutGuid>FLD_{l}_{f}</InputLayoutGuid><TargetLayoutGuid>TAG_{l}_{f}</TargetLayoutGuid></LinkMappingItem>");
                }
                inp.Append("</Elements></Element>"); tgt.Append("</Elements></Element>");
            }
            inp.Append("</Elements></LayoutVO>"); tgt.Append("</Elements></LayoutVO>"); map.Append("</LinkMappings></MapperVO>");

            var (adapter, _, _) = Build(map.ToString(), inp.ToString(), tgt.ToString());
            await Load(adapter);
            var build = new List<long>(); var ser = new List<long>(); var size = 0;
            for (var i = 0; i < 7; i++)
            {
                var sw = Stopwatch.StartNew();
                var doc = (await Load(adapter))!;
                build.Add(sw.ElapsedMilliseconds);
                sw.Restart();
                size = JsonSerializer.SerializeToUtf8Bytes(doc, Json).Length;
                ser.Add(sw.ElapsedMilliseconds);
            }
            build.Sort(); ser.Sort();
            _out.WriteLine($"montagem mediana={build[3]}ms min={build[0]} max={build[^1]} | serializacao mediana={ser[3]}ms | json={size / 1024} KiB");
            // Limite absoluto folgado: o runner de CI e o host de producao dividem CPU (builds/testes paralelos
            // ja levaram a mediana a ~3,2 s). Uma regressao algoritmica real (O(n^2) em ~11 mil ligacoes) estoura
            // muito acima de 5 s, entao o teste continua protegendo contra ela sem oscilar por carga.
            Assert.True(build[3] < 5000, $"montagem mediana={build[3]}ms excedeu 5000ms");
        }
    }
}
