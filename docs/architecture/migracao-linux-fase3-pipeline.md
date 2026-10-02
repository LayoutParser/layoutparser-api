# Fase 3 - Pipeline Linux (issue #578, Epic #575)

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

## Fluxo de deploy de producao (atualizado 2026-10-02)

- Merge `develop -> master` dispara `deploy-linux.yml`, que **pausa aguardando 1 clique de aprovacao**
  (environment `production`, Required reviewers = `elson-vinicius-lopes`; restrito a branch `master`).
  Merges que so alteram `docs/**` ou `*.md` nao disparam.
- Apos aprovar: testes, publish, backup em `<path>.prev`, rsync, restart e smoke `/health/ready`
  (HTTP 200, 12x10 s). Falha => **rollback automatico** para `.prev`. Timeout 30 min; concurrency
  `deploy-prod-linux` sem cancelamento.
- Execucao manual (reserva): Actions > Deploy API Linux > Run workflow (branch master) e digitar `DEPLOY-LINUX`
  (a aprovacao do environment tambem se aplica).
- O `deploy.yml` Windows ja foi removido (commit 72f4906).

Historico da Fase 3 (pipeline originalmente manual):

Adicionados **em paralelo** (sem tocar `deploy.yml`/`ci-dev.yml`): `.github/workflows/ci-dev-linux.yml` e
`deploy-linux.yml`, ambos `workflow_dispatch` (o de producao exige digitar `DEPLOY-LINUX`).
Deploy = `dotnet publish` + rsync para `/opt/layoutparser-api` + unit `deploy/linux/layoutparser-api.service`
(`EnvironmentFile=/etc/layoutparser-api/secrets.env`, chmod 600) + `systemctl restart`.
Smoke: `/health/ready` HTTP 200 estrito, 12 tentativas x 10 s; falha => rollback para `<path>.prev`.

**Rollback do cutover:** manter o servico Windows e o `deploy.yml` intactos ate a validacao; reverter =
nao acionar os workflows Linux e apontar o BFF de volta ao .42.

**Decisoes/pre-requisitos do dono:** (1) runner Linux self-hosted com labels `linux`+`dev-local` / `linux`+`production`
e sudo sem senha limitado a systemctl/rsync/cp; (2) provisionar `/etc/layoutparser-api/secrets.env` uma vez;
(3) Variables opcionais `DEPLOY_PATH_*_LINUX`, `API_URL_*_LINUX`; (4) **bloqueio tecnico**: a API referencia
`LayoutParserLib.dll` (net48) e o `DecryptionService` usa `LayoutParserDecrypt.exe` - sem Fase 1 (#576) e sidecar
(#577) o build/execucao Linux nao fecha; os steps de build da Lib sao placeholder ate essa decisao.
Nao portado: config-drift, publicacao do runner low-code e alerta SMTP do deploy.yml.
