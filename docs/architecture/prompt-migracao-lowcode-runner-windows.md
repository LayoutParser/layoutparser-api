# Prompt para o time do lowcode-runner — migração para serviço Windows

> Uso: copiar o bloco abaixo e entregar ao time/agente responsável pelo `LayoutParserLowCodeRunner`.
> Contexto: issues #575 (épico), #577 (contrato de sidecar), #581 (eliminar dependência do runner), #641 (diagnóstico `runner_unavailable`).

```text
Contexto
A LayoutParserApi agora roda em produção num host LINUX (Ubuntu, systemd, serviço
`layoutparser-api`, porta 5000 em loopback). O LayoutParserLowCodeRunner.exe (.NET Framework /
Windows, executa mapeadores Sysmiddle "low-code") NÃO roda nesse host. Hoje a API fica com
/health/ready = Degraded e o caminho "sysmiddle" do endpoint
POST /api/transformationexecution/execute-candidates falha com
"LowCode:RunnerPath não configurado". Precisamos desmembrar o runner da API e publicá-lo como
SERVIÇO PRÓPRIO NO WINDOWS, consumido pela API via HTTP.

Objetivo
Entregar o lowcode-runner como serviço Windows independente, com contrato HTTP estável, que a
API Linux chama no lugar de executar o .exe localmente.

Requisitos
1. Hospedagem: serviço Windows (Windows Service ou IIS), reinício automático, logs estruturados
   com CorrelationId propagado do header (ex.: X-Correlation-Id).
2. Contrato HTTP (proposta; validar com a API, ver #577):
   - POST /run  -> entrada: mapeador (MapperGuid + ProjectId; o MapperGuid NÃO é único global),
     documento de entrada (TXT/XML) e opções; saída: XML transformado, status, erros,
     tempo de execução. Limite de tamanho e timeout explícitos.
   - GET /health -> 200 quando o runner e suas dependências (Lib/Decrypt) estão operacionais.
   - Respostas de erro com código estável (ex.: mapper_not_found, runtime_error, timeout).
3. Segurança: NÃO expor à internet. Rede restrita (só o IP do host da API), autenticação
   service-to-service (token/mTLS), nenhuma credencial em log. O runner recebe conteúdo de
   documentos fiscais de clientes: nunca logar o conteúdo, nunca enviar a nuvem.
4. Dados: o runner só LÊ do SQL do ConnectUs (172.31.249.51 é SOMENTE LEITURA; nenhuma escrita
   ou DDL). Se precisar persistir algo, usar banco próprio, nunca esse servidor.
5. Dependências: documentar LayoutParserLib/LayoutParserDecrypt exigidos (a Decrypt é a fonte
   da verdade da criptografia Sysmiddle) e a versão do .NET Framework.
6. Resiliência: o runner indisponível NÃO pode derrubar a API; a API trata como
   "runner_unavailable" (#641). O runner deve responder ao health mesmo sob carga.
7. Desempenho: indicar concorrência suportada (processos paralelos?) e tempo típico por
   transformação, para a API dimensionar timeout e fila.

Critérios de aceite
- Serviço instalado e iniciando sozinho no Windows; /health verde.
- Execução de um mapeador de referência via POST /run com saída idêntica à do .exe local.
- Teste de carga básico (N requisições paralelas) sem vazamento de processo/memória.
- Documento curto com: URL base, autenticação, contrato, limites, como reiniciar e onde ler logs.

Entregáveis
- Pacote/instalador do serviço + runbook de deploy e rollback.
- Contrato OpenAPI do runner.
- Lista de pendências conhecidas (ex.: mapeadores que dependem de recursos locais do Windows).

Perguntas em aberto para vocês responderem
- Host Windows de destino e IP/porta (para a API configurar LowCode:RunnerUrl)?
- Qual mecanismo de autenticação entre API e runner?
- Há plano de eliminar o runner proprietário (#581), reimplementando a lógica em .NET portável?
```
