# Fase 4 - Rede confiavel BFF<->API (issue #579)

`Security__TrustedProxyNetworks__N` (CIDR) permite ao `TrustedIdentityMiddleware` aceitar `x-iis-user`/`x-iis-roles`
alem do loopback. Sem a variavel, so loopback (seguro por padrao).

**Recomendacao:**
- BFF e API no mesmo host/pod: NAO configurar (loopback basta; bind `127.0.0.1`).
- Hosts/containers distintos: rede docker/VLAN dedicada so com BFF e API, CIDR /29 ou /30 do BFF (ex.
  `Security__TrustedProxyNetworks__0=10.20.0.8/29`), firewall (ufw/nftables) na porta da API aceitando so esse CIDR.
  Nunca `0.0.0.0/0`, nunca faixa corporativa ampla (qualquer host nela forjaria identidade).
- Se ha reverse proxy no meio, o `RemoteIpAddress` visto e o do proxy: listar o CIDR do proxy, nao dos clientes.

**Decisao do dono (nao adivinhavel do repo):** topologia final (mesmo host vs separados), o CIDR real e quem administra
o firewall. Enquanto nao decidido, nada foi configurado em ambiente algum.
