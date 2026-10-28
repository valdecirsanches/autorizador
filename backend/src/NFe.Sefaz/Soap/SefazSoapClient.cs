using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;

namespace NFe.Sefaz.Soap;

/// <summary>Opcoes de transporte para as chamadas a SEFAZ.</summary>
public sealed class SefazSoapOptions
{
    /// <summary>Timeout de cada chamada. A SEFAZ pode ser lenta em horario de pico.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quando true, aceita o certificado do servidor mesmo sem cadeia confiavel.
    /// A cadeia ICP-Brasil frequentemente nao esta no trust store de containers
    /// Linux. Use apenas em desenvolvimento: em producao instale as raizes
    /// ICP-Brasil no sistema e mantenha esta opcao em false.
    /// </summary>
    public bool PermitirCadeiaNaoConfiavel { get; set; }

    /// <summary>Registra os envelopes enviados e recebidos no log (nivel Debug).</summary>
    public bool LogarEnvelopes { get; set; }
}

/// <summary>Resultado bruto de uma chamada SOAP.</summary>
public sealed record RespostaSoap(
    HttpStatusCode StatusHttp,
    string EnvelopeEnviado,
    string EnvelopeRecebido,
    XElement ConteudoRetorno,
    long DuracaoMs);

public sealed class SefazSoapException : Exception
{
    public HttpStatusCode? StatusHttp { get; }
    public string? EnvelopeRecebido { get; }

    public SefazSoapException(
        string message,
        HttpStatusCode? statusHttp = null,
        string? envelopeRecebido = null,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusHttp = statusHttp;
        EnvelopeRecebido = envelopeRecebido;
    }
}

/// <summary>
/// Cliente SOAP 1.2 para os web services da NF-e, com autenticacao mutua TLS
/// usando o certificado A1 da empresa.
/// </summary>
public sealed class SefazSoapClient : IDisposable
{
    private readonly SefazSoapOptions _opts;
    private readonly ILogger<SefazSoapClient>? _log;

    // Um HttpClient por certificado: criar por requisicao esgotaria sockets e
    // impediria o reuso de sessao TLS, que e caro no handshake mutuo.
    private readonly ConcurrentDictionary<string, HttpClient> _clientes = new();

    // Copias do certificado cliente pertencentes ao cache. O chamador carrega o
    // certificado com `using` e o descarta ao fim de cada consulta; se o handler
    // cacheado referenciasse esse objeto, a proxima consulta usaria um handle ja
    // liberado ("m_safeCertContext is an invalid handle"). Guardamos uma copia
    // independente, cuja vida acompanha o HttpClient.
    private readonly ConcurrentDictionary<string, X509Certificate2> _certificadosCache = new();
    private bool _disposed;

    public SefazSoapClient(SefazSoapOptions? opts = null, ILogger<SefazSoapClient>? log = null)
    {
        _opts = opts ?? new SefazSoapOptions();
        _log = log;
    }

