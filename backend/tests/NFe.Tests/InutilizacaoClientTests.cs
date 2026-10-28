using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;
using NFe.Sefaz.Xml;

namespace NFe.Tests;

public class InutilizacaoClientTests
{
    private const string PortalFiscal = "http://www.portalfiscal.inf.br/nfe";

    private static InutilizacaoClient.DadosInutilizacao Dados(
        string just = "Quebra de sequencia por falha no sistema emissor.",
        int serie = 1,
        long ini = 105,
        long fim = 110) => new(
        Uf: "RS",
        Ambiente: TipoAmbiente.Homologacao,
        CodigoUf: 43,
        Ano: 2026,
        Cnpj: "12.345.678/0001-95",
        Modelo: ModeloDocumento.NFe,
        Serie: serie,
        NumeroInicial: ini,
        NumeroFinal: fim,
        Justificativa: just);

    private static XElement RetInut(string corpoInterno)
        => XElement.Parse($"""<retInutNFe xmlns="{PortalFiscal}" versao="4.00"><infInut>{corpoInterno}</infInut></retInutNFe>""");

    // ---- Montagem do Id -------------------------------------------------

    [Fact]
    public void MontarId_SegueLayoutOficial_43Caracteres()
    {
        // ID + cUF(2) + ano(2) + CNPJ(14) + mod(2) + serie(3) + nNFIni(9) + nNFFin(9)
        var id = InutilizacaoClient.MontarId(
            cUf: 43, ano: 2026, cnpj: "12345678000195",
            modelo: ModeloDocumento.NFe, serie: 1, ini: 105, fin: 110);

        Assert.Equal("ID4326123456780001955500100000010500000011", id.Replace(" ", ""));
        Assert.Equal(43, id.Length);
        Assert.StartsWith("ID", id);
    }

    // ---- Montagem do inutNFe -------------------------------------------

    [Fact]
    public void Montar_GeraInfInutComCamposObrigatorios()
    {
        var (xml, id) = InutilizacaoClient.Montar(Dados());
        var inut = XElement.Parse(xml);
        var inf = inut.Descendants().First(e => e.Name.LocalName == "infInut");

        Assert.Equal("inutNFe", inut.Name.LocalName);
        Assert.Equal("4.00", inut.Attribute("versao")!.Value);
        Assert.Equal(id, inf.Attribute("Id")!.Value);

        string V(string n) => inf.Elements().First(e => e.Name.LocalName == n).Value;
        Assert.Equal("2", V("tpAmb")); // Homologacao
        Assert.Equal("INUTILIZAR", V("xServ"));
        Assert.Equal("43", V("cUF"));
        Assert.Equal("26", V("ano")); // 2 digitos
        Assert.Equal("12345678000195", V("CNPJ"));
        Assert.Equal("55", V("mod"));
        Assert.Equal("1", V("serie"));
        Assert.Equal("105", V("nNFIni"));
        Assert.Equal("110", V("nNFFin"));
        Assert.Contains("Quebra de sequencia", V("xJust"));
    }

