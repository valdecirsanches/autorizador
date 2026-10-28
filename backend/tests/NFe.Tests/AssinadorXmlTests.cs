using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using NFe.Sefaz.Xml;

namespace NFe.Tests;

public class AssinadorXmlTests
{
    private const string ChaveExemplo = "35240812345678000195550010000000011000000154";

    // Esqueleto minimo com infNFe identificado por Id, suficiente para exercitar
    // a mecanica da assinatura sem depender do XSD completo.
    private static string NFeSkeleton(string chave44) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <NFe xmlns="http://www.portalfiscal.inf.br/nfe">
          <infNFe versao="4.00" Id="NFe{chave44}">
            <ide><cUF>35</cUF><natOp>VENDA</natOp></ide>
            <emit><CNPJ>12345678000195</CNPJ></emit>
          </infNFe>
        </NFe>
        """;

    private static X509Certificate2 Cert()
    {
        var pfx = CertificadoFalso.CriarPfx("senha");
        return X509CertificateLoader.LoadPkcs12(pfx, "senha", X509KeyStorageFlags.Exportable);
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        ns.AddNamespace("nfe", "http://www.portalfiscal.inf.br/nfe");
        return ns;
    }

    [Fact]
    public void Assinar_InsereSignatureComoIrmaDeInfNFe()
    {
        using var cert = Cert();
        var idRef = "NFe" + ChaveExemplo;

        var assinado = new AssinadorXml().Assinar(
            NFeSkeleton(ChaveExemplo), idRef, cert, nomeTagAssinatura: "NFe");

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(assinado);
        var ns = Ns(doc);

        // A Signature deve estar sob o elemento raiz NFe (irma de infNFe).
        var sig = doc.SelectSingleNode("/nfe:NFe/ds:Signature", ns);
        Assert.NotNull(sig);
        Assert.Equal("NFe", sig!.ParentNode!.LocalName);
    }

    [Fact]
    public void Assinar_ReferenciaApontaParaOIdDoInfNFe()
    {
        using var cert = Cert();
        var idRef = "NFe" + ChaveExemplo;

        var assinado = new AssinadorXml().Assinar(NFeSkeleton(ChaveExemplo), idRef, cert, "NFe");

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(assinado);
        var uri = doc.SelectSingleNode(
            "//*[local-name()='Reference']/@URI")!.Value;

        Assert.Equal("#" + idRef, uri);
    }

    [Fact]
    public void Assinar_UsaAlgoritmosExigidosPelaSefaz()
    {
        using var cert = Cert();
        var assinado = new AssinadorXml().Assinar(
            NFeSkeleton(ChaveExemplo), "NFe" + ChaveExemplo, cert, "NFe");

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(assinado);

        string Alg(string local) =>
            doc.SelectSingleNode($"//*[local-name()='{local}']/@Algorithm")!.Value!;

        Assert.Equal(SignedXml.XmlDsigC14NTransformUrl,
            doc.SelectSingleNode("//*[local-name()='CanonicalizationMethod']/@Algorithm")!.Value);
        Assert.Equal(SignedXml.XmlDsigRSASHA1Url, Alg("SignatureMethod"));
        Assert.Equal(SignedXml.XmlDsigSHA1Url, Alg("DigestMethod"));

        // Deve haver as duas transforms: enveloped e C14N.
        var transforms = doc.SelectNodes("//*[local-name()='Transform']/@Algorithm")!
            .Cast<XmlNode>().Select(n => n.Value).ToList();
        Assert.Contains(SignedXml.XmlDsigEnvelopedSignatureTransformUrl, transforms);
        Assert.Contains(SignedXml.XmlDsigC14NTransformUrl, transforms);
    }

    [Fact]
    public void Assinar_IncluiX509CertificateNoKeyInfo()
    {
        using var cert = Cert();
        var assinado = new AssinadorXml().Assinar(
            NFeSkeleton(ChaveExemplo), "NFe" + ChaveExemplo, cert, "NFe");

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(assinado);

        var x509 = doc.SelectSingleNode("//*[local-name()='X509Certificate']");
        Assert.NotNull(x509);
        Assert.False(string.IsNullOrWhiteSpace(x509!.InnerText));
    }

    [Fact]
    public void Verificar_DocumentoRecemAssinado_RetornaTrue()
    {
        using var cert = Cert();
        var assinador = new AssinadorXml();
        var assinado = assinador.Assinar(
            NFeSkeleton(ChaveExemplo), "NFe" + ChaveExemplo, cert, "NFe");

        Assert.True(assinador.Verificar(assinado));
    }

    [Fact]
    public void Verificar_ConteudoAdulteradoAposAssinatura_RetornaFalse()
    {
        using var cert = Cert();
        var assinador = new AssinadorXml();
        var assinado = assinador.Assinar(
            NFeSkeleton(ChaveExemplo), "NFe" + ChaveExemplo, cert, "NFe");

        // Altera um valor dentro de infNFe apos assinar: o digest nao confere.
        var adulterado = assinado.Replace("<natOp>VENDA</natOp>", "<natOp>FRAUDE</natOp>");
        Assert.NotEqual(assinado, adulterado);

        Assert.False(assinador.Verificar(adulterado));
    }

    [Fact]
    public void Assinar_CertificadoSemChavePrivada_Lanca()
    {
        using var comChave = Cert();
        // Reexporta apenas a parte publica.
        using var soPublico = X509CertificateLoader.LoadCertificate(comChave.Export(X509ContentType.Cert));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AssinadorXml().Assinar(NFeSkeleton(ChaveExemplo), "NFe" + ChaveExemplo, soPublico, "NFe"));

        Assert.Contains("chave privada", ex.Message);
    }

    [Fact]
    public void Assinar_IdInexistente_Lanca()
    {
        using var cert = Cert();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AssinadorXml().Assinar(NFeSkeleton(ChaveExemplo), "NFeOUTRACHAVE", cert, "NFe"));

        Assert.Contains("Id", ex.Message);
    }

    [Fact]
    public void Assinar_NaoReindentaOConteudoAssinado()
    {
        using var cert = Cert();
        var assinado = new AssinadorXml().Assinar(
            NFeSkeleton(ChaveExemplo), "NFe" + ChaveExemplo, cert, "NFe");

        // O conteudo original de infNFe deve permanecer intacto byte a byte;
        // reindentacao invalidaria o digest.
        Assert.Contains("<emit><CNPJ>12345678000195</CNPJ></emit>", assinado);
    }
}