    /// <summary>
    /// Envia o XML da mensagem ao endpoint resolvido e devolve o conteudo util
    /// da resposta (o elemento dentro de nfeResultMsg).
    /// </summary>
    public async Task<RespostaSoap> EnviarAsync(
        EndpointResolvido endpoint,
        string xmlMensagem,
        CertificadoSefaz certificado,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(certificado);
        if (string.IsNullOrWhiteSpace(xmlMensagem))
            throw new ArgumentException("XML da mensagem vazio.", nameof(xmlMensagem));

        var envelope = MontarEnvelope(endpoint.Servico, xmlMensagem);
        var cronometro = Stopwatch.StartNew();

        if (_opts.LogarEnvelopes)
            _log?.LogDebug("[SEFAZ] POST {Url}\n{Envelope}", endpoint.Url, envelope);

        HttpStatusCode statusHttp;
        string corpo;
        try
        {
            if (certificado.Remoto)
            {
                // A3: o TLS mutuo exige a chave privada, que esta no token do
                // cliente. O agente faz o POST e devolve a resposta bruta; todo o
                // tratamento dela continua aqui, igual ao A1.
                var remota = await certificado.PostSoapAsync(endpoint.Url, envelope, ct);
                statusHttp = remota.Status;
                corpo = remota.Corpo;
            }
            else
            {
                var cliente = ObterCliente(certificado.ParaTransporteLocal(), endpoint.Url);

                using var conteudo = new StringContent(envelope, new UTF8Encoding(false));
                conteudo.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml")
                    {
                        CharSet = "utf-8"
                    };

                using var resposta = await cliente.PostAsync(endpoint.Url, conteudo, ct);
                statusHttp = resposta.StatusCode;
                corpo = await resposta.Content.ReadAsStringAsync(ct);
            }
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SefazSoapException(
                $"Timeout de {_opts.Timeout.TotalSeconds:0}s ao chamar " +
                $"{endpoint.Servico} em {endpoint.NomeAutorizador} ({endpoint.Url}).",
                inner: ex);
        }
        catch (HttpRequestException ex)
        {
            // Causa mais comum em container: cadeia ICP-Brasil ausente no trust
            // store. A mensagem externa costuma ser generica ("see inner
            // exception"); desdobramos toda a cadeia de inner exceptions para a
            // causa REAL (ex.: "RemoteCertificateChainErrors") aparecer no card.
            throw new SefazSoapException(
                $"Falha de transporte ao chamar {endpoint.Servico} em " +
                $"{endpoint.NomeAutorizador} ({endpoint.Url}): {DescreverCadeia(ex)}",
                inner: ex);
        }

        cronometro.Stop();

        if (_opts.LogarEnvelopes)
            _log?.LogDebug("[SEFAZ] {Status} em {Ms}ms\n{Corpo}",
                (int)statusHttp, cronometro.ElapsedMilliseconds, corpo);

        // A SEFAZ devolve 500 com SOAP Fault em erros de schema: a mensagem
        // do Fault e mais util que o codigo HTTP, entao tentamos extrair.
        var falha = ExtrairFault(corpo);
        if (falha is not null)
            throw new SefazSoapException(
                $"SOAP Fault de {endpoint.NomeAutorizador} em {endpoint.Servico}: {falha}",
                statusHttp, corpo);

        if ((int)statusHttp is < 200 or > 299)
            throw new SefazSoapException(
                $"HTTP {(int)statusHttp} de {endpoint.NomeAutorizador} " +
                $"em {endpoint.Servico}. " +
                (statusHttp == HttpStatusCode.Forbidden
                    ? "403 normalmente indica certificado cliente ausente, invalido ou sem credenciamento na UF."
                    : string.Empty),
                statusHttp, corpo);

        var retorno = ExtrairRetorno(corpo, endpoint.Servico)
            ?? throw new SefazSoapException(
                $"Resposta sem elemento '{NamespacesSefaz.ElementoRetorno(endpoint.Servico)}'.",
                statusHttp, corpo);

        return new RespostaSoap(
            statusHttp, envelope, corpo, retorno, cronometro.ElapsedMilliseconds);
    }

    /// <summary>
    /// Concatena a mensagem da excecao e de todas as suas inner exceptions.
    /// Sem isso, erros de TLS aparecem apenas como "see inner exception" e a
    /// causa concreta (cadeia, nome do host, protocolo) fica escondida.
    /// </summary>
    private static string DescreverCadeia(Exception ex)
    {
        var partes = new List<string>();
        for (Exception? atual = ex; atual is not null; atual = atual.InnerException)
        {
            var texto = atual.Message?.Trim();
            if (!string.IsNullOrEmpty(texto) &&
                (partes.Count == 0 || partes[^1] != texto))
                partes.Add(texto);
        }
        return string.Join(" -> ", partes);
    }

    /// <summary>Monta o envelope SOAP 1.2 com a mensagem dentro de nfeDadosMsg.</summary>
    public static string MontarEnvelope(ServicoSefaz servico, string xmlMensagem)
    {
        var soap = XNamespace.Get(NamespacesSefaz.Soap12);
        var wsdl = XNamespace.Get(NamespacesSefaz.Wsdl(servico));

        // Parse antes de embutir: falha aqui e erro de programacao nosso, nao da SEFAZ.
        var mensagem = XElement.Parse(RemoverDeclaracao(xmlMensagem), LoadOptions.PreserveWhitespace);

        var envelope = new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap12", NamespacesSefaz.Soap12),
            new XElement(soap + "Body",
                new XElement(wsdl + "nfeDadosMsg", mensagem)));

