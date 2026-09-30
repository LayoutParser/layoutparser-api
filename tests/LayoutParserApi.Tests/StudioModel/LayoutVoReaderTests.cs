using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.StudioModel;
using LayoutParserApi.Services.StudioModel.Sysmiddle;

using Xunit;

namespace LayoutParserApi.Tests.StudioModel
{
    public sealed class LayoutVoReaderTests
    {
        private static StudioNode N(LayoutReadResult r, string idSuffix) => r.Nodes.Single(n => n.Key.EndsWith(idSuffix)).Value;

        [Fact]
        public void Amostra01_tipos_props_e_offset_corretos()
        {
            var r = LayoutVoReader.Read(StudioModelTestSupport.InputXml, "input");

            Assert.Equal("text-positional", r.Format);
            Assert.Equal(new[] { "LIN_00000000-0000-0000-0000-00000000b001" }, r.RootIds);
            Assert.Equal("line", N(r, "b001").Type);
            Assert.Equal("LINHA_CAB", N(r, "b001").Path);

            var tipo = N(r, "c001");
            Assert.Equal("field", tipo.Type);
            Assert.Equal(0, tipo.Props["offset"]);
            Assert.Equal(3, tipo.Props["length"]);
            Assert.Equal("CAB", tipo.Props["staticValue"]);
            Assert.Equal(3, N(r, "c002").Props["offset"]);           // NumeroPedido: 0 + 3
            Assert.Equal("LINHA_CAB/NumeroPedido", N(r, "c002").Path);

            // linha filha não conta no offset dos campos irmãos; campos da filha reiniciam em 0
            Assert.Equal("LINHA_CAB/LINHA_ITEM", N(r, "b002").Path);
            Assert.Equal(0, N(r, "c003").Props["offset"]);
            Assert.Equal(3, N(r, "c004").Props["offset"]);
            Assert.Equal(8, N(r, "c005").Props["offset"]);
            Assert.Equal("Right", N(r, "c005").Props["align"]);
            Assert.Equal(3, N(r, "b002").Order);                     // Sequence da linha filha
        }

        [Fact]
        public void Amostra02_quirk_Elements_Elements_e_atributos()
        {
            var r = LayoutVoReader.Read(StudioModelTestSupport.TargetXml, "target");
            Assert.Equal("xml", r.Format);
            Assert.Equal("groupTag", N(r, "e001").Type);
            Assert.Equal("pedido/item/qtd", N(r, "f003").Path);
            Assert.DoesNotContain(N(r, "f001").Props.Keys, k => k == "offset"); // offset só em posicional

            // quirk: itens dentro de <Elements><Elements>
            var quirk = StudioModelTestSupport.TargetXml.Replace("<Element ", "<Elements ").Replace("</Element>", "</Elements>");
            var q = LayoutVoReader.Read(quirk, "target");
            Assert.Equal(r.Nodes.Select(n => n.Key), q.Nodes.Select(n => n.Key));
        }

        [Fact]
        public void Choice_e_Sequence_viram_nos_mas_nao_entram_no_path()
        {
            var xml = """
                <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="XmlLayoutVO"><LayoutType>Xml</LayoutType>
                <Elements><Element xsi:type="GroupTagElementVO"><ElementGuid>G1</ElementGuid><Sequence>1</Sequence><Name>raiz</Name>
                 <Elements>
                  <Element xsi:type="ChoiceElementVO"><ElementGuid>C1</ElementGuid><Sequence>1</Sequence><Name>esc</Name>
                   <Elements><Element xsi:type="TagElementVO"><ElementGuid>T1</ElementGuid><Sequence>1</Sequence><Name>a</Name></Element></Elements>
                  </Element>
                  <Element xsi:type="SequenceElementVO"><ElementGuid>S1</ElementGuid><Sequence>2</Sequence><Name>seq</Name></Element>
                 </Elements></Element></Elements></LayoutVO>
                """;
            var r = LayoutVoReader.Read(xml, "target");
            Assert.Equal("choice", r.Nodes.Single(n => n.Key == "C1").Value.Type);
            Assert.Equal("sequence", r.Nodes.Single(n => n.Key == "S1").Value.Type);
            Assert.Equal("raiz/a", r.Nodes.Single(n => n.Key == "T1").Value.Path);
            Assert.Equal("C1", r.Nodes.Single(n => n.Key == "T1").Value.ParentId);
        }

