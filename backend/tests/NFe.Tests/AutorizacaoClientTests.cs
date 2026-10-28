using System.Xml.Linq;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;

namespace NFe.Tests;

public class AutorizacaoClientTests
{
    private const string PortalFiscal = "http://www.portalfiscal.inf.br/nfe";

    // NF-e assinada minima usada para montar envio e distribuicao. Nao e um
    // documento completo; basta ter <NFe><infNFe Id="NFe..."> e <Signature>.
    private const string NFeAssinada =
        """
        <NFe xmlns="http://www.portalfiscal.inf.br/nfe"><infNFe versao="4.00" Id="NFe43250412345678000195550010000000011000000015"><ide><cUF>43</cUF></ide></infNFe><Signature xmlns="http://www.w3.org/2000/09/xmldsig#"><SignedInfo /></Signature></NFe>
        """;

    private static XElement RetEnvi(string corpoInterno)
        => XElement.Parse(
            $"""<retEnviNFe xmlns="{PortalFiscal}" versao="4.00">{corpoInterno}</retEnviNFe>""");

    // ---- Envio (enviNFe) ------------------------------------------------

    [Fact]
    public void MontarEnvio_UsaModoSincrono_E_EmbuteNFe()
    {
        var xml = AutorizacaoClient.MontarEnvio("240728123", NFeAssinada);
        var env = XElement.Parse(xml);

        Assert.Equal("enviNFe", env.Name.LocalName);
        Assert.Equal("4.00", env.Attribute("versao")!.Value);
        Assert.Equal("240728123", env.Elements().First(e => e.Name.LocalName == "idLote").Value);
        // indSinc=1 e o que faz o lote resolver na mesma requisicao.
        Assert.Equal("1", env.Elements().First(e => e.Name.LocalName == "indSinc").Value);
        Assert.Contains(env.Elements(), e => e.Name.LocalName == "NFe");
    }

