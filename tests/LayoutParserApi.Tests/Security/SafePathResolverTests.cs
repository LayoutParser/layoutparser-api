using LayoutParserApi.Services.Security;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Trava o helper único do P0 (path traversal). O bug provado era
    /// <c>GET /api/document/layout/C:%5CWindows%5Cwin.ini → 200</c> com o win.ini real:
    /// <c>Path.Combine(base, x)</c> DESCARTA o base quando <c>x</c> é enraizado. Estes testes
    /// existem para o bug não voltar — cada entrada perigosa TEM que virar <c>null</c> (recusa).
    /// </summary>
    public class SafePathResolverTests
    {
        // Neutro de plataforma: em Linux "C:\app\base" seria um caminho RELATIVO e IsInsideBase daria falso.
        private static readonly string Base = Path.Combine(Path.GetTempPath(), "app", "base");

        [Theory]
        [InlineData("layout.xml")]
        [InlineData("LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe.tcl")]
        [InlineData("a-b_c.1")]
        public void Nome_valido_resolve_dentro_da_base(string nome)
        {
            var resolved = SafePathResolver.Resolve(Base, nome);

            Assert.NotNull(resolved);
            Assert.StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar, resolved);
            Assert.EndsWith(nome, resolved);
        }

        [Theory]
        [InlineData(@"C:\Windows\win.ini")]      // caminho ENRAIZADO — o vetor exato do P0
        [InlineData(@"..\..\appsettings.json")]  // traversal relativo (barra invertida)
        [InlineData("../../appsettings.json")]   // traversal relativo (barra normal)
        [InlineData(@"sub\arquivo.xml")]         // separador embutido
        [InlineData("sub/arquivo.xml")]
        [InlineData("..")]                        // só o pai
        [InlineData("a:b")]                        // dois-pontos (drive / alternate data stream)
        [InlineData("arq*.xml")]                   // curinga fora da lista branca
        [InlineData("arq|.xml")]
        [InlineData(" ")]
        [InlineData("")]
        public void Nome_perigoso_e_recusado(string nome)
        {
            Assert.Null(SafePathResolver.Resolve(Base, nome));
        }

        [Fact]
        public void Entrada_nula_e_recusada_sem_lancar()
        {
            Assert.Null(SafePathResolver.Resolve(Base, null!));
            Assert.Null(SafePathResolver.Resolve(null!, "layout.xml"));
            Assert.Null(SafePathResolver.Resolve("", "layout.xml"));
        }

        /// <summary>
        /// A lista branca permite '.', então ".." casaria a regex — a recusa explícita de ".." é o
        /// que fecha isso. Um nome com ponto simples (versão de layout) tem que passar.
        /// </summary>
        [Fact]
        public void Ponto_simples_passa_mas_ponto_duplo_nao()
        {
            Assert.NotNull(SafePathResolver.Resolve(Base, "4.00.tcl"));
            Assert.Null(SafePathResolver.Resolve(Base, "4..00.tcl"));
        }

        [Fact]
        public void IsInsideBase_aceita_dentro_recusa_fora()
        {
            var raiz = Path.GetTempPath();
            Assert.True(SafePathResolver.IsInsideBase(Base, Path.Combine(Base, "sub", "f.txt")));
            Assert.False(SafePathResolver.IsInsideBase(Base, Path.Combine(raiz, "app", "other", "f.txt")));
            // Prefixo de nome NÃO conta como contido: "base_evil" não está dentro de "base".
            Assert.False(SafePathResolver.IsInsideBase(Base, Path.Combine(raiz, "app", "base_evil", "f.txt")));
        }
    }
}
