namespace LayoutParserApi.Services.Transformation.LowCode
{
    public class LowCodeRunnerOptions
    {
        public string RunnerPath { get; set; } = "";

        // ✅ Runner como serviço HTTP (contrato v1, issue #641). Preenchida => o caminho HTTP substitui
        // o .exe (RunnerPath passa a ser ignorado). Vazia => comportamento legado com RunnerPath.
        // Exemplo: http://lowcoderunner.local:5230
        public string BaseUrl { get; set; } = "";

        // Timeout do HttpClient do runner: folga sobre os 180s de execução do servidor.
        public int HttpTimeoutSeconds { get; set; } = 200;
        public string SysmiddleDir { get; set; } = "";
        public string GlobalFolder { get; set; } = "";
        // ✅ Identificador do PROJETO Sysmiddle (o mesmo <PackageMappers> do config.xml da instância).
        // Passou a ser OBRIGATÓRIO em 2026-08-10: o runner low-code deixou de depender do
        // appConnector.Client.Core e, com ele, do fallback
        // ConnectorApplicationManager.Instance.GetServerPackage(). Vazio agora faz o runner sair com
        // exit=9 (RunnerExitCodes.PackageNotConfigured) e mensagem explícita — de propósito, para
        // não degradar em "mapeador não encontrado" longe da causa.
        //
        // ⚠️ Mesmo aviso dos campos abaixo: o deploy PRESERVA o appsettings.json do destino
        // (ci-dev.yml/deploy.yml), então este valor NÃO chega sozinho ao servidor. Em produção,
        // configure a variável de ambiente LowCode__Package (ação de @lp-devops).
        public string Package { get; set; } = "";
        public string? DefaultMapperName { get; set; }

        // Seleção de mappers no banco (tbMapper)
        public int ProjectId { get; set; } = 2;
        public List<string> AllowedPackageGuids { get; set; } = new();

        // ✅ Seleção multi-candidato (LowCode-auto): quando há N>1 mapeadores genuinamente plausíveis
        // (MapperGuid distintos) para o mesmo layoutGuid, capamos em top-N pelos mesmos critérios de
        // prioridade já existentes (input match > target match > mais recente) antes de rodar em paralelo.
        public int MultiCandidateTopN { get; set; } = 4;

        // ✅ Timeout por invocação do runner (processo externo x86). Cobre o ciclo de vida inteiro
        // do processo (start + leitura de stdout/stderr + exit) — não só a espera de exit — porque
        // uma leitura de stream travada também precisa disparar o kill.
        //
        // ⚠️ Default corrigido de 15s para 180s em 2026-08-10. O 15s vinha de uma premissa FALSA
        // ("bootstrap ~0,5-1s, logo 15s é folga generosa"): media-se o bootstrap, não a transformação.
        // A medição A/B por fase (@lp-backend-dev, mesma Bin, commit-base vs. novo) desmentiu:
        //     Bootstrap()                          0,7-1,5s   (removido)
        //     init InstanceFactory/APIManager      12-38s     (permanece — dispara no 1º APIManager.Instance)
        //     ExecuteMapper                        38-73s     (permanece — é o motor)
        //     transformação completa               48-137s
        // Com 15s o processo era morto no meio SEMPRE: nenhuma transformação chegava ao fim.
        // 180s = ~30% de folga sobre os 137s do pior caso medido, numa faixa que já varia 3x na
        // MESMA build. Não existe valor "justo" aqui: subdimensionar mata trabalho bom (caro e
        // silencioso); superdimensionar só atrasa a detecção de travamento (barato e visível).
        //
        // ⚠️ Este default é a ÚLTIMA LINHA DE DEFESA, não o canal principal. O deploy PRESERVA o
        // appsettings.json do destino, então o valor efetivo em produção vem da variável de ambiente
        // LowCode__RunnerTimeoutSeconds (ci-dev.yml/deploy.yml). O default só vale quando a chave
        // não existe em lugar nenhum — e é justamente aí que ele precisava parar de ser inviável.
        public int RunnerTimeoutSeconds { get; set; } = 180;