    [Fact]
    public void Montar_ProducaoUsaTpAmb1()
    {
        var (xml, _) = InutilizacaoClient.Montar(Dados() with { Ambiente = TipoAmbiente.Producao });
        var inf = XElement.Parse(xml).Descendants().First(e => e.Name.LocalName == "infInut");
        Assert.Equal("1", inf.Elements().First(e => e.Name.LocalName == "tpAmb").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("curta")] // < 15
    public void Montar_JustificativaInvalida_Lanca(string just)
    {
        Assert.Throws<ArgumentException>(() => InutilizacaoClient.Montar(Dados(just)));
    }

    [Fact]
    public void Montar_FaixaInvertida_Lanca()
    {
        Assert.Throws<ArgumentException>(() =>
            InutilizacaoClient.Montar(Dados(ini: 110, fim: 105)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void Montar_SerieForaDoIntervalo_Lanca(int serie)
    {
        Assert.Throws<ArgumentException>(() => InutilizacaoClient.Montar(Dados(serie: serie)));
    }

    // ---- Assinatura + round-trip ---------------------------------------

    [Fact]
    public void InutAssinado_PreservaSignatureIrmaDeInfInut()
    {
        var (xml, id) = InutilizacaoClient.Montar(Dados());

        var pfx = CertificadoFalso.CriarPfx("s3nha", cnpj: "12345678000195");
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, "s3nha");

        var assinado = new AssinadorXml().Assinar(xml, id, cert, nomeTagAssinatura: "inutNFe");
        Assert.True(new AssinadorXml().Verificar(assinado));

        var inut = XElement.Parse(assinado);
        Assert.Equal("inutNFe", inut.Name.LocalName);
        // Signature deve ser filha direta de <inutNFe>, irma de infInut.
        Assert.Contains(inut.Elements(), e => e.Name.LocalName == "infInut");
        Assert.Contains(inut.Elements(), e => e.Name.LocalName == "Signature");
    }

    // ---- Interpretacao do retInutNFe ------------------------------------

    [Fact]
    public void Interpretar_Homologado102_ExtraiProtocoloEData()
    {
        var ret = RetInut(
            """
            <tpAmb>2</tpAmb>
            <verAplic>RS20250101</verAplic>
            <cStat>102</cStat>
            <xMotivo>Inutilizacao de numero homologado</xMotivo>
            <cUF>43</cUF>
            <ano>26</ano>
            <CNPJ>12345678000195</CNPJ>
            <mod>55</mod>
            <serie>1</serie>
            <nNFIni>105</nNFIni>
            <nNFFin>110</nNFFin>
            <dhRecbto>2026-08-03T14:30:05-03:00</dhRecbto>
            <nProt>143260000098765</nProt>
            """);

        var r = InutilizacaoClient.InterpretarRetorno(ret);

        Assert.Equal("102", r.CodigoStatus);
        Assert.Equal("143260000098765", r.Protocolo);
        Assert.Equal(new DateTime(2026, 8, 3, 17, 30, 5, DateTimeKind.Utc), r.DataRegistro);
    }

    [Fact]
    public void Interpretar_Rejeitado_NaoHomologa()
    {
        var ret = RetInut(
            """
            <tpAmb>2</tpAmb>
            <cStat>241</cStat>
            <xMotivo>Rejeicao: Um numero da faixa ja foi utilizado</xMotivo>
            """);

        var r = InutilizacaoClient.InterpretarRetorno(ret);
        Assert.Equal("241", r.CodigoStatus);
        Assert.Null(r.Protocolo);
    }

    [Fact]
    public void Interpretar_SemCStat_Lanca()
    {
        Assert.Throws<SefazSoapException>(() =>
            InutilizacaoClient.InterpretarRetorno(RetInut("<tpAmb>2</tpAmb>")));
    }

    // ---- Distribuicao (procInutNFe) -------------------------------------

    [Fact]
    public void MontarDistribuicao_GeraProcComInutERet_NaOrdem()
    {
        var (xml, id) = InutilizacaoClient.Montar(Dados());
        var pfx = CertificadoFalso.CriarPfx("s3nha", cnpj: "12345678000195");
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, "s3nha");
        var assinado = new AssinadorXml().Assinar(xml, id, cert, nomeTagAssinatura: "inutNFe");

        var ret = RetInut("<cStat>102</cStat><nProt>143260000098765</nProt>").ToString();
        var proc = XElement.Parse(InutilizacaoClient.MontarDistribuicao(assinado, ret));

        Assert.Equal("ProcInutNFe", proc.Name.LocalName);
        Assert.Equal(
            new[] { "inutNFe", "retInutNFe" },
            proc.Elements().Select(e => e.Name.LocalName).ToArray());
    }
}
