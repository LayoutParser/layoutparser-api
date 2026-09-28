using System.Net;

using LayoutParserApi.Services.Security;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>Fase 4 da migração Linux (issue #579): redes CIDR confiáveis além do loopback.</summary>
    public class TrustedIdentityNetworksTests
    {
        [Theory]
        [InlineData("10.20.0.5", "10.20.0.0/24", true)]
        [InlineData("10.20.1.5", "10.20.0.0/24", false)]   // fora da rede
        [InlineData("::ffff:10.20.0.5", "10.20.0.0/24", true)] // IPv4 mapeado
        [InlineData("10.20.0.5", "lixo", false)]           // CIDR inválido: ignora, não lança
        [InlineData("10.20.0.5", "", false)]
        public void IsInTrustedNetworks_casos(string ip, string cidr, bool esperado)
        {
            Assert.Equal(esperado, TrustedIdentityPolicy.IsInTrustedNetworks(IPAddress.Parse(ip), new[] { cidr }));
        }

        [Fact]
        public void IsInTrustedNetworks_nulos_sao_fail_closed()
        {
            Assert.False(TrustedIdentityPolicy.IsInTrustedNetworks(null, new[] { "10.0.0.0/8" }));
            Assert.False(TrustedIdentityPolicy.IsInTrustedNetworks(IPAddress.Parse("10.0.0.1"), null));
        }

        [Fact]
        public void ShouldTrust_rede_confiavel_passa_mesmo_com_guarda()
        {
            Assert.True(TrustedIdentityPolicy.ShouldTrust(false, true, true));
            Assert.False(TrustedIdentityPolicy.ShouldTrust(false, true, false));
        }
    }
}
