using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Nfse.Contratos;
using NFe.Sefaz.Xml;

namespace NFe.Nfse.Nacional;

/// <summary>
/// Provedor do Sistema Nacional NFS-e (gov.br): API REST da SEFIN Nacional com
/// mTLS. Emissao sincrona (POST /nfse), consulta (GET /nfse/{chave}) e eventos
/// (POST /nfse/{chave}/eventos). Todo XML trafega como GZip+Base64.
/// </summary>
public sealed class ProvedorNacional : INfseProvedor
{
    private readonly HttpMtlsClientFactory _http;
    private readonly GeradorXmlDps _geradorDps;
    private readonly GeradorXmlEventoNfse _geradorEvento;
    private readonly AssinadorXml _assinador;
    private readonly ILogger<ProvedorNacional>? _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProvedorNacional(
        HttpMtlsClientFactory http,
        GeradorXmlDps geradorDps,
        GeradorXmlEventoNfse geradorEvento,
        AssinadorXml assinador,
        ILogger<ProvedorNacional>? log = null)
    {
        _http = http;
        _geradorDps = geradorDps;
        _geradorEvento = geradorEvento;
        _assinador = assinador;
        _log = log;
    }

    public PadraoNfse Padrao => PadraoNfse.Nacional;

    public (string Xml, string Id) GerarXml(DpsDados dados) => _geradorDps.Gerar(dados);

    /// <summary>A Signature e filha de DPS, irma de infDPS (mesmo padrao da NF-e).</summary>
    public string Assinar(string xml, string id, X509Certificate2 certificado)
        => _assinador.Assinar(xml, id, certificado, nomeTagAssinatura: "DPS");

    // ------------------------------------------------------------- emissao

    public async Task<ResultadoEmissaoNfse> EmitirAsync(
        string xmlAssinado, EmpresaNfseConfig config, TipoAmbiente ambiente,
        X509Certificate2 certificado, CancellationToken ct = default)
    {
        var url = NfseNacionalEndpoints.UrlEmitir(ambiente);
        var body = JsonSerializer.Serialize(new { dpsXmlGZipB64 = GzipBase64.Compactar(xmlAssinado) });

        var (status, corpo, ms) = await ChamarAsync(HttpMethod.Post, url, body, certificado, ct);

        using var json = TentarJson(corpo);
        var raiz = json?.RootElement;

        // Sucesso: 200/201 com chaveAcesso + nfseXmlGZipB64.
        if (status is (HttpStatusCode.OK or HttpStatusCode.Created) && raiz is { } ok
            && Prop(ok, "chaveAcesso") is { Length: 50 } chave)
        {
            var xmlNfse = GzipBase64.TentarDescompactar(Prop(ok, "nfseXmlGZipB64"));
            var info = xmlNfse is null ? null : LerInfNfse(xmlNfse);

            return new ResultadoEmissaoNfse(
                Sucesso: true,
                ChaveAcesso: chave,
                NumeroNfse: info?.NumeroNfse,
                CodigoVerificacao: null,
                XmlNfse: xmlNfse,
                Codigo: info?.CStat ?? "100",
                Motivo: "NFS-e gerada com sucesso",
                RetornoBruto: corpo,
                DataAutorizacao: info?.DhProc ?? ParseData(Prop(ok, "dataHoraProcessamento")),
                DuracaoMs: ms);
        }

        // 5xx/429 sem lista de erros = SEFIN fora do ar. A DPS nao foi analisada, entao nao
        // pode virar "Rejeitada": vira falha de transmissao e e reenviada com o MESMO nDPS.
        if (EhIndisponibilidade(status) && !TemListaDeErros(raiz))
            throw new NfseProvedorException(
                $"SEFIN Nacional indisponivel (HTTP {(int)status}). A DPS nao foi processada.",
                (int)status, corpo);

        var (cod, msg) = ExtrairErros(raiz, status, corpo);
        return new ResultadoEmissaoNfse(false, null, null, null, null, cod, msg, corpo, null, ms);
    }

