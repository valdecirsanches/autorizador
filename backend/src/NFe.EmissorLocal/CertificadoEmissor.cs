using System.Security.Cryptography.X509Certificates;
using NFe.Sefaz.Seguranca;

namespace NFe.EmissorLocal;

/// <summary>Certificado da loja, carregado uma vez na subida.</summary>
public sealed class CertificadoEmissor : IDisposable
{
    private readonly X509Certificate2? _cert;

    public CertificadoEmissor(OpcoesEmissorLocal o, ILogger<CertificadoEmissor> log)
    {
        try
        {
            _cert = Carregar(o);
            if (!_cert.HasPrivateKey)
                throw new InvalidOperationException("o certificado nao tem chave privada.");
            Cnpj = CnpjCertificado.Extrair(_cert);
            Validade = _cert.NotAfter;
            log.LogInformation("[EmissorLocal] Certificado carregado: CNPJ {Cnpj}, valido ate {Validade:dd/MM/yyyy}.",
                Cnpj ?? "(nao identificado)", Validade);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Erro = ex.Message;
            log.LogError(ex, "[EmissorLocal] Certificado nao carregado: {Erro}", ex.Message);
        }
    }

    public string? Cnpj { get; }
    public DateTime? Validade { get; }
    public string? Erro { get; }

    /// <summary>Certificado pronto para assinar; lanca se nao foi carregado ou esta vencido.</summary>
    public CertificadoSefaz ParaAssinar()
    {
        if (_cert is null)
            throw new InvalidOperationException($"Certificado indisponivel: {Erro}");
        if (_cert.NotAfter < DateTime.Now)
            throw new InvalidOperationException($"Certificado vencido em {_cert.NotAfter:dd/MM/yyyy}.");
        return new CertificadoLocal(_cert, donoDoCertificado: false);
    }

    private static X509Certificate2 Carregar(OpcoesEmissorLocal o)
    {
        if (!string.IsNullOrWhiteSpace(o.CertificadoArquivo))
            return X509CertificateLoader.LoadPkcs12FromFile(o.CertificadoArquivo!, o.CertificadoSenha);

        var thumb = (o.CertificadoThumbprint ?? "").Replace(" ", "").ToUpperInvariant();
        foreach (var local in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, local);
            store.Open(OpenFlags.ReadOnly);
            var achados = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false);
            if (achados.Count > 0) return achados[0];
        }
        throw new InvalidOperationException($"Certificado {thumb} nao encontrado no repositorio do Windows.");
    }

    public void Dispose() => _cert?.Dispose();
}
