using NFe.Infrastructure.Servicos;
using Xunit;

namespace NFe.Tests;

public sealed class LocalizadorXmlPastaTests : IDisposable
{
    private const string Chave = "35261003707154000106550010000711191017882660";
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "xmlpasta-" + Guid.NewGuid().ToString("N"));

    public LocalizadorXmlPastaTests() => Directory.CreateDirectory(_raiz);

    public void Dispose()
    {
        try { Directory.Delete(_raiz, true); } catch (IOException) { }
    }

    private string Gravar(string relativo, string conteudo)
    {
        var caminho = Path.Combine(_raiz, relativo);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }

    private static string NFe(string chave) =>
        $"<NFe xmlns=\"http://www.portalfiscal.inf.br/nfe\"><infNFe Id=\"NFe{chave}\" versao=\"4.00\"/></NFe>";

    [Fact]
    public void Encontra_pelo_nome_do_arquivo_na_pasta_do_mes_da_chave()
    {
        Gravar($"202610/NFe/{Chave}-nfe.xml", NFe(Chave));

        var achado = LocalizadorXmlPasta.Localizar(_raiz, Chave);

        Assert.NotNull(achado);
        Assert.Contains(Chave, achado!.Xml);
    }

    [Fact]
    public void Encontra_quando_a_pasta_informada_e_a_do_cnpj_com_subpasta_NFe()
    {
        Gravar($"NFe/202610/NFe/{Chave}.xml", NFe(Chave));

        Assert.NotNull(LocalizadorXmlPasta.Localizar(Path.Combine(_raiz), Chave));
    }

    [Fact]
    public void Nao_le_a_subpasta_de_Evento()
    {
        Gravar($"202610/Evento/{Chave}-evento.xml", NFe(Chave));

        Assert.Null(LocalizadorXmlPasta.Localizar(_raiz, Chave));
    }

    [Fact]
    public void Chave_invalida_ou_pasta_relativa_nao_localiza()
    {
        Assert.Null(LocalizadorXmlPasta.Localizar(_raiz, "123"));
        Assert.NotNull(LocalizadorXmlPasta.Validar("relativa/pasta"));
        Assert.NotNull(LocalizadorXmlPasta.Validar(""));
        Assert.Null(LocalizadorXmlPasta.Validar(_raiz));
    }

    [Fact]
    public void Diagnostico_informa_meses_e_xmls()
    {
        Gravar($"202609/NFe/a.xml", NFe(Chave));
        Gravar($"202610/NFe/b.xml", NFe(Chave));
        Gravar($"202610/NFe/c.xml", NFe(Chave));

        var d = LocalizadorXmlPasta.Diagnosticar(_raiz);

        Assert.True(d.Existe);
        Assert.Equal(2, d.Meses);
        Assert.Equal("202610", d.UltimoMes);
        Assert.Equal(2, d.XmlsNoUltimoMes);
    }

    [Fact]
    public void Diagnostico_de_pasta_inexistente()
    {
        var d = LocalizadorXmlPasta.Diagnosticar(Path.Combine(_raiz, "nao-existe"));

        Assert.False(d.Existe);
    }
}