        // ✅ Limite de concorrência do runner NO PROCESSO INTEIRO da API (não por request/documento):
        // se dois uploads multi-candidato chegarem juntos, o total de processos do runner rodando ao
        // mesmo tempo ainda respeita este teto. Default 2: bootstrap caro (~0.5-1s) e host FiatMQ
        // sensível a execução concorrente — 2-3 é o intervalo seguro sugerido, escolhemos o piso.
        public int MaxConcurrentRunners { get; set; } = 2;

        // ✅ Teto para a entrega SÍNCRONA das transformações candidatas no response do upload/parse
        // (ParseController). Deliberadamente bem menor que RunnerTimeoutSeconds: como os candidatos
        // rodam em paralelo (Task.WhenAll), o caso comum (poucas variantes, runner saudável) completa
        // bem antes disso; se estourar, cai para processamento em background sem bloquear o parse.
        public int SyncDeliveryTimeoutSeconds { get; set; } = 6;

        // ✅ Teto ABSOLUTO da request HTTP de POST /api/transformation-execution/execute-candidates.
        // Existe porque "quanto o trabalho pode demorar" e "quanto o cliente HTTP espera" são duas
        // perguntas diferentes, e o endpoint respondia as duas com a mesma resposta: o budget era
        // RunnerTimeoutSeconds * MaxConcurrentRunners, que com o timeout corrigido (180s) virou 360s
        // — seis minutos segurando o cliente. Pior: a fórmula CRESCIA com MaxConcurrentRunners, ou
        // seja, aumentar a concorrência aumentava a espera máxima, exatamente ao contrário do efeito
        // real de ter mais slots.
        //
        // O budget efetivo é min(ondas de fila, este teto) — ver ExecuteTransformationCandidates.
        // Default 90s: acomoda UMA transformação típica inteira (48-137s medidos, mediana bem abaixo
        // do topo) sem prometer o pior caso ao cliente. Estourar não perde trabalho: o pathway
        // sysmiddle persiste em disco dentro da própria chamada e responde depois pelo ticket.
        // Mesmo padrão de SyncDeliveryTimeoutSeconds no ParseController — teto de entrega bem menor
        // que o teto do motor. Override em produção só por LowCode__CandidatesRequestTimeoutSeconds.
        public int CandidatesRequestTimeoutSeconds { get; set; } = 90;

        // ✅ Teto para entregar o XML da transformação INLINE no payload do parse. Acima disso o
        // campo outputXml é omitido e o front busca pelo endpoint de corpo
        // (GET /api/parse/transformations/{ticket}/candidates/{mapperGuid}).
        // Default 262144 (256 KB): medido em material real, input de 35 KB gera saída de ~4,2 KB —
        // o caso comum cabe inline com folga enorme e o teto só protege do outlier.
        //
        // ⚠️ O deploy PRESERVA o appsettings.json do destino (ci-dev.yml/deploy.yml), então config
        // nova adicionada ao repo NÃO chega ao servidor: este default precisa ser seguro sozinho.
        // Override em produção só por variável de ambiente LowCode__InlineXmlMaxChars.
        public int InlineXmlMaxChars { get; set; } = 262144;

        // ✅ Janela de frescor do cache-first de transformações (mesmo documento + mesmo layout
        // não roda o runner de novo). Vale para o Redis (TTL da chave) e para a decisão de PULAR o
        // runner com base no índice em disco — os artefatos em disco não expiram (corpus de treino).
        // Default 2h: cobre com folga o fluxo real (parse → clique em "Gerar Transformação XML",
        // segundos depois) sem congelar para sempre um resultado de um mapper que pode ter mudado.
        // Mesmo aviso do campo acima: override só por LowCode__TransformationCacheTtlHours.
        public int TransformationCacheTtlHours { get; set; } = 2;

    }
}


