# Explicador Sysmiddle: redução de regras opacas (2026-10-01)

## O que mudou
- `GET /api/sysmiddle/functions`: catálogo somente leitura (builtins + ~80 classes NDD lidas por
  `MetadataLoadContext`, sem executar nada). DLL ausente => só builtins + 3 funções NDD curadas.
  Limite: a DLL é ofuscada (Name/descrição/parâmetros são strings resolvidas só em runtime). O nome
  exposto é o da classe e os parâmetros saem como `pars:object[]`. `FormaterDecimal`/`RemoveZerosLeft`
  (nomes da DSL) não são nomes de classe — ficam como entradas curadas.
- Explicador: funções builtin do catálogo (Trim, IsNullOrEmpty...) deixam de tornar o ramo opaco; regras sem
  ramo `T.` agora são varridas (`#.x = I.y`, if/else, funções). Opaca por função NDD/não catalogada =>
  `functions[]` + "usa função NDD X". Só temporárias/if com builtins => `best_effort`.
  `ExplainedRule.Functions` é campo aditivo (omitido quando nulo); formato de coverage intocado.

## 125 (dono) vs 138 (explicador) — NÃO verificado com dado real
Não consegui reproduzir: o dump dos mapeadores descriptografados foi bloqueado (dado de cliente), então
só há hipóteses pelo código. `OpaqueRuleCount` conta `ExplainedRule`, e uma `MapperRule` com N ramos
`T.` de função desconhecida gera N itens opacos (+1 por fallback). Hipótese principal: 125 = regras DSL
(MapperRule) distintas com algo opaco; 138 = itens explicados (ramos). Alternativa: 125 vem de outra
contagem (ex.: só fallback "fora da gramática", sem os ramos de função desconhecida). Para fechar:
agrupar as 138 opacas por `evidence.reference` (nome da regra) e contar distintas; se der ~125, confirma.
