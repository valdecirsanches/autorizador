using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Nfse.Contratos;
using NFe.Nfse.Nacional;
using NFe.Sefaz.Xml;

namespace NFe.Nfse.Abrasf;

/// <summary>
/// Esqueleto GENERICO do padrao ABRASF 2.0x (GerarNfse / ConsultarNfsePorRps /
/// CancelarNfse) via SOAP com mTLS. Cada prefeitura/provedor (Ginfes, ISSNet,
/// Betha, WebISS, etc.) costuma divergir em namespace, envelope, nome de
/// operacao e regras de assinatura — por isso este provedor deve ser VALIDADO
/// contra o manual do municipio antes de uso em producao. URL e versao vem de
/// <see cref="EmpresaNfseConfig"/>.
/// </summary>
public sealed class ProvedorAbrasfGenerico : INfseProvedor
{
    public const string NsAbrasf = "http://www.abrasf.org.br/nfse.xsd";

    private static readonly XNamespace Ns = NsAbrasf;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly HttpMtlsClientFactory _http;
    private readonly AssinadorXml _assinador;
    private readonly ILogger<ProvedorAbrasfGenerico>? _log;

    public ProvedorAbrasfGenerico(HttpMtlsClientFactory http, AssinadorXml assinador,
        ILogger<ProvedorAbrasfGenerico>? log = null)
    {
        _http = http;
        _assinador = assinador;
        _log = log;
    }

    public PadraoNfse Padrao => PadraoNfse.Abrasf;

    // ----------------------------------------------------------------- RPS

