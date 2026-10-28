using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NFe.Sefaz.Seguranca;

/// <summary>
/// RSA cuja chave privada nao esta nesta maquina: SignHash e delegado ao agente
/// do certificado A3. O SignedXml calcula digest e canonicalizacao localmente e
/// so o hash do SignedInfo cruza o canal; o XML nunca sai da API.
///
/// O SignedXml e sincrono, entao a chamada ao agente bloqueia a thread da
/// requisicao. O agente aplica seu proprio timeout, que limita essa espera.
/// </summary>
public sealed class RsaRemoto : RSA
{
    private readonly IAgenteCertificado _agente;
    private readonly RSAParameters _parametrosPublicos;

    public RsaRemoto(X509Certificate2 publico, IAgenteCertificado agente)
    {
        ArgumentNullException.ThrowIfNull(publico);
        _agente = agente ?? throw new ArgumentNullException(nameof(agente));

        using var rsaPublica = publico.GetRSAPublicKey()
            ?? throw new InvalidOperationException(
                "O certificado A3 nao possui chave publica RSA.");

        _parametrosPublicos = rsaPublica.ExportParameters(includePrivateParameters: false);
        KeySizeValue = rsaPublica.KeySize;
    }

    public override byte[] SignHash(
        byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        ArgumentNullException.ThrowIfNull(hash);

        // A SEFAZ exige PKCS#1 v1.5 (RSA-SHA1); nao ha motivo para aceitar PSS.
        if (padding != RSASignaturePadding.Pkcs1)
            throw new CryptographicException(
                "Somente o padding PKCS#1 v1.5 e suportado na assinatura A3.");

        try
        {
            return _agente.AssinarHashAsync(hash, hashAlgorithm).GetAwaiter().GetResult();
        }
        catch (AgenteCertificadoException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not CryptographicException)
        {
            throw new AgenteCertificadoException(
                "Falha ao assinar com o certificado A3 no agente: " + ex.Message, ex);
        }
    }

    public override RSAParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters)
            throw new CryptographicException(
                "A chave privada do certificado A3 nao sai do token.");

        return _parametrosPublicos;
    }

    public override void ImportParameters(RSAParameters parameters) =>
        throw new NotSupportedException("A chave do certificado A3 nao pode ser substituida.");
}
