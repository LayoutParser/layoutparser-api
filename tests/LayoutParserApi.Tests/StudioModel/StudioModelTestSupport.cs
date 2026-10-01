using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.StudioModel;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Fakes e fixtures SINTÉTICAS (amostras 01-03 do design §7; nenhum dado real de cliente).</summary>
    internal static class StudioModelTestSupport
    {
        public const string MapperGuid = "MAP_00000000-0000-0000-0000-000000001001";
        public const string InputLayoutGuid = "LAY_00000000-0000-0000-0000-00000000a001";
        public const string TargetLayoutGuid = "LAY_00000000-0000-0000-0000-00000000a002";

        public static string Fixture(string name)
            => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "StudioModel", name)).Replace("\r\n", "\n"); // normaliza EOL: checkout CRLF (Windows) nao pode alterar o golden

        public static string InputXml => Fixture("01-layout-entrada-txt.xml");
        public static string TargetXml => Fixture("02-layout-saida-xml.xml");
        public static string MapperXml => Fixture("03-mapeador.xml");

        public sealed class FakeMappers : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();
            public Exception? Throw { get; set; }
            public Task<List<Mapper>> GetAllMappersAsync() => Throw != null ? throw Throw : Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string g) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string g) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        public sealed class FakeLayouts : ICachedLayoutService
        {
            public Dictionary<string, LayoutRecord> ByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest r) => throw new NotSupportedException();
            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => throw new NotSupportedException();
            public Task<LayoutRecord?> GetLayoutByGuidAsync(string g) => Task.FromResult(ByGuid.TryGetValue(g, out var l) ? l : null);
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
            public Task ClearCacheAsync() => Task.CompletedTask;
            public ILayoutDatabaseService GetLayoutDatabaseService() => throw new NotSupportedException();
        }

        public sealed class FakeCatalog : IDataTypeCatalog
        {
            public string? ResolveName(string guid) => "Str_MAX";
        }

        public static (SysmiddleStudioModelAdapter Adapter, FakeMappers Mappers, FakeLayouts Layouts) Build(
            string? mapperXml = null, string? inputXml = null, string? targetXml = null, IDataTypeCatalog? catalog = null)
        {
            var mappers = new FakeMappers();
            mappers.Mappers.Add(new Mapper
            {
                MapperGuid = MapperGuid,
                Name = "MAP_EXEMPLO",
                DecryptedContent = mapperXml ?? MapperXml,
                InputLayoutGuidFromXml = InputLayoutGuid,
                TargetLayoutGuidFromXml = TargetLayoutGuid,
            });
            var layouts = new FakeLayouts();
            layouts.ByGuid[InputLayoutGuid] = new LayoutRecord { Name = "IN", DecryptedContent = inputXml ?? InputXml };
            layouts.ByGuid[TargetLayoutGuid] = new LayoutRecord { Name = "OUT", DecryptedContent = targetXml ?? TargetXml };
            return (new SysmiddleStudioModelAdapter(mappers, layouts, NullLogger<SysmiddleStudioModelAdapter>.Instance, catalog), mappers, layouts);
        }

        public static Task<LayoutParserApi.Models.Dtos.StudioModel.StudioModelDocument?> Load(SysmiddleStudioModelAdapter a)
            => a.LoadAsync(new StudioModelRequest(Guid.NewGuid(), MapperGuid), CancellationToken.None);
    }
}
