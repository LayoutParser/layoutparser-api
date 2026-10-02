using Microsoft.Extensions.Configuration;

namespace LayoutParserApi.Services.Storage
{
    /// <summary>
    /// Ponto ÚNICO de resolução dos diretórios de dados da API (tcl, xsl, Examples, MLData...).
    /// <para>
    /// Raiz de dados (<see cref="DataRoot"/>), por ordem: (1) <c>Storage:DataRoot</c>; (2) no Linux,
    /// <c>/var/lib/layoutparser-api</c> SE já existir (é onde a produção mantém os dados); (3)
    /// <c>{AppContext.BaseDirectory}/data</c>. O passo 2 só vale com a pasta existente para que
    /// dev/CI não tentem escrever em /var/lib sem permissão. Em produção o diretório da aplicação
    /// (/opt/layoutparser-api) é SOMENTE LEITURA para o serviço — por isso nenhum default de escrita
    /// cai mais debaixo de <c>AppContext.BaseDirectory</c> quando existe a raiz de produção.
    /// </para>
    /// <para>
    /// Cada subcaminho respeita a chave legada (ex.: <c>TransformationPipeline:TclPath</c>) quando
    /// definida — a chave legada SEMPRE vence; só na ausência dela vale o derivado de DataRoot.
    /// Nomes de subpastas são minúsculos/exatos como na produção (case-sensitive no Linux):
    /// <c>tcl</c>, <c>xsl</c>, <c>Examples</c>, <c>LearningModels</c>, <c>ExpectedOutputs</c>, <c>MLData</c>, <c>ai</c>.
    /// </para>
    /// </summary>
    public class StoragePaths
    {
        /// <summary>Raiz de dados padrão em produção Linux.</summary>
        public const string LinuxProductionDataRoot = "/var/lib/layoutparser-api";

        private readonly IConfiguration _configuration;

        public StoragePaths(IConfiguration configuration)
        {
            _configuration = configuration;
            DataRoot = ResolveDataRoot(configuration);
        }

        public string DataRoot { get; }

        public string Tcl => Resolve("TransformationPipeline:TclPath", "tcl");
        public string Xsl => Resolve("TransformationPipeline:XslPath", "xsl");
        public string Examples => Resolve("TransformationPipeline:ExamplesPath", "Examples");
        public string ExamplesTcl => Resolve("TransformationPipeline:ExamplesTclPath", Path.Combine(Examples, "tcl"));
        public string ExamplesXsl => Resolve("TransformationPipeline:ExamplesXslPath", Path.Combine(Examples, "xsl"));
        public string LearningModels => Resolve("TransformationPipeline:LearningModelsPath", "LearningModels");
        public string ExpectedOutputs => Resolve("TransformationPipeline:ExpectedOutputsPath", "ExpectedOutputs");
        /// <summary>Pasta de exemplos lida pelo serviço de testes automatizados (<c>Examples:Path</c>).</summary>
        public string TestExamples => Resolve("Examples:Path", "Examples");
        /// <summary>Pasta de exemplos do aprendizado por exemplo (<c>TransformationPipeline:LearningExamplesPath</c>).</summary>
        public string LearningExamples => Resolve("TransformationPipeline:LearningExamplesPath", "Examples");
        public string Xsd => Resolve("XsdValidation:BasePath", "xsd");
        public string Pdf => Resolve("XsdValidation:PdfBasePath", "pdf");
        public string TransformationRules => Resolve("TransformationRules:Path", "TransformationRules");

        /// <summary>Raiz de MLData (sem chave legada própria).</summary>
        public string MlData => Path.Combine(DataRoot, "MLData");
        public string LearningData => Resolve("ML:LearningDataPath", Path.Combine("MLData", "DocumentPatterns"));
        public string TrainingSamples => Resolve("ML:TrainingSamplesPath", Path.Combine("MLData", "TrainingSamples"));
        public string FiscalAnalyses => Resolve("ML:FiscalAnalysesPath", Path.Combine("MLData", "FiscalAnalyses"));
        public string FiscalMappingPackages => Resolve("ML:FiscalMappingPackagesPath", Path.Combine("MLData", "FiscalMappingPackages"));
        public string LowCodeTransformations => Resolve("ML:LowCodeTransformationsPath", Path.Combine("MLData", "LowCodeTransformations"));
        /// <summary>Default do store de tickets de candidatos IA (a option <c>StorePath</c>, se houver, vence no consumidor).</summary>
        public string AiCandidates => Path.Combine(MlData, "AiTransformationCandidates");
        public string AiTrainingData => Resolve("XslSynth:TrainingDataPath", Path.Combine("ai", "XslSynth", "training-data"));

        private string Resolve(string legacyKey, string relativeToDataRoot)
        {
            var configured = _configuration[legacyKey];
            return !string.IsNullOrWhiteSpace(configured) ? configured : Path.Combine(DataRoot, relativeToDataRoot);
        }

        private static string ResolveDataRoot(IConfiguration configuration)
        {
            var configured = configuration["Storage:DataRoot"];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            if (OperatingSystem.IsLinux() && Directory.Exists(LinuxProductionDataRoot))
                return LinuxProductionDataRoot;
            return Path.Combine(AppContext.BaseDirectory, "data");
        }
    }
}
