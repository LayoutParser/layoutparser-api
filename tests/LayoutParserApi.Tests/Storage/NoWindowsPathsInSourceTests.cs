namespace LayoutParserApi.Tests.Storage;

/// <summary>
/// Guarda de regressão: a produção é Linux; código/config de produção não pode carregar caminhos
/// Windows (inetpub, C:\). Varre Services, Controllers, Models, Program.cs e appsettings*.json.
/// Linhas de comentário são ignoradas; arquivos legitimamente Windows ficam na allowlist.
/// </summary>
public class NoWindowsPathsInSourceTests
{
    // Windows-específicos por natureza: antivírus Defender e sanitizador de caminhos do runner.
    private static readonly string[] Allowlist =
    {
        Path.Combine("Services", "Fiscal", "WindowsDefenderAntivirusScanner.cs"),
        Path.Combine("Services", "Transformation", "LowCode", "LowCodeErrorSanitizer.cs"),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LayoutParserApi.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repo não encontrada.");
    }

    [Fact]
    public void CodigoEConfigDeProducao_NaoContemCaminhosWindows()
    {
        var root = RepoRoot();
        var files = new List<string> { Path.Combine(root, "Program.cs") };
        files.AddRange(Directory.GetFiles(root, "appsettings*.json", SearchOption.TopDirectoryOnly));
        foreach (var sub in new[] { "Services", "Controllers", "Models" })
            files.AddRange(Directory.GetFiles(Path.Combine(root, sub), "*.cs", SearchOption.AllDirectories));

        var violacoes = new List<string>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file);
            if (Allowlist.Contains(rel)) continue;
            var json = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                var t = line.TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*")) continue;
                // linha "//chave" de comentário em JSON do projeto usa valor-texto; ainda assim varre.
                if (line.Contains("inetpub", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(@"C:\", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(@"C:\\", StringComparison.OrdinalIgnoreCase)
                    || (json && line.Contains(@"C:/", StringComparison.OrdinalIgnoreCase)))
                    violacoes.Add($"{rel}:{n}: {line.Trim()}");
            }
        }

        Assert.True(violacoes.Count == 0, "Caminhos Windows encontrados:\n" + string.Join("\n", violacoes));
    }
}
