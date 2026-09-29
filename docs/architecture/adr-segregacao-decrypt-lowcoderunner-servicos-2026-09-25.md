# ADR — Segregação de Decrypt e LowCodeRunner em serviços de rede, para viabilizar migração da API para Linux

> `@lp-architect` (Aria), 2026-09-25. Formalização de duas rodadas de análise já feitas nesta
> sessão de trabalho, a pedido explícito do dono, antes de iniciar a implementação.
> Decisão de arquitetura — **não implementada aqui**. Execução: `@lp-backend-dev` (código) +
> `@lp-devops` (infra/deploy).

## Status

**Aceito.** Aguarda `@lp-backend-dev`/`@lp-devops` sequenciarem a implementação conforme a ordem
recomendada abaixo, com validação de licença FiatMQ pendente antes da etapa do LowCodeRunner.

## Contexto

A `LayoutParserApi` roda hoje inteira em Windows, como serviço Windows nativo
(`UseWindowsService()` em `Program.cs`). Três dependências prendem a API ao Windows:

1. **`LayoutParserLib`** — biblioteca de criptografia Sysmiddle, referenciada via `HintPath` como
   DLL .NET Framework 4.8.1.
2. **`LayoutParserDecrypt.exe`** — processo externo, chamado via `Process.Start` (timeout 30s),
   que invoca `LayoutParserLib` para descriptografar payloads.
3. **LowCodeRunner** — motor Sysmiddle real e proprietário, **x86**, com **licença FiatMQ por
   host**. Hoje vive em `tools/LowCodeRunner` dentro do próprio repositório da API, chamado via
   `Process.Start` com concorrência limitada por `SemaphoreSlim` e timeout configurável (default
   180s).

O dono quer migrar a API principal para Linux e segregar os dois componentes presos ao Windows
em serviços de rede separados, reaproveitando os repositórios já existentes: `LayoutParserDecrypt`
e `LayoutParserLowCodeRunner` — este último **já tem a lógica migrada** de `tools/LowCodeRunner`
para um `Program.cs` de 307 linhas, não é um esqueleto vazio a ser escrito do zero.

## 1. Achado técnico que muda a leitura do problema

> ### ⚠️ Errata (2026-09-25, mesma sessão — corrigido durante a implementação da Parte 1)
>
> ~~Investigação de `LayoutParserLib/CryptographySysMiddle.cs` (~41 linhas): o algoritmo usa
> **somente** `System.Security.Cryptography.RijndaelManaged` — criptografia 100% gerenciada, **sem
> P/Invoke, sem COM interop**. Tecnicamente, esse código poderia rodar em .NET moderno em Linux
> hoje, sem adaptação de plataforma.~~
>
> **Correção:** essa conclusão estava certa sobre a ausência de P/Invoke/COM, mas errada na
> prática para a chave/IV reais deste projeto. A partir do .NET Core/.NET 5+, `RijndaelManaged`
> deixou de ser o algoritmo Rijndael "de verdade" (bloco/chave de tamanho variável) e virou um
> shim fino sobre AES, que só aceita chaves de **128/192/256 bits**. A chave usada em produção
> (`"dbc%$#h92785"`, 96 bits) e o IV (`"Ca#&UjO){Qwz*@FcsPs"`, 152 bits) **não são tamanhos
> válidos para AES** — logo não rodam em .NET moderno, mesmo sem nenhuma dependência nativa.
>
> Comprovado empiricamente pelo `@lp-backend-dev` (Dex) durante a implementação da Parte 1
> (branch `feat/decrypt-http-service-adr` do repo `LayoutParserDecrypt`): um teste de round-trip
> (`CreateEncryptor`/`CreateDecryptor` com essa chave/IV) falha em `net10.0` com
> `ArgumentException: Specified key is not a valid size for this algorithm`. O mesmo código roda
> normalmente em .NET Framework 4.8.1, onde `RijndaelManaged` ainda é Rijndael de verdade.
>
> **Consequência aplicada:** `LayoutParserDecrypt` **permanece em .NET Framework 4.8.1** — não foi
> migrado para .NET moderno como este ADR sugeria como caminho natural. Só o transporte mudou
> (subprocess local → HTTP via `System.Net.HttpListener` auto-hospedado, já que ASP.NET Core
> moderno não roda em .NET Framework clássico). O restante deste ADR (§2–§4, decisão de HTTP+Polly,
> ordem de implementação) continua válido — a correção afeta só a leitura de "o que bloqueia
> Windows" abaixo e o §5 (fora de escopo).

