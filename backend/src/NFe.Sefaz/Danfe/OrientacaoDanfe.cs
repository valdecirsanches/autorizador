namespace NFe.Sefaz.Danfe;

/// <summary>Orientacao da pagina A4 do DANFE em PDF.</summary>
public enum OrientacaoDanfe
{
    Retrato = 0,
    Paisagem = 1
}

public static class OrientacaoDanfeParser
{
    /// <summary>
    /// Interpreta o texto vindo do app. Nulo/vazio resulta em retrato (padrao).
    /// Aceita "retrato"/"portrait" e "paisagem"/"landscape", sem diferenciar
    /// maiusculas. Retorna false para qualquer outro valor.
    /// </summary>
    public static bool TryParse(string? valor, out OrientacaoDanfe orientacao)
    {
        orientacao = OrientacaoDanfe.Retrato;
        if (string.IsNullOrWhiteSpace(valor))
            return true;

        switch (valor.Trim().ToLowerInvariant())
        {
            case "retrato":
            case "portrait":
                orientacao = OrientacaoDanfe.Retrato;
                return true;
            case "paisagem":
            case "landscape":
                orientacao = OrientacaoDanfe.Paisagem;
                return true;
            default:
                return false;
        }
    }
}
