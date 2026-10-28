namespace NFe.Domain;

/// <summary>
/// Codigos IBGE das unidades federativas, usados no campo cUF da chave de
/// acesso e do XML. Sao os dois primeiros digitos do codigo de municipio IBGE.
/// </summary>
public static class CodigoUf
{
    private static readonly Dictionary<string, int> PorSigla = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RO"] = 11, ["AC"] = 12, ["AM"] = 13, ["RR"] = 14, ["PA"] = 15,
        ["AP"] = 16, ["TO"] = 17, ["MA"] = 21, ["PI"] = 22, ["CE"] = 23,
        ["RN"] = 24, ["PB"] = 25, ["PE"] = 26, ["AL"] = 27, ["SE"] = 28,
        ["BA"] = 29, ["MG"] = 31, ["ES"] = 32, ["RJ"] = 33, ["SP"] = 35,
        ["PR"] = 41, ["SC"] = 42, ["RS"] = 43, ["MS"] = 50, ["MT"] = 51,
        ["GO"] = 52, ["DF"] = 53,

        // Codigo especial para operacoes com o exterior (EX).
        ["EX"] = 99
    };

    private static readonly Dictionary<int, string> PorCodigo =
        PorSigla.ToDictionary(kv => kv.Value, kv => kv.Key.ToUpperInvariant());

    /// <summary>Converte a sigla da UF no codigo IBGE. Lanca se desconhecida.</summary>
    public static int Codigo(string uf)
    {
        if (string.IsNullOrWhiteSpace(uf))
            throw new ArgumentException("UF nao informada.", nameof(uf));

        if (PorSigla.TryGetValue(uf.Trim(), out var codigo))
            return codigo;

        throw new ArgumentException($"UF desconhecida: '{uf}'.", nameof(uf));
    }

    /// <summary>Converte o codigo IBGE na sigla da UF. Lanca se desconhecido.</summary>
    public static string Sigla(int codigo)
    {
        if (PorCodigo.TryGetValue(codigo, out var sigla))
            return sigla;

        throw new ArgumentException($"Codigo de UF desconhecido: {codigo}.", nameof(codigo));
    }

    public static bool SiglaValida(string uf) =>
        !string.IsNullOrWhiteSpace(uf) && PorSigla.ContainsKey(uf.Trim());

    /// <summary>
    /// Deriva a UF a partir do codigo de municipio IBGE (7 digitos), cujos dois
    /// primeiros digitos sao o codigo da UF.
    /// </summary>
    public static string SiglaDoMunicipio(string codigoMunicipioIbge)
    {
        if (string.IsNullOrWhiteSpace(codigoMunicipioIbge) || codigoMunicipioIbge.Length < 2)
            throw new ArgumentException(
                "Codigo de municipio IBGE invalido.", nameof(codigoMunicipioIbge));

        return Sigla(int.Parse(codigoMunicipioIbge[..2]));
    }
}
