# ADR — Reavaliação: fine-tuning de "julgamento fiscal" (CFOP/ST/regras de negócio), não só sintaxe (2026-09-08)

> **Atualização 2026-09-29:** a `LayoutParserLib` foi removida/arquivada; a criptografia Sysmiddle vive no `layoutparser-decrypt` (fonte da verdade). Menções à Lib abaixo são contexto histórico.

> **PT-BR.** Autoria: `@lp-architect`. Reavalia parte da Decisão 2 de
> [`gemini-openai-decommission-decision.md`](../../.claude/agent-memory/lp-architect/gemini-openai-decommission-decision.md)
> (2026-07-21) e a Reconciliação de 2026-07-21 ("IA fiscal especializada não exige treinar
> nada") à luz de [`adr-fine-tuning-nichado-ollama-2026-09-02.md`](adr-fine-tuning-nichado-ollama-2026-09-02.md),
> que já reverteu a proibição geral de fine-tuning. **Não substitui essas decisões — complementa,
> distinguindo dois tipos de conhecimento fiscal que até aqui foram tratados como um só.**
> Não implementa nada.

## 1. Pergunta que motivou esta reavaliação

O dono perguntou se, dado que o fine-tuning de **sintaxe** (TXT→TCL/XSLT via DSL Sysmiddle)
provou ser viável e deu resultado bom, faz sentido também treinar o modelo (ou um irmão
especializado) em **regras de negócio fiscal** — CFOP, substituição tributária, e afins — em
vez de continuar só com RAG para esse propósito.

## 2. Fato novo que dispara a reavaliação — confirmado nesta sessão, não suposição

- **QLoRA rodou de verdade, CPU-only, na VM `172.25.32.5`:** 4 vCPU, 15GB RAM, **sem GPU**
  (`nvidia-smi` não existe) — hardware **mais fraco**, não mais forte, que o servidor de produção
  citado na decisão original (`BRNDDAPPBLD01`, i7-4790 Haswell 2014, 8 threads, 32GB RAM, também
  sem GPU). Fonte: [`finetuning-vm-hardware-toolchain.md`](../../.claude/agent-memory/lp-parser-llm/finetuning-vm-hardware-toolchain.md).
- **Resultado medido, não estimado:** modelo base Qwen2.5-Coder-1.5B-Instruct, dataset
  `sysmiddle-dsl-dataset-2026-09-02.jsonl` (6044 exemplos, 3 épocas), `train_runtime` ~40h
  (144.837s), `train_loss` 0.383, `mean_token_accuracy` 90%. Adapter mergeado, convertido para
  GGUF, quantizado Q4_K_M (986MB), plugado em produção via Ollama (PR #335), validado com
  qualidade superior ao modelo genérico em 4 cenários de teste.
- **Conclusão factual:** a premissa "treinar é impraticável nesse hardware, tratar como
  brinquedo" (seção "⚠️ Treinar/fine-tunar... também precisa de GPU" da decisão original de
  julho) está **invalidada por medição real**, não por uma VM diferente/mais forte — é o mesmo
  patamar de hardware fraco, só que agora com um treino real de 40h rodado e servido em produção.
  Isso já havia sido reconhecido em setembro (ver `adr-fine-tuning-nichado-ollama-2026-09-02.md`,
  que reverteu a proibição geral) — esta reavaliação foca especificamente se isso também vale
  para o sub-caso "julgamento de regra fiscal", que aquele ADR não cobriu explicitamente.

## 3. A distinção que decide esta reavaliação — dois problemas de dado diferentes

O ADR de 2026-09-02 e o treino executado resolvem **sintaxe**: dado um layout de input e um
schema de destino, gerar a regra DSL/TCL/XSLT correta. É um problema de **tradução estruturada**
— o dataset é `(layout_input, xsd_destino, regra_dsl)`, sempre determinístico dado o par
input/output, verificável objetivamente contra o XSD.

"Julgamento de regra fiscal" (CFOP × tipo de operação, substituição tributária, e afins) é um
problema diferente: dado um **documento fiscal completo com seu contexto de negócio**, decidir
se uma regra fiscal foi aplicada corretamente. O dataset necessário seria
`(documento_fiscal, regra_aplicável, veredito_correto)` — não existe hoje, e não é o mesmo
dataset que já foi construído. Confirmado nesta sessão:

- `Services/Fiscal/*.cs` (`FiscalMappingRuleExtractor`, `MappingDraftRuleTranspiler`,
  `MappingSuggestionService` etc.) implementa a plataforma de **geração/tradução** de mapeamento
  fiscal (issue #151, ver
  [`reversao-pos-hoc-vs-cogeracao-2026-09-07.md`](../../.claude/agent-memory/lp-architect/reversao-pos-hoc-vs-cogeracao-2026-09-07.md)) —
  não contém, e não gera, exemplos rotulados de "documento X + regra Y + veredito correto/errado".
- Busca por `CFOP` no repositório inteiro retorna apenas a menção na visão de arquitetura
  (`ia-fiscal-diagnosis-vision.md`) e o índice desta memória — **zero código, zero dataset, zero
  tabela CFOP indexada**. A reconciliação de 2026-07-21 (["IA fiscal especializada" não exige
  treinar nada]) descreveu um plano de RAG sobre a tabela CFOP que **nunca foi implementado** —
  continua greenfield, tanto para RAG quanto para fine-tuning.
- O dataset que existe (`sysmiddle-dsl-dataset-2026-09-02.jsonl`) é 100% pares de tradução
  estrutural (ex.: `Regra_chaveDeAcesso`, `Rule_gIBSCBSMono`) — nenhum exemplo tem a forma
  "documento fiscal + julgamento de regra de negócio correto/incorreto".

**Conclusão desta seção:** a viabilidade técnica de treinar (hardware) deixou de ser o
bloqueio. O bloqueio agora é **ausência de dataset rotulado para o problema específico de
julgamento fiscal** — que é um problema diferente do que já foi resolvido, não uma extensão
trivial dele.

## 4. Risco de overfitting/memorização — se aplica mais forte aqui que no caso já resolvido

A Decisão 3 da memória original (geração sintética) já levantou o risco de o modelo "decorar"
CNPJ/valor real em vez de aprender o padrão geral, mesmo rodando 100% on-prem. Esse risco se
aplica de forma **mais severa** ao fine-tuning de julgamento fiscal do que ao de sintaxe já
feito, por dois motivos:

1. **Volume de dados real disponível é pequeno.** O corpus de documentos fiscais reais e
   diversos (múltiplos clientes, múltiplos cenários de CFOP/ST) é, pelas memórias já registradas
   (`backfill-catalogo-nao-viavel-2026-09-08.md`), **insuficiente** — só 4 de 54 pares reais
   confirmados na trilha de retraining F4. Um dataset pequeno de julgamento fiscal (dezenas de
   exemplos, não milhares como o de sintaxe) é justamente o cenário onde um modelo pequeno tende
   a memorizar exemplos individuais em vez de generalizar a regra.
2. **A saída é um veredito de "correto/incorreto", não uma tradução estrutural verificável
   contra XSD.** No caso de sintaxe, o verificador determinístico (XSD, diff estrutural) pega
   erro do modelo objetivamente. No caso de julgamento fiscal, não há verificador tão direto —
   um modelo que "decorou" (documento específico → veredito específico) passa despercebido em
   validação superficial, porque a métrica de "acertou o veredito" não distingue generalização de
   memorização sem um conjunto de teste genuinamente novo (documentos/cenários nunca vistos no
   treino).

CFOP em particular agrava isso por ser **tabela de consulta finita e pública** (código-tipo de
operação × natureza), não um padrão estatístico a generalizar — pedir para uma rede neural
"aprender" isso nos pesos é pedir para ela decorar uma tabela de lookup, o oposto do que
fine-tuning faz bem.

## 5. Custo de manutenção — NT da SEFAZ, quantificado onde possível

A objeção original ("cada NT nova exigiria re-fine-tuning; RAG só precisa de exemplo novo no
índice") continua válida e **pesa mais**, não menos, para julgamento fiscal do que para sintaxe:

- Regra de sintaxe (DSL/XSLT) muda quando o **schema de destino** muda — evento relativamente
  raro e já documentado (`nt-pipeline-design.md`, pipeline S1-S5 de auto-atualização por NT).
- Regra de **julgamento fiscal** (que CFOP é válido para qual tipo de operação, quando aplicar
  substituição tributária) muda com frequência maior e por fontes múltiplas: NT da SEFAZ, mas
  também legislação estadual (ST varia por UF), convênios ICMS, e a Reforma Tributária em curso
  (IBS/CBS, NT2025.002, já em andamento neste projeto) — um ciclo de mudança visivelmente mais
  ativo que o de schema XML. Não há no repositório uma contagem confiável de "quantas NT por
  ano" para citar um número exato; o achado concreto é que o volume de mudança de regra de
  negócio fiscal tende a ser maior e mais fragmentado (múltiplas fontes normativas) que o de
  schema — não consigo quantificar precisamente, mas a direção do argumento não muda.
- Para fine-tuning fixar esse conhecimento nos pesos, cada mudança normativa exigiria novo ciclo
  de dataset + treino (~40h medidas, mesmo para o modelo pequeno já feito) + validação +
  re-deploy do adapter — um custo operacional recorrente que RAG não tem (só adicionar/atualizar
  o exemplo no índice).

## 6. Decisão — híbrido assimétrico, não "sim" nem "não" uniforme

Não é uma resposta em cima do muro: as duas metades do problema têm respostas diferentes e
específicas.

| Sub-problema | Decisão | Por quê |
|---|---|---|
| **CFOP × tipo de operação** (lookup de tabela finita) | **RAG, não fine-tuning.** Mantém a reconciliação de 2026-07-21 inalterada. | É consulta de tabela, não reconhecimento de padrão — fine-tuning pediria à rede para decorar o que deveria ser indexado e recuperado. Hardware deixou de ser o motivo de rejeitar, mas a natureza do problema continua sendo. |
| **Substituição tributária / regras fiscais mais "fuzzy"** (múltiplos fatores, exceções por UF/convênio, potencialmente ambíguas) | **RAG primeiro; reabrir fine-tuning só se e quando existir dataset rotulado real de volume suficiente** (não hoje). | O teto de qualidade de RAG-sem-treino é real (já sinalizado em julho) e essas regras são mais próximas de "julgamento" que "lookup" — mas fine-tuning aqui sem dataset adequado é apenas risco de memorização sem generalização, pelo argumento da seção 4. Fine-tuning deixou de estar bloqueado por hardware; está bloqueado por dado. |
| **Sintaxe TXT→TCL/XSLT/DSL** (já resolvido) | Mantém decisão de setembro — fine-tuning já em produção, continuar capturando dado incremental (`adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md`). | Fora do escopo desta reavaliação — só citado para não confundir os dois workstreams. |

**Não recomendo** "fine-tuning fiscal agora" porque isso repetiria, para julgamento fiscal, o
mesmo erro que a Decisão 2 original evitou para diagnóstico XSD: treinar sobre dataset pequeno
demais, sem verificador robusto, com alto risco de memorização mascarada de generalização — só
que agora sem a desculpa de hardware, o que tornaria mais fácil "só tentar" sem questionar se o
dado sustenta.

## 7. Plano faseado, caso o dono opte por avançar

Não recomendo pular para fine-tuning; recomendo este caminho, que preserva opcionalidade:

1. **Fase RAG (já decidida em julho, ainda não implementada — greenfield):** indexar a tabela
   CFOP × tipo de operação real (fonte pública SEFAZ) e cenários rotulados sintéticos/reais
   (Faker/Mimesis + variação deliberada, como já desenhado). Entrega valor imediato, sem
   dependência de volume de dado real.
2. **Captura incremental de julgamento humano real como subproduto de operação**, não como
   projeto de coleta dedicado: sempre que um analista corrigir/confirmar uma decisão de CFOP/ST
   em produção (via o mesmo mecanismo de correção guiada por humano já desenhado em
   `contrato-correcao-guiada-humano-2026-09-08.md`), gravar o triplo
   `(documento, regra, veredito_curado)` num dataset append-only — mesmo padrão já recomendado
   para o dataset de sintaxe em setembro. Isso é o único jeito honesto de acumular volume real
   sem inventar dado.
3. **Gate de decisão explícito, não automático:** só reabrir fine-tuning de julgamento fiscal
   quando esse dataset acumulado tiver volume e diversidade que justifiquem (ordem de grandeza:
   centenas de exemplos curados cobrindo múltiplos cenários/UF/exceções, não dezenas de um único
   cliente) — critério a refinar com `@lp-parser-llm` quando o volume real existir, não fixar um
   número arbitrário agora.
4. Quando esse gate for atingido, tratar como continuação do mesmo pipeline LoRA/QLoRA já
   validado (mesmo servidor, mesma toolchain) — não como projeto novo do zero.

## 8. Registro de impacto no ecossistema (boundary entre repos)

Nenhum impacto direto em `LayoutParserLib`/`LayoutParserDecrypt`/`LayoutParserReact` — esta
reavaliação é escopo de `LayoutParserApi` (RAG de CFOP viveria em `Services/Fiscal`/`ai/`,
como o resto da plataforma fiscal). Se a Fase RAG avançar, o React só ganharia superfície nova
se o dono quiser expor explicação/citação de CFOP na UI de diagnóstico — não decidido aqui.

## 9. Próximos passos (não decididos aqui — recomendação para o dono/`@lp-pm`)

- Recomendo abrir um item de backlog para a Fase RAG de CFOP (ainda greenfield desde julho) —
  quem formaliza isso no GitHub Project é `@lp-pm`, não eu.
- Recomendo que a captura incremental do triplo `(documento, regra, veredito)` seja anexada como
  requisito não-funcional do endpoint de correção humana já em desenvolvimento (issue/branch
  `feat/endpoint-field-correction-345`), para não exigir instrumentação separada depois —
  decisão de implementação cabe a `@lp-backend-dev`.
