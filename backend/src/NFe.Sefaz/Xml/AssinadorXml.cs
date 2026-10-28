using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using NFe.Sefaz.Seguranca;

namespace NFe.Sefaz.Xml;

/// <summary>
/// Assina documentos da NF-e conforme o Manual de Orientacao do Contribuinte
/// 4.00: assinatura enveloped sobre o elemento identificado por Id, com
/// canonicalizacao C14N, RSA-SHA1 e digest SHA-1. Esses algoritmos sao os
/// exigidos pela SEFAZ e nao devem ser "modernizados" para SHA-256, sob pena de
/// rejeicao (por exemplo, codigo 297 assinatura difere do calculado).
/// </summary>
public sealed class AssinadorXml
{
    /// <summary>
    /// Assina o elemento cujo atributo Id corresponde a <paramref name="idReferencia"/>
    /// (por exemplo "NFe3524..."). A assinatura e inserida como irmao do elemento
    /// assinado, logo apos ele, conforme exigido pelo layout.
    /// </summary>
    /// <param name="xml">Documento a assinar (NF-e, evento, inutilizacao).</param>
    /// <param name="idReferencia">Valor do atributo Id do elemento a referenciar.</param>
    /// <param name="certificado">Certificado A1 com chave privada exportavel.</param>
    /// <param name="nomeTagAssinatura">
    /// Elemento pai onde a Signature sera anexada. Para NF-e e o proprio "NFe":
    /// a Signature deve ser IRMA de infNFe, nunca sua filha. Por isso o default
    /// e "NFe" — usar "infNFe" aqui coloca a assinatura no lugar errado e a
    /// SEFAZ/XSD rejeita ("invalid child element 'Signature'").
    /// </param>
    public string Assinar(
        string xml,
        string idReferencia,
        X509Certificate2 certificado,
        string nomeTagAssinatura = "NFe")
    {
        ArgumentNullException.ThrowIfNull(certificado);

        using var local = new CertificadoLocal(certificado, donoDoCertificado: false);
        return Assinar(xml, idReferencia, local, nomeTagAssinatura);
    }

    /// <summary>
    /// Assina com qualquer tipo de certificado. No A1 a chave privada esta em
    /// memoria; no A3 o hash do SignedInfo e assinado pelo token via agente.
    /// </summary>
    public string Assinar(
        string xml,
        string idReferencia,
        CertificadoSefaz certificado,
        string nomeTagAssinatura = "NFe")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(idReferencia);
        ArgumentNullException.ThrowIfNull(certificado);

        // PreserveWhitespace e essencial: qualquer reindentacao apos assinar
        // invalida o digest e a SEFAZ rejeita com "assinatura difere".
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);

        var elemento = LocalizarElementoPorId(doc, idReferencia);
        if (elemento is null)
            throw new InvalidOperationException(
                $"Nenhum elemento com Id='{idReferencia}' foi encontrado no documento.");

        using var chave = certificado.ObterChaveAssinatura();

        var signedXml = new SignedXml(doc)
        {
            SigningKey = chave
        };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;

        var referencia = new Reference
        {
            Uri = "#" + idReferencia,
            DigestMethod = SignedXml.XmlDsigSHA1Url
        };
        // A ordem importa: primeiro remove a propria assinatura (enveloped),
        // depois canonicaliza o restante.
        referencia.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        referencia.AddTransform(new XmlDsigC14NTransform());
        signedXml.AddReference(referencia);

        // KeyInfo com o X509 do emitente, exigido pela SEFAZ.
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificado.Publico));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        var assinatura = signedXml.GetXml();

        // Anexa a Signature ao elemento pai indicado. Para a NF-e, a Signature e
        // filha do elemento raiz "NFe", irma de infNFe.
        var pai = ResolverPai(doc, elemento, nomeTagAssinatura);
        pai.AppendChild(doc.ImportNode(assinatura, true));

        return doc.OuterXml;
    }

    /// <summary>
    /// Verifica a assinatura de um documento ja assinado, conferindo tanto a
    /// integridade (digest) quanto a chave publica embutida.
    /// </summary>
    public bool Verificar(string xmlAssinado)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlAssinado);

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xmlAssinado);

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        var lista = doc.SelectNodes("//ds:Signature", ns);
        if (lista is null || lista.Count == 0)
            return false;

        var signedXml = new SignedXml(doc);
        signedXml.LoadXml((XmlElement)lista[0]!);

        // Sem argumento, CheckSignature usa a chave publica do KeyInfo e valida
        // o digest. Nao valida a cadeia ICP-Brasil: isso e responsabilidade de
        // outra etapa (validacao do certificado).
        return signedXml.CheckSignature();
    }

    private static XmlElement? LocalizarElementoPorId(XmlDocument doc, string id)
    {
        // GetElementById depende de DTD declarando o tipo ID, o que a NF-e nao
        // possui. Buscamos o atributo Id manualmente em qualquer namespace.
        foreach (XmlElement el in doc.GetElementsByTagName("*"))
        {
            var attr = el.GetAttribute("Id");
            if (!string.IsNullOrEmpty(attr) && attr == id)
                return el;
        }

        return null;
    }

    private static XmlNode ResolverPai(XmlDocument doc, XmlElement referenciado, string nomeTagPai)
    {
        // Se o elemento referenciado ja for o desejado como pai, usa-o.
        if (referenciado.LocalName == nomeTagPai)
            return referenciado;

        // Caso comum: assinamos infNFe e a Signature vai no pai NFe.
        var pai = referenciado.ParentNode;
        if (pai is XmlElement && pai.LocalName == nomeTagPai)
            return pai;

        // Fallback: procura o elemento pedido no documento.
        var candidatos = doc.GetElementsByTagName(nomeTagPai);
        if (candidatos.Count > 0)
            return candidatos[0]!;

        // Ultimo recurso: pai imediato do elemento referenciado.
        return referenciado.ParentNode
            ?? throw new InvalidOperationException(
                $"Nao foi possivel localizar o elemento pai '{nomeTagPai}' para a assinatura.");
    }
}
