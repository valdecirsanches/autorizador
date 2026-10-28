using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Soap;

namespace NFe.Sefaz.Servicos;

/// <summary>Situacao do protocolo de uma NF-e dentro do lote.</summary>
public sealed record ProtocoloNFe(
    string ChaveAcesso,
    string CodigoStatus,
    string Motivo,
    string? NumeroProtocolo,
    string? DigestValue,
    DateTime? DataAutorizacao,
    string? VersaoAplicacao,
    string XmlProtocolo)
{
    /// <summary>cStat 100 (autorizado) e 150 (autorizado fora de prazo).</summary>
    public bool Autorizada => CodigoStatus is "100" or "150";

    /// <summary>110/301/302 sao denegacoes: numeracao nao pode ser reaproveitada.</summary>
    public bool Denegada => CodigoStatus is "110" or "301" or "302";
}

/// <summary>Retorno interpretado do envio sincrono de lote (enviNFe indSinc=1).</summary>
public sealed record AutorizacaoResultado(
    string Uf,
    string Autorizador,
    TipoAmbiente Ambiente,
    string IdLote,
    string CodigoStatusLote,
    string MotivoLote,
    string? VersaoAplicacao,
    DateTime? DataHoraSefaz,
    ProtocoloNFe? Protocolo,
    long TempoRespostaMs,
    string XmlEnviado,
    string XmlRecebido)
{
    /// <summary>
    /// No modo sincrono o lote resolve na hora: cStat 104 (lote processado)
    /// significa que o protocolo interno carrega o veredito real.
    /// </summary>
    public bool LoteProcessado => CodigoStatusLote == "104";

    /// <summary>Atalho: a nota foi autorizada de fato.</summary>
    public bool Autorizada => LoteProcessado && Protocolo?.Autorizada == true;

    /// <summary>
    /// XML de distribuicao (nfeProc). So existe quando ha protocolo, autorizado
    /// ou nao — a SEFAZ exige guardar o protocolo mesmo em rejeicao com protocolo.
    /// </summary>
    public string? XmlDistribuicao { get; init; }
}

/// <summary>
/// Envia NF-e ja assinada em lote sincrono (NFeAutorizacao4, indSinc=1) e
/// interpreta o retorno. Nao toca banco: recebe o XML assinado e o certificado.
/// </summary>
public sealed class AutorizacaoClient
{
    private readonly SefazEndpointRegistry _registry;
    private readonly SefazSoapClient _soap;

    public AutorizacaoClient(SefazEndpointRegistry registry, SefazSoapClient soap)
    {
        _registry = registry;
        _soap = soap;
    }

