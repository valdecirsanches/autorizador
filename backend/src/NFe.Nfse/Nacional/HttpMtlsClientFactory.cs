using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NFe.Sefaz.Soap;

namespace NFe.Nfse.Nacional;

/// <summary>Opcoes de transporte do modulo NFS-e.</summary>
public sealed class NfseHttpOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Ver <see cref="SefazSoapOptions.PermitirCadeiaNaoConfiavel"/>: mesma semantica.</summary>
    public bool PermitirCadeiaNaoConfiavel { get; set; }

    public bool LogarCorpos { get; set; }
}

/// <summary>
/// Fabrica/cache de <see cref="HttpClient"/> com autenticacao mutua TLS (mTLS)
/// usando o A1 da empresa. Replica a tecnica ja validada no SefazSoapClient
/// (um cliente por thumbprint+host, copia independente do certificado, validacao
/// da cadeia ICP-Brasil embutida) sem alterar aquele arquivo.
/// </summary>
public sealed class HttpMtlsClientFactory : IDisposable
{
    private readonly NfseHttpOptions _opts;
    private readonly ILogger<HttpMtlsClientFactory>? _log;
    private readonly ConcurrentDictionary<string, HttpClient> _clientes = new();
    private readonly ConcurrentDictionary<string, X509Certificate2> _certificados = new();
    private bool _disposed;

    public HttpMtlsClientFactory(NfseHttpOptions? opts = null, ILogger<HttpMtlsClientFactory>? log = null)
    {
        _opts = opts ?? new NfseHttpOptions();
        _log = log;
    }

    public NfseHttpOptions Opcoes => _opts;

    public HttpClient Obter(X509Certificate2 certificado, string url)
    {
        ArgumentNullException.ThrowIfNull(certificado);

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var chave = $"{certificado.Thumbprint}|{host}|{_opts.PermitirCadeiaNaoConfiavel}";

        return _clientes.GetOrAdd(chave, _ =>
        {
            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                               | System.Security.Authentication.SslProtocols.Tls13,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var copia = ClonarComChavePrivada(certificado);
            _certificados[chave] = copia;
            handler.ClientCertificates.Add(copia);

            handler.ServerCertificateCustomValidationCallback = (_, cert, _, erros) =>
            {
                // O servidor do gov.br pode usar cadeia publica (nao ICP-Brasil).
                // Se o SO ja validou (erros == None) aceitamos; caso contrario
                // tentamos a cadeia ICP-Brasil embutida e, por fim, a flag.
                if (erros == SslPolicyErrors.None) return true;

                var aceito = TrustStoreIcpBrasil.Validar(cert, erros, _opts.PermitirCadeiaNaoConfiavel);
                if (!aceito)
                    _log?.LogWarning(
                        "[NFSe] Certificado do servidor {Assunto} recusado. Erros: {Erros}.",
                        cert?.Subject, erros);
                return aceito;
            };

            var http = new HttpClient(handler, disposeHandler: true) { Timeout = _opts.Timeout };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AprovadorNFSe/1.0");
            return http;
        });
    }

    private static X509Certificate2 ClonarComChavePrivada(X509Certificate2 origem)
    {
        var senha = Guid.NewGuid().ToString("N");
        var pfx = origem.Export(X509ContentType.Pkcs12, senha);
        return X509CertificateLoader.LoadPkcs12(
            pfx, senha, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var c in _clientes.Values) c.Dispose();
        _clientes.Clear();
        foreach (var c in _certificados.Values) c.Dispose();
        _certificados.Clear();
    }
}