        [Fact]
        public void Sequence_ordena_irmaos_de_qualquer_tipo()
        {
            var xml = """
                <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="TextLayoutVO"><LayoutType>TextPositional</LayoutType>
                <Elements><Element xsi:type="LineElementVO"><ElementGuid>L</ElementGuid><Sequence>1</Sequence><Name>L</Name><Elements>
                  <Element xsi:type="LineElementVO"><ElementGuid>FILHA</ElementGuid><Sequence>2</Sequence><Name>filha</Name></Element>
                  <Element xsi:type="FieldElementVO"><ElementGuid>F2</ElementGuid><Sequence>3</Sequence><Name>f2</Name><LengthField>2</LengthField></Element>
                  <Element xsi:type="FieldElementVO"><ElementGuid>F1</ElementGuid><Sequence>1</Sequence><Name>f1</Name><LengthField>4</LengthField></Element>
                </Elements></Element></Elements></LayoutVO>
                """;
            var r = LayoutVoReader.Read(xml, "input");
            Assert.Equal(new[] { "L", "F1", "FILHA", "F2" }, r.Nodes.Select(n => n.Key));
            Assert.Equal(4, r.Nodes.Single(n => n.Key == "F2").Value.Props["offset"]);
        }

        [Fact]
        public void Tipo_desconhecido_vira_unknown_sem_excecao()
        {
            var xml = """
                <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="XmlLayoutVO">
                <Elements><Element xsi:type="FooBarElementVO"><ElementGuid>X1</ElementGuid><Name>x</Name></Element></Elements></LayoutVO>
                """;
            var r = LayoutVoReader.Read(xml, "target");
            var n = r.Nodes.Single().Value;
            Assert.Equal("unknown", n.Type);
            Assert.Equal("FooBarElementVO", n.Props["xsiType"]);
            Assert.Contains(r.Diagnostics, d => d.Code == "UNKNOWN_NODE_TYPE" && d.Id == "X1");
        }

        [Fact]
        public void Offset_nao_resolvido_quando_irmao_anterior_sem_length()
        {
            var xml = """
                <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="TextLayoutVO"><LayoutType>TextPositional</LayoutType>
                <Elements><Element xsi:type="LineElementVO"><ElementGuid>L</ElementGuid><Name>L</Name><Elements>
                  <Element xsi:type="FieldElementVO"><ElementGuid>A</ElementGuid><Sequence>1</Sequence><Name>a</Name></Element>
                  <Element xsi:type="FieldElementVO"><ElementGuid>B</ElementGuid><Sequence>2</Sequence><Name>b</Name><LengthField>2</LengthField></Element>
                </Elements></Element></Elements></LayoutVO>
                """;
            var r = LayoutVoReader.Read(xml, "input");
            Assert.False(r.Nodes.Single(n => n.Key == "B").Value.Props.ContainsKey("offset"));
            Assert.Contains(r.Diagnostics, d => d.Code == "OFFSET_UNRESOLVED" && d.Id == "B");
        }

        [Fact]
        public void No_sem_ElementGuid_recebe_id_sintetico_e_diagnostico()
        {
            var xml = """
                <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="XmlLayoutVO">
                <Elements><Element xsi:type="TagElementVO"><Name>t</Name></Element></Elements></LayoutVO>
                """;
            var r = LayoutVoReader.Read(xml, "target");
            Assert.StartsWith("idx:target:", r.Nodes.Single().Key);
            Assert.Contains(r.Diagnostics, d => d.Code == "MISSING_ELEMENT_GUID");
        }

        // ---------- tolerância (design §7-2): variantes derivadas das amostras ----------

        private static object[] Shape(LayoutReadResult r)
            => r.Nodes.Select(n => (object)(n.Key, n.Value.Type, n.Value.Name, n.Value.Path, n.Value.ParentId, n.Value.Order, n.Value.Required)).ToArray();

        private static string Transform(string xml, Action<XElement> edit)
        {
            var doc = XDocument.Parse(xml);
            edit(doc.Root!);
            return doc.ToString();
        }

