# Runbook — Hardening da senha do SQL em repouso no host

> ⚠️ **ATUALIZAÇÃO (2026-09-28, issue #580 / Epic #575):** com Linux como destino do runtime da API,
> **DPAPI (Opção C e `ProtectedData`) NÃO é o caminho definitivo** — é Windows-only. Não iniciar
> implementação de DPAPI. Alternativa cross-platform: variável de ambiente via systemd
> `EnvironmentFile` (`chmod 600`, ver `deploy/linux/`) ou secret do orquestrador (Docker/Kubernetes
> secrets). O restante deste runbook vale só enquanto o host de produção for Windows.

Item 2 do plano de remediação de 2026-08-15 (`.claude/rules/security.md`, seção
"2026-08-15 — rotação da senha SQL descartada"). Como a senha do SQL Server (login
`macgyver`) é uma credencial compartilhada por ~231.890 times na NDD, ela **não pode ser
rotacionada** — a mitigação possível é reduzir onde/como ela fica exposta em repouso.

Hoje a senha vive em texto plano no `Environment` do serviço Windows
(`HKLM\SYSTEM\CurrentControlSet\Services\LayoutParserApi\Environment`, `REG_MULTI_SZ`),
legível por qualquer admin local com `reg query`/PowerShell. Este documento avalia as três
opções listadas no plano e recomenda uma.

## Avaliação técnica das três opções

### Opção A — DPAPI direto na env var — **descartada, tecnicamente inviável como pedido**

`ProtectedData`/`ConvertTo-SecureString` com DPAPI protegem **blobs/arquivos**, não uma
variável de ambiente de processo. Não existe "env var criptografada" nativa no Windows: o
valor que o processo lê via `Environment.GetEnvironmentVariable` já precisa estar em texto
plano no momento da leitura — a proteção DPAPI só existiria enquanto o dado estivesse
*armazenado* (ex.: um arquivo `.dat` no disco), sendo descriptografado antes de virar env var.
Isso desloca o problema (o valor descriptografado voltaria a viver em texto plano no registro
do serviço) sem reduzir a exposição real. **Não recomendado.**

### Opção B — Windows Credential Manager + leitura via P/Invoke no `Program.cs`

Guarda a senha no Credential Manager da máquina (`cmdkey`/`CredWrite`, escopo
`CRED_PERSIST_LOCAL_MACHINE`) e o código lê via P/Invoke (`Advapi32.dll CredRead`) no boot da
API, populando a connection string em memória. Reduz a exposição em repouso (a senha não fica
mais no registro do serviço nem em nenhum arquivo de config), mas:

- **Exige mudança de código em C#** (novo componente de leitura de credencial + wiring no
  `Program.cs`) — fora do escopo direto do `@lp-devops`, seria handoff para `@lp-backend-dev`.
- Acesso ao Credential Manager de `LOCAL_MACHINE` por um serviço Windows depende da conta de
  serviço ter sido a que gravou a credencial (ou ACL explícita) — mais uma peça operacional
  para o runbook de deploy gerenciar (quem grava `cmdkey`, quando, em qual conta).
- Nenhuma biblioteca .NET nativa de primeira classe para isso; a maioria dos pacotes NuGet
  (`CredentialManagement`) são de terceiros, não mantidos pela Microsoft.

### Opção C — `ProtectedConfigurationBuilder` do ASP.NET Core (`Microsoft.Configuration.ConfigurationBuilders.UserSecrets`/`Azure`/local com DPAPI) — **recomendada**

O pacote `Microsoft.Configuration.ConfigurationBuilders.*` permite anexar um *configuration
builder* que criptografa uma **seção inteira do `appsettings.Production.json`** em repouso,
usando DPAPI (`Machine` scope) por baixo, nativo do ecossistema .NET Core/`IConfiguration`.

Motivos para preferir esta opção para o porte deste projeto:

- **Nenhuma mudança de C# necessária além de registrar o builder** — é configuração declarada
  em `appsettings.json`/`Program.cs` (uma linha em `ConfigurationBuilder.AddUserSecrets` /
  `AddEnvironmentVariables` vira `AddProtectedJsonFile` ou equivalente). Ainda assim, **essa
  linha em `Program.cs` é código** — coordenar com `@lp-backend-dev` antes de aplicar, mesmo
  sendo trivial.
- Usa o canal que **já existe** e é gerenciado com upsert (`appsettings.Production.json`,
  criado pelo step "Config drift" do `deploy.yml` quando `MIGRATE_CONFIG_TO_REPO=true` rodar)
  em vez de introduzir um canal novo (Credential Manager) que ninguém no time opera hoje.
- DPAPI `Machine` scope amarra a criptografia ao host — um `appsettings.Production.json`
  copiado para outra máquina (ex.: se vazar por backup) não pode ser descriptografado lá. Boa
  propriedade defensiva, já que o risco que motivou isto foi justamente "o arquivo saiu do
  host onde deveria ficar" (git público).
