using LayoutParserApi.Services.Storage;
using Microsoft.Extensions.Configuration;

namespace LayoutParserApi.Tests.Storage;

public class StoragePathsTests
{
    private static StoragePaths Build(Dictionary<string, string?>? values = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build());

    [Fact]
    public void DataRoot_PorChave_DerivaTodosOsSubcaminhos()
    {
        var root = Path.Combine(Path.GetTempPath(), "lp-storage-test");
        var p = Build(new() { ["Storage:DataRoot"] = root });

        Assert.Equal(root, p.DataRoot);
        Assert.Equal(Path.Combine(root, "tcl"), p.Tcl);
        Assert.Equal(Path.Combine(root, "xsl"), p.Xsl);
        Assert.Equal(Path.Combine(root, "Examples"), p.Examples);
        Assert.Equal(Path.Combine(root, "Examples", "tcl"), p.ExamplesTcl);
        Assert.Equal(Path.Combine(root, "Examples", "xsl"), p.ExamplesXsl);
        Assert.Equal(Path.Combine(root, "LearningModels"), p.LearningModels);
        Assert.Equal(Path.Combine(root, "ExpectedOutputs"), p.ExpectedOutputs);
        Assert.Equal(Path.Combine(root, "MLData"), p.MlData);
        Assert.Equal(Path.Combine(root, "MLData", "LowCodeTransformations"), p.LowCodeTransformations);
        Assert.Equal(Path.Combine(root, "ai", "XslSynth", "training-data"), p.AiTrainingData);
        Assert.Equal(Path.Combine(root, "xsd"), p.Xsd);
        Assert.Equal(Path.Combine(root, "pdf"), p.Pdf);
        Assert.Equal(Path.Combine(root, "TransformationRules"), p.TransformationRules);
    }

    [Fact]
    public void ChaveLegada_VenceSobreDataRoot()
    {
        var p = Build(new()
        {
            ["Storage:DataRoot"] = "/raiz",
            ["TransformationPipeline:TclPath"] = "/legado/tcl",
            ["TransformationPipeline:ExamplesPath"] = "/legado/ex",
            ["ML:LowCodeTransformationsPath"] = "/legado/lc",
        });

        Assert.Equal("/legado/tcl", p.Tcl);
        Assert.Equal("/legado/ex", p.Examples);
        // ExamplesTcl deriva de Examples (já sobrescrito), a menos que tenha chave própria.
        Assert.Equal(Path.Combine("/legado/ex", "tcl"), p.ExamplesTcl);
        Assert.Equal("/legado/lc", p.LowCodeTransformations);
        Assert.Equal(Path.Combine("/raiz", "xsl"), p.Xsl);
    }

    [Fact]
    public void SemChaves_DefaultNuncaFicaSobAppDirNoLinuxDeProducao_ENuncaEhWindows()
    {
        var p = Build();

        if (OperatingSystem.IsLinux() && Directory.Exists(StoragePaths.LinuxProductionDataRoot))
            Assert.Equal(StoragePaths.LinuxProductionDataRoot, p.DataRoot);
        else
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "data"), p.DataRoot);

        Assert.DoesNotContain("inetpub", p.Tcl, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("tcl", p.Tcl);
    }
}
