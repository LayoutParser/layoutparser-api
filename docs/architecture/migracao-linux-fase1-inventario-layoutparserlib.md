# Fase 1 — Inventário de compatibilidade do LayoutParserLib (issue #576, Epic #575)

**Status: PARCIAL — depende de acesso ao repositório LayoutParserLib.**

O código do LayoutParserLib não está disponível localmente e `LayoutParserLib.dll` não existe em disco
(o build local emite `MSB3245: Could not locate the assembly "LayoutParserLib"`). O que se sabe:

- A API o referencia como DLL via `HintPath ..\LayoutParserLib\bin\$(Configuration)\LayoutParserLib.dll` (`LayoutParserApi.csproj`).
- Papel: criptografia Sysmiddle.

## Checklist para fechar (executar no repo LayoutParserLib)

```powershell
# Target framework
Select-String -Path *.csproj -Pattern "TargetFramework"
# Win32 / DPAPI / P-Invoke
Select-String -Path **\*.cs -Pattern "DllImport|LibraryImport|ProtectedData|System.Security.Cryptography.ProtectedData|Registry|System.Management|WindowsIdentity"
```

| Item | Resultado |
|------|-----------|
| Target framework | _a preencher_ |
| P/Invoke / DPAPI / Win32-only | _a preencher_ |
| Conclusão (portável / com adaptação / não portável → sidecar) | _a preencher_ |

Regra de decisão: `net48` ou uso Win32 → entra no sidecar (Fase 2, `docs/architecture/migracao-linux-fase2-contrato-sidecar.md`).
`netstandard2.x`/`net6+` sem Win32 → portável sem alteração.
