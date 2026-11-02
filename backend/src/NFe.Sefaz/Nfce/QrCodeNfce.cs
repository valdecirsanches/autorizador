using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;

namespace NFe.Sefaz.Nfce;

/// <summary>
/// QR Code da NFC-e (grupo infNFeSupl), conforme o Manual de Especificacoes
/// Tecnicas do DANFE NFC-e e QR Code e a NT 2025.001:
/// <list type="bullet">
/// <item>v2 on-line:  url?p=chave|2|tpAmb|idCSC|hash, hash = SHA-1(chave|2|tpAmb|idCSC + CSC);</item>
/// <item>v2 off-line: url?p=chave|2|tpAmb|dia|vNF|digVal(hex)|idCSC|hash, hash = SHA-1(seq + CSC);</item>
/// <item>v3 on-line:  url?p=chave|3|tpAmb (sem CSC);</item>
/// <item>v3 off-line: url?p=chave|3|tpAmb|dia|vNF|tp_idDest|cDest|assinatura, assinatura =
///   base64(RSA-SHA1 de "chave|3|tpAmb|dia|vNF|tp_idDest|cDest" com a chave do certificado).</item>
/// </list>
/// O infNFeSupl fica FORA do infNFe (entre infNFe e Signature), por isso e
/// inserido depois da assinatura sem invalida-la.
/// </summary>
public static class QrCodeNfce
{
    private const string NsNfe = "http://www.portalfiscal.inf.br/nfe";
    private const string NsDsig = "http://www.w3.org/2000/09/xmldsig#";

    /// <summary>Dados do XML assinado usados no QR Code.</summary>
    public sealed record Dados(
        string Chave, int TpAmb, int TpEmis, string DhEmi, decimal VNF,
        string? DigestValue, string TpIdDest, string CDest);

    /// <summary>
    /// Versao efetiva: a pedida (2 ou 3) ou, sem pedido, 3 em contingencia
    /// off-line e quando nao ha CSC, e 2 na emissao normal com CSC.
    /// </summary>
    public static int VersaoEfetiva(int? pedida, bool contingenciaOffline, bool temCsc)
    {
        if (pedida is 2 or 3) return pedida.Value;
        if (pedida is not null)
            throw new ArgumentException("versaoQrCode deve ser 2 ou 3.");
        return contingenciaOffline || !temCsc ? 3 : 2;
    }

    /// <summary>
    /// Gera o QR Code e insere o infNFeSupl no XML ja assinado. Devolve o XML
    /// final e os valores gravados (qrCode e urlChave).
    /// </summary>
    public static (string Xml, string QrCode, string UrlChave) Aplicar(
        string xmlAssinado, UrlsNfce urls, string? csc, string? idCsc, int? versaoPedida,
        CertificadoSefaz? certificado)
    {
        var dados = Ler(xmlAssinado);
        var offline = dados.TpEmis == 9;
        var versao = VersaoEfetiva(versaoPedida, offline, !string.IsNullOrWhiteSpace(csc));
        var qr = Montar(dados, urls.QrCode, versao, csc, idCsc, certificado);
        return (Inserir(xmlAssinado, qr, urls.UrlChave), qr, urls.UrlChave);
    }

