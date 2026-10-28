using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NFe.Sefaz.Seguranca;

/// <summary>Resposta HTTP bruta de um POST SOAP feito pelo agente do certificado A3.</summary>
public sealed record RespostaTransporteSoap(HttpStatusCode Status, string Corpo);

/// <summary>
/// Falha de comunicacao com o agente do certificado A3 (desconectado, timeout,
/// token bloqueado, PIN invalido). A API a traduz em 503 com mensagem clara.
/// </summary>
public sealed class AgenteCertificadoException : Exception
{
    public AgenteCertificadoException(string message) : base(message) { }
    public AgenteCertificadoException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Canal com o agente instalado na maquina onde o token A3 esta plugado. So
/// duas operacoes cruzam o canal: assinar um hash e fazer o POST mTLS.
/// </summary>
public interface IAgenteCertificado
{
    /// <summary>Assina um hash ja calculado com a chave privada do token (PKCS#1 v1.5).</summary>
    Task<byte[]> AssinarHashAsync(
        byte[] hash, HashAlgorithmName algoritmo, CancellationToken ct = default);

    /// <summary>Faz o POST SOAP 1.2 com TLS mutuo usando o certificado do token.</summary>
    Task<RespostaTransporteSoap> PostSoapAsync(
        string url, string envelope, CancellationToken ct = default);
}

/// <summary>
/// Certificado usado pelos clientes SEFAZ, independente de onde a chave privada
/// esta. No A1 a chave esta em memoria e a API assina e fala TLS. No A3 a chave
/// fica no token do cliente e as duas operacoes sao delegadas ao agente.
///
/// A conversao implicita a partir de <see cref="X509Certificate2"/> mantem os
/// chamadores antigos (A1 carregado manualmente, testes) funcionando sem mudanca.
/// </summary>
public abstract class CertificadoSefaz : IDisposable
{
    /// <summary>Certificado X.509; no A3 contem so a parte publica.</summary>
    public abstract X509Certificate2 Publico { get; }

    /// <summary>true quando a chave privada nao esta nesta maquina (A3).</summary>
    public abstract bool Remoto { get; }

    public string Thumbprint => Publico.Thumbprint ?? string.Empty;

    /// <summary>
    /// Chave RSA para o SignedXml. No A1 e a chave privada real; no A3 e um
    /// proxy que delega SignHash ao agente. O chamador descarta o retorno.
    /// </summary>
    public abstract RSA ObterChaveAssinatura();

    /// <summary>Somente A3: POST SOAP feito pelo agente. No A1 o transporte e local.</summary>
    public virtual Task<RespostaTransporteSoap> PostSoapAsync(
        string url, string envelope, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Este certificado e local: o transporte TLS e feito pela propria API.");

    /// <summary>Somente A1: certificado com chave privada para o handler HTTP.</summary>
    public virtual X509Certificate2 ParaTransporteLocal() =>
        throw new NotSupportedException(
            "Certificado A3: o transporte e feito pelo agente, nao pela API.");

    public abstract void Dispose();

    public static implicit operator CertificadoSefaz?(X509Certificate2? certificado) =>
        certificado is null ? null : new CertificadoLocal(certificado, donoDoCertificado: false);
}

/// <summary>Certificado A1: a chave privada esta no X509Certificate2 carregado em memoria.</summary>
public sealed class CertificadoLocal : CertificadoSefaz
{
    private readonly X509Certificate2 _certificado;
    private readonly bool _dono;

    /// <param name="donoDoCertificado">
    /// Quando true, o Dispose libera o X509Certificate2. Use false quando o
    /// chamador ja gerencia o ciclo de vida (por exemplo, com `using`).
    /// </param>
    public CertificadoLocal(X509Certificate2 certificado, bool donoDoCertificado = true)
    {
        _certificado = certificado ?? throw new ArgumentNullException(nameof(certificado));
        _dono = donoDoCertificado;
    }

    public override X509Certificate2 Publico => _certificado;
    public override bool Remoto => false;

    public override RSA ObterChaveAssinatura()
    {
        if (!_certificado.HasPrivateKey)
            throw new InvalidOperationException(
                "O certificado nao possui chave privada; nao e possivel assinar.");

        return _certificado.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Nao foi possivel obter a chave RSA privada.");
    }

    public override X509Certificate2 ParaTransporteLocal() => _certificado;

    public override void Dispose()
    {
        if (_dono) _certificado.Dispose();
    }
}

/// <summary>Certificado A3: so a parte publica fica na API; a chave esta no token do cliente.</summary>
public sealed class CertificadoRemoto : CertificadoSefaz
{
    private readonly X509Certificate2 _publico;
    private readonly IAgenteCertificado _agente;

    public CertificadoRemoto(X509Certificate2 publico, IAgenteCertificado agente)
    {
        _publico = publico ?? throw new ArgumentNullException(nameof(publico));
        _agente = agente ?? throw new ArgumentNullException(nameof(agente));
    }

    public override X509Certificate2 Publico => _publico;
    public override bool Remoto => true;

    public override RSA ObterChaveAssinatura() => new RsaRemoto(_publico, _agente);

    public override Task<RespostaTransporteSoap> PostSoapAsync(
        string url, string envelope, CancellationToken ct = default) =>
        _agente.PostSoapAsync(url, envelope, ct);

    public override void Dispose() => _publico.Dispose();
}
