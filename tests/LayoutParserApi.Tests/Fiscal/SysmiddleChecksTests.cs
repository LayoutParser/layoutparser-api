using LayoutParserApi.Services.Fiscal;

namespace LayoutParserApi.Tests.Fiscal
{
    public class SysmiddleChecksTests
    {
        private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

        private static string Line(int seq, string name, string init, string fields = "") =>
            $"<Element xmlns:xsi=\"{Xsi}\" xsi:type=\"LineElementVO\"><Name>{name}</Name><Sequence>{seq}</Sequence><InitialValue>{init}</InitialValue><MaximumOccurrence>1</MaximumOccurrence><Elements>{fields}</Elements></Element>";

        private static string Field(int seq, string name, int len) =>
            $"<Element xmlns:xsi=\"{Xsi}\" xsi:type=\"FieldElementVO\"><Name>{name}</Name><Sequence>{seq}</Sequence><LengthField>{len}</LengthField></Element>";

        // Ordem declarada: 001 -> 050 -> 098 -> trailer. O trailer tem InitialValue vazio (casa com qualquer coisa).
        private static string Layout() =>
            "<Layout><Elements>" +
            Line(1, "LINHA001", "001", Field(1, "A001", 2)) +
            Line(2, "LINHA050", "050", Field(1, "TotalX", 2)) +
            Line(3, "LINHA098", "098", Field(1, "Ref098", 2)) +
            Line(4, "LINHA999", "", Field(1, "Fim", 2)) +
            "</Elements></Layout>";

        [Fact]
        public void Ordem_correta_consome_tudo()
        {
            var r = SysmiddleParseOrderChecker.Simulate(Layout(), "001AA050BB098CC");
            Assert.True(r.Consumed);
        }

        [Fact]
        public void Registro_098_fora_de_ordem_faz_trailer_engolir_o_050_e_sobra_conteudo()
        {
            // 098 antes do 050: o 050 não casa mais, o trailer o engole e o resto sobra.
            var r = SysmiddleParseOrderChecker.Simulate(Layout(), "001AA098CC050BB");
            Assert.False(r.Consumed);
            Assert.Equal("LINHA999", r.LastMatchedLine);
        }

        [Fact]
        public void Referencias_do_mapper_sao_checadas_contra_o_layout_com_caixa_e_linha()
        {
            var mapper = "I.LINHA050/TotalX I.LINHA050/totalx I.LINHA001/Ref098 I.LINHA777/Z I.LINHA050/Nada";
            var issues = TclLayoutReferenceChecker.Check(Layout(), mapper).ToDictionary(i => i.Reference);

            Assert.False(issues.ContainsKey("I.LINHA050/TotalX"));
            Assert.Equal("caixa-diferente", issues["I.LINHA050/totalx"].Problem);
            Assert.Equal("TotalX", issues["I.LINHA050/totalx"].Suggestion);
            Assert.Equal("campo-inexistente", issues["I.LINHA001/Ref098"].Problem);
            Assert.Equal("LINHA098/Ref098", issues["I.LINHA001/Ref098"].Suggestion);
            Assert.Equal("linha-inexistente", issues["I.LINHA777/Z"].Problem);
            Assert.Equal("campo-inexistente", issues["I.LINHA050/Nada"].Problem);
        }
    }
}
