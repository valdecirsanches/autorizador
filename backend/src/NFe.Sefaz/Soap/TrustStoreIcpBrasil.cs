using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace NFe.Sefaz.Soap;

/// <summary>
/// Valida o certificado do servidor da SEFAZ contra a cadeia de ACs da
/// ICP-Brasil embutida no assembly (raizes + intermediarias), sem depender do
/// trust store do sistema operacional.
///
/// Motivacao: em Linux (containers, servidores, maquinas de desenvolvimento) as
/// raizes da ICP-Brasil frequentemente NAO estao instaladas, entao o handshake
/// TLS com a SEFAZ falha com "UntrustedRoot" / "unable to get local issuer
/// certificate". Como todos os endpoints da SEFAZ usam certificados ICP-Brasil,
/// embutimos a cadeia oficial e validamos contra ela de forma explicita e
/// segura (a validacao de nome de host continua ativa).
/// </summary>
public static class TrustStoreIcpBrasil
{
    private static readonly Lazy<(X509Certificate2Collection Raizes, X509Certificate2Collection Intermediarias)>
        _cadeia = new(Carregar);

    /// <summary>Raizes ICP-Brasil embutidas (autoassinadas).</summary>
    public static X509Certificate2Collection Raizes => _cadeia.Value.Raizes;

    /// <summary>ACs intermediarias ICP-Brasil embutidas.</summary>
    public static X509Certificate2Collection Intermediarias => _cadeia.Value.Intermediarias;

    private static (X509Certificate2Collection, X509Certificate2Collection) Carregar()
    {
        var asm = typeof(TrustStoreIcpBrasil).Assembly;
        var nome = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("icp-brasil-bundle.pem", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Bundle 'icp-brasil-bundle.pem' nao encontrado nos recursos embutidos do assembly.");

        using var stream = asm.GetManifestResourceStream(nome)
            ?? throw new InvalidOperationException($"Nao foi possivel abrir o recurso '{nome}'.");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();

        var todos = new X509Certificate2Collection();
        todos.ImportFromPem(pem);

        var raizes = new X509Certificate2Collection();
        var intermediarias = new X509Certificate2Collection();
        foreach (var cert in todos)
        {
            // Raiz = emissor igual ao titular (autoassinada).
            if (string.Equals(cert.SubjectName.Name, cert.IssuerName.Name, StringComparison.Ordinal))
                raizes.Add(cert);
            else
                intermediarias.Add(cert);
        }

        return (raizes, intermediarias);
    }

    /// <summary>
    /// Callback de validacao do certificado do servidor. Aceita quando a cadeia
    /// encadeia numa raiz ICP-Brasil embutida. Nome de host divergente e outros
    /// erros continuam sendo rejeitados.
    /// </summary>
    /// <param name="permitirCadeiaNaoConfiavel">
    /// Quando true, aceita erros de cadeia mesmo que a validacao ICP-Brasil
    /// falhe (ultimo recurso para desenvolvimento). Nome de host divergente
    /// ainda e rejeitado.
    /// </param>
    public static bool Validar(
        X509Certificate2? servidor,
        SslPolicyErrors erros,
        bool permitirCadeiaNaoConfiavel)
    {
        if (erros == SslPolicyErrors.None) return true;

        // Nome de host divergente ou certificado ausente nunca sao aceitos:
        // indicariam redirecionamento indevido do trafego fiscal.
        if ((erros & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        if ((erros & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        if (servidor is null) return false;

        // Reconstroi a cadeia confiando SOMENTE nas raizes ICP-Brasil embutidas.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(Raizes);
        chain.ChainPolicy.ExtraStore.AddRange(Intermediarias);
        // A SEFAZ frequentemente nao publica CRL/OCSP acessiveis; a checagem de
        // revogacao online quebraria o handshake por timeout. Emissores de NF-e
        // tradicionalmente nao fazem essa checagem no canal de transporte.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        var ok = chain.Build(servidor);
        return ok || permitirCadeiaNaoConfiavel;
    }
}