    [Fact]
    public void MontarEnvio_PreservaAssinatura_SemReformatar()
    {
        var xml = AutorizacaoClient.MontarEnvio("1", NFeAssinada);

        // O bloco <Signature> precisa sair intacto; qualquer reindentacao do
        // conteudo de <NFe> invalidaria o DigestValue na SEFAZ.
        Assert.Contains("<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\">", xml);
        Assert.Contains("Id=\"NFe43250412345678000195550010000000011000000015\"", xml);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567890123456")] // 16 digitos
    [InlineData("abc")]
    public void MontarEnvio_IdLoteInvalido_Lanca(string idLote)
    {
        Assert.Throws<ArgumentException>(() => AutorizacaoClient.MontarEnvio(idLote, NFeAssinada));
    }

    // ---- Interpretacao do retorno --------------------------------------

    [Fact]
    public void Interpretar_NotaAutorizada_ExtraiProtocolo_E_MontaDistribuicao()
    {
        var ret = RetEnvi(
            """
            <tpAmb>2</tpAmb>
            <verAplic>RS20250101</verAplic>
            <cStat>104</cStat>
            <xMotivo>Lote processado</xMotivo>
            <cUF>43</cUF>
            <dhRecbto>2026-07-28T14:30:00-03:00</dhRecbto>
            <protNFe versao="4.00">
              <infProt>
                <tpAmb>2</tpAmb>
                <verAplic>RS20250101</verAplic>
                <chNFe>43250412345678000195550010000000011000000015</chNFe>
                <dhRecbto>2026-07-28T14:30:05-03:00</dhRecbto>
                <nProt>143260000012345</nProt>
                <digVal>abc123=</digVal>
                <cStat>100</cStat>
                <xMotivo>Autorizado o uso da NF-e</xMotivo>
              </infProt>
            </protNFe>
            """);

        var r = AutorizacaoClient.InterpretarRetorno(ret, NFeAssinada);

        Assert.Equal("104", r.CodigoStatusLote);
        Assert.Equal("Lote processado", r.MotivoLote);
        // dhRecbto do lote convertido para UTC (Brasilia -03:00).
        Assert.Equal(new DateTime(2026, 7, 28, 17, 30, 0, DateTimeKind.Utc), r.DataHoraSefaz);

        Assert.NotNull(r.Protocolo);
        Assert.True(r.Protocolo!.Autorizada);
        Assert.False(r.Protocolo.Denegada);
        Assert.Equal("100", r.Protocolo.CodigoStatus);
        Assert.Equal("143260000012345", r.Protocolo.NumeroProtocolo);
        Assert.Equal("43250412345678000195550010000000011000000015", r.Protocolo.ChaveAcesso);

        // Distribuicao (nfeProc) montada com NF-e + protNFe.
        Assert.NotNull(r.XmlDistribuicao);
        var proc = XElement.Parse(r.XmlDistribuicao!);
        Assert.Equal("nfeProc", proc.Name.LocalName);
        Assert.Contains(proc.Elements(), e => e.Name.LocalName == "NFe");
        Assert.Contains(proc.Elements(), e => e.Name.LocalName == "protNFe");
    }

    [Fact]
    public void Interpretar_NotaDenegada_MarcaDenegada()
    {
        var ret = RetEnvi(
            """
            <tpAmb>2</tpAmb>
            <cStat>104</cStat>
            <xMotivo>Lote processado</xMotivo>
            <protNFe versao="4.00">
              <infProt>
                <chNFe>43250412345678000195550010000000011000000015</chNFe>
                <nProt>143260000099999</nProt>
                <cStat>302</cStat>
                <xMotivo>Rejeicao: Irregularidade fiscal do destinatario</xMotivo>
              </infProt>
            </protNFe>
            """);

        var r = AutorizacaoClient.InterpretarRetorno(ret, NFeAssinada);

        Assert.True(r.Protocolo!.Denegada);
        Assert.False(r.Protocolo.Autorizada);
        // Denegada tem protocolo: a distribuicao ainda deve ser montada e guardada.
        Assert.NotNull(r.XmlDistribuicao);
    }

    [Fact]
    public void Interpretar_LoteRejeitadoSemProtocolo_NaoMontaDistribuicao()
    {
        // cStat 225: falha de schema no lote. Nao ha protNFe.
        var ret = RetEnvi(
            """
            <tpAmb>2</tpAmb>
            <verAplic>RS20250101</verAplic>
            <cStat>225</cStat>
            <xMotivo>Rejeicao: Falha no Schema XML do lote de NFe</xMotivo>
            <cUF>43</cUF>
            """);

        var r = AutorizacaoClient.InterpretarRetorno(ret, NFeAssinada);

        Assert.Equal("225", r.CodigoStatusLote);
        Assert.Null(r.Protocolo);
        Assert.Null(r.XmlDistribuicao);
    }

    [Fact]
    public void Interpretar_CStatDoLote_NaoConfundeComCStatDoProtocolo()
    {
        // Lote 104 (processado) mas protocolo 100 (autorizado): os dois cStat
        // coexistem. O do lote e o direct child; o da nota fica em infProt.
        var ret = RetEnvi(
            """
            <cStat>104</cStat>
            <xMotivo>Lote processado</xMotivo>
            <protNFe versao="4.00">
              <infProt>
                <chNFe>43250412345678000195550010000000011000000015</chNFe>
                <nProt>1</nProt>
                <cStat>100</cStat>
                <xMotivo>Autorizado o uso da NF-e</xMotivo>
              </infProt>
            </protNFe>
            """);

        var r = AutorizacaoClient.InterpretarRetorno(ret, NFeAssinada);

        Assert.Equal("104", r.CodigoStatusLote);
        Assert.Equal("100", r.Protocolo!.CodigoStatus);
    }

    [Fact]
    public void Interpretar_SemCStat_Lanca()
    {
        var ret = RetEnvi("<tpAmb>2</tpAmb>");

        Assert.Throws<SefazSoapException>(
            () => AutorizacaoClient.InterpretarRetorno(ret, NFeAssinada));
    }

    // ---- Distribuicao (nfeProc) ----------------------------------------

    [Fact]
    public void MontarDistribuicao_JuntaNFeEProtocolo_NaOrdemCorreta()
    {
        var prot =
            """
            <protNFe xmlns="http://www.portalfiscal.inf.br/nfe" versao="4.00"><infProt><chNFe>43250412345678000195550010000000011000000015</chNFe><nProt>1</nProt><cStat>100</cStat></infProt></protNFe>
            """;

        var xml = AutorizacaoClient.MontarDistribuicao(NFeAssinada, prot);
        var proc = XElement.Parse(xml);

        Assert.Equal("nfeProc", proc.Name.LocalName);
        var filhos = proc.Elements().Select(e => e.Name.LocalName).ToList();
        // A ordem exigida pelo schema procNFe e NFe seguida de protNFe.
        Assert.Equal(new[] { "NFe", "protNFe" }, filhos);
    }
}
