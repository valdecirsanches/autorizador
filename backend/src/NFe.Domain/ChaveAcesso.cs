using System.Text;

namespace NFe.Domain;

/// <summary>
/// Compoe, valida e interpreta a chave de acesso de 44 digitos da NF-e.
///
/// Layout (Manual de Orientacao do Contribuinte 4.00):
///   cUF(2) AAMM(4) CNPJ(14) mod(2) serie(3) nNF(9) tpEmis(1) cNF(8) cDV(1)
///   = 43 digitos de dados + 1 digito verificador.
/// </summary>
public static class ChaveAcesso
{
    /// <summary>
    /// Monta a chave completa (44 digitos) ja com o digito verificador.
    /// </summary>
    public static string Compor(
        string uf,
        int ano,
        int mes,
        string cnpj,
        ModeloDocumento modelo,
        int serie,
        long numero,
        TipoEmissao tipoEmissao,
        string codigoNumerico)
    {
        var cUF = CodigoUf.Codigo(uf);

        // Aceita ano com 2 ou 4 digitos; a chave usa apenas os 2 ultimos.
        var aa = ano % 100;
        if (mes is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(mes), mes, "Mes deve estar entre 1 e 12.");

        var cnpjLimpo = SomenteDigitos(cnpj);
        if (cnpjLimpo.Length != 14)
            throw new ArgumentException("CNPJ deve ter 14 digitos.", nameof(cnpj));

        if (serie is < 0 or > 999)
            throw new ArgumentOutOfRangeException(nameof(serie), serie, "Serie deve estar entre 0 e 999.");

        if (numero is < 1 or > 999_999_999)
            throw new ArgumentOutOfRangeException(
                nameof(numero), numero, "Numero deve estar entre 1 e 999999999.");

        var cNF = SomenteDigitos(codigoNumerico);
        if (cNF.Length != 8)
            throw new ArgumentException(
                "Codigo numerico (cNF) deve ter 8 digitos.", nameof(codigoNumerico));

        var sb = new StringBuilder(44);
        sb.Append(cUF.ToString("D2"));
        sb.Append(aa.ToString("D2"));
        sb.Append(mes.ToString("D2"));
        sb.Append(cnpjLimpo);
        sb.Append(((int)modelo).ToString("D2"));
        sb.Append(serie.ToString("D3"));
        sb.Append(numero.ToString("D9"));
        sb.Append(((int)tipoEmissao).ToString("D1"));
        sb.Append(cNF);

        var chave43 = sb.ToString();
        sb.Append(CalcularDigitoVerificador(chave43));

        return sb.ToString();
    }

    /// <summary>
    /// Digito verificador por modulo 11 sobre os 43 primeiros digitos. Pesos de
    /// 2 a 9 aplicados da direita para a esquerda, reiniciando ciclicamente.
    /// Resto 0 ou 1 produz DV 0.
    /// </summary>
    public static int CalcularDigitoVerificador(string chave43)
    {
        if (chave43 is null || chave43.Length != 43)
            throw new ArgumentException(
                "A base do calculo deve ter exatamente 43 digitos.", nameof(chave43));

        var soma = 0;
        var peso = 2;

        for (var i = chave43.Length - 1; i >= 0; i--)
        {
            var d = chave43[i];
            if (d is < '0' or > '9')
                throw new ArgumentException("A chave deve conter apenas digitos.", nameof(chave43));

            soma += (d - '0') * peso;
            peso = peso == 9 ? 2 : peso + 1;
        }

        var resto = soma % 11;
        return resto is 0 or 1 ? 0 : 11 - resto;
    }

    /// <summary>Valida os 44 digitos, incluindo a conferencia do DV.</summary>
    public static bool Valida(string? chave)
    {
        if (string.IsNullOrWhiteSpace(chave) || chave.Length != 44)
            return false;

        foreach (var c in chave)
            if (c is < '0' or > '9')
                return false;

        var dvEsperado = CalcularDigitoVerificador(chave[..43]);
        return dvEsperado == chave[43] - '0';
    }

    /// <summary>
    /// Formato "NFe" + 44 digitos usado como atributo Id do elemento infNFe.
    /// </summary>
    public static string ParaId(string chave44) => "NFe" + chave44;

    private static string SomenteDigitos(string? valor)
    {
        if (string.IsNullOrEmpty(valor)) return string.Empty;

        var sb = new StringBuilder(valor.Length);
        foreach (var c in valor)
            if (c is >= '0' and <= '9')
                sb.Append(c);

        return sb.ToString();
    }
}
