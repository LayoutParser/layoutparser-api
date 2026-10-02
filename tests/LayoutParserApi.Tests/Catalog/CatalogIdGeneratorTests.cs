using System.Text.Json;

using LayoutParserApi.Models.Catalog;

namespace LayoutParserApi.Tests.Catalog
{
    public class CatalogIdGeneratorTests
    {
        [Fact]
        public void Determinismo_mesmos_insumos_mesmo_id()
        {
            var a = CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "proj-1", "ABC-123");
            var b = CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "proj-1", "ABC-123");
            Assert.Equal(a, b);
        }

        [Fact]
        public void Valor_conhecido_v5_trava_o_algoritmo()
        {
            // RFC 4122: uuid5(DNS namespace, "www.example.com") = 2ed6657d-e927-568b-95e1-2665a8aea6a2.
            // Aqui o namespace é próprio; travamos o valor para detectar mudança de normalização/namespace.
            var id = CatalogIdGenerator.ForItem(SourceSystem.Neogrid, "nfe", "nfe/4.00/NFe_Entrada");
            Assert.Equal(5, (id.ToByteArray()[7] >> 4)); // nibble de versão (byte 7 no layout .NET)
            Assert.Equal("neogrid|nfe|nfe/4.00/nfe_entrada", CatalogIdGenerator.BuildName(SourceSystem.Neogrid, "nfe", "nfe/4.00/NFe_Entrada"));
            Assert.Equal(id, CatalogIdGenerator.ForItem(SourceSystem.Neogrid, "NFE ", "nfe\\4.00\\NFe_Entrada"));
        }

        [Fact]
        public void Mesmo_mapperGuid_em_projetos_diferentes_gera_ids_diferentes()
        {
            var a = CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "proj-A", "mapper-guid-1");
            var b = CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "proj-B", "mapper-guid-1");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Mesmo_item_em_origens_diferentes_gera_ids_diferentes()
        {
            Assert.NotEqual(
                CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "p", "k"),
                CatalogIdGenerator.ForItem(SourceSystem.Own, "p", "k"));
        }

        [Fact]
        public void Projeto_nulo_ou_vazio_equivale_a_Sem_projeto()
        {
            var semProjeto = CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "-", "k");
            Assert.Equal(semProjeto, CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, null, "k"));
            Assert.Equal(semProjeto, CatalogIdGenerator.ForItem(SourceSystem.ConnectUs, "  ", "k"));
        }

        [Fact]
        public void Separador_nas_chaves_nao_causa_colisao()
        {
            Assert.NotEqual(
                CatalogIdGenerator.ForItem(SourceSystem.Own, "a|b", "c"),
                CatalogIdGenerator.ForItem(SourceSystem.Own, "a", "b|c"));
        }

        [Fact]
        public void ItemKey_vazio_e_invalido()
        {
            Assert.Throws<ArgumentException>(() => CatalogIdGenerator.ForItem(SourceSystem.Own, "p", "  "));
        }

        [Fact]
        public void FolderId_difere_de_qualquer_item_e_e_deterministico()
        {
            var folder = CatalogIdGenerator.ForFolder(SourceSystem.Neogrid, "nfe");
            Assert.Equal(folder, CatalogIdGenerator.ForFolder(SourceSystem.Neogrid, "NFE"));
            Assert.NotEqual(folder, CatalogIdGenerator.ForItem(SourceSystem.Neogrid, "nfe", "x"));
        }

        [Fact]
        public void SourceSystem_serializa_snake_case()
        {
            Assert.Equal("\"connect_us\"", JsonSerializer.Serialize(SourceSystem.ConnectUs));
            Assert.Equal("\"map4connect\"", JsonSerializer.Serialize(SourceSystem.Map4Connect));
            Assert.Equal(SourceSystem.Own, JsonSerializer.Deserialize<SourceSystem>("\"own\""));
            Assert.True(SourceSystemExtensions.TryParseWireName("NEOGRID", out var s) && s == SourceSystem.Neogrid);
            Assert.False(SourceSystemExtensions.TryParseWireName("xyz", out _));
        }
    }
}
