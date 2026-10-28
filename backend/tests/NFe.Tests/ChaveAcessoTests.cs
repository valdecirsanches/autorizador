using NFe.Domain;

namespace NFe.Tests;

public class ChaveAcessoTests
{
    /// <summary>
    /// Vetores dourados derivados de primeiros principios com o algoritmo do MOC
    /// 4.00 (modulo 11, pesos ciclicos 2..9 da direita para a esquerda). O DV foi
    /// calculado por uma implementacao independente, nao copiado de terceiros.
    /// </summary>
    [Theory]
    [InlineData("3524081234567800019555001000000001100000015", 4)]
    [InlineData("4324019320976500010355002000012345112345678", 0)]
    [InlineData("3123120062390400017355000000000010100000000", 3)]
    public void CalcularDigitoVerificador_ConfereComVetorDourado(string base43, int dvEsperado)
    {
        Assert.Equal(dvEsperado, ChaveAcesso.CalcularDigitoVerificador(base43));
    }

    /// <summary>
    /// Resto 0 ou 1 da divisao por 11 sempre produz DV 0. Um DV calculado como
    /// "11 - resto" nesses casos daria 11 ou 10, valores invalidos de um digito.
    /// </summary>
    [Theory]
    [InlineData("0000000000000000000000000000000000000000000")] // soma 0, resto 0
    [InlineData("0000000000000000000000000000000000000000006")] // resto 1
    public void CalcularDigitoVerificador_Resto0Ou1_RetornaZero(string base43)
    {
        Assert.Equal(0, ChaveAcesso.CalcularDigitoVerificador(base43));
    }

    [Fact]
    public void Compor_ProduzChaveDe44Digitos_ComDvCorreto()
    {
        var chave = ChaveAcesso.Compor(
            uf: "SP", ano: 2024, mes: 8, cnpj: "12.345.678/0001-95",
            modelo: ModeloDocumento.NFe, serie: 1, numero: 1,
            tipoEmissao: TipoEmissao.Normal, codigoNumerico: "00000015");

        Assert.Equal(44, chave.Length);
        Assert.Equal("35240812345678000195550010000000011000000154", chave);
        Assert.True(ChaveAcesso.Valida(chave));
    }

    [Fact]
    public void Compor_UsaCodigoIbgeDaUf_NosDoisPrimeirosDigitos()
    {
        var chave = ChaveAcesso.Compor(
            "RS", 2024, 1, "93209765000103", ModeloDocumento.NFe, 2, 12345,
            TipoEmissao.Normal, "12345678");

        Assert.StartsWith("43", chave); // RS = 43
        Assert.Equal("43240193209765000103550020000123451123456780", chave);
    }

    [Fact]
    public void Compor_AceitaAnoDe2Ou4Digitos()
    {
        var a = ChaveAcesso.Compor("SP", 2024, 8, "12345678000195",
            ModeloDocumento.NFe, 1, 1, TipoEmissao.Normal, "00000015");
        var b = ChaveAcesso.Compor("SP", 24, 8, "12345678000195",
            ModeloDocumento.NFe, 1, 1, TipoEmissao.Normal, "00000015");

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("12345678000195", 14)] // ja limpo
    [InlineData("12.345.678/0001-95", 14)] // com mascara
    public void Compor_RemoveMascaraDoCnpj(string cnpj, int _)
    {
        var chave = ChaveAcesso.Compor("SP", 2024, 8, cnpj,
            ModeloDocumento.NFe, 1, 1, TipoEmissao.Normal, "00000015");

        Assert.Equal("12345678000195", chave.Substring(6, 14));
    }

    [Fact]
    public void Valida_ChaveComDvAdulterado_RetornaFalse()
    {
        var chave = "35240812345678000195550010000000011000000154";
        var adulterada = chave[..43] + (chave[43] == '0' ? '1' : '0');

        Assert.True(ChaveAcesso.Valida(chave));
        Assert.False(ChaveAcesso.Valida(adulterada));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("3524081234567800019555001000000001100000015X")] // contem letra
    public void Valida_EntradaMalformada_RetornaFalse(string? chave)
    {
        Assert.False(ChaveAcesso.Valida(chave));
    }

    [Theory]
    [InlineData(0)]     // numero minimo e 1
    [InlineData(1_000_000_000)] // acima do maximo de 9 digitos
    public void Compor_NumeroForaDoIntervalo_Lanca(long numero)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChaveAcesso.Compor(
            "SP", 2024, 8, "12345678000195", ModeloDocumento.NFe, 1, numero,
            TipoEmissao.Normal, "00000015"));
    }

    [Fact]
    public void Compor_CnpjInvalido_Lanca()
    {
        Assert.Throws<ArgumentException>(() => ChaveAcesso.Compor(
            "SP", 2024, 8, "123", ModeloDocumento.NFe, 1, 1,
            TipoEmissao.Normal, "00000015"));
    }

    [Fact]
    public void Compor_CodigoNumericoComTamanhoErrado_Lanca()
    {
        Assert.Throws<ArgumentException>(() => ChaveAcesso.Compor(
            "SP", 2024, 8, "12345678000195", ModeloDocumento.NFe, 1, 1,
            TipoEmissao.Normal, "123"));
    }

    [Fact]
    public void Compor_UfDesconhecida_Lanca()
    {
        Assert.Throws<ArgumentException>(() => ChaveAcesso.Compor(
            "ZZ", 2024, 8, "12345678000195", ModeloDocumento.NFe, 1, 1,
            TipoEmissao.Normal, "00000015"));
    }

    [Fact]
    public void ParaId_PrefixaComNFe()
    {
        var chave = "35240812345678000195550010000000011000000154";
        Assert.Equal("NFe" + chave, ChaveAcesso.ParaId(chave));
    }

    [Theory]
    [InlineData("SP", 35)]
    [InlineData("RS", 43)]
    [InlineData("MG", 31)]
    [InlineData("AM", 13)]
    public void CodigoUf_ConverteSiglaParaCodigoIbge(string uf, int esperado)
    {
        Assert.Equal(esperado, CodigoUf.Codigo(uf));
    }

    [Fact]
    public void CodigoUf_SiglaDoMunicipio_UsaDoisPrimeirosDigitos()
    {
        // 3550308 = Sao Paulo capital, UF 35 = SP.
        Assert.Equal("SP", CodigoUf.SiglaDoMunicipio("3550308"));
    }
}