    internal static bool EhIndisponibilidade(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

    private static bool TemListaDeErros(JsonElement? raiz)
    {
        if (raiz is not { ValueKind: JsonValueKind.Object } r) return false;
        foreach (var p in r.EnumerateObject())
            if (string.Equals(p.Name, "erros", StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                return true;
        return false;
    }

    // ------------------------------------------------------------ consulta

    public async Task<ResultadoConsultaNfse> ConsultarAsync(
        string chaveAcesso, EmpresaNfseConfig config, TipoAmbiente ambiente,
        X509Certificate2 certificado, CancellationToken ct = default)
    {
        var url = NfseNacionalEndpoints.UrlConsultar(ambiente, chaveAcesso);
        var (status, corpo, _) = await ChamarAsync(HttpMethod.Get, url, null, certificado, ct);

        using var json = TentarJson(corpo);
        var raiz = json?.RootElement;

        if (status == HttpStatusCode.OK && raiz is { } ok)
        {
            var xmlNfse = GzipBase64.TentarDescompactar(Prop(ok, "nfseXmlGZipB64"));
            var info = xmlNfse is null ? null : LerInfNfse(xmlNfse);
            return new ResultadoConsultaNfse(
                Encontrada: true,
                Cancelada: info?.Cancelada ?? false,
                ChaveAcesso: Prop(ok, "chaveAcesso") ?? chaveAcesso,
                NumeroNfse: info?.NumeroNfse,
                XmlNfse: xmlNfse,
                Codigo: info?.CStat ?? "100",
                Motivo: "NFS-e localizada",
                RetornoBruto: corpo);
        }

        if (status == HttpStatusCode.NotFound)
            return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, "404",
                "NFS-e nao encontrada para a chave informada.", corpo);

        var (cod, msg) = ExtrairErros(raiz, status, corpo);
        return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, cod, msg, corpo);
    }

    // -------------------------------------------------------- cancelamento

    public async Task<ResultadoEventoNfse> CancelarAsync(
        NotaServico nota, string codigoMotivo, string justificativa, int sequencia,
        EmpresaNfseConfig config, X509Certificate2 certificado, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nota.ChaveAcesso))
            throw new InvalidOperationException("Nota sem chave de acesso; nao e possivel cancelar.");

        var cnpj = nota.Empresa?.Cnpj
            ?? throw new InvalidOperationException("Nota sem empresa carregada.");

        var (xml, id) = _geradorEvento.GerarCancelamento(
            nota.ChaveAcesso, cnpj, nota.Ambiente, config.VersaoAplicativo, codigoMotivo, justificativa, sequencia);

        var assinado = _assinador.Assinar(xml, id, certificado, nomeTagAssinatura: "pedRegEvento");

        var url = NfseNacionalEndpoints.UrlEventos(nota.Ambiente, nota.ChaveAcesso);
        var body = JsonSerializer.Serialize(new { pedidoRegistroEventoXmlGZipB64 = GzipBase64.Compactar(assinado) });

        var (status, corpo, _) = await ChamarAsync(HttpMethod.Post, url, body, certificado, ct);

        using var json = TentarJson(corpo);
        var raiz = json?.RootElement;

        if (status is (HttpStatusCode.OK or HttpStatusCode.Created) && raiz is { } ok)
        {
            var xmlEvento = GzipBase64.TentarDescompactar(Prop(ok, "eventoXmlGZipB64"));
            var protocolo = xmlEvento is null ? null : ValorLocal(xmlEvento, "nProt") ?? ValorLocal(xmlEvento, "nSeqEvento");
            var dh = xmlEvento is null ? null : ParseData(ValorLocal(xmlEvento, "dhProc"));

            return new ResultadoEventoNfse(
                Homologado: true,
                Protocolo: protocolo,
                Codigo: "100",
                Motivo: "Evento de cancelamento registrado",
                XmlEnviado: assinado,
                RetornoBruto: corpo,
                DataRegistro: dh ?? ParseData(Prop(ok, "dataHoraProcessamento")));
        }

