using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #425 — <see cref="LayoutTreeController"/>. RBAC/membership (404 fail-closed por
    /// <c>RequireWorkspaceRoleFilter</c>) já é coberto na integração real do filtro (mesmo padrão
    /// não retestado por controller unitário nos demais controllers da governança — ver
    /// <c>MappingDraftsControllerListTests</c>); aqui cobrimos o que o controller decide sozinho:
    /// mapper resolvido → 200 com o contrato, mapper não encontrado → 404, falha de infra → 503.
    /// </summary>
    public sealed class LayoutTreeControllerTests
    {
        private sealed class FakeLayoutTreeService : ILayoutTreeService
        {
            public LayoutTreeResponse? Response { get; set; }
            public Exception? ThrowOnCall { get; set; }

            public Task<LayoutTreeResponse?> GetLayoutTreeAsync(string mappingId, CancellationToken cancellationToken, LayoutTreeDslOptions? dslOptions = null)
                => ThrowOnCall is not null ? throw ThrowOnCall : Task.FromResult(Response);
        }

        private static LayoutTreeController BuildController(FakeLayoutTreeService service)
            => new(service, NullLogger<LayoutTreeController>.Instance);

        [Fact]
        public async Task Mapper_nao_encontrado_retorna_404()
        {
            var service = new FakeLayoutTreeService { Response = null };
            var controller = BuildController(service);

            var result = await controller.GetLayoutTree(Guid.NewGuid(), "MAP_NAO_EXISTE", CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Mapper_encontrado_retorna_200_com_contrato()
        {
            var expected = new LayoutTreeResponse(
                "MAP_1",
                new LayoutTreeSide("LAY_SOURCE", "text", Array.Empty<LayoutTreeNodeDto>()),
                new LayoutTreeSide("LAY_TARGET", "xml", Array.Empty<LayoutTreeNodeDto>()),
                Array.Empty<LayoutTreeRule>(),
                Array.Empty<string>());
            var service = new FakeLayoutTreeService { Response = expected };
            var controller = BuildController(service);

            var result = await controller.GetLayoutTree(Guid.NewGuid(), "MAP_1", CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(expected, ok.Value);
        }

        [Fact]
        public async Task Falha_de_infra_no_catalogo_de_mappers_retorna_503_sem_derrubar_o_processo()
        {
            var service = new FakeLayoutTreeService { ThrowOnCall = new InvalidOperationException("SQL indisponível") };
            var controller = BuildController(service);

            var result = await controller.GetLayoutTree(Guid.NewGuid(), "MAP_1", CancellationToken.None);

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
        }
    }
}
