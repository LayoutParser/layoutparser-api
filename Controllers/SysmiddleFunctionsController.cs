using LayoutParserApi.Services.Sysmiddle;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>Catálogo somente leitura das funções da DSL Sysmiddle (builtins + funções NDD).</summary>
    [ApiController]
    [Route("api/sysmiddle/functions")]
    public class SysmiddleFunctionsController : ControllerBase
    {
        private readonly ISysmiddleFunctionCatalog _catalog;

        public SysmiddleFunctionsController(ISysmiddleFunctionCatalog catalog) => _catalog = catalog;

        /// <summary>
        /// Lista as funções conhecidas: <c>name</c>, <c>origin</c> (<c>builtin</c>|<c>ndd-custom</c>),
        /// <c>parameters[{name,type}]</c>, <c>returnType</c> e <c>description</c> (PT-BR).
        /// As funções NDD vêm de reflection somente-leitura da DLL (nada é executado); como a DLL é
        /// ofuscada, seus parâmetros aparecem como <c>pars:object[]</c>. Sem a DLL, só os builtins.
        /// </summary>
        /// <response code="200">Lista de funções.</response>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<SysmiddleFunctionInfo>), StatusCodes.Status200OK)]
        public IActionResult GetAll() => Ok(_catalog.GetAll());
    }
}
