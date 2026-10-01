using XslSynth.Core;
using Xunit;

namespace LayoutParserApi.Tests.Sysmiddle;

public class SysmiddleDslTokenizerTests
{
    [Theory]
    [InlineData("T.a = I.ns:Campo.x@y$z;", "ns:Campo.x@y$z")]
    [InlineData("T.a = I.CAB/VL-TOT;", "CAB/VL")]
    [InlineData("T.a = I.CAB/NOME COMPLETO ;", "CAB/NOME COMPLETO")]
    public void Input_segue_a_tokenizacao_do_motor(string dsl, string esperado) =>
        Assert.Equal(esperado, SysmiddleDslTokenizer.InputPaths(dsl).Single());

    [Fact]
    public void Input_em_aspas_simples_e_comentarios_e_ignorado()
    {
        var dsl = "T.a = 'x I.FAKE y'; // I.COM1\n/* I.COM2 */ T.b = I.REAL;";
        Assert.Equal(new[] { "REAL" }, SysmiddleDslTokenizer.InputPaths(dsl));
    }

    [Fact]
    public void Aspas_duplas_nao_protegem()
    {
        Assert.StartsWith("DENTRO", SysmiddleDslTokenizer.InputPaths("T.a = \"I.DENTRO\";").Single());
    }

    [Fact]
    public void Categorias_F_S_N_nao_sao_referencias() =>
        Assert.Empty(SysmiddleDslTokenizer.Scan("F.Func(S.x, N.y);"));

    [Fact]
    public void Target_so_em_atribuicao()
    {
        var dsl = "if (T.a/b == 'x') begin T.c/d = I.L; end";
        Assert.Equal(new[] { "c/d" }, SysmiddleDslTokenizer.TargetPaths(dsl));
    }

    [Fact]
    public void Hash_e_dollar_param_em_espaco_barra_ou_dois_pontos()
    {
        var refs = SysmiddleDslTokenizer.Scan("T.a = F(#.v1 + $.v2/x);");
        Assert.Contains(refs, r => r.Kind == SysmiddleRefKind.Hash && r.Path == "v1");
        Assert.Contains(refs, r => r.Kind == SysmiddleRefKind.Dollar && r.Path == "v2");
    }

    [Fact]
    public void Nao_reconhece_prefixo_colado_a_identificador() =>
        Assert.Empty(SysmiddleDslTokenizer.InputPaths("T.a = FI.x;"));
}
