using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Soap;

namespace NFe.Tests;

/// <summary>
/// Cobre a montagem do envelope SOAP 1.2 e a interpretacao das respostas.
/// Erro de namespace ou de estrutura aqui gera falha de schema no autorizador,
/// que responde com Fault generico e sem indicar a causa real.
/// </summary>
public class SefazSoapClientTests
{
    private const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private const string PortalFiscal = "http://www.portalfiscal.inf.br/nfe";

    private static XElement Envelope(ServicoSefaz servico, string mensagem) =>
        XElement.Parse(SefazSoapClient.MontarEnvelope(servico, mensagem));

    private const string ConsStatServ =
        """<consStatServ xmlns="http://www.portalfiscal.inf.br/nfe" versao="4.00"><tpAmb>2</tpAmb><cUF>43</cUF><xServ>STATUS</xServ></consStatServ>""";

    /// <summary>
    /// Corpo real devolvido pelo IIS do SVRS de homologacao quando o certificado
    /// cliente nao e aceito (capturado em 2026-07-28, HTTP 403). Nao e XML, e o
    /// parsing precisa degradar sem lancar excecao de XML, para que a mensagem
    /// util de 403 chegue ao chamador.
    /// </summary>
    private const string Html403DoIis =
        """
        <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Strict//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd">
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>403 - Forbidden: Access is denied.</title>
        <style type="text/css"><!-- body{margin:0;} --></style>
        </head><body><div id="header"><h1>Server Error</h1></div></body></html>
        """;

    [Fact]
    public void Resposta403EmHtml_NaoQuebraOParsing()
    {
        // Ambos precisam retornar null em vez de lancar: o cliente depende disso
        // para reportar "403 indica certificado ausente ou sem credenciamento".
        Assert.Null(SefazSoapClient.ExtrairFault(Html403DoIis));
        Assert.Null(SefazSoapClient.ExtrairRetorno(Html403DoIis, ServicoSefaz.StatusServico));
    }

    [Fact]
    public void Envelope_UsaSoap12_E_ContemNfeDadosMsg()
    {
        var env = Envelope(ServicoSefaz.StatusServico, ConsStatServ);

        Assert.Equal(XName.Get("Envelope", Soap12), env.Name);

        var body = Assert.Single(env.Elements(XName.Get("Body", Soap12)));
        var dados = Assert.Single(body.Elements());
        Assert.Equal("nfeDadosMsg", dados.Name.LocalName);
    }

    [Theory]
    [InlineData(ServicoSefaz.StatusServico, "NFeStatusServico4")]
    [InlineData(ServicoSefaz.Autorizacao, "NFeAutorizacao4")]
    [InlineData(ServicoSefaz.RetAutorizacao, "NFeRetAutorizacao4")]
    [InlineData(ServicoSefaz.ConsultaProtocolo, "NFeConsultaProtocolo4")]
    [InlineData(ServicoSefaz.Inutilizacao, "NFeInutilizacao4")]
    [InlineData(ServicoSefaz.ConsultaCadastro, "CadConsultaCadastro4")]
    [InlineData(ServicoSefaz.RecepcaoEvento, "NFeRecepcaoEvento4")]
    public void NfeDadosMsg_UsaNamespaceDoWsdlDoServico(ServicoSefaz servico, string sufixo)
    {
        // O nfeDadosMsg precisa estar no namespace do WSDL daquele servico
        // especificamente; reaproveitar o de outro servico causa erro de schema.
        var env = Envelope(servico, ConsStatServ);

        var dados = env.Descendants().First(e => e.Name.LocalName == "nfeDadosMsg");

        Assert.Equal($"http://www.portalfiscal.inf.br/nfe/wsdl/{sufixo}", dados.Name.NamespaceName);
    }

    [Fact]
    public void Envelope_PreservaNamespaceDaMensagemInterna()
    {
        // A mensagem precisa continuar em portalfiscal.inf.br/nfe, e nao herdar
        // o namespace do WSDL do elemento pai.
        var env = Envelope(ServicoSefaz.StatusServico, ConsStatServ);

        var cons = env.Descendants().First(e => e.Name.LocalName == "consStatServ");

        Assert.Equal(PortalFiscal, cons.Name.NamespaceName);
        Assert.Equal("4.00", cons.Attribute("versao")?.Value);
    }

