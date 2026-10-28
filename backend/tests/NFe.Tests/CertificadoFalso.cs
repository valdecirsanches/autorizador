using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NFe.Tests;

/// <summary>
/// Gera certificados A1 sinteticos para teste, incluindo a extensao OtherName do
/// perfil ICP-Brasil que carrega o CNPJ do titular (OID 2.16.76.1.3.3).
///
/// Serve para exercitar todo o fluxo de importacao, cifragem e assinatura sem
/// depender de um certificado real emitido por AC.
/// </summary>
public static class CertificadoFalso
{
    private const string OidCnpjIcpBrasil = "2.16.76.1.3.3";

    /// <summary>
    /// Cria um PFX autoassinado com chave RSA 2048 e, opcionalmente, o CNPJ do
    /// titular na extensao SubjectAlternativeName.
    /// </summary>
    public static byte[] CriarPfx(
        string senha,
        string cnpj = "12345678000195",
        string razaoSocial = "EMPRESA TESTE LTDA",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool incluirCnpj = true)
    {
        var de = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var ate = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);

        using var rsa = RSA.Create(2048);

        var subject = new X500DistinguishedName(
            $"CN={razaoSocial}:{cnpj}, OU=Certificado PF A1, O=ICP-Brasil, C=BR");

        var req = new CertificateRequest(
            subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        // A NF-e exige assinatura digital e nao repudio.
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
                true));

        if (incluirCnpj)
            req.CertificateExtensions.Add(ExtensaoSanComCnpj(cnpj));

        using var cert = req.CreateSelfSigned(de, ate);
        return cert.Export(X509ContentType.Pfx, senha);
    }

    /// <summary>
    /// Monta a extensao SubjectAlternativeName contendo um GeneralName otherName
    /// com o OID do CNPJ ICP-Brasil.
    ///
    /// Estrutura ASN.1:
    ///   SubjectAltName ::= SEQUENCE OF GeneralName
    ///   GeneralName ::= [0] otherName { type-id OID, value [0] EXPLICIT ANY }
    /// </summary>
    private static X509Extension ExtensaoSanComCnpj(string cnpj)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            // otherName e o contexto [0] construido.
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                writer.WriteObjectIdentifier(OidCnpjIcpBrasil);

                // value [0] EXPLICIT
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    // O perfil ICP-Brasil usa PrintableString com os 14 digitos.
                    writer.WriteCharacterString(UniversalTagNumber.PrintableString, cnpj);
                }
            }
        }

        return new X509Extension(
            new Oid("2.5.29.17"), writer.Encode(), critical: false);
    }
}
