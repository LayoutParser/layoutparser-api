# Fase 2 - Requisitos da VM Windows do sidecar (issue #577)

Somente requisitos (nada provisionado). Contrato: `migracao-linux-fase2-contrato-sidecar.md`.
- SO: Windows Server 2019/2022 Core ou com Desktop minimo; .NET Framework 4.8.1 (runtime) e, se o sidecar for .NET 10 fino, ASP.NET Core Hosting.
- Dimensao inicial: 2 vCPU, 4 GB RAM, 40 GB disco (LowCodeRunner x86 e DLLs Sysmiddle: confirmar consumo).
- Software: LayoutParserDecrypt.exe, LowCodeRunner + DLLs Sysmiddle (licenciamento a confirmar com o dono), LayoutParserLib.dll.
- Rede: IP fixo em rede isolada da API; firewall Windows liberando a porta do sidecar APENAS para o CIDR da API; sem internet de saida; sem autenticacao por segredo.
- Operacao: servico Windows com conta de servico dedicada (sem admin), logs em arquivo sem conteudo de documento, `/health` monitorado; patching mensal.
- Decisoes do dono: hospedagem (Hyper-V/VMware/nuvem), licencas Sysmiddle, quem opera a VM, e resultado da Fase 1 (#576) que pode reduzir o escopo ao so LowCodeRunner.
