using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.StudioModel;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using static LayoutParserApi.Tests.StudioModel.StudioModelTestSupport;

namespace LayoutParserApi.Tests.StudioModel
{
    public sealed class StudioModelServiceTests
    {
        private sealed class StubAdapter : IStudioModelAdapter
        {
            public string Engine => "sysmiddle";
            public StudioCapabilities Capabilities { get; } = new(false, new());
            public int Calls { get; private set; }
            public Task<StudioModelDocument?> LoadAsync(StudioModelRequest r, CancellationToken ct) { Calls++; return Task.FromResult<StudioModelDocument?>(null); }
        }

        private static (StudioModelService Svc, StubAdapter Adapter) Build()
        {
            var a = new StubAdapter();
            return (new StudioModelService(new[] { a }, NullLogger<StudioModelService>.Instance), a);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("SYSMIDDLE")]
        public async Task Engine_omitido_ou_sysmiddle_usa_o_adaptador_sysmiddle(string? engine)
        {
            var (svc, a) = Build();
            await svc.GetAsync(Guid.NewGuid(), "MAP", engine, CancellationToken.None);
            Assert.Equal(1, a.Calls);
        }

        [Theory]
        [InlineData("tcl")]
        [InlineData("xslt")]
        public async Task Engines_conhecidos_sem_adaptador_lancam_501(string engine)
            => await Assert.ThrowsAsync<StudioModelEngineNotSupportedException>(() => Build().Svc.GetAsync(Guid.NewGuid(), "MAP", engine, CancellationToken.None));

        [Fact]
        public async Task Engine_invalido_lanca_400()
            => await Assert.ThrowsAsync<StudioModelInvalidEngineException>(() => Build().Svc.GetAsync(Guid.NewGuid(), "foo", "foo", CancellationToken.None));
    }

    public sealed class StudioModelControllerTests
    {
        private sealed class FakeService : IStudioModelService
        {
            public StudioModelDocument? Doc { get; set; }
            public Exception? Throw { get; set; }
            public string? LastEngine { get; private set; }
            public Task<StudioModelDocument?> GetAsync(Guid w, string m, string? engine, CancellationToken ct)
            {
                LastEngine = engine;
                return Throw != null ? throw Throw : Task.FromResult(Doc);
            }
        }

        private static StudioModelController Build(FakeService s, string? ifNoneMatch = null)
        {
            var http = new DefaultHttpContext();
            if (ifNoneMatch != null) http.Request.Headers["If-None-Match"] = ifNoneMatch;
            return new StudioModelController(s, NullLogger<StudioModelController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        private static async Task<StudioModelDocument> RealDoc() => (await Load(StudioModelTestSupport.Build().Adapter))!;

        [Fact]
        public async Task Mapper_inexistente_retorna_404()
            => Assert.IsType<NotFoundResult>(await Build(new FakeService()).GetStudioModel(Guid.NewGuid(), "MAP", "sysmiddle", default));

        [Fact]
        public async Task Sucesso_retorna_200_com_ETag_e_Cache_Control()
        {
            var doc = await RealDoc();
            var c = Build(new FakeService { Doc = doc });
            var result = await c.GetStudioModel(Guid.NewGuid(), "MAP", "sysmiddle", default);

            Assert.Same(doc, Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(doc.Artifact.ETag, c.Response.Headers["ETag"].ToString());
            Assert.Equal("private, no-cache", c.Response.Headers["Cache-Control"].ToString());
        }

        [Fact]
        public async Task If_None_Match_igual_ao_eTag_retorna_304()
        {
            var doc = await RealDoc();
            var result = await Build(new FakeService { Doc = doc }, doc.Artifact.ETag).GetStudioModel(Guid.NewGuid(), "MAP", null, default);
            Assert.Equal(304, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }

        [Fact]
        public async Task Engine_invalido_retorna_400_com_error()
        {
            var r = await Build(new FakeService { Throw = new StudioModelInvalidEngineException("Engine inválido.") }).GetStudioModel(Guid.NewGuid(), "MAP", "x", default);
            Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(r).StatusCode);
        }

        [Fact]
        public async Task Engine_nao_implementado_retorna_501_com_error()
        {
            var r = await Build(new FakeService { Throw = new StudioModelEngineNotSupportedException("Engine não suportado nesta versão.") }).GetStudioModel(Guid.NewGuid(), "MAP", "tcl", default);
            var o = Assert.IsType<ObjectResult>(r);
            Assert.Equal(501, o.StatusCode);
            Assert.Contains("error", o.Value!.GetType().GetProperties().Select(p => p.Name));
        }

        [Fact]
        public async Task Catalogo_fora_retorna_503_com_error()
        {
            var r = await Build(new FakeService { Throw = new StudioModelUnavailableException("x") }).GetStudioModel(Guid.NewGuid(), "MAP", "sysmiddle", default);
            Assert.Equal(503, Assert.IsType<ObjectResult>(r).StatusCode);
        }
    }
}