O motivo documentado no próprio README do repositório `LayoutParserDecrypt` para justificar o
processo Windows separado ("a API não consegue executar `RijndaelManaged` em processo de forma
compatível") é, na verdade, **tecnicamente correto** para a chave/IV reais em uso — ver errata
acima. Não é só decisão herdada; o algoritmo genuinamente não roda em .NET moderno com esses
parâmetros.

**Conclusão (revisada):** o Decrypt continua preso ao Windows/.NET Framework por um motivo técnico
real (incompatibilidade de tamanho de chave), não apenas por decisão herdada — além do
LowCodeRunner (motor proprietário x86, licença por host), que já era o bloqueador reconhecido.
Ver §5 (fora de escopo) para o que seria necessário para eliminar esse salto no futuro.

## 2. Decisão de transporte: HTTP interno com Polly, não message broker

### Opções avaliadas

| Opção | Veredito |
|---|---|
| **REST/HTTP interno + Polly** (retry, circuit breaker, timeout) | **Escolhida** |
| Redis pub/sub | Descartada |
| RabbitMQ | Descartada |
| Kafka | Descartada |
| MQSeries | Descartada |
| gRPC | Preterida por ora — revisitar se HTTP não performar com payloads grandes |

### Justificativa

1. **O uso é request-response síncrono do ponto de vista do usuário** — upload → espera → resposta.
   Não é fire-and-forget. Um broker pub/sub no meio exigiria reinventar request-response por cima
   dele (correlation ID + fila de resposta dedicada + timeout duplicado) — complexidade estrutural
   desnecessária para o padrão de uso real.
2. **MQSeries no projeto hoje é só formato de documento** (`LayoutType.MqSeries`), não infraestrutura
   de fila operada pelo time — não há reaproveitamento real. Seria broker novo do zero; IBM MQ é
   pago (o projeto já tem histórico de evitar custo pago recorrente, ex. remoção do CodeQL por
   exigir GHAS — ver `.claude/rules/security.md`).
3. **Redis já tem um papel definido no projeto: cache opcional, com fallback gracioso se cair**
   (`.claude/rules/dotnet-standards.md` §Resiliência). Usá-lo como pipe de transporte de documento
   mudaria seu papel para dependência rígida — contradiz o princípio central de resiliência do
   projeto (a app deve subir e funcionar degradada sem Redis).
4. **Kafka penaliza payload grande por padrão** (limite ~1MB sem tuning); documentos fiscais/XML
   parseados podem exceder isso facilmente.
5. **RabbitMQ/Kafka são infraestrutura operacional nova** para `@lp-devops` provisionar, monitorar
   e potencialmente pagar licença/suporte em escala — sem ganho claro dado que o padrão de uso é
   síncrono, não streaming/fire-and-forget.

### Alternativa preterida: gRPC

Melhor candidato de segunda escolha para streaming de payload grande e contrato fortemente
tipado. Preterido por ora porque adiciona mais atrito de tooling que HTTP+JSON, dado que o
projeto já é 100% ASP.NET Core `[ApiController]` — REST se encaixa sem ferramentas novas. Pode
ser revisitado se, na prática, REST não performar bem com os payloads maiores do domínio fiscal.

## 3. Mudança de contrato: de subprocess local para serviço de rede

| Aspecto | Hoje (subprocess) | Depois (serviço de rede) |
|---|---|---|
| Transporte do payload | Arquivo temporário / arg de CLI | Corpo de request HTTP |
| Lifecycle | `Process.Start`, controlado pela própria API | Serviço Windows de longa duração, fora do controle direto da API |
| Timeout | Kill de processo local (30s Decrypt / 180s LowCodeRunner default) | Cancelamento cooperativo via `CancellationToken` no cliente HTTP + timeout também do lado servidor (defesa em profundidade — não depender só do lado que chama) |
| Formato de erro | Exit code + `stderr` | Contrato de erro HTTP estruturado (status code + corpo JSON) |
| Concorrência | `SemaphoreSlim` in-process, do lado da API/runner | Precisa migrar para **dentro do novo serviço** — a API em Linux perde visibilidade direta de quantos slots estão livres. Sinal recomendado: **HTTP 503** quando o serviço estiver saturado, com `Retry-After` se o cliente Polly puder aproveitar |
| Correlação de request | Passado como argumento de CLI | Header HTTP `X-Correlation-ID` — já é o padrão usado no projeto para o front (ver `.claude/rules/dotnet-standards.md` §Logging) |

### Risco de licença a validar antes de avançar (LowCodeRunner)

Centralizar o LowCodeRunner num serviço único de rede pode mudar o padrão de concorrência que a
licença **FiatMQ (por host)** assume implicitamente — hoje, cada instância local de
`Process.Start` roda dentro do host da própria API. Este **não é um risco técnico, é uma decisão
de licenciamento** que precisa confirmação explícita com quem administra a licença Sysmiddle
antes de implementar a etapa do LowCodeRunner. Não é decidível por nenhum agente.

## 4. Ordem de implementação recomendada

1. **Decrypt primeiro.** É uma função pura sem estado (`Decrypt(string) -> string`), menor risco,
   e serve como prova de conceito do padrão HTTP interno (contrato de erro, Polly, correlationId)
   antes de mexer no componente mais arriscado.
2. **LowCodeRunner depois.** Mais complexo: exige migrar o controle de concorrência para dentro do
   serviço, timeout mais longo, e depende da validação de licença FiatMQ (§3) antes de avançar.

## 5. Fora de escopo deste ADR

Se o Decrypt deveria ser **reescrito em .NET moderno para rodar em Linux também** — eliminando um
dos dois saltos de rede Linux→Windows que a API passaria a ter — é uma pergunta tecnicamente
válida. **Atualizado pela errata do §1:** essa reescrita agora envolve dois problemas empilhados,
não um: (1) portabilidade de plataforma (ausência de P/Invoke/COM não é mais suficiente — precisa
adicionar) (2) trocar o próprio algoritmo, já que `RijndaelManaged` no .NET moderno não aceita a
chave/IV reais em uso (96/152 bits — ver errata). Um candidato viável seria substituir
`RijndaelManaged` por uma implementação que aceite bloco/chave de tamanho variável (ex.:
BouncyCastle `RijndaelEngine`), validando compatibilidade byte-a-byte com dado já criptografado em
produção. Trabalho não trivial de validação, não uma decisão a tomar dentro deste ADR. Registrar
como candidato a ADR futuro se o dono quiser avançar essa frente depois que a segregação básica
estiver estável.

## Consequências

**Positivas:**
- A API principal pode migrar para Linux.
- A superfície Windows do ecossistema fica mínima — só o que realmente precisa (LowCodeRunner,
  motor proprietário x86).
- Sem infraestrutura operacional nova (nenhum broker a provisionar/pagar).
- Consistente com o padrão de resiliência já estabelecido no projeto (Redis opcional, degrade
  gracioso, Polly para dependências externas).

**Negativas / riscos:**
- Dois novos serviços de rede para `@lp-devops` operar e monitorar (deploy, disponibilidade,
  logs).
- Novo ponto de falha de rede entre a API (Linux) e os serviços Windows.
- Cancelamento cooperativo via `CancellationToken` é estruturalmente mais frágil que kill de
  processo local — precisa de timeout defensivo também do lado servidor.
- A licença FiatMQ precisa de validação explícita antes de centralizar o LowCodeRunner —
  bloqueador não técnico que pode atrasar a etapa 2.

## Dono natural de cada etapa

| Etapa | Dono |
|---|---|
| Wrapper HTTP do Decrypt (`LayoutParserDecrypt`) | `@lp-backend-dev` |
| Wrapper HTTP do LowCodeRunner, incluindo migração do semáforo de concorrência para dentro do serviço | `@lp-backend-dev`, com acesso a ambiente Windows com a licença Sysmiddle |
| Cliente HTTP + Polly na API (substituindo `Process.Start`) | `@lp-backend-dev` |
| Deploy/infra dos dois novos serviços Windows + rede entre a API Linux e eles | `@lp-devops` |
| Validação da licença FiatMQ sob o novo padrão de concorrência centralizada | Dono do projeto / fornecedor Sysmiddle — fora do alcance de qualquer agente |
