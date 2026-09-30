using System.Text;
using LayoutParserApi.Services.StudioModel.Tcl;
using LayoutParserApi.Services.XmlAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>Hierarquia/campos diretos do TclGeneratorService (dados sintéticos).</summary>
    public sealed class TclGeneratorHierarchyTests
    {
        private static string Field(string n) =>
            $"<Element xsi:type=\"FieldElementVO\"><Sequence>1</Sequence><Name>{n}</Name><StartValue>1</StartValue><LengthField>3</LengthField></Element>";

        private static string Line(string name, string inner, string? parent = null) =>
            $"<Element xsi:type=\"LineElementVO\"><Name>{name}</Name>{(parent == null ? "" : $"<ParentElement>{parent}</ParentElement>")}<Elements>{inner}</Elements></Element>";

        private static string Doc(string body) =>
            "<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><Elements>" + body + "</Elements></LayoutVO>";

        private static string Gen(string body) =>
            new TclGeneratorService(NullLogger<TclGeneratorService>.Instance).GenerateTclFromLayoutXml(Doc(body));

        [Fact]
        public void Aninhamento_em_3_niveis_gera_CHILD_e_campos_somente_do_proprio_nivel()
        {
            var tcl = Gen(Line("A", Field("fa") + Line("B", Field("fb") + Line("C", Field("fc")))));
            Assert.Contains("<CHILD>B</CHILD>", tcl);
            Assert.Contains("<CHILD>C</CHILD>", tcl);
            var r = TclReader.Read(tcl);
            Assert.Equal(1, r.Nodes.Count(kv => kv.Value.ParentId == "tcl:A" && kv.Value.Type == "field"));
            Assert.Equal(1, r.Nodes.Count(kv => kv.Value.ParentId == "tcl:A/B" && kv.Value.Type == "field"));
            Assert.Contains("tcl:A/B/C", r.Nodes.Select(k => k.Key));
        }

        [Fact]
        public void ParentElement_usa_comparacao_exata_LINHA_1_vs_LINHA_10()
        {
            var tcl = Gen(Line("LINHA_1", Field("x")) + Line("LINHA_10", Field("y"), "LINHA_10X") + Line("LINHA_2", Field("z"), "LINHA_1"));
            var l1 = tcl.Substring(tcl.IndexOf("name=\"LINHA_1\"", StringComparison.Ordinal));
            l1 = l1.Substring(0, l1.IndexOf("</LINE>", StringComparison.Ordinal));
            Assert.Contains("<CHILD>LINHA_2</CHILD>", l1);
            Assert.DoesNotContain("LINHA_10", l1);
        }

        [Fact]
        public void Sem_ParentElement_e_sem_aninhamento_nao_emite_CHILD()
        {
            Assert.DoesNotContain("<CHILD>", Gen(Line("A", Field("a")) + Line("B", Field("b"))));
        }

        [Fact]
        public void Tolera_quirk_Elements_dentro_de_Elements()
        {
            var tcl = Gen("<Element xsi:type=\"LineElementVO\"><Name>A</Name><Elements><Elements>" + Field("a") + Line("B", Field("b")) + "</Elements></Elements></Element>");
            Assert.Contains("<CHILD>B</CHILD>", tcl);
        }

        [Fact]
        public void Reader_limita_profundidade_sem_lancar()
        {
            var sb = new StringBuilder("<MAP>");
            for (var i = 0; i < 200; i++) sb.Append($"<LINE name=\"L{i}\"><CHILD>L{i + 1}</CHILD></LINE>");
            sb.Append("<LINE name=\"L200\"/></MAP>");
            var r = TclReader.Read(sb.ToString());
            Assert.Contains(r.Diagnostics, d => d.Code == "TCL_MAX_DEPTH");
        }
    }
}