        var (cod, msg) = ExtrairErros(raiz, status, corpo);
        return new ResultadoEventoNfse(false, null, cod, msg, assinado, corpo, null);
    }

    // ---------------------------------------------------------- transporte

    private async Task<(HttpStatusCode Status, string Corpo, long Ms)> ChamarAsync(
        HttpMethod metodo, string url, string? jsonBody, X509Certificate2 cert, CancellationToken ct)
    {
        var http = _http.Obter(cert, url);
        var sw = Stopwatch.StartNew();

        if (_http.Opcoes.LogarCorpos)
            _log?.LogDebug("[NFSe] {Metodo} {Url}\n{Body}", metodo, url, jsonBody);

        try
        {
            using var req = new HttpRequestMessage(metodo, url);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, new UTF8Encoding(false), "application/json");

            using var resp = await http.SendAsync(req, ct);
            var corpo = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (_http.Opcoes.LogarCorpos)
                _log?.LogDebug("[NFSe] {Status} em {Ms}ms\n{Corpo}", (int)resp.StatusCode, sw.ElapsedMilliseconds, corpo);

            // 401/403 quase sempre e mTLS: certificado nao aceito ou CNPJ nao habilitado.
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new NfseProvedorException(
                    $"HTTP {(int)resp.StatusCode} da SEFIN Nacional em {url}. Normalmente indica " +
                    "certificado cliente nao aceito ou empresa sem habilitacao no ambiente.",
                    (int)resp.StatusCode, corpo);

            return (resp.StatusCode, corpo, sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NfseProvedorException(
                $"Timeout de {_http.Opcoes.Timeout.TotalSeconds:0}s ao chamar {url}.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new NfseProvedorException(
                $"Falha de transporte ao chamar {url}: {DescreverCadeia(ex)}", inner: ex);
        }
    }

    private static string DescreverCadeia(Exception ex)
    {
        var partes = new List<string>();
        for (Exception? a = ex; a is not null; a = a.InnerException)
        {
            var t = a.Message?.Trim();
            if (!string.IsNullOrEmpty(t) && (partes.Count == 0 || partes[^1] != t)) partes.Add(t);
        }
        return string.Join(" -> ", partes);
    }

    // -------------------------------------------------------------- parse

    private static JsonDocument? TentarJson(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try { return JsonDocument.Parse(corpo); }
        catch (JsonException) { return null; }
    }

    private static string? Prop(JsonElement el, string nome)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    /// <summary>
    /// Erros da SEFIN vem como { "erros": [ { "codigo", "descricao", "complemento" } ] }.
    /// Devolve o primeiro codigo e a lista completa de descricoes.
    /// </summary>
    internal static (string Codigo, string Mensagem) ExtrairErros(JsonElement? raiz, HttpStatusCode status, string corpo)
    {
        if (raiz is { ValueKind: JsonValueKind.Object } r)
        {
            foreach (var p in r.EnumerateObject())
            {
                if (!string.Equals(p.Name, "erros", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind != JsonValueKind.Array) continue;

                var itens = new List<string>();
                string? primeiro = null;
                foreach (var e in p.Value.EnumerateArray())
                {
                    var cod = Prop(e, "codigo");
                    var desc = Prop(e, "descricao") ?? Prop(e, "mensagem") ?? e.ToString();
                    var comp = Prop(e, "complemento");
                    primeiro ??= cod;
                    itens.Add(string.IsNullOrEmpty(comp) ? $"{cod}: {desc}" : $"{cod}: {desc} ({comp})");
                }
                if (itens.Count > 0)
                    return (primeiro ?? ((int)status).ToString(), string.Join(" | ", itens));
            }

            var msg = Prop(r, "mensagem") ?? Prop(r, "message") ?? Prop(r, "title");
            if (!string.IsNullOrWhiteSpace(msg))
                return (((int)status).ToString(), msg);
        }

        var resumo = string.IsNullOrWhiteSpace(corpo) ? "sem corpo" : corpo.Trim();
        if (resumo.Length > 500) resumo = resumo[..500] + "...";
        return (((int)status).ToString(), $"HTTP {(int)status} da SEFIN Nacional: {resumo}");
    }

    internal sealed record InfNfse(string? NumeroNfse, string? CStat, DateTime? DhProc, bool Cancelada);

    internal static InfNfse? LerInfNfse(string xmlNfse)
    {
        try
        {
            var doc = XDocument.Parse(xmlNfse);
            var inf = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "infNFSe");
            if (inf is null) return null;

            string? V(string n) => inf.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim();

            var cancelada = doc.Descendants().Any(e => e.Name.LocalName is "e101101" or "e105102");
            return new InfNfse(V("nNFSe"), V("cStat"), ParseData(V("dhProc")), cancelada);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string? ValorLocal(string xml, string nome)
    {
        try
        {
            return XDocument.Parse(xml).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static DateTime? ParseData(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : null;
    }
}
