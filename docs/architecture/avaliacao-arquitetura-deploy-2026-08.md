# Avaliação da arquitetura de deploy — LayoutParser API (2026-08-11)

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

> `@lp-architect` (Aria). Leitura integral de `.github/workflows/deploy.yml` (produção, 978 linhas) e
> `ci-dev.yml` (dev, 697). Veredito honesto: **o tratamento de configuração e segredo está entre o
> melhor que já vi num deploy deste porte; a mecânica de entrega e verificação tem lacunas sérias.**
> Uma coisa não anula a outra.

---

## O que está MUITO bem feito (não mexer)

Registro porque é raro e porque quem for endurecer o resto não pode regredir isto:

1. **Build explícito do `.csproj`, nunca da solution** (`deploy.yml:142`). Com o projeto de testes na
   `.sln`, um build pela solution restauraria xUnit **em produção** e uma falha de restore quebraria o
   deploy. A escolha é consciente e comentada.
2. **`paths-ignore`** evita deploy de produção para mudança sem efeito de runtime (docs, mcp, tools).
3. **Config por env var `Section__Key`** (upsert em `deploy.yml:535`), porque o `appsettings.json` do
   destino é preservado. O upsert **preserva** variáveis definidas à mão (ex.: `Database__Password`)
   em vez de zerá-las — sobrescrever em bloco apagaria a senha do SQL.
4. **Config drift em dry-run** (`deploy.yml:372`) antes de inverter a fonte da verdade. Não inverte às
   cegas num host cujo `appsettings` de produção nunca foi comparado.
5. **Guard-rail de segredo** (`deploy.yml:468`): a migração **aborta** se fosse deixar um segredo em
   disco sem equivalente no Environment. "Abortar é frustrante; migrar e quebrar o banco é pior, e
   migrar mantendo o segredo em disco é fingir que resolveu." — exatamente o julgamento certo.
6. **Descarte de segredo morto com aviso de revogação** (`deploy.yml:455`): Gemini/OpenAI/ElasticSearch
   ainda vivem no `appsettings` de produção com valor; a migração os descarta e diz quais revogar.
7. **Publicação do runner por detecção de conteúdo** (versão do `log4net`), não por nome de pasta
   (`deploy.yml:289`). Evita o falso negativo de procurar `AppConnector.DIR` literal.
8. **Diagnóstico não-fatal** (`deploy.yml:902`) para o que só se vê de dentro do host.

Isso é maturidade operacional real. As lacunas abaixo não a contradizem — coexistem.

---

## As lacunas, por risco

### 🔴 1. Produção não tem verificação pós-deploy nenhuma

**Verificado:** `deploy.yml` **não tem smoke test, health gate ou qualquer sonda** depois de subir o
serviço. A sequência final (`:859-878`) é: `Start-Service` → `Start-Sleep 2` → conferir
`Status -eq 'Running'` → **"Deploy concluído com sucesso"**.

`Status = Running` só diz que o **processo** subiu. Não diz que ele conecta no SQL, que o catálogo
carrega, que o decryptor existe, que o runner responde. **Um deploy que sobe um processo incapaz de
servir é declarado sucesso** — é o mesmo mecanismo que deixou o LowCode rodar sem runner por semanas.

E o `ci-dev.yml`, que **tem** smoke test, bate numa **rota de negócio** (`/api/document/layouts`,
`:581`) **aceitando 404 como sucesso** (`:579`). Um catálogo vazio porque o decryptor quebrou responde
`200` com lista vazia, ou `404` controlado — e o smoke test passa nos dois. **O gate valida que a
aplicação responde HTTP, não que ela funciona.**

**Correção:** depende do `/health/ready` que o `@lp-backend-dev` está construindo agora (P1.3 do plano
de segurança). Quando existir: os **dois** workflows batem em `/health/ready` exigindo **200 estrito**,
e o deploy de produção **falha** se a readiness não fechar em N segundos. Aí o "sucesso" passa a
significar "consegue servir", não "o .exe abriu".

### 🔴 2. Cópia parcial é tolerada e reportada como sucesso

**Verificado** (`deploy.yml:789-813`): os arquivos são copiados um a um para dentro do `\api` **vivo**;
erro de cópia incrementa `$errorCount`, emite `Write-Warning` e **o loop continua**. No fim:
`"Arquivos copiados ($copiedCount arquivos, $errorCount erros)"` e o deploy **segue e conclui**.

Uma cópia que falha na metade — arquivo travado, disco cheio, permissão — deixa um **deploy
Frankenstein**: parte dos binários novos, parte dos antigos, versões de assembly potencialmente
incompatíveis, e "sucesso" no log. É a mesma anti-regra que a auditoria de segurança pegou no código
da aplicação, agora no próprio deploy.

**Correção:** `$errorCount > 0` deve **falhar o step**. E ver o item 3, que resolve isto na raiz.

### 🟠 3. Sobrescrita in-place, sem swap atômico nem rollback de binário

