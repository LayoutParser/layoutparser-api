# Regras do Sysmiddle que a geração de TCL/XSLT precisa respeitar

Origem: caso IVG / SAP Grupo CNH (2026-09-28) — `<total>` ausente na NF-e e alerta "Documento com conteúdo
não definido". Estas regras devem entrar como **conhecimento do gerador** (prompt/RAG do TCL/XSLT Mapping
Studio) e como **checks determinísticos** antes de gerar ou testar um mapper.

## 1. O parse acontece ANTES das regras TCL
O Sysmiddle lê o documento contra o layout inteiro primeiro; só então as regras do mapper rodam. Se o parse
deixar sobra, o alerta "conteúdo não definido" aparece e as regras leem campos vazios: as tags derivadas
somem, sem nenhum erro de regra. **Conclusão: nunca diagnosticar ausência de tag olhando só o mapper.**

## 2. O parser é sequencial e não volta atrás
- Linhas são lidas na ordem de `Sequence` do layout, sem retrocesso.
- Linha casa se o buffer começa com o `InitialValue`; linha opcional que não casa é pulada.
- Linha com `InitialValue` vazio (ex.: trailer `999999`) casa com qualquer coisa e **engole** um registro
  que chegou fora de ordem; tudo depois dele fica sem leitura.
- Consequência: registro fora da ordem do layout derruba todos os blocos seguintes (no caso IVG: bloco 098
  no meio do item → perdeu `total`, `transp`, `pag`, `infCpl`).

## 3. A planilha do cliente é a fonte da ordem
Os blocos da planilha são sequenciais; o layout deve espelhá-la. Quando o documento real diverge da
ordem, a correção é **na origem** (sistema emissor), não mover blocos no layout para "fazer passar".
Só alterar o layout se a planilha mudar antes.

## 4. Layouts MQSeries de 600 caracteres
Registro = 6 dígitos de sequência + 3 de tipo + campos, sem quebra de linha. O campo `Sequencia` (último
de cada LINHA) consome o prefixo de 6 dígitos do PRÓXIMO registro. Não tratar 606 como estouro.

## 5. Referências do mapper vêm do layout, sensíveis a caixa
`I.LINHAxxx/Campo` deve usar a grafia exata do layout (`PercentualDoDiferimentoUF` ≠
`PercentualdoDiferimentoUF`) e a LINHA correta (`gTribCompraGov` mora na LINHA046, não na 045).
Referência errada não gera erro: o campo sai vazio e a tag some.

## 6. Cuidados de regra
- Regras com o mesmo nome podem ter alvos diferentes (`dadosAdic` × `infAdic`): comparar pelo alvo, não pelo nome.
- Condições copiadas de outro grupo (ex.: `gCBS`/`gMono` testando `ValorTotalDoIBS`) geram tags indevidas
  quando os campos chegam zerados.

## Checks determinísticos implementados
| Check | Classe | Detecta |
|-------|--------|---------|
| Ordem do documento vs layout | `SysmiddleParseOrderChecker.Simulate` | registro fora de ordem → sobra de conteúdo (offset, prévia, última linha casada) |
| Referências do mapper vs layout | `TclLayoutReferenceChecker.Check` | linha inexistente, campo inexistente (sugere a LINHA correta), caixa diferente |

Testes: `tests/LayoutParserApi.Tests/Fiscal/SysmiddleChecksTests.cs`.

## Pendente (decisão de produto)
- Ligar os dois checks ao fluxo do Studio (gate antes de `MappingCompileService`/`MappingTestRunService` e
  como contexto do disparo de geração), e injetar as seções 1–6 no prompt do gerador.
- Ponto em aberto com o cliente: o Bloco 098 por item (`nItem`) — a planilha precisa definir.