        // A SEFAZ rejeita BOM e nao exige declaracao XML no envelope.
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    private static string RemoverDeclaracao(string xml)
    {
        var texto = xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!texto.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return texto;

        var fim = texto.IndexOf("?>", StringComparison.Ordinal);
        return fim < 0 ? texto : texto[(fim + 2)..].TrimStart();
    }

    /// <summary>
    /// Localiza o elemento de retorno independente de prefixo ou aninhamento.
    /// Publico para permitir teste do parsing sem depender de rede.
    /// </summary>
    public static XElement? ExtrairRetorno(string corpo, ServicoSefaz servico)
    {
        XDocument doc;
        try { doc = XDocument.Parse(corpo); }
        catch (System.Xml.XmlException) { return null; }

        var alvo = NamespacesSefaz.ElementoRetorno(servico);

        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == alvo)
            // Fallback: alguns autorizadores encapsulam o retorno como string
            // escapada dentro de nfeResultMsg.
            ?? DesescapadoDentroDeResultMsg(doc, alvo);
    }

    private static XElement? DesescapadoDentroDeResultMsg(XDocument doc, string alvo)
    {
        var resultMsg = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "nfeResultMsg");

        if (resultMsg is null || resultMsg.HasElements) return null;

        var texto = resultMsg.Value?.Trim();
        if (string.IsNullOrEmpty(texto) || !texto.StartsWith('<')) return null;

        try
        {
            var interno = XDocument.Parse(texto);
            return interno.Descendants().FirstOrDefault(e => e.Name.LocalName == alvo);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// Extrai a descricao de um SOAP Fault, ou null se a resposta nao for Fault.
    /// Publico para permitir teste do parsing sem depender de rede.
    /// </summary>
    public static string? ExtrairFault(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo) ||
            !corpo.Contains("Fault", StringComparison.OrdinalIgnoreCase))
            return null;

        XDocument doc;
        try { doc = XDocument.Parse(corpo); }
        catch (System.Xml.XmlException) { return null; }

        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is null) return null;

        // SOAP 1.2 usa Reason/Text; SOAP 1.1 usa faultstring.
        var motivo = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                     ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                     ?? fault.Value;

        return string.IsNullOrWhiteSpace(motivo) ? "sem descricao" : motivo.Trim();
    }

    private HttpClient ObterCliente(X509Certificate2 certificado, string url)
    {
        // Um HttpClient por (certificado + host + flag). O host entra na chave
        // porque compartilhar um unico cliente mTLS entre hosts distintos (ex.:
        // producao e homologacao da mesma UF) causava falha alternada na segunda
        // consulta ("invalid handle" / erro de TLS) conforme a ordem de uso.
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

            // Copia independente do certificado: o chamador descarta o dele com
            // `using` ao fim da consulta, mas o handler cacheado sobrevive entre
            // requisicoes e precisa de um handle valido.
            var certificadoCache = ClonarComChavePrivada(certificado);
            _certificadosCache[chave] = certificadoCache;
            handler.ClientCertificates.Add(certificadoCache);

            // Validacao do certificado do SERVIDOR sempre ativa: confiamos na
            // cadeia ICP-Brasil embutida no assembly, independente do trust
            // store do SO. Isso resolve o classico "UntrustedRoot" em Linux sem
            // exigir instalacao manual das raizes. A flag so entra como ultimo
            // recurso, quando ate a validacao ICP-Brasil falha.
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, erros) =>
            {
                var aceito = TrustStoreIcpBrasil.Validar(
                    cert, erros, _opts.PermitirCadeiaNaoConfiavel);

                if (!aceito || erros != SslPolicyErrors.None)
                    _log?.LogWarning(
                        "[SEFAZ] Certificado do servidor {Assunto} com erros {Erros}. Aceito: {Aceito}.",
                        cert?.Subject, erros, aceito);

                return aceito;
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = _opts.Timeout
            };
        });
    }

    /// <summary>
    /// Cria uma copia independente do certificado (com a chave privada) cuja
    /// vida e gerida por este cache, evitando o "invalid handle" quando o
    /// chamador descarta a instancia original. Exporta para PKCS#12 com senha
    /// efemera e recarrega, o que funciona de forma consistente no Linux.
    /// </summary>
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

        foreach (var cliente in _clientes.Values) cliente.Dispose();
        _clientes.Clear();

        foreach (var cert in _certificadosCache.Values) cert.Dispose();
        _certificadosCache.Clear();
    }
}
