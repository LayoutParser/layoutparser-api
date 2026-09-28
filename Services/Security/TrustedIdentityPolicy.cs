namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Núcleo puro da extração de identidade: a decisão de confiança e o parse do CSV de papéis.
    /// Separado do <c>TrustedIdentityMiddleware</c> pelo mesmo motivo de <c>LowCodeCandidatesBudget</c>
    /// — o middleware depende de <c>HttpContext</c>; esta classe é pura e é exatamente a parte que
    /// precisa de teste, porque uma decisão de confiança frouxa abre a porta sem ninguém notar.
    /// </summary>
    public static class TrustedIdentityPolicy
    {
        /// <summary>
        /// 🔴 A GUARDA, em forma pura. Só confia nos headers de identidade quando a origem é confiável.
        /// Com <paramref name="trustLoopbackOnly"/> ligado (default de produção), confiar exige
        /// <paramref name="isLoopback"/> — o salto do BFF co-hospedado. Fora de loopback, retorna
        /// <c>false</c> e os headers são ignorados por completo.
        /// </summary>
        public static bool ShouldTrust(bool isLoopback, bool trustLoopbackOnly, bool inTrustedNetwork = false)
            => !trustLoopbackOnly || isLoopback || inTrustedNetwork;

        /// <summary>
        /// Fase 4 da migração Linux (issue #579): topologia multi-host. Retorna <c>true</c> se o IP de origem
        /// pertence a alguma das redes CIDR confiáveis (ex.: <c>10.20.0.0/24</c> — rede interna isolada entre
        /// BFF e API). Lista vazia/nula ou IP nulo → <c>false</c> (fail-closed). CIDR inválido é ignorado,
        /// nunca lança. IPv4 mapeado em IPv6 (<c>::ffff:a.b.c.d</c>) é normalizado antes da comparação.
        /// </summary>
        public static bool IsInTrustedNetworks(System.Net.IPAddress? remoteIp, IEnumerable<string>? cidrs)
        {
            if (remoteIp == null || cidrs == null)
                return false;

            if (remoteIp.IsIPv4MappedToIPv6)
                remoteIp = remoteIp.MapToIPv4();

            foreach (var cidr in cidrs)
            {
                if (string.IsNullOrWhiteSpace(cidr))
                    continue;

                if (System.Net.IPNetwork.TryParse(cidr.Trim(), out var rede) && rede.Contains(remoteIp))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Parseia o CSV de papéis (<c>x-iis-roles</c>): separa por vírgula, apara espaços e descarta
        /// entradas vazias. <c>null</c>/vazio → lista vazia, nunca lança.
        /// </summary>
        public static IReadOnlyList<string> ParseRoles(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return Array.Empty<string>();

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