        [Fact]
        public void Ordem_trocada_dos_elementos_produz_o_mesmo_modelo()
        {
            var baseline = LayoutVoReader.Read(StudioModelTestSupport.InputXml, "input");
            var reversed = Transform(StudioModelTestSupport.InputXml, root =>
            {
                foreach (var el in root.Descendants().Where(e => e.Name.LocalName == "Element").ToList())
                {
                    var kids = el.Elements().Reverse().ToList();
                    el.RemoveNodes();
                    el.Add(kids);
                }
                foreach (var c in root.Descendants().Where(e => e.Name.LocalName == "Elements").ToList())
                {
                    var kids = c.Elements().Reverse().ToList();
                    c.RemoveNodes();
                    c.Add(kids);
                }
            });
            var r = LayoutVoReader.Read(reversed, "input");
            Assert.Equal(Shape(baseline), Shape(r));
            Assert.Equal(baseline.Nodes.Select(n => n.Value.Props.GetValueOrDefault("offset")), r.Nodes.Select(n => n.Value.Props.GetValueOrDefault("offset")));
        }

        [Fact]
        public void Sem_Description_e_IsRequired_nao_quebra()
        {
            var x = Transform(StudioModelTestSupport.InputXml, root =>
                root.Descendants().Where(e => e.Name.LocalName is "Description" or "IsRequired").ToList().ForEach(e => e.Remove()));
            var r = LayoutVoReader.Read(x, "input");
            Assert.Equal(7, r.Nodes.Count); // 2 linhas + 5 campos
            Assert.All(r.Nodes, n => Assert.False(n.Value.Required));
            Assert.All(r.Nodes, n => Assert.False(n.Value.Props.ContainsKey("description")));
        }

        [Fact]
        public void Elementos_de_versoes_novas_entram_em_variantFields_e_nao_em_extra()
        {
            var x = Transform(StudioModelTestSupport.InputXml, root =>
            {
                foreach (var f in root.Descendants().Where(e => e.Name.LocalName == "Element" && e.Element("Name")!.Value == "NumeroPedido"))
                    f.Add(new XElement("FullXPath", "a/b"), new XElement("ElementType", "X"), new XElement("ParentElement", "P"), new XElement("ElementWithoutValue", "false"));
            });
            var r = LayoutVoReader.Read(x, "input");
            var baseline = LayoutVoReader.Read(StudioModelTestSupport.InputXml, "input");
            Assert.Equal(Shape(baseline), Shape(r));
            Assert.Superset(new HashSet<string> { "FullXPath", "ElementType", "ParentElement", "ElementWithoutValue" }, r.VariantFields);
            Assert.All(r.Nodes, n => Assert.False(n.Value.Props.ContainsKey("extra")));
        }

        [Fact]
        public void Elementos_desconhecidos_no_meio_sao_ignorados_e_preservados_em_extra()
        {
            var x = Transform(StudioModelTestSupport.InputXml, root =>
                root.Descendants().First(e => e.Name.LocalName == "Element" && e.Element("Name")?.Value == "NumeroPedido")
                    .Element("Sequence")!.AddAfterSelf(new XElement("CampoNovoDaVersao", "valor")));
            var r = LayoutVoReader.Read(x, "input");
            var n = r.Nodes.Single(k => k.Key.EndsWith("c002")).Value;
            Assert.Equal("LINHA_CAB/NumeroPedido", n.Path);
            var extra = Assert.IsType<Dictionary<string, string>>(n.Props["extra"]);
            Assert.Equal("valor", extra["CampoNovoDaVersao"]);
        }

        [Fact]
        public void Layout_sem_LayoutType_deriva_formato_do_xsi_type()
        {
            var x = Transform(StudioModelTestSupport.TargetXml, root => root.Element("LayoutType")!.Remove());
            Assert.Equal("xml", LayoutVoReader.Read(x, "target").Format);
        }

        [Fact]
        public void Xml_invalido_lanca_XmlException_para_o_chamador_degradar()
            => Assert.ThrowsAny<System.Xml.XmlException>(() => LayoutVoReader.Read("<LayoutVO>", "input"));
    }
}
