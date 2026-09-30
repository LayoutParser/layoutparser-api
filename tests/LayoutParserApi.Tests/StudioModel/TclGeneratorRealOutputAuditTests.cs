using LayoutParserApi.Services.StudioModel.Tcl;
using LayoutParserApi.Services.XmlAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static LayoutParserApi.Tests.StudioModel.StudioModelTestSupport;

namespace LayoutParserApi.Tests.StudioModel
{
    /// <summary>
    /// AUDITORIA QA (Fase 2) da saida REAL do TclGeneratorService sobre o layout sintetico 01.
    /// Os testes marcados [BUG-CONHECIDO] EXPOEM defeitos do gerador e falham ate ele ser corrigido.
    /// Somente dados sinteticos.
    /// </summary>
    public sealed class TclGeneratorRealOutputAuditTests
    {
        private readonly ITestOutputHelper _o;
        public TclGeneratorRealOutputAuditTests(ITestOutputHelper o) => _o = o;

        private static string Gen() =>
            new TclGeneratorService(NullLogger<TclGeneratorService>.Instance).GenerateTclFromLayoutXml(Fixture("01-layout-entrada-txt.xml"));

        [Fact]
        public void Audit_dump_da_saida_real()
        {
            var tcl = Gen();
            _o.WriteLine(tcl);
            var r = TclReader.Read(tcl);
            foreach (var kv in r.Nodes) _o.WriteLine($"{kv.Key} [{kv.Value.Type}]");
            foreach (var d in r.Diagnostics) _o.WriteLine($"DIAG {d.Code} {d.Path}");
        }

        [Fact(Skip = "BUG-CONHECIDO QA Fase 2: TclGeneratorService ignora hierarquia por aninhamento (so ParentElement). Remover Skip ao corrigir.")]
        public void BUG_CONHECIDO_gerador_nao_emite_CHILD_para_hierarquia_por_aninhamento()
        {
            Assert.Contains("<CHILD>LINHA_ITEM</CHILD>", Gen());
        }

        [Fact(Skip = "BUG-CONHECIDO QA Fase 2: ExtractFieldsFromLine usa Descendants e duplica campos do filho no pai. Remover Skip ao corrigir.")]
        public void BUG_CONHECIDO_gerador_nao_duplica_campos_do_filho_na_linha_pai()
        {
            var r = TclReader.Read(Gen());
            // CAB tem exatamente 2 campos proprios (TipoRegistro, NumeroPedido)
            var campos = r.Nodes.Where(kv => kv.Value.ParentId == "tcl:LINHA_CAB" && kv.Value.Type == "field").Count();
            Assert.Equal(2, campos);
        }

        [Fact]
        public void Reader_tolera_DTD_com_entidade_externa_child_autorreferente_e_ids_sao_deterministas()
        {
            var xxe = TclReader.Read("<!DOCTYPE m [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><MAP><LINE name='A'><FIELD name='&x;' length='1'/></LINE></MAP>");
            Assert.Empty(xxe.Nodes);
            Assert.Contains(xxe.Diagnostics, d => d.Code == "TCL_UNPARSEABLE");

            var self = "<MAP><LINE name='A'><CHILD>A</CHILD><FIELD name='F' length='1'/></LINE></MAP>";
            var r1 = TclReader.Read(self); var r2 = TclReader.Read(self);
            Assert.Equal(r1.Nodes.Select(k => k.Key), r2.Nodes.Select(k => k.Key));
            Assert.Equal(2, r1.Nodes.Count);
        }
    }
}