O serviço é parado, os arquivos são copiados **por cima** do `\api` de produção, o serviço volta. Só o
`appsettings.json` é backupeado (`:704`) — **os binários da versão anterior não são**. Consequências:

- **Sem rollback.** Build novo quebrado ⇒ não há revert automático; a versão que funcionava foi
  sobrescrita em disco.
- **Janela de indisponibilidade** = tempo de parar + copiar centenas de arquivos + subir.
- combinado com o item 2, uma cópia interrompida corrompe a instalação viva sem cópia de segurança do
  que havia.

**Correção (padrão staoff/atômico):** publicar numa pasta nova versionada
(`\releases\<sha-ou-timestamp>`), e o deploy vira **parar → apontar → subir**: renomear/symlink
`\api` para a release nova (rename é atômico), com a anterior intacta ao lado. Rollback = reapontar
para a release anterior e reiniciar. Elimina o Frankenstein (item 2), dá rollback e encurta a janela.
Para um serviço single-instance interno, é o suficiente — não precisa de blue-green.

### 🟠 4. Sem `concurrency` e sem `environment` de proteção

**Verificado:** o `deploy.yml` não declara `concurrency:` nem `environment:`.

- **Sem `concurrency`:** dois pushes para `master` disparam **dois deploys simultâneos** parando/subindo
  o mesmo serviço e copiando um por cima do outro. Corrida clássica.
- **Sem `environment`:** **qualquer push para `master` vai direto para produção** — sem revisor
  obrigatório, sem wait timer, sem trilha de aprovação. Para um sistema que processa documento fiscal
  de cliente, isso é fraco.

**Correção:** `concurrency: { group: deploy-prod, cancel-in-progress: false }` (fila, não cancela um
deploy no meio). E um `environment: production` com **required reviewer** — o próprio dono do projeto
aprova o deploy no GitHub antes de rodar. Custa um clique; compra uma barreira contra push acidental.

### 🟠 5. O runner do GitHub Actions vive no servidor de produção

O job roda em `[self-hosted, windows, production]` (`:31`) — o runner está **na** `172.25.32.42`. Ou
seja: **o código de workflow de qualquer push para `master` executa na máquina de produção**, com
privilégio para parar serviço, escrever em `\api` e — item novo desta leva — **escrever na Bin de um
produto de terceiros** (a instância AppConnector, `:282`). Quem consegue push em `master`, ou compromete
uma action de terceiro referenciada, roda código como esse runner no host de produção.

Não dá para eliminar sem separar build de deploy (o runner precisa estar no host para o deploy local),
mas dá para **reduzir a superfície**:
- Fixar as actions por **SHA**, não por tag (`actions/checkout@v4` → `@<sha>`): tag é mutável, SHA não.
- Separar **build** (runner efêmero/hospedado, produz artefato versionado) de **deploy** (runner de
  produção, só baixa o artefato e troca a release). Hoje build e deploy são o mesmo job — uma falha de
  restore e uma falha de entrega se confundem, e o host de produção precisa do SDK .NET completo + MSBuild.

### 🟡 6. Checkout de repos privados sem checagem do PAT

`GH_PAT_TOKEN || GITHUB_TOKEN` (`:45`, `:53`) para clonar `LayoutParserLib`/`LayoutParserDecrypt`. Se o
PAT for necessário (repos privados sob outra visibilidade) e estiver ausente, o fallback para
`GITHUB_TOKEN` **falha o checkout silenciosamente** e o erro só aparece lá na frente, no build, como
"projeto não encontrado". Vale uma checagem explícita com mensagem clara.

---

## Ordem recomendada

1. **Item 1 (health gate)** — casa com o `/health/ready` que o Dex já está construindo. Assim que o
   endpoint existir, apontar os dois smoke tests para ele com 200 estrito. **Maior retorno.**
2. **Item 2 (`$errorCount` fatal)** — uma linha, alto valor. Imediato.
3. **Item 4 (`concurrency` + `environment`)** — barato, e o `environment` com reviewer é a barreira
   que falta contra push acidental para produção.
4. **Item 3 (release atômica + rollback)** — mais trabalho, resolve 2 e a indisponibilidade de vez.
5. **Item 5 (pin por SHA; separar build de deploy)** — endurecimento de supply-chain; planejar.
6. **Item 6** — pequeno, oportunista.

**Tudo isto é `@lp-devops`.** Nada aqui muda código de aplicação. E nada é premissa para o P0/P1 de
segurança que já está em andamento — são trilhas independentes.

> **Nota de método:** os itens 1, 2, 4 foram verificados por leitura direta do YAML (grep confirmando
> ausência de `smoke/health/concurrency/environment` e presença do `$errorCount` tolerado). Os itens
> 3, 5, 6 são recomendação de padrão, não defeito provado — implementá-los é decisão de custo/risco do
> dono do projeto com o `@lp-devops`.
