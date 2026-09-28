using System.Xml.Linq;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>Resultado da simulação do parser posicional do Sysmiddle sobre um documento.</summary>
    /// <param name="Consumed">Documento consumido por inteiro (sem "conteúdo não definido").</param>
    /// <param name="LeftoverOffset">Offset onde a leitura parou (só se não consumiu tudo).</param>
    /// <param name="LeftoverPreview">Início do que sobrou (máx. 60 chars).</param>
    /// <param name="LastMatchedLine">Última linha do layout que casou antes de parar.</param>
    public sealed record SysmiddleParseSimulation(bool Consumed, int LeftoverOffset, string LeftoverPreview, string? LastMatchedLine);

    /// <summary>
    /// Réplica determinística do algoritmo do <c>SysMiddle.Map4Connect.Parser</c> (decompilado), para
    /// detectar ANTES de gerar/rodar o mapper o erro "Documento com conteúdo não definido".
    /// Regras do parser: elementos processados em ordem de <c>Sequence</c>, SEM retrocesso; linha com
    /// <c>InitialValue</c> casa se o buffer começa com ele; <c>InitialValue</c> vazio casa com qualquer
    /// coisa (ex.: trailer 999999 engole o registro fora de ordem); linha opcional que não casa é pulada;
    /// linha repete até <c>MaximumOccurrence</c>. O que sobra vira o alerta. Causa típica: registro
    /// enviado fora da ordem declarada no layout/planilha (caso IVG: bloco 098 no meio do item).
    /// Só cobre LineElementVO/FieldElementVO (layouts sem RepeaterGroup/Choice).
    /// </summary>
    public static class SysmiddleParseOrderChecker
    {
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

        private sealed class Node
        {
            public bool IsField;
            public string Name = "";
            public int Seq;
            public int Len;
            public string Init = "";
            public int Max = 1;
            public List<Node> Children = new();
        }

        private sealed class State
        {
            public State(string buffer) { Buffer = buffer; }
            public string Buffer;
            public int Pos;
            public string? Last;
        }

        public static SysmiddleParseSimulation Simulate(string layoutXml, string document)
        {
            var doc = XDocument.Parse(layoutXml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""));
            var root = doc.Root ?? throw new ArgumentException("Layout sem raiz.", nameof(layoutXml));
            var top = Build(root.Elements().FirstOrDefault(e => e.Name.LocalName == "Elements"));

            var state = new State(document);
            Run(top, state);

            var leftover = state.Buffer.Length - state.Pos;
            return leftover == 0
                ? new SysmiddleParseSimulation(true, document.Length, "", state.Last)
                : new SysmiddleParseSimulation(false, state.Pos, state.Buffer.Substring(state.Pos, Math.Min(60, leftover)), state.Last);
        }

        private static List<Node> Build(XElement? elements)
        {
            var list = new List<Node>();
            if (elements == null) return list;

            foreach (var e in elements.Elements())
            {
                var type = (string?)e.Attribute(Xsi + "type");
                string Str(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value ?? "";
                int Int(string n, int d) => int.TryParse(Str(n), out var v) ? v : d;

                if (type == "FieldElementVO")
                {
                    list.Add(new Node { IsField = true, Name = Str("Name"), Seq = Int("Sequence", 0), Len = Int("LengthField", 0) });
                }
                else if (type == "LineElementVO")
                {
                    list.Add(new Node
                    {
                        Name = Str("Name"),
                        Seq = Int("Sequence", 0),
                        Init = Str("InitialValue"),
                        Max = Math.Max(Int("MaximumOccurrence", 1), 1),
                        Children = Build(e.Elements().FirstOrDefault(x => x.Name.LocalName == "Elements"))
                    });
                }
            }
            return list;
        }

        private static void Run(IEnumerable<Node> nodes, State s)
        {
            foreach (var n in nodes.OrderBy(x => x.Seq))
            {
                if (n.IsField)
                {
                    s.Pos = Math.Min(s.Pos + n.Len, s.Buffer.Length);
                    continue;
                }

                for (var count = 0; count < n.Max; count++)
                {
                    var rest = s.Buffer.Length - s.Pos;
                    var match = n.Init.Length == 0
                        ? rest > 0
                        : string.CompareOrdinal(s.Buffer, s.Pos, n.Init, 0, n.Init.Length) == 0;
                    if (!match) break;

                    s.Pos += n.Init.Length;
                    s.Last = n.Name;
                    Run(n.Children, s);
                }
            }
        }
    }
}
