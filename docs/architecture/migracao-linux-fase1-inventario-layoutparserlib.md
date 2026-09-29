# Fase 1 - Inventario de compatibilidade do LayoutParserLib (issue #576, Epic #575)

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

**Conclusao (2026-09-29): a API NAO depende do LayoutParserLib. Decisao do dono: considerar "portavel sem alteracao" e remover.**

Evidencias:
- Repositorio `layoutparser-lib`: class library .NET Framework 4.8.1 (csproj nao-SDK) com 3 arquivos:
  `CryptographySysMiddle.cs` (Rijndael/AES, `Decrypt(string)`) e `RollingFileLogger.cs` (interno).
- `LayoutParserApi`: nenhum tipo da Lib e usado em codigo (so um comentario sobre o formato de log em
  `UnifiedLogReaderService`); a `Reference`/`HintPath` no csproj era sobra. Desde o #569 a descriptografia e por HTTP
  (sidecar Decrypt).
- `layoutparser-decrypt` embute os fontes da criptografia por conta propria (a Lib nao e dependencia dele).

Acoes tomadas:
- Removida a `Reference LayoutParserLib` de `LayoutParserApi.csproj`.
- Removidos o checkout e o build da Lib de `ci-dev-linux.yml` e `deploy-linux.yml` (net48 nao compila no Linux).
- Pipelines Windows (`deploy.yml`, `ci-dev.yml`, `pr-validate.yml`) NAO foram alterados (ainda constroem a Lib e a
  copiam para o Decrypt); limpar depois do cutover.

Concluido (2026-09-29): `layoutparser-lib` arquivada; a criptografia vive no `layoutparser-decrypt`. Pendencia remanescente: pipelines Windows (`deploy.yml`, `ci-dev.yml`, `pr-validate.yml`) podem ainda referenciar a Lib (nao verificado nesta revisao).
