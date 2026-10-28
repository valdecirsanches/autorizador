using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;

namespace NFe.AgenteA3;

/// <summary>POST SOAP com TLS mutuo usando o certificado do token.</summary>
public sealed class TransporteSoap : IDisposable
{
    private readonly HttpClient _http;

    public TransporteSoap(TokenCertificado token, bool permitirCadeiaNaoConfiavel)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = [token.Certificado],
                RemoteCertificateValidationCallback = (_, _, _, erros) =>
                    erros == SslPolicyErrors.None
                    || (permitirCadeiaNaoConfiavel
                        && erros == SslPolicyErrors.RemoteCertificateChainErrors),
            },
        };

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// O agente nao pode virar um proxy aberto: so fala HTTPS com servidores .gov.br,
    /// que e onde ficam os web services da SEFAZ.
    /// </summary>
    public static void ValidarDestino(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith(".gov.br", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Destino recusado: so HTTPS em dominios .gov.br.");
    }

    public async Task<(int Status, string Corpo)> PostAsync(
        string url, string envelope, CancellationToken ct)
    {
        ValidarDestino(url);

        using var conteudo = new StringContent(envelope, new UTF8Encoding(false));
        conteudo.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };

        using var resposta = await _http.PostAsync(url, conteudo, ct);
        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        return ((int)resposta.StatusCode, corpo);
    }

    public void Dispose() => _http.Dispose();
}
