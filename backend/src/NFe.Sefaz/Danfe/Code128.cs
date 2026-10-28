using System.Globalization;
using System.Text;

namespace NFe.Sefaz.Danfe;

/// <summary>
/// Gera o codigo de barras Code128-C da chave de acesso da NF-e (44 digitos)
/// como SVG. O DANFE exige a chave representada em Code128-C, conforme o Manual
/// de Orientacao do Contribuinte. Retorna SVG (vetorial, nitido em qualquer
/// escala) para ser embutido diretamente no PDF via QuestPDF.
/// </summary>
public static class Code128
{
    // Tabela canonica de padroes do Code128 (indices 0..106). Cada padrao e a
    // sequencia de larguras de modulos: barra, espaco, barra, espaco... O ultimo
    // (106) e o STOP, com 7 elementos (termina em barra).
    private static readonly string[] Patterns =
    {
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213",
        "221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
        "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211",
        "212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
        "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331",
        "231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214",
        "112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
        "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141",
        "214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
        "114131","311141","411131","211412","211214","211232","2331112"
    };

    private const int StartC = 105;
    private const int Stop = 106;

    /// <summary>
    /// Constroi o SVG do Code128-C para uma cadeia numerica. Caracteres nao
    /// numericos sao ignorados; comprimento impar recebe um zero a esquerda.
    /// </summary>
    public static string ToSvg(string valor, double height = 40, double moduleWidth = 1.0)
    {
        var digits = new string((valor ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'></svg>";
        if (digits.Length % 2 != 0) digits = "0" + digits;

        // Codigos: Start C, pares de digitos, checksum, Stop.
        var codes = new List<int> { StartC };
        for (var i = 0; i < digits.Length; i += 2)
            codes.Add(int.Parse(digits.Substring(i, 2), CultureInfo.InvariantCulture));

        long soma = StartC;
        for (var i = 1; i < codes.Count; i++) soma += (long)codes[i] * i;
        codes.Add((int)(soma % 103));
        codes.Add(Stop);

        // Largura total em modulos.
        double totalModulos = 0;
        foreach (var c in codes)
            foreach (var ch in Patterns[c])
                totalModulos += ch - '0';

        var width = totalModulos * moduleWidth;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{width:0.###}' height='{height:0.###}' viewBox='0 0 {width:0.###} {height:0.###}'>");

        double x = 0;
        var barra = true; // cada padrao comeca por barra
        foreach (var c in codes)
        {
            foreach (var ch in Patterns[c])
            {
                var w = (ch - '0') * moduleWidth;
                if (barra)
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<rect x='{x:0.###}' y='0' width='{w:0.###}' height='{height:0.###}' fill='black'/>");
                x += w;
                barra = !barra;
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }
}
