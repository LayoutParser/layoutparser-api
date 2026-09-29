# Fase 6 — Eliminar o LowCodeRunner proprietário (issue #581, Epic #575)

Investigação de longo prazo, sem prazo e não bloqueante.

## Estado levantado (a partir do repositório)
- O LowCodeRunner (x86, DLLs Sysmiddle/ConnectUs com licença atrelada a host) é o único motivo de o
  sidecar Windows (Fase 2) existir junto com o Decrypt.
- Análise recente do parser do Sysmiddle (`SysMiddle.Map4Connect.Parser`, decompilado com ilspycmd):
  o algoritmo de parse posicional é simples e já foi **replicado** em script
  (`.claude/tmp/simula-parser-sysmiddle.ps1`), reproduzindo os alertas exatos do produto. Ou seja, o
  **parse** é reimplementável em .NET puro.
- Ainda **não coberto**: o interpretador das regras TCL (`RuleInterpretor`/`RealMapperParser`) e as
  funções nativas usadas pelos mappers.

## Avaliação
- Viável no horizonte previsível? **Parcialmente**: parse sim; execução de regras TCL exige paridade
  com o interpretador — usar o harness atual (LowCodeRunner) como oráculo, comparando saídas em corpus
  de documentos reais.
- Cuidado: as DLLs são proprietárias; reimplementar comportamento observável é diferente de copiar
  código — validar com o jurídico antes de usar código decompilado.

## Plano de descontinuação (esboço, sem compromisso)
1. Porta do parser para C# (nova classe testada contra o oráculo).
2. Interpretador TCL incremental, por famílias de função, com teste diferencial.
3. Flag por layout para rotear ao runtime novo; desligar o sidecar quando a paridade fechar.