    /// <summary>Monta a URL do QR Code para os dados e a versao informados.</summary>
    public static string Montar(
        Dados d, string urlQrCode, int versao, string? csc, string? idCsc, CertificadoSefaz? certificado)
    {
        var url = (urlQrCode ?? "").Trim();
        if (url.Length == 0) throw new ArgumentException("URL do QR Code da NFC-e nao informada.");
        if (!url.Contains("?p=", StringComparison.Ordinal)) url += "?p=";

        var offline = d.TpEmis == 9;
        var dia = DiaEmissao(d.DhEmi);
        var vNF = d.VNF.ToString("0.00", CultureInfo.InvariantCulture);

        if (versao == 3)
        {
            if (!offline) return $"{url}{d.Chave}|3|{d.TpAmb}";

            if (certificado is null)
                throw new InvalidOperationException(
                    "QR Code v3 em contingencia off-line exige o certificado do emitente para assinar.");
            // Estrangeiro: so o separador (cDest vazio), conforme o manual 6.00.
            var cDest = d.TpIdDest == "3" ? "" : d.CDest;
            var seq = $"{d.Chave}|3|{d.TpAmb}|{dia}|{vNF}|{d.TpIdDest}|{cDest}";
            return $"{url}{seq}|{AssinarBase64(seq, certificado)}";
        }

        if (versao != 2) throw new ArgumentException("versaoQrCode deve ser 2 ou 3.");

        var token = (csc ?? "").Trim();
        var id = new string((idCsc ?? "").Where(char.IsDigit).ToArray());
        if (token.Length == 0 || id.Length == 0)
            throw new ArgumentException(
                "QR Code v2 da NFC-e exige o CSC e o ID do CSC (configuracao.csc e configuracao.idCsc). " +
                "Sem CSC, use versaoQrCode 3.");
        // idCSC sem zeros a esquerda ("000001" -> "1").
        var cscId = long.Parse(id, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        if (!offline)
        {
            var seqOn = $"{d.Chave}|2|{d.TpAmb}|{cscId}";
            return $"{url}{seqOn}|{Sha1Hex(seqOn + token)}";
        }

        if (string.IsNullOrEmpty(d.DigestValue))
            throw new InvalidOperationException("XML sem DigestValue: assine antes de gerar o QR Code off-line.");
        var digHex = Convert.ToHexString(Encoding.ASCII.GetBytes(d.DigestValue)).ToLowerInvariant();
        var seqOff = $"{d.Chave}|2|{d.TpAmb}|{dia}|{vNF}|{digHex}|{cscId}";
        return $"{url}{seqOff}|{Sha1Hex(seqOff + token)}";
    }

    /// <summary>Le do XML assinado os campos usados pelo QR Code.</summary>
    public static Dados Ler(string xmlAssinado)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xmlAssinado);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NsNfe);
        ns.AddNamespace("ds", NsDsig);

        string Txt(string xpath) => doc.SelectSingleNode(xpath, ns)?.InnerText?.Trim() ?? "";

        var id = (doc.SelectSingleNode("//n:infNFe/@Id", ns)?.Value ?? "").Trim();
        var chave = id.StartsWith("NFe", StringComparison.Ordinal) ? id[3..] : id;
        if (chave.Length != 44) throw new InvalidOperationException("XML da NFC-e sem chave de acesso valida.");

        var tpIdDest = "";
        var cDest = "";
        if (Txt("//n:infNFe/n:dest/n:CNPJ") is { Length: > 0 } cnpj) { tpIdDest = "1"; cDest = cnpj; }
        else if (Txt("//n:infNFe/n:dest/n:CPF") is { Length: > 0 } cpf) { tpIdDest = "2"; cDest = cpf; }
        else if (doc.SelectSingleNode("//n:infNFe/n:dest/n:idEstrangeiro", ns) is not null) { tpIdDest = "3"; }

        return new Dados(
            Chave: chave,
            TpAmb: int.Parse(Txt("//n:infNFe/n:ide/n:tpAmb"), CultureInfo.InvariantCulture),
            TpEmis: int.Parse(Txt("//n:infNFe/n:ide/n:tpEmis"), CultureInfo.InvariantCulture),
            DhEmi: Txt("//n:infNFe/n:ide/n:dhEmi"),
            VNF: decimal.Parse(Txt("//n:infNFe/n:total/n:ICMSTot/n:vNF"), CultureInfo.InvariantCulture),
            DigestValue: Txt("//ds:Signature/ds:SignedInfo/ds:Reference/ds:DigestValue") is { Length: > 0 } dv ? dv : null,
            TpIdDest: tpIdDest,
            CDest: cDest);
    }

    /// <summary>
    /// Insere (ou substitui) o infNFeSupl imediatamente antes da Signature, sem
    /// reserializar o restante do documento (a assinatura continua valida).
    /// </summary>
    public static string Inserir(string xmlAssinado, string qrCode, string urlChave)
    {
        var xml = RemoverInfNFeSupl(xmlAssinado);
        var pos = xml.IndexOf("<Signature", StringComparison.Ordinal);
        if (pos < 0) throw new InvalidOperationException("XML sem Signature: assine antes de inserir o QR Code.");

        var supl = "<infNFeSupl><qrCode><![CDATA[" + qrCode + "]]></qrCode><urlChave>" +
                   SecurityElement.Escape(urlChave) + "</urlChave></infNFeSupl>";
        return xml.Insert(pos, supl);
    }

    /// <summary>qrCode e urlChave gravados no XML (null quando nao ha infNFeSupl).</summary>
    public static (string? QrCode, string? UrlChave) LerInfNFeSupl(string? xml)
    {
        if (string.IsNullOrEmpty(xml) || !xml.Contains("<infNFeSupl", StringComparison.Ordinal))
            return (null, null);
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("n", NsNfe);
            return (doc.SelectSingleNode("//n:infNFeSupl/n:qrCode", ns)?.InnerText?.Trim(),
                    doc.SelectSingleNode("//n:infNFeSupl/n:urlChave", ns)?.InnerText?.Trim());
        }
        catch (XmlException)
        {
            return (null, null);
        }
    }

    private static string RemoverInfNFeSupl(string xml)
    {
        var ini = xml.IndexOf("<infNFeSupl", StringComparison.Ordinal);
        if (ini < 0) return xml;
        const string fim = "</infNFeSupl>";
        var f = xml.IndexOf(fim, ini, StringComparison.Ordinal);
        return f < 0 ? xml : xml.Remove(ini, f + fim.Length - ini);
    }

    /// <summary>Dia (2 digitos) do dhEmi "AAAA-MM-DDThh:mm:ss-03:00", no fuso do proprio dhEmi.</summary>
    private static string DiaEmissao(string dhEmi) =>
        dhEmi.Length >= 10 ? dhEmi.Substring(8, 2) : throw new InvalidOperationException("dhEmi invalido.");

    private static string Sha1Hex(string texto) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(texto)));

    /// <summary>RSA PKCS#1 v1.5 com SHA-1, em base64 (A1 local ou A3 via agente).</summary>
    private static string AssinarBase64(string texto, CertificadoSefaz certificado)
    {
        using var rsa = certificado.ObterChaveAssinatura();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(texto));
        return Convert.ToBase64String(rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
    }
}