    /// <summary>
    /// Envia uma NF-e assinada. <paramref name="xmlNFeAssinada"/> deve ser o
    /// elemento &lt;NFe&gt; com a assinatura, exatamente como sera transmitido —
    /// nao serializar novamente para nao invalidar a assinatura.
    /// </summary>
    public async Task<AutorizacaoResultado> EnviarAsync(
        string uf,
        TipoAmbiente ambiente,
        string xmlNFeAssinada,
        CertificadoSefaz certificado,
        string? idLote = null,
        string? autorizadorOverride = null,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xmlNFeAssinada))
            throw new ArgumentException("XML da NF-e vazio.", nameof(xmlNFeAssinada));

        var endpoint = _registry.Resolver(
            uf, ambiente, ServicoSefaz.Autorizacao, autorizadorOverride, contingencia);

        var lote = string.IsNullOrWhiteSpace(idLote) ? GerarIdLote() : idLote;
        var envio = MontarEnvio(lote, xmlNFeAssinada);

        var resposta = await _soap.EnviarAsync(endpoint, envio, certificado, ct);

        var retorno = InterpretarRetorno(resposta.ConteudoRetorno, xmlNFeAssinada);

        return new AutorizacaoResultado(
            Uf: endpoint.Uf,
            Autorizador: endpoint.Autorizador,
            Ambiente: endpoint.Ambiente,
            IdLote: lote,
            CodigoStatusLote: retorno.CodigoStatusLote,
            MotivoLote: retorno.MotivoLote,
            VersaoAplicacao: retorno.VersaoAplicacao,
            DataHoraSefaz: retorno.DataHoraSefaz,
            Protocolo: retorno.Protocolo,
            TempoRespostaMs: resposta.DuracaoMs,
            XmlEnviado: envio,
            XmlRecebido: resposta.EnvelopeRecebido)
        {
            XmlDistribuicao = retorno.XmlDistribuicao
        };
    }

    /// <summary>Monta enviNFe 4.00 no modo sincrono, embutindo a NF-e assinada.</summary>
    public static string MontarEnvio(string idLote, string xmlNFeAssinada)
    {
        ValidarIdLote(idLote);

        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var nfe = ExtrairNFe(xmlNFeAssinada);

        // Preserva o whitespace: a NF-e ja esta assinada e qualquer reformatacao
        // do conteudo dentro de <NFe> invalidaria o DigestValue.
        var envio = new XElement(ns + "enviNFe",
            new XAttribute("versao", NamespacesSefaz.VersaoLayout),
            new XElement(ns + "idLote", idLote),
            new XElement(ns + "indSinc", "1"),
            nfe);

        return envio.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Monta o nfeProc (XML de distribuicao) juntando a NF-e assinada ao protocolo.
    /// Este e o arquivo que deve ser guardado e enviado ao destinatario.
    /// </summary>
    public static string MontarDistribuicao(string xmlNFeAssinada, string xmlProtocolo)
    {
        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var nfe = ExtrairNFe(xmlNFeAssinada);
        var prot = XElement.Parse(xmlProtocolo, LoadOptions.PreserveWhitespace);

        var proc = new XElement(ns + "nfeProc",
            new XAttribute("versao", NamespacesSefaz.VersaoLayout),
            nfe,
            prot);

        return proc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Campos interpretados do retEnviNFe, sem depender de rede.</summary>
    public sealed record RetornoInterpretado(
        string CodigoStatusLote,
        string MotivoLote,
        string? VersaoAplicacao,
        DateTime? DataHoraSefaz,
        ProtocoloNFe? Protocolo)
    {
        public string? XmlDistribuicao { get; init; }
    }

    /// <summary>
    /// Interpreta o conteudo do retEnviNFe (elemento retornado dentro do
    /// nfeResultMsg). Publico e estatico para ser testavel sem rede: recebe o
    /// XML de retorno cru e a NF-e assinada usada para montar a distribuicao.
    /// </summary>
    public static RetornoInterpretado InterpretarRetorno(
        XElement retEnviNFe, string xmlNFeAssinada)
    {
        string? Valor(XElement? escopo, string nome) => escopo?
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();

        // cStat de lote fica no nivel do retEnviNFe; buscamos o primeiro cStat
        // que NAO esteja dentro de um protNFe para nao confundir com o da nota.
        var cStatLote = retEnviNFe
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "cStat")?.Value?.Trim()
            ?? Valor(retEnviNFe, "cStat")
            ?? throw new SefazSoapException("Retorno de enviNFe sem cStat de lote.");

        var xMotivoLote = retEnviNFe
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "xMotivo")?.Value?.Trim()
            ?? Valor(retEnviNFe, "xMotivo")
            ?? string.Empty;

        var verAplicLote = retEnviNFe
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "verAplic")?.Value?.Trim();

        var dhLote = retEnviNFe
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "dhRecbto")?.Value?.Trim();

        // protNFe pode estar ausente quando o lote foi rejeitado antes de
        // processar (ex.: cStat 225, schema invalido). Nesse caso o veredito
        // esta no proprio cStat do lote.
        var protElem = retEnviNFe.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "protNFe");

        ProtocoloNFe? protocolo = null;
        string? distribuicao = null;

        if (protElem is not null)
        {
            var inf = protElem.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "infProt");

            protocolo = new ProtocoloNFe(
                ChaveAcesso: Valor(inf, "chNFe") ?? string.Empty,
                CodigoStatus: Valor(inf, "cStat") ?? string.Empty,
                Motivo: Valor(inf, "xMotivo") ?? string.Empty,
                NumeroProtocolo: Valor(inf, "nProt"),
                DigestValue: Valor(inf, "digVal"),
                DataAutorizacao: StatusServicoClient.ParseDataHora(Valor(inf, "dhRecbto")),
                VersaoAplicacao: Valor(inf, "verAplic"),
                XmlProtocolo: protElem.ToString(SaveOptions.DisableFormatting));

            // Distribuicao so faz sentido quando ha protocolo vinculado a nota.
            if (!string.IsNullOrEmpty(protocolo.NumeroProtocolo))
                distribuicao = MontarDistribuicao(xmlNFeAssinada, protocolo.XmlProtocolo);
        }

        return new RetornoInterpretado(
            CodigoStatusLote: cStatLote,
            MotivoLote: xMotivoLote,
            VersaoAplicacao: verAplicLote,
            DataHoraSefaz: StatusServicoClient.ParseDataHora(dhLote),
            Protocolo: protocolo)
        {
            XmlDistribuicao = distribuicao
        };
    }

    /// <summary>Extrai o elemento NFe preservando o conteudo assinado.</summary>
    private static XElement ExtrairNFe(string xml)
    {
        var elem = XElement.Parse(RemoverDeclaracao(xml), LoadOptions.PreserveWhitespace);

        if (elem.Name.LocalName == "NFe") return elem;

        // Aceita nfeProc ou envelope: pega o primeiro NFe interno.
        return elem.Descendants()
                   .FirstOrDefault(e => e.Name.LocalName == "NFe")
               ?? throw new ArgumentException(
                   "XML fornecido nao contem um elemento <NFe>.", nameof(xml));
    }

    private static string RemoverDeclaracao(string xml)
    {
        var texto = xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!texto.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return texto;

        var fim = texto.IndexOf("?>", StringComparison.Ordinal);
        return fim < 0 ? texto : texto[(fim + 2)..].TrimStart();
    }

    /// <summary>idLote e livre, ate 15 digitos. Usamos timestamp + aleatorio.</summary>
    private static string GerarIdLote()
    {
        var ticks = DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        var rnd = Random.Shared.Next(0, 1000).ToString("000", CultureInfo.InvariantCulture);
        return ticks + rnd;
    }

    private static void ValidarIdLote(string idLote)
    {
        if (string.IsNullOrWhiteSpace(idLote) || idLote.Length > 15 ||
            !idLote.All(char.IsDigit))
            throw new ArgumentException(
                "idLote deve conter de 1 a 15 digitos numericos.", nameof(idLote));
    }
}
