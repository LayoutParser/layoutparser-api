namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Configuração do consumo da identidade injetada pelo BFF. Ligada à seção <c>Security</c>
    /// (env vars <c>Security__TrustedUserHeader</c>, etc.), para que API e BFF fiquem sincronizáveis
    /// por config, sem recompilar.
    /// </summary>
    /// <remarks>
    /// Os defaults dos headers espelham o contrato lido do BFF (<c>server/src/config.ts</c>):
    /// <c>x-iis-user</c> / <c>x-iis-roles</c>. O BFF remove as versões <i>inbound</i> desses headers
    /// antes de injetar (anti-spoof na camada dele) — mas isso só protege o caminho browser→BFF; o
    /// caminho direto-para-a-API é fechado por <see cref="TrustIdentityFromLoopbackOnly"/>.
    /// </remarks>
    public class TrustedIdentityOptions
    {
        /// <summary>Seção de configuração (env var: <c>Security__...</c>).</summary>
        public const string SectionName = "Security";

        /// <summary>Header que carrega o usuário. Default <c>x-iis-user</c> (env <c>Security__TrustedUserHeader</c>).</summary>
        public string TrustedUserHeader { get; set; } = "x-iis-user";

        /// <summary>Header que carrega os papéis (CSV). Default <c>x-iis-roles</c> (env <c>Security__TrustedRolesHeader</c>).</summary>
        public string TrustedRolesHeader { get; set; } = "x-iis-roles";

        /// <summary>
        /// 🔴 A GUARDA. Quando <c>true</c> (default), a identidade dos headers só é confiada se a
        /// conexão for <b>loopback</b> (<c>127.0.0.1</c>/<c>::1</c>) — ou seja, o salto do BFF
        /// co-hospedado. Qualquer origem de outro host tem os headers <b>ignorados por completo</b>
        /// (identidade anônima), então um atacante remoto não forja identidade mesmo com a API em
        /// <c>0.0.0.0</c>.
        /// <para>Desligar isto (<c>false</c>) só faz sentido se o BFF estiver em outro host E a rede já
        /// garantir a origem por firewall — nesse caso a trava é de infra, não desta flag. Não há chave
        /// para isto no <c>appsettings.json</c> de propósito: quem precisar desligar assume o risco
        /// explicitamente via <c>Security__TrustIdentityFromLoopbackOnly=false</c>.</para>
        /// </summary>
        public bool TrustIdentityFromLoopbackOnly { get; set; } = true;

        /// <summary>
        /// Fase 4 da migração Linux (issue #579): redes CIDR (env <c>Security__TrustedProxyNetworks__0=10.20.0.0/24</c>)
        /// de onde os headers de identidade também são confiados, além do loopback. Serve à topologia
        /// multi-host (BFF e API em containers/hosts distintos) sem desligar a guarda. Default vazio =
        /// comportamento atual (somente loopback). A rede precisa ser isolada — CIDR largo reabre a forja.
        /// </summary>
        public string[] TrustedProxyNetworks { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Header com o provedor de identidade (<c>entra</c>/<c>google</c>/<c>development</c>) — Slice 1
        /// (issue #225). Default conforme o contrato cross-repo
        /// <c>fiscal-workspace-and-mapping-explanation-api.md</c> §1.
        /// </summary>
        public string IdentityProviderHeader { get; set; } = "x-layoutparser-identity-provider";

        /// <summary>
        /// Header com o <c>sub</c> imutável do provedor. NUNCA deve ser logado nem devolvido ao
        /// navegador — só o <c>UserId</c> interno resolvido a partir dele é observável.
        /// </summary>
        public string IdentitySubjectHeader { get; set; } = "x-layoutparser-identity-subject";

        /// <summary>Header com o <c>tid</c>/issuer do provedor, quando disponível.</summary>
        public string IdentityTenantHeader { get; set; } = "x-layoutparser-identity-tenant";

        /// <summary>
        /// Header com o e-mail VERIFICADO do usuário (o BFF só o envia com <c>email_verified=true</c>).
        /// Persistido normalizado em minúsculas; convites pendentes para esse e-mail viram membership.
        /// Confiança = mesma guarda de loopback/rede confiável dos demais headers de identidade.
        /// </summary>
        public string IdentityEmailHeader { get; set; } = "x-layoutparser-identity-email";
    }
}
