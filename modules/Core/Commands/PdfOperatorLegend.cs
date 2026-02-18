using System;
using System.Collections.Generic;

namespace Obj.Commands
{
    internal static class PdfOperatorLegend
    {
        private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
        {
            ["BT"] = "inicio do objeto de texto",
            ["ET"] = "fim do objeto de texto",
            ["Tf"] = "define fonte e tamanho",
            ["Tm"] = "define a matriz de texto (posicao/orientacao)",
            ["Td"] = "move a posicao do texto (tx, ty)",
            ["TD"] = "move a posicao e define o leading",
            ["T*"] = "vai para a proxima linha",
            ["Tj"] = "mostra texto (string)",
            ["TJ"] = "mostra texto com ajustes (array; numeros + afastam, - aproximam)",
            ["'"] = "proxima linha + mostra texto",
            ["\""] = "seta espacamento + proxima linha + mostra texto",
            ["Tc"] = "espacamento entre caracteres",
            ["Tw"] = "espacamento entre palavras",
            ["Tz"] = "escala horizontal do texto",
            ["TL"] = "define leading (altura de linha)",
            ["Tr"] = "modo de renderizacao do texto",
            ["Ts"] = "text rise (eleva/abaixa texto)",
            ["rg"] = "define cor de preenchimento (RGB)",
            ["RG"] = "define cor de contorno (RGB)"
        };

        internal static string? Describe(string op)
        {
            return Descriptions.TryGetValue(op, out var desc) ? desc : null;
        }

        internal static string AppendDescription(string line, string op)
        {
            var desc = Describe(op);
            if (string.IsNullOrWhiteSpace(desc)) return line;
            return $"{line}  -> {desc}";
        }
    }
}