- Sem custo/licença nova, mesmo espírito do resto do plano (`gitleaks`, `SecurityCodeScan`).

**Pré-requisito:** este runbook pressupõe que a migração de config (`MIGRATE_CONFIG_TO_REPO=
true`, já implementada em `deploy.yml`) já rodou — sem isso não existe
`appsettings.Production.json` no host para proteger. Ver seção "Config drift" em `deploy.yml`.

## Runbook — comandos para o dono aplicar (via RDP no host de produção)

> Executar como Administrador. Faça backup do `appsettings.Production.json` atual antes de
> qualquer passo (o `deploy.yml` já faz isso a cada migração, mas confirme).

**1. Instalar o pacote no projeto** (isto é uma mudança de `.csproj` — coordenar com
`@lp-backend-dev`; incluído aqui só para o runbook ficar completo, não para o `@lp-devops`
executar):

```powershell
dotnet add LayoutParserApi.csproj package Microsoft.Configuration.ConfigurationBuilders.UserSecrets
```

**2. Registrar o builder em `appsettings.Production.json`** (o arquivo passa a ter uma seção
`configBuilders` apontando para si mesmo com um sufixo protegido — o pacote suporta seção
"protected" via `Microsoft.Configuration.ConfigurationBuilders.Xml`/`Json` combinado com
`DpapiEncryptionMethod` do lado do provider de `Microsoft.Extensions.Configuration.Json`
estendido; alternativa mais simples e mais testada no ecossistema .NET 10: usar
`dotnet user-secrets` com `--project` **apontando para o diretório de deploy**, já que
`UserSecretsId` grava sob `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`, protegido por
ACL do perfil do usuário que roda o serviço — DPAPI `CurrentUser` é o mecanismo por trás do
próprio Windows Data Protection API no perfil, mesmo efeito prático de "não legível por outro
usuário/perfil" com muito menos código novo):

```powershell
# Rodar na conta que executa o servico LayoutParserApi (verificar em services.msc -> Log On As).
# Se o servico roda como LocalSystem, o UserSecretsId fica sob o perfil de LocalSystem -
# aceitavel (LocalSystem so e acessivel por outro processo LocalSystem/admin), mas documente
# qual conta foi usada para o proximo operador nao se perder.
cd C:\inetpub\wwwroot\layoutparser\api
dotnet user-secrets set "Database:Password" "<senha-atual>" --id "<UserSecretsId-do-csproj>"
```

**3. Confirmar leitura em runtime.** A precedência já documentada em `security.md`
(`appsettings.json` → `user-secrets` → env vars → args) faz o valor de user-secrets vencer o
`appsettings.Production.json`/`appsettings.json` — **mas só se `Program.cs` chamar
`AddUserSecrets` também fora do ambiente `Development`** (hoje, por padrão do template ASP.NET
Core, `AddUserSecrets` normalmente só é adicionado quando `IsDevelopment()`). **Isto é a
mudança de código real que este runbook depende** — handoff explícito para
`@lp-backend-dev`: adicionar `builder.Configuration.AddUserSecrets<Program>(optional: true)`
incondicionalmente (não só em dev), e então o passo 2 acima passa a valer em produção também.

**4. Remover a variável de ambiente do serviço** (só depois de confirmar que o passo 3 está
lendo a senha do user-secrets):

```powershell
$serviceName = "LayoutParserApi"
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$cur = (Get-ItemProperty -Path $regPath -Name 'Environment' -ErrorAction SilentlyContinue).Environment
$novo = $cur | Where-Object { $_ -notmatch '^Database__Password=' }
Set-ItemProperty -Path $regPath -Name 'Environment' -Value ([string[]]$novo) -Type MultiString
Restart-Service -Name $serviceName
```

**5. Validar.** Bater em `/health/ready` e confirmar `sql: Healthy` — se cair para
`Unhealthy`, a leitura do user-secrets falhou; reverter o passo 4 (recolocar
`Database__Password` no Environment) até depurar.

## Resumo do handoff necessário

| Passo | Quem |
|-------|------|
| Confirmar viabilidade da Opção C (DPAPI/user-secrets) vs. considerar Credential Manager (Opção B) se o time preferir não tocar `Program.cs` | `@lp-architect` / dono, decisão de produto |
| `builder.Configuration.AddUserSecrets<Program>(optional: true)` fora de `IsDevelopment()` | `@lp-backend-dev` |
| Executar os passos 1-5 acima no host (`172.25.32.42`) | Dono do projeto (acesso RDP), com `@lp-devops` orientando |
| Atualizar `.claude/rules/security.md` (marcar item concluído) e este runbook com o resultado | `@lp-devops` |

**Não executado nesta sessão** — o `@lp-devops` não tem acesso direto ao host de produção
(ver `.claude/agent-memory/lp-devops/prod-42-acesso-bloqueado.md`); este documento é o runbook
pronto para o dono aplicar via RDP, com o handoff de código já identificado para
`@lp-backend-dev`.
