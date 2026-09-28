# Fase 3 - Pipeline Linux (issue #578, Epic #575)

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
