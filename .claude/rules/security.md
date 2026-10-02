---
description: Regras de segurança e a pendência crítica de segredos versionados.
---

# Segurança — LayoutParser API

## 🔴 REGRA NÃO-NEGOCIÁVEL (2026-09-06): `172.31.249.51` é SOMENTE LEITURA

O SQL Server em `172.31.249.51` (banco `ConnectUS_Macgyver`, login `macgyver`, config
`Database:*` do projeto) é uma credencial **compartilhada por ~231.890 times dentro da NDD
inteira** — nunca exclusiva deste projeto (ver seção "rotação descartada" abaixo). O dono
determinou explicitamente: **nenhum agente pode fazer `CREATE TABLE`, `ALTER TABLE`,
`CREATE INDEX`, `INSERT`, `UPDATE`, `DELETE` ou qualquer outra escrita/DDL nesse servidor.**
É só consulta.

**Contexto do porquê:** descobrimos em 2026-09-06 que várias tabelas fiscais do projeto
(`tbFiscalProject`, `tbFiscalMappingPackage`, `tbFiscalMappingPackageRevision`,
`tbPackageArtifact`, `tbMappingDraft*`, `tbMappingRelease`) estavam sendo criadas via DDL
lazy nesse banco compartilhado, por config errada (`Database:*` em vez de
`IdentityDatabase:*`). Corrigido na PR #314 — essas tabelas agora vivem em
`IdentityDatabase:*` (banco dedicado do projeto, migrando para o Docker SQL na VM Ubuntu,
`elson@172.25.32.5:1433`, container `layoutparser-identity-sql`).

**Regra prática para qualquer agente:** se uma tarefa parecer exigir escrever/criar algo em
`172.31.249.51` ou na config `Database:*`, isso é sinal de config apontando pro banco errado
— não é uma tarefa legítima. Reporte ao dono, não implemente. Dado do projeto sempre vai em
`IdentityDatabase:*`.

## Segredos versionados — status da remediação

Os segredos estavam em texto plano no [`appsettings.json`](../../appsettings.json) **e** em fallbacks
hardcoded no código (`GeminiAIService`, `LayoutDatabaseService`, `ElasticSearchLogger`).

| Segredo | Onde | Status |
|---------|------|--------|
| API key do **Gemini** | `Gemini:ApiKey` | Removido do código/JSON ✅ · Gemini decomissionado (2026-07-21) · **Revogada e deletada pelo dono em 2026-08-17** ✅ |
| Senha do **SQL Server** | `Database:Password` | **REGRESSÃO em 2026-07-18** (ver abaixo) · removido de novo ✅ · repositórios da org PÚBLICOS desde 2026-08-15 · **ROTAÇÃO NÃO É OPÇÃO (ver 2026-08-15 abaixo) — mitigação é limpeza de histórico + hardening em repouso + prevenção de reincidência** 🔴🔴 |
| Credenciais do **Elastic** | `ElasticSearch:Username/Password` | ✅ Removido — mecanismo nunca foi conectado ao pipeline real (Serilog é o logging efetivo); código morto (`ILoggingStrategy`/`ElasticSearch*`) e config removidos em 2026-07-27 |

### 🔴🔴 2026-08-15 — rotação da senha SQL descartada: credencial compartilhada org-wide

O dono confirmou uma restrição crítica que **invalida a linha de ação anterior** ("rotacionar"):
a senha do SQL Server (login `macgyver`, host `172.31.249.51`, banco `ConnectUS_Macgyver`)
é uma credencial **compartilhada por ~231.890 times dentro da NDD inteira**, não exclusiva deste
projeto. Trocá-la não é uma decisão que este time pode tomar unilateralmente — quebraria todo
consumidor da credencial fora deste repositório. **Rotação sai do plano de remediação.**