    public (string Xml, string Id) GerarXml(DpsDados d)
    {
        var r = d.Request;
        var p = d.Prestador;
        var cnpj = Digitos(p.Cnpj);
        var im = Digitos(p.InscricaoMunicipal);
        var id = "rps" + d.NumeroDps.ToString(Inv);
        var v = r.Valores;
        var t = r.Tomador;

        var valores = new XElement(Ns + "Valores",
            El("ValorServicos", Dec(v.ValorServico)));
        if (v.Deducoes > 0) valores.Add(El("ValorDeducoes", Dec(v.Deducoes)));
        if (v.ValorPis > 0) valores.Add(El("ValorPis", Dec(v.ValorPis)));
        if (v.ValorCofins > 0) valores.Add(El("ValorCofins", Dec(v.ValorCofins)));
        if (v.ValorInss > 0) valores.Add(El("ValorInss", Dec(v.ValorInss)));
        if (v.ValorIr > 0) valores.Add(El("ValorIr", Dec(v.ValorIr)));
        if (v.ValorCsll > 0) valores.Add(El("ValorCsll", Dec(v.ValorCsll)));
        if (v.ValorIss > 0) valores.Add(El("ValorIss", Dec(v.ValorIss)));
        if (v.AliquotaIss > 0) valores.Add(El("Aliquota", (v.AliquotaIss / 100m).ToString("0.0000", Inv)));
        if (v.DescontoIncondicionado > 0) valores.Add(El("DescontoIncondicionado", Dec(v.DescontoIncondicionado)));
        if (v.DescontoCondicionado > 0) valores.Add(El("DescontoCondicionado", Dec(v.DescontoCondicionado)));

        var servico = new XElement(Ns + "Servico",
            valores,
            El("IssRetido", v.RetencaoIssqn == TipoRetencaoIssqn.NaoRetido ? "2" : "1"),
            El("ItemListaServico", r.Servico.ItemListaServico ?? ItemDaTribNac(r.Servico.CodigoTributacaoNacional)));
        if (!string.IsNullOrWhiteSpace(r.Servico.Cnae)) servico.Add(El("CodigoCnae", Digitos(r.Servico.Cnae)));
        if (!string.IsNullOrWhiteSpace(r.Servico.CodigoTributacaoMunicipal))
            servico.Add(El("CodigoTributacaoMunicipio", r.Servico.CodigoTributacaoMunicipal));
        servico.Add(El("Discriminacao", Truncar(Limpar(r.Servico.Descricao), 2000)));
        servico.Add(El("CodigoMunicipio", Digitos(r.Servico.CodigoMunicipioPrestacao ?? p.CodigoMunicipio)));
        servico.Add(El("ExigibilidadeISS", "1"));
        servico.Add(El("MunicipioIncidencia", Digitos(r.Servico.CodigoMunicipioPrestacao ?? p.CodigoMunicipio)));

        var prestador = new XElement(Ns + "Prestador",
            new XElement(Ns + "CpfCnpj", El("Cnpj", cnpj)));
        if (im.Length > 0) prestador.Add(El("InscricaoMunicipal", im));

        var infDecl = new XElement(Ns + "InfDeclaracaoPrestacaoServico",
            new XAttribute("Id", id),
            new XElement(Ns + "Rps",
                new XElement(Ns + "IdentificacaoRps",
                    El("Numero", d.NumeroDps.ToString(Inv)),
                    El("Serie", d.Serie.TrimStart('0').PadLeft(1, '0')),
                    El("Tipo", "1")),
                El("DataEmissao", d.DataEmissao.ToString("yyyy-MM-dd", Inv)),
                El("Status", "1")),
            El("Competencia", d.DataCompetencia.ToString("yyyy-MM-dd", Inv)),
            servico,
            prestador);

        if (t is not null && Digitos(t.Documento).Length > 0)
        {
            var doc = Digitos(t.Documento);
            var tom = new XElement(Ns + "Tomador",
                new XElement(Ns + "IdentificacaoTomador",
                    new XElement(Ns + "CpfCnpj", doc.Length == 14 ? El("Cnpj", doc) : El("Cpf", doc))),
                El("RazaoSocial", Truncar(Limpar(t.Nome), 150)));
            if (!string.IsNullOrWhiteSpace(t.Logradouro) && Digitos(t.CodigoMunicipio).Length == 7)
                tom.Add(new XElement(Ns + "Endereco",
                    El("Endereco", Truncar(Limpar(t.Logradouro), 125)),
                    El("Numero", Truncar(Limpar(t.Numero ?? "S/N"), 10)),
                    El("Bairro", Truncar(Limpar(t.Bairro ?? "CENTRO"), 60)),
                    El("CodigoMunicipio", Digitos(t.CodigoMunicipio)),
                    El("Uf", ""),
                    El("Cep", Digitos(t.Cep).PadLeft(8, '0'))));
            if (!string.IsNullOrWhiteSpace(t.Email) || !string.IsNullOrWhiteSpace(t.Telefone))
            {
                var c = new XElement(Ns + "Contato");
                if (!string.IsNullOrWhiteSpace(t.Telefone)) c.Add(El("Telefone", Digitos(t.Telefone)));
                if (!string.IsNullOrWhiteSpace(t.Email)) c.Add(El("Email", t.Email.Trim()));
                tom.Add(c);
            }
            infDecl.Add(tom);
        }

        infDecl.Add(El("OptanteSimplesNacional", d.Config.OpSimpNac == OpcaoSimplesNacional.NaoOptante ? "2" : "1"));
        infDecl.Add(El("IncentivoFiscal", "2"));

        var rps = new XElement(Ns + "Rps", infDecl);
        var doc2 = new XDocument(new XDeclaration("1.0", "UTF-8", null), rps);
        var sb = new StringBuilder();
        using (var w = new StringWriter(sb, Inv)) doc2.Save(w, SaveOptions.DisableFormatting);
        return (sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\""), id);
    }

    public string Assinar(string xml, string id, X509Certificate2 certificado)
        => _assinador.Assinar(xml, id, certificado, nomeTagAssinatura: "Rps");

    // -------------------------------------------------------------- SOAP

    public async Task<ResultadoEmissaoNfse> EmitirAsync(string xmlAssinado, EmpresaNfseConfig config,
        TipoAmbiente ambiente, X509Certificate2 certificado, CancellationToken ct = default)
    {
        var url = ExigirUrl(config);
        var pedido = new XElement(Ns + "GerarNfseEnvio",
            XElement.Parse(xmlAssinado, LoadOptions.PreserveWhitespace));

        var (corpo, ms) = await SoapAsync(url, "GerarNfse", pedido.ToString(SaveOptions.DisableFormatting), certificado, ct);
        var ret = Interno(corpo);

        var msgs = ret.Descendants().Where(e => e.Name.LocalName == "MensagemRetorno").ToList();
        var comp = ret.Descendants().FirstOrDefault(e => e.Name.LocalName == "CompNfse");
        if (comp is not null)
        {
            var inf = comp.Descendants().FirstOrDefault(e => e.Name.LocalName == "InfNfse");
            string? V(string n) => inf?.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim();
            return new ResultadoEmissaoNfse(true, null, V("Numero"), V("CodigoVerificacao"),
                comp.ToString(SaveOptions.DisableFormatting), "100", "NFS-e gerada com sucesso", corpo,
                DateTime.TryParse(V("DataEmissao"), Inv, DateTimeStyles.AssumeLocal, out var dt) ? dt.ToUniversalTime() : null, ms);
        }

        var (cod, msg) = Erros(msgs, corpo);
        return new ResultadoEmissaoNfse(false, null, null, null, null, cod, msg, corpo, null, ms);
    }

    public async Task<ResultadoConsultaNfse> ConsultarAsync(string chaveAcesso, EmpresaNfseConfig config,
        TipoAmbiente ambiente, X509Certificate2 certificado, CancellationToken ct = default)
    {
        // No ABRASF a "chave" e o numero da NFS-e; a consulta por numero exige
        // o CNPJ do prestador, que nao temos aqui. Mantido como nao suportado
        // no esqueleto generico.
        await Task.CompletedTask;
        return new ResultadoConsultaNfse(false, false, chaveAcesso, null, null, "501",
            "Consulta por chave nao implementada para o provedor ABRASF generico.", "");
    }

    public async Task<ResultadoEventoNfse> CancelarAsync(NotaServico nota, string codigoMotivo, string justificativa,
        int sequencia, EmpresaNfseConfig config, X509Certificate2 certificado, CancellationToken ct = default)
    {
        var url = ExigirUrl(config);
        var cnpj = Digitos(nota.Empresa?.Cnpj);
        var im = Digitos(nota.Empresa?.InscricaoMunicipal);
        var id = "canc" + (nota.NumeroNfse ?? "0");

        var infPed = new XElement(Ns + "InfPedidoCancelamento",
            new XAttribute("Id", id),
            new XElement(Ns + "IdentificacaoNfse",
                El("Numero", nota.NumeroNfse ?? ""),
                new XElement(Ns + "CpfCnpj", El("Cnpj", cnpj)),
                El("InscricaoMunicipal", im),
                El("CodigoMunicipio", Digitos(config.CodigoMunicipioEmissao ?? nota.Empresa?.CodigoMunicipio))),
            El("CodigoCancelamento", codigoMotivo));

        var pedido = new XElement(Ns + "Pedido", infPed);
        var assinado = _assinador.Assinar(pedido.ToString(SaveOptions.DisableFormatting), id, certificado, "Pedido");
        var envio = new XElement(Ns + "CancelarNfseEnvio", XElement.Parse(assinado, LoadOptions.PreserveWhitespace));

        var (corpo, _) = await SoapAsync(url, "CancelarNfse", envio.ToString(SaveOptions.DisableFormatting), certificado, ct);
        var ret = Interno(corpo);

        var conf = ret.Descendants().FirstOrDefault(e => e.Name.LocalName == "Confirmacao");
        if (conf is not null)
        {
            var dh = conf.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataHora")?.Value;
            return new ResultadoEventoNfse(true, null, "100", "Cancelamento confirmado", assinado, corpo,
                DateTime.TryParse(dh, Inv, DateTimeStyles.AssumeLocal, out var d) ? d.ToUniversalTime() : null);
        }

        var msgs = ret.Descendants().Where(e => e.Name.LocalName == "MensagemRetorno").ToList();
        var (cod, msg) = Erros(msgs, corpo);
        return new ResultadoEventoNfse(false, null, cod, msg, assinado, corpo, null);
    }

    private async Task<(string Corpo, long Ms)> SoapAsync(string url, string operacao, string xmlPedido,
        X509Certificate2 cert, CancellationToken ct)
    {
        // Envelope SOAP 1.2 generico: <operacao>Request com nfseCabecMsg + nfseDadosMsg
        // como texto escapado (forma mais comum nos provedores ABRASF 2.0x).
        XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";
        XNamespace ws = "http://nfse.abrasf.org.br";
        var cabec = $"<cabecalho xmlns=\"{NsAbrasf}\" versao=\"{_versao(url)}\"><versaoDados>{_versao(url)}</versaoDados></cabecalho>";

        var env = new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
            new XElement(soap + "Body",
                new XElement(ws + operacao + "Request",
                    new XElement(ws + "nfseCabecMsg", cabec),
                    new XElement(ws + "nfseDadosMsg", xmlPedido))));

        var http = _http.Obter(cert, url);
        var sw = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(env.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            using var resp = await http.PostAsync(url, content, ct);
            var corpo = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();
            if (!resp.IsSuccessStatusCode && !corpo.Contains("MensagemRetorno", StringComparison.OrdinalIgnoreCase))
                throw new NfseProvedorException($"HTTP {(int)resp.StatusCode} do provedor ABRASF em {url}.", (int)resp.StatusCode, corpo);
            return (corpo, sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            throw new NfseProvedorException($"Falha de transporte ao chamar {url}: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NfseProvedorException($"Timeout ao chamar {url}.", inner: ex);
        }
    }

    private string _versaoCache = "2.04";
    private string _versao(string _) => _versaoCache;

    private string ExigirUrl(EmpresaNfseConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.AbrasfUrl))
            throw new InvalidOperationException(
                "Configuracao NFS-e da empresa sem AbrasfUrl. Informe a URL do webservice do municipio.");
        if (!string.IsNullOrWhiteSpace(cfg.AbrasfVersao)) _versaoCache = cfg.AbrasfVersao;
        return cfg.AbrasfUrl;
    }

    /// <summary>Desembrulha o retorno escapado dentro de outputXML / *Response.</summary>
    private static XElement Interno(string corpo)
    {
        var doc = XDocument.Parse(corpo);
        var txt = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "outputXML" or "return"
            || e.Name.LocalName.EndsWith("Result", StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(txt) && txt.TrimStart().StartsWith('<'))
        {
            try { return XElement.Parse(txt); } catch (System.Xml.XmlException) { }
        }
        return doc.Root!;
    }

    private static (string, string) Erros(List<XElement> msgs, string corpo)
    {
        if (msgs.Count == 0)
        {
            var r = corpo.Length > 500 ? corpo[..500] + "..." : corpo;
            return ("ERR", "Retorno inesperado do provedor ABRASF: " + r);
        }
        string? primeiro = null;
        var lista = msgs.Select(m =>
        {
            var cod = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Codigo")?.Value ?? "?";
            var men = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Mensagem")?.Value ?? "";
            var cor = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Correcao")?.Value;
            primeiro ??= cod;
            return string.IsNullOrWhiteSpace(cor) ? $"{cod}: {men}" : $"{cod}: {men} ({cor})";
        });
        var texto = string.Join(" | ", lista);
        return (primeiro ?? "ERR", texto);
    }

    private static string ItemDaTribNac(string cTribNac)
    {
        var d = Digitos(cTribNac).PadLeft(6, '0');
        return $"{d[..2].TrimStart('0').PadLeft(1, '0')}.{d[2..4]}";
    }

    private static XElement El(string n, string v) => new(Ns + n, v);
    private static string Dec(decimal v) => v.ToString("0.00", Inv);
    private static string Digitos(string? s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\D", "");
    private static string Limpar(string? s) => string.IsNullOrWhiteSpace(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();
    private static string Truncar(string s, int max) => s.Length <= max ? s : s[..max];
}
