namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Regras do Sysmiddle que o gerador (LLM) precisa conhecer — texto único, reaproveitável em
    /// qualquer prompt de geração de TCL/XSLT. Fonte e justificativa:
    /// <c>docs/architecture/regras-sysmiddle-para-geracao-tcl.md</c> (caso IVG, 2026-09-28).
    /// </summary>
    public static class SysmiddleGenerationRules
    {
        public const string PromptSection =
            "REGRAS DO SYSMIDDLE (obrigatórias):\n" +
            "1. O parse do documento contra o layout acontece ANTES das regras do mapper. Se sobrar conteúdo " +
            "no parse (\"Documento com conteúdo não definido\"), as regras leem campos vazios e as tags derivadas " +
            "somem sem erro de regra. Nunca conclua que uma tag ausente é falha do mapper sem antes considerar o parse.\n" +
            "2. O parser é sequencial: lê as linhas na ordem de Sequence do layout, sem voltar atrás. Registro fora " +
            "da ordem faz o trailer (InitialValue vazio) engolir o restante. A ordem correta é a da planilha do " +
            "cliente; a correção de um documento fora de ordem é na origem, nunca mover blocos do layout.\n" +
            "3. Em layouts MQSeries de 600 caracteres, o campo Sequencia consome o prefixo de 6 dígitos do próximo " +
            "registro; 606 não é estouro.\n" +
            "4. Referências I.LINHAxxx/Campo devem usar a grafia EXATA do layout (sensível a caixa) e a LINHA " +
            "correta. Se não houver evidência do campo no layout, registre uma pergunta aberta em vez de inventar.\n" +
            "5. Regras com o mesmo nome podem ter alvos diferentes; compare pelo alvo. Não copie condições de " +
            "outro grupo sem trocar o campo testado.\n" +
            "6. Se a planilha for ambígua quanto à ordem ou à posição de um bloco, devolva confidence \"low\" com " +
            "questions preenchido.";
    }
}