    [Fact]
    public void Envelope_NaoContemBomNemDeclaracaoXml()
    {
        // A SEFAZ rejeita envelope com BOM ou com declaracao antes do Envelope.
        var xml = SefazSoapClient.MontarEnvelope(
            ServicoSefaz.StatusServico,
            "\uFEFF<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + ConsStatServ);

        Assert.DoesNotContain('\uFEFF', xml);
        Assert.DoesNotContain("<?xml", xml);
        Assert.StartsWith("<", xml);
    }

    [Fact]
    public void Envelope_MensagemInvalida_FalhaAntesDeEnviar()
    {
        // Melhor falhar local do que gastar um round-trip e receber Fault opaco.
        Assert.ThrowsAny<System.Xml.XmlException>(
            () => SefazSoapClient.MontarEnvelope(ServicoSefaz.StatusServico, "<nao-fechado>"));
    }

    [Fact]
    public void ExtrairRetorno_EncontraRetConsStatServ()
    {
        var resposta = $"""
            <soap:Envelope xmlns:soap="{Soap12}"><soap:Body>
              <nfeResultMsg xmlns="http://www.portalfiscal.inf.br/nfe/wsdl/NFeStatusServico4">
                <retConsStatServ xmlns="{PortalFiscal}" versao="4.00">
                  <tpAmb>2</tpAmb><cStat>107</cStat>
                  <xMotivo>Servico em Operacao</xMotivo><cUF>43</cUF>
                </retConsStatServ>
              </nfeResultMsg>
            </soap:Body></soap:Envelope>
            """;

        var ret = SefazSoapClient.ExtrairRetorno(resposta, ServicoSefaz.StatusServico);

        Assert.NotNull(ret);
        Assert.Equal("107", ret!.Descendants()
            .First(e => e.Name.LocalName == "cStat").Value);
    }

    [Fact]
    public void ExtrairRetorno_SuportaRetornoEscapadoComoTexto()
    {
        // Alguns autorizadores devolvem o XML util escapado dentro de
        // nfeResultMsg em vez de como elemento filho.
        var interno =
            $"""<retConsStatServ xmlns="{PortalFiscal}" versao="4.00"><cStat>108</cStat></retConsStatServ>""";

        var resposta = $"""
            <soap:Envelope xmlns:soap="{Soap12}"><soap:Body>
              <nfeResultMsg>{System.Security.SecurityElement.Escape(interno)}</nfeResultMsg>
            </soap:Body></soap:Envelope>
            """;

        var ret = SefazSoapClient.ExtrairRetorno(resposta, ServicoSefaz.StatusServico);

        Assert.NotNull(ret);
        Assert.Equal("108", ret!.Descendants()
            .First(e => e.Name.LocalName == "cStat").Value);
    }

    [Fact]
    public void ExtrairRetorno_ServicoDiferente_NaoConfundeElemento()
    {
        // Uma resposta de StatusServico nao deve satisfazer uma consulta de
        // Autorizacao: isso mascararia troca de endpoint.
        var resposta = $"""
            <soap:Envelope xmlns:soap="{Soap12}"><soap:Body>
              <retConsStatServ xmlns="{PortalFiscal}"><cStat>107</cStat></retConsStatServ>
            </soap:Body></soap:Envelope>
            """;

        Assert.Null(SefazSoapClient.ExtrairRetorno(resposta, ServicoSefaz.Autorizacao));
    }

    [Fact]
    public void ExtrairRetorno_CorpoNaoXml_RetornaNull()
    {
        // Portais fora do ar costumam devolver HTML de erro.
        Assert.Null(SefazSoapClient.ExtrairRetorno(
            "<html><body>Service Unavailable</body></html>", ServicoSefaz.StatusServico));
        Assert.Null(SefazSoapClient.ExtrairRetorno("nao e xml", ServicoSefaz.StatusServico));
    }

    [Fact]
    public void ExtrairFault_Soap12_LeReasonText()
    {
        var fault = $"""
            <soap:Envelope xmlns:soap="{Soap12}"><soap:Body><soap:Fault>
              <soap:Code><soap:Value>soap:Sender</soap:Value></soap:Code>
              <soap:Reason><soap:Text xml:lang="pt">Erro de schema no lote</soap:Text></soap:Reason>
            </soap:Fault></soap:Body></soap:Envelope>
            """;

        Assert.Equal("Erro de schema no lote", SefazSoapClient.ExtrairFault(fault));
    }

    [Fact]
    public void ExtrairFault_Soap11_LeFaultstring()
    {
        var fault = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body><soapenv:Fault>
                <faultcode>soapenv:Client</faultcode>
                <faultstring>Certificado sem credenciamento</faultstring>
              </soapenv:Fault></soapenv:Body></soapenv:Envelope>
            """;

        Assert.Equal("Certificado sem credenciamento", SefazSoapClient.ExtrairFault(fault));
    }

    [Fact]
    public void ExtrairFault_RespostaNormal_RetornaNull()
    {
        var ok = $"""
            <soap:Envelope xmlns:soap="{Soap12}"><soap:Body>
              <retConsStatServ xmlns="{PortalFiscal}"><cStat>107</cStat></retConsStatServ>
            </soap:Body></soap:Envelope>
            """;

        Assert.Null(SefazSoapClient.ExtrairFault(ok));
    }
}