Isso muda o cálculo de risco: como o segredo não pode ser invalidado, o vazamento em texto plano
no histórico do git (regressão de 2026-07-18) **é permanente** enquanto o histórico não for
limpo — e os 4 repositórios da org estão públicos desde 2026-08-15, então qualquer pessoa na
internet já pode ler esses commits hoje. A resposta correta deixa de ser "invalidar a senha" e
passa a ser: (1) reduzir a exposição futura, (2) reduzir o raio de dano se a senha vazada for
usada, (3) impedir reincidência.

**Prioridades, em ordem:**

1. **[PRIORIDADE #1] Limpar o histórico do git** (`git filter-repo`/BFG, ver seção abaixo) —
   única ação que efetivamente reduz a exposição, já que a senha não pode ser trocada.
   Dono: `@lp-devops`, sob confirmação explícita do dono do projeto (reescreve histórico,
   exige re-clone de todo mundo). Antes disso, considerar também **voltar os repos a privado**
   como mitigação imediata e reversível enquanto a limpeza é preparada.
2. **Hardening da senha em repouso no host** — hoje, mesmo fora do `appsettings.json`, a senha
   fica em texto plano no `Environment` do serviço Windows (`HKLM\SYSTEM\...\Services\
   LayoutParserApi\Environment`), legível por qualquer admin local. Avaliar DPAPI
   (`ProtectedData` com `Machine` scope), Windows Credential Manager, ou
   `ProtectedConfigurationBuilder` do ASP.NET Core para criptografar a connection string em
   repouso — sem infra nova (Vault/Consul já descartados por porte do projeto). Dono:
   `@lp-devops` (host) com apoio de `@lp-backend-dev` (código, se precisar de leitura custom).
3. **Nunca logar/exibir a connection string.** Checado nesta sessão: os `LogError(ex, ...)` em
   `Services/Database/CachedMapperService.cs` e `MapperDatabaseService.cs` logam `ex.Message`,
   mas `SqlException.Message` do SqlClient não inclui a senha (só server/DB/user) — risco baixo,
   não zero. Confirmar que nenhum outro ponto loga a connection string completa (`ex.ToString()`
   em nível `Debug`/`Trace`, por exemplo). Dono: `@lp-backend-dev`.
4. **Prevenir reincidência com mecanismo técnico, não só disciplina** — a regressão de
   2026-07-18 aconteceu porque alguém testou local com a senha no `appsettings.json` e comitou
   junto. Propor: (a) hook de pre-commit local com `gitleaks`/`detect-secrets`; (b) step no CI
   (`ci-dev.yml`/`deploy.yml`) que escaneia o diff do PR por padrão de connection string com
   senha antes de permitir merge. Ambos gratuitos, sem licença. Dono: `@lp-backend-dev` (hook
   local) + `@lp-devops` (step de CI).
5. **Compartimentalizar o dano no lado do SQL** — avaliar com o DBA se o login `macgyver`, tal
   como usado por esta API, tem permissões mais amplas do que o necessário (acesso de
   escrita/DDL em bases que a API não toca). Restringir ao mínimo necessário não impede o
   vazamento, mas reduz o que alguém com a senha comprometida consegue fazer. Dono: escalar ao
   DBA (fora do alcance de qualquer agente).

**Reversão futura:** se algum dia a NDD decidir isolar este projeto com um login SQL próprio
(não compartilhado), a rotação volta a ser viável e o runbook antigo (abaixo) pode ser reativado.

### ⚠️ REGRESSÃO (2026-07-18) — senha SQL voltou ao repositório

A senha do SQL Server **reapareceu em texto plano** no `appsettings.json` comitado e entrou no
**histórico da `master` via merge da PR #7**. A remoção foi refeita em 2026-07-18 (placeholder `""`),
mas o valor está de novo em commits públicos do histórico.

- A limpeza de histórico (`git filter-repo`/BFG, seção abaixo) precisa cobrir **também** esses
  commits novos — é a única mitigação real agora que rotação está fora de cogitação (ver
  2026-08-15 acima).
- Causa raiz a vigiar: ao testar localmente com a senha no JSON, o arquivo acaba indo junto no commit.
  Use `dotnet user-secrets` (dev) ou o mecanismo de CI abaixo — **nunca** edite o segredo no `appsettings.json`.

### Plano de remediação

- [x] **Substituir** valores no `appsettings.json` por **placeholders vazios** (`""`).
- [x] **Remover** os fallbacks hardcoded (`?? "<segredo>"`) no código → `?? string.Empty`.
- [x] **Ignorar** `appsettings.*.local.json` no `.gitignore`.
- [x] **Documentar** uso de `dotnet user-secrets` (dev) e env vars `Section__Key` (prod) — ver README §9.
- [ ] ~~Rotacionar a senha do SQL Server~~ — **DESCARTADO em 2026-08-15**: credencial compartilhada
      por ~231.890 times na NDD, fora do controle deste projeto. Ver seção 2026-08-15 acima.
- [x] **Limpar o histórico do git** (`git filter-repo` / BFG) — executado em 2026-08-15
      (`@lp-devops`, sob confirmação do dono): force-push feito, repos voltaram a público.
- [ ] **Hardening em repouso** da senha no host (DPAPI/Credential Manager/`ProtectedConfigurationBuilder`) — `@lp-devops`.
      Avaliação e runbook prontos (recomendação: `ProtectedConfigurationBuilder`/user-secrets
      com DPAPI, opção C — ver [`docs/architecture/runbook-hardening-senha-sql-em-repouso.md`](../../docs/architecture/runbook-hardening-senha-sql-em-repouso.md));
      falta aplicar no host de produção (dono, via RDP) + um handoff pontual de código para
      `@lp-backend-dev` (`AddUserSecrets` fora de `IsDevelopment()`).
- [x] **Step de CI** anti-reincidência (`gitleaks`) — `.github/workflows/gitleaks.yml`, roda em
      todo PR contra `develop`/`master`/`main`, escaneando o diff introduzido pelo PR
      (`@lp-devops`). Falta a metade de `@lp-backend-dev`: hook de pre-commit local.
- [x] **Revogar/desprovisionar** (não rotacionar) a API key do Gemini exposta — **revogada e deletada pelo dono em 2026-08-17** ✅.

### Como configurar os segredos (dev)

O `UserSecretsId` já está no `.csproj`. A precedência é
`appsettings.json` → `user-secrets` (Development) → env vars → args.

```bash
dotnet user-secrets set "Database:Password" "<senha>"
dotnet user-secrets set "Gemini:ApiKey" "<key>"
# Produção: variáveis de ambiente no formato Section__Key
#   Database__Password=...  Gemini__ApiKey=...
```

### Segredos no CI de dev (`ci-dev.yml`) — mecanismo e runbook de rotação

O deploy de dev instala a API como **serviço Windows nativo** e injeta o segredo no ambiente
**do serviço** (registro `HKLM\SYSTEM\...\Services\LayoutParserApi\Environment`, `REG_MULTI_SZ`)
a partir do secret **`DB_PASSWORD_DEV`** do GitHub Actions. O valor nunca aparece em log
(o Actions mascara secrets e o workflow não ecoa o valor).

**Variables/Secrets que o operador precisa criar** (GitHub → repo `LayoutParserApi` →
Settings → Secrets and variables → Actions):

| Nome | Tipo | Valor | Obrigatório |
|------|------|-------|-------------|
| `DEPLOY_PATH_DEV` | **Variable** | `C:\inetpub\wwwroot\layoutparser` (máquina dev) | Sim — o deploy falha sem ela |
| `API_URL_DEV` | **Variable** | URL da instância dev (default `http://localhost:5100` se ausente) | Não |
| `DB_PASSWORD_DEV` | **Secret** | Senha do SQL **atual em uso** (hoje ainda a comprometida — ver nota abaixo) | Não — sem ela a API sobe degradada (sem SQL) |

> ⚠️ **Status (atualizado 2026-08-15):** a rotação da senha SQL foi **descartada** — é
> credencial compartilhada por ~231.890 times na NDD, fora do controle deste projeto (ver seção
> 2026-08-15 acima). O secret `DB_PASSWORD_DEV` continua com a senha atual (comprometida, mas
> permanente) — a mitigação real é a limpeza do histórico do git e o hardening em repouso, não a
> troca do valor.

**Runbook de rotação da senha SQL** (mantido apenas como referência histórica — **não aplicável
enquanto a senha for compartilhada org-wide**; reativar só se este projeto ganhar um login SQL
próprio no futuro):

1. No SQL Server: `ALTER LOGIN <login> WITH PASSWORD = '<nova-senha>'`.
2. No GitHub: atualizar o secret `DB_PASSWORD_DEV` (e o equivalente de produção, quando existir).
3. Redisparar o deploy (`workflow_dispatch` do CI Dev ou novo push) — o step reescreve o
   `Environment` do serviço e reinicia a API com a senha nova.
4. Validar smoke test verde e conexão SQL nos logs (sem imprimir a senha).

### Revogação da API key do Gemini — CONCLUÍDA em 2026-08-17 ✅

O dono revogou e deletou a chave no console do provedor. Item fechado — texto abaixo preservado
como registro histórico da decisão e do runbook seguido.

Decisão de arquitetura: Gemini e OpenAI foram **abandonados por completo** como provedores de LLM
neste projeto — Ollama local assume 100% do papel (loop RAG gerar → validar → corrigir, sem
fine-tuning). Motivo de fundo: dado fiscal sensível não deve sair pra nuvem sem autorização explícita
(ver "Regras gerais" abaixo). Detalhe da decisão: [memória de `@lp-architect`](../agent-memory/lp-architect/gemini-openai-decommission-decision.md).

Com o decommission, a ação sobre a chave do Gemini deixa de ser "gerar uma chave nova" (rotação) e
vira **revogar/desprovisionar de vez** — não há mais consumidor previsto, então não faz sentido reemitir.

> **Nota de risco factual (não é motivo pra baixar a prioridade da revogação):** hoje nenhum dos
> serviços que consomem a chave do Gemini (`GeminiAIService`, `SemanticAIGenerator` etc.) está
> registrado no DI em `Program.cs` — os endpoints que dependem deles quebram com exceção em runtime,
> então a chave não vaza *agora* por acidente de código. Isso não é remediação deliberada, é bug —
> a remoção desse código morto é tarefa do `@lp-backend-dev` (Dex), já mapeada em
> `docs/architecture/ai-roadmap-dispatch.md` (Grupo 1). Não muda a urgência de revogar a chave: ela
> já esteve exposta em texto plano no histórico do repo.

**Fora do alcance do `@lp-devops`:** revogar a chave exige acesso interativo ao console do provedor
(Google AI Studio / Google Cloud Console) com a conta que a gerou — **não é algo que o agente executa
via terminal.** Passos manuais para o dono do projeto:

1. Acessar [Google AI Studio → API keys](https://aistudio.google.com/app/apikey) (ou Google Cloud
   Console → APIs & Services → Credentials, se a chave foi provisionada por lá) com a conta usada
   para gerar a chave do `Gemini:ApiKey`.
2. Localizar a chave associada a este projeto e **deletar/revogar** (prefira revogar a apenas
   desativar, se a UI oferecer as duas opções — revogação impede reuso mesmo que o valor exposto
   tenha sido copiado por terceiros).
3. Confirmar, na mesma tela, ausência de uso/billing após a revogação — serve de confirmação de que
   a chave morreu, além de evitar custo residual.
4. **Não gerar chave nova.** Checado nesta sessão: não há `GEMINI_API_KEY`/secret equivalente em
   `.github/workflows/ci-dev.yml` ou `deploy.yml` — nada a limpar do lado do GitHub Actions. Se a
   decisão de decommission for revertida no futuro, gerar uma chave nova **nesse momento**, não antes.
5. Avisar `@lp-devops` (ou marcar diretamente neste arquivo) quando a revogação estiver concluída,
   para atualizar a tabela acima de 🔴 para ✅.

> ⚠️ A limpeza do histórico do git (seção abaixo) continua pendente e **também** cobre os commits
> onde a chave do Gemini apareceu em texto plano — revogar a chave não substitui essa limpeza, mas
> reduz a urgência dela especificamente para este segredo (chave morta não é mais explorável mesmo
> que ainda apareça no histórico).

### Estado-alvo recomendado (não implementado)

Migrar a conexão SQL para **autenticação integrada Windows / gMSA** (Group Managed Service Account):
elimina a senha da configuração por completo e o AD rotaciona a credencial automaticamente.
Com isso, `DB_PASSWORD_DEV`/`Database__Password` deixam de existir. Recomendação de arquitetura —
exige alinhamento com o time de infra/AD antes de qualquer mudança.

### Limpeza do histórico do git (proposta — NÃO executar sem confirmação)

Os segredos antigos **persistem nos commits anteriores** mesmo após este commit. Para removê-los:

1. **Pré-requisitos:** repo limpo (sem alterações pendentes), avisar todos que têm clone/fork,
   e ter um backup (`git clone --mirror`).
2. **Opção A — `git filter-repo`** (recomendado):
   ```bash
   pip install git-filter-repo
   # criar replacements.txt com:  <segredo-antigo>==>REMOVIDO
   git filter-repo --replace-text replacements.txt
   ```
3. **Opção B — BFG Repo-Cleaner:**
   ```bash
   bfg --replace-text replacements.txt
   git reflog expire --expire=now --all && git gc --prune=now --aggressive
   ```
4. **Force-push** a história reescrita e **invalidar** os reflogs no remoto.
   Exige coordenação: todos reclonam; PRs/branches abertos quebram.

> ⚠️ **Para a senha SQL, a limpeza É a mitigação principal** (rotação não é opção — ver seção
> 2026-08-15 acima): qualquer clone feito antes da limpeza ainda contém o segredo, mas ao menos
> deixa de ser publicamente acessível via GitHub. Para a API key do Gemini, revogação continua
> sendo a ação que efetivamente invalida o segredo — a limpeza de histórico é complementar,
> não substitui a revogação.

## Alerta de deploy por e-mail (2026-08-18)

> **Nota de 2026-10-02 — DESATUALIZADO / NOT ACTIVE:** o `deploy.yml` (Windows, servidor .42)
> foi **removido** do repositório (commit `72f4906`). O alerta SMTP descrito nesta seção referia-se
> a ele e **não está mais ativo**. A produção agora é o Linux, e o deploy é
> `.github/workflows/deploy-linux.yml` (push em `master` com aprovação de 1 clique no environment
> `production`, backup + smoke test `/health/ready` com rollback automático; commit `2db8af0`).
> A mecânica e os secrets `SMTP_*`/`ALERT_EMAIL_TO` abaixo só voltam a valer **se o alerta for
> portado ao `deploy-linux.yml`** — decisão do dono, ainda não tomada. Texto preservado como
> histórico; as demais menções a `deploy.yml` neste arquivo (linhas sobre CI/Gemini) também se
> referem ao workflow Windows removido.

Item 1 do raio-X de maturidade (`docs/architecture/specs-execucao-maturidade-2026-08-16.md`).
Canal decidido pelo dono: **e-mail** (Teams/Slack ficaram de fora — pago). Implementado em
`.github/workflows/deploy.yml`, usando a Action gratuita `dawidd6/action-send-mail@v3`, disparada
em 2 pontos do job de deploy:

1. Logo após o step **Smoke test de readiness (pos-deploy)** — dispara quando esse step falha
   (`steps.smoke_test.outcome == 'failure'`), cobrindo os casos "ROLLBACK OK" e "ROLLBACK FALHOU".
   O corpo do e-mail reaproveita o mesmo resumo que já vai pro `$GITHUB_STEP_SUMMARY`.
2. No fim do job — fallback genérico para "DEPLOY ABORTADO" (falha antes do smoke test rodar,
   ex.: build/publish quebrou). Só dispara se o smoke test **não** foi quem falhou, pra não
   duplicar o e-mail do caso 1.

**Provedor SMTP ainda não escolhido pelo dono.** Os dois steps checam `secrets.SMTP_SERVER != ''`
na condição `if:` — enquanto os secrets abaixo não existirem, os steps são **pulados**
silenciosamente (não fazem o workflow falhar). Nenhuma ação é necessária até o dono decidir o
provedor.

### Secrets a criar quando o provedor for escolhido

Em GitHub → repo `LayoutParserApi` → **Settings → Secrets and variables → Actions → New repository secret**:

| Secret | Descrição |
|--------|-----------|
| `SMTP_SERVER` | Host do servidor SMTP (ex.: `smtp.gmail.com`) |
| `SMTP_PORT` | Porta (ex.: `587` para TLS) |
| `SMTP_USERNAME` | Usuário/e-mail de envio |
| `SMTP_PASSWORD` | Senha/senha de app (nunca a senha normal da conta, se o provedor suportar senha de app) |
| `ALERT_EMAIL_TO` | Endereço de destino dos alertas (só o dono, um endereço) |

### Passo a passo — Gmail + senha de app (opção mais acessível)

1. Acessar [myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords) com a
   conta Google que vai enviar os alertas (exige verificação em 2 etapas ativada na conta).
2. Gerar uma **senha de app** nova (ex.: nome "LayoutParserApi Deploy Alert") — copiar o valor de
   16 caracteres exibido (só aparece uma vez).
3. Criar os secrets no GitHub com:
   - `SMTP_SERVER` = `smtp.gmail.com`
   - `SMTP_PORT` = `587`
   - `SMTP_USERNAME` = o e-mail Gmail usado para gerar a senha de app
   - `SMTP_PASSWORD` = a senha de app gerada no passo 2 (não a senha normal da conta)
   - `ALERT_EMAIL_TO` = e-mail do dono que deve receber os alertas
4. Não é preciso redisparar nada manualmente — os steps já existentes no `deploy.yml` passam a
   disparar automaticamente no próximo deploy que falhar o smoke test (ou abortar antes dele).
5. Não há como validar o envio real sem um deploy que falhe de propósito — se quiser confirmar o
   mecanismo, `workflow_dispatch` não cobre isso hoje (o gatilho é sempre push a `master`); o
   caminho mais seguro é revisar a sintaxe do step e confiar no smoke test real do próximo deploy.

Se o provedor escolhido não for Gmail (Outlook/Office 365, SendGrid etc.), os nomes dos 5 secrets
continuam os mesmos — só os valores de `SMTP_SERVER`/`SMTP_PORT`/`SMTP_USERNAME`/`SMTP_PASSWORD`
mudam conforme a documentação do provedor.

## CodeQL desativado — Dependabot mantido (2026-08-15)

**Causa raiz do erro de CI** (`Advanced Security must be enabled...`): CodeQL *code scanning* em
repositório **privado** exige GitHub Advanced Security (GHAS), pago. O repo está privado desde
2026-08-12. A análise roda e conclui, só o **upload do SARIF** falha — todo run do
`.github/workflows/codeql.yml` falha eternamente, sem alternativa de config (não é bug de YAML/
permissions, ver comentário já presente no arquivo). Como `SecurityCodeScan` (Roslyn, gratuito, já
ativo com `security-code-scan-baseline.json`) cobre o mesmo papel de SAST pra C#, o CodeQL é
puro desperdício de minutos de CI + ruído de "falhou" a cada push/PR/segunda-feira.

**Decisão: remover `.github/workflows/codeql.yml` por completo** (não só o step de upload — sem
GHAS o job de `analyze` não tem efeito nenhum; manter só a análise local sem upload seria rodar
`autobuild`/`security-extended` por ~30min pra descartar o resultado). Instrução exata pra
`@lp-devops`: apagar o arquivo (o `LayoutParserReact` tem workflow idêntico — mesmo repo privado,
mesma limitação de GHAS; a distinção não é "publico vs privado", é confirmar se o dono quer
remover lá também, decisão dele).

**Dependabot NÃO é o problema — mantido como está.** Confirmado lendo `.github/dependabot.yml`:
são `version updates` (PRs automáticos de bump de dependência, `nuget` + `github-actions`), que
é gratuito em qualquer repositório (público ou privado), sem GHAS. O que exige GHAS em repo
privado é uma família diferente — `Dependabot alerts` (scanning de vulnerabilidade) e `secret
scanning` — nenhum dos dois está configurado aqui (não há nada em `Settings → Security`
habilitando alerts, só o `dependabot.yml` de version updates). O dono lembrava de "parar
Dependabot por licença" — essa lembrança se aplica ao CodeQL (mesma família "Advanced Security"),
não ao Dependabot version updates. Nenhuma ação necessária no `dependabot.yml`.

**Resumo pra `@lp-devops` executar:**
1. `git rm .github/workflows/codeql.yml` (LayoutParserApi). Avaliar o mesmo no LayoutParserReact.
2. `dependabot.yml` — **não mexer**, já é 100% gratuito e correto.
3. `SecurityCodeScan` + baseline — **não mexer**, é o substituto ativo do CodeQL.

## CodeQL REATIVADO — repos voltaram a público (2026-09-25)

A premissa da remoção acima (repo privado → GHAS pago) **deixou de valer**. Os 3 repos do
ecossistema (`LayoutParserApi`, `LayoutParserDecrypt`, `layoutparser-portal`) estão **públicos**
desde a limpeza de histórico de 2026-08-15 (ver seção "🔴🔴 2026-08-15" acima — a decisão de
manter público foi tomada ali, não revertida). Em repositório **público**, code scanning via
`github/codeql-action` é **gratuito**, sem exigir GHAS/licença. O `layoutparser-portal` já
reativou o CodeQL nesse meio-tempo (`javascript-typescript` + `actions`, `build-mode: none`) e
serviu de template para esta reativação — confirmado sem necessidade de mudança.

**Ações desta sessão:**

1. **`LayoutParserApi`** — recriado `.github/workflows/codeql.yml`. `on:` usa `develop`/`master`
   (não `main` — branches reais deste repo, confirmado via `git branch -a`). Matriz:
   `csharp` + `actions`. `build-mode: none` (não `autobuild`) — decisão deliberada: o
   `LayoutParserApi.csproj` referencia `LayoutParserLib` via `HintPath` relativo a um repo
   **irmão** (`..\LayoutParserLib\bin\$(Configuration)\LayoutParserLib.dll`) que não existe no
   checkout isolado do workflow (o CodeQL só clona este repo). Um `autobuild` real quebraria
   nessa `Reference` ausente. `build-mode: none` para C# é suportado desde CodeQL CLI 2.16 —
   faz extração/análise direta do código-fonte sem precisar resolver todas as referências,
   cobrindo o mesmo escopo de `security-extended` (SQLi, path traversal etc.) sem depender de
   build completo. **Limitação conhecida e não resolvida:** análise fluxo-sensível que dependa
   de tipos/símbolos definidos em `LayoutParserLib` (não neste repo) pode ficar mais fraca do
   que um build real conseguiria — se isso incomodar no futuro, a alternativa é um step manual
   que primeiro reconstrói um stub de `LayoutParserLib.dll` antes do `dotnet build`
   (`build-mode: manual`), não implementado aqui.
2. **`LayoutParserDecrypt`** — recriado `.github/workflows/codeql.yml` no repo. `on:` usa só
   `master` (sem `develop` — fluxo real deste repo é `feat/** → master` direto, confirmado via
   `git branch -a`). Matriz: `csharp` + `actions`. Diferente da API, este projeto é **standalone**
   (o próprio `.csproj` documenta em comentário que o código de `LayoutParserLib` foi embutido
   como arquivos-fonte locais exatamente para não depender de repo irmão) — mas é um projeto
   **clássico não-SDK** (`TargetFrameworkVersion v4.8.1`, `OutputType Exe`), que exige MSBuild
   (não `dotnet build`) e por isso o `build.yml` existente já roda em `windows-latest` com
   `microsoft/setup-msbuild` + `nuget restore`/`msbuild` explícitos, não `dotnet`.
   `build-mode: none` do CodeQL roda em `ubuntu-latest` e não builda nada — não tem esse
   problema, mas também não se beneficia da build real já validada no `build.yml`. Optou-se por
   manter `build-mode: none` + `ubuntu-latest` (mesmo padrão dos outros dois repos, mais simples
   e sem custo de manter um segundo caminho de build MSBuild dentro do CodeQL) — se no futuro
   quiser uma análise mais profunda equivalente ao `autobuild`, o caminho seria
   `build-mode: manual` em `windows-latest` reaproveitando os mesmos steps de
   `setup-msbuild`/`nuget restore`/`msbuild` do `build.yml`.
3. **`layoutparser-portal`** — conferido, sem alteração necessária.

## Regras gerais (todos os agentes)

- **NUNCA** comite segredos, connection strings ou tokens.
- Ao **detectar** um segredo em texto plano (em qualquer arquivo), **pare**, sinalize ao usuário e acione `@lp-devops`. Não silencie.
- **Nunca** logue credenciais nem conteúdo sensível de documentos de cliente.
- **LLM em nuvem (Gemini/OpenAI):** não envie documentos/dados reais de cliente sem autorização explícita. Prefira **Ollama local** para dados sensíveis.
- CORS está liberado para origens específicas em `Program.cs` — não abra para `*` em produção.
- **Identidade vem do BFF, não há `[Authorize]` ainda.** A API não autentica ninguém diretamente:
  `Services/Security/TrustedIdentityMiddleware.cs` lê os headers `x-iis-user`/`x-iis-roles`
  (configuráveis via `Security:TrustedUserHeader`/`Security:TrustedRolesHeader`) injetados pelo BFF
  Fastify (`layoutparser-portal/server/`, autenticação Entra OIDC) e popula `ICurrentUser` +
  `HttpContext.User`. Só confia nesses headers se a origem da requisição for **loopback**
  (`TrustIdentityFromLoopbackOnly`, default `true`, deliberadamente fora do `appsettings.json`) —
  isso fecha forja de identidade mesmo com a API respondendo em `0.0.0.0`. Nenhum endpoint tem
  `[Authorize]`/enforcement por papel ainda — é decisão de produto em aberto. Detalhe e status:
  [`docs/architecture/rollout-p2-autenticacao.md`](../../docs/architecture/rollout-p2-autenticacao.md).
- **`ApiKeyGateFilter`/`ApiKeyGatePolicy` foram removidos** (branch `feat/identidade-do-bff`,
  commit `c7489ca`) — a chave compartilhada deixou de ser o mecanismo de defesa da fronteira
  BFF↔API; a defesa hoje é rede (API só deve escutar `127.0.0.1`, em andamento por `@lp-devops`) +
  a guarda de loopback do middleware acima. Não reintroduza `Security:ApiKey`/`Security:AnonymousPaths`.
