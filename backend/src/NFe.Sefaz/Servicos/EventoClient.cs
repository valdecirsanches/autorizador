using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Soap;

namespace NFe.Sefaz.Servicos;

/// <summary>Codigos de tpEvento suportados.</summary>
public static class TiposEvento
{
    public const string Cancelamento = "110111";
    public const string CartaCorrecao = "110110";

    public static string Descricao(string tpEvento) => tpEvento switch
    {
        Cancelamento => "Cancelamento",
        CartaCorrecao => "Carta de Correcao",
        _ => "Evento"
    };
}

/// <summary>Retorno interpretado de um evento (retEvento dentro do retEnvEvento).</summary>
public sealed record EventoResultado(
    string Uf,
    string Autorizador,
    TipoAmbiente Ambiente,
    string TipoEvento,
    int SequenciaEvento,
    string ChaveAcesso,
    string CodigoStatusLote,
    string MotivoLote,
    string? CodigoStatusEvento,
    string? MotivoEvento,
    string? NumeroProtocolo,
    DateTime? DataRegistro,
    string? VersaoAplicacao,
    long TempoRespostaMs,
    string XmlEnviado,
    string XmlRecebido)
{
    /// <summary>cStat 128 indica que o lote de eventos foi processado.</summary>
    public bool LoteProcessado => CodigoStatusLote == "128";

    /// <summary>
    /// 135 (registrado e vinculado) e 155 (cancelamento homologado fora de prazo)
    /// significam evento homologado. 136 e "registrado, nao vinculado a NF-e".
    /// </summary>
    public bool Homologado => CodigoStatusEvento is "135" or "155";

    /// <summary>procEventoNFe (evento assinado + retEvento), XML de guarda.</summary>
    public string? XmlDistribuicao { get; init; }
}

/// <summary>
/// Cliente do servico NFeRecepcaoEvento4 (RecepcaoEvento). Monta o envEvento
/// generico e interpreta o retEnvEvento. Nao toca banco nem assina: recebe o
/// evento ja assinado e o certificado, como o AutorizacaoClient.
/// </summary>
public sealed class EventoClient
{
    private readonly SefazEndpointRegistry _registry;
    private readonly SefazSoapClient _soap;

    public EventoClient(SefazEndpointRegistry registry, SefazSoapClient soap)
    {
        _registry = registry;
        _soap = soap;
    }

    /// <summary>
    /// Dados minimos de um evento de cancelamento (tpEvento 110111). O
    /// cancelamento e sempre nSeqEvento 1.
    /// </summary>
    public sealed record DadosCancelamento(
        string Uf,
        TipoAmbiente Ambiente,
        int CodigoOrgao,
        string Cnpj,
        string ChaveAcesso,
        string ProtocoloNFe,
        string Justificativa,
        DateTimeOffset? DataEvento = null);

    /// <summary>Id do infEvento: "ID" + tpEvento(6) + chNFe(44) + nSeqEvento(2).</summary>
    public static string MontarIdEvento(string tpEvento, string chaveAcesso, int nSeqEvento) =>
        $"ID{tpEvento}{chaveAcesso}{nSeqEvento:D2}";

    /// <summary>
    /// Monta o infEvento (SEM assinatura) de um cancelamento. O chamador assina
    /// o Id retornado e envia via <see cref="EnviarAsync"/>. Retorna tambem o Id
    /// da referencia usada na assinatura.
    /// </summary>
    public static (string Xml, string IdReferencia) MontarCancelamento(DadosCancelamento d)
    {
        if (string.IsNullOrWhiteSpace(d.ChaveAcesso) || d.ChaveAcesso.Trim().Length != 44)
            throw new ArgumentException("Chave de acesso deve ter 44 digitos.", nameof(d));
        if (string.IsNullOrWhiteSpace(d.ProtocoloNFe))
            throw new ArgumentException("Protocolo de autorizacao (nProt) obrigatorio.", nameof(d));

        var just = (d.Justificativa ?? string.Empty).Trim();
        if (just.Length < 15 || just.Length > 255)
            throw new ArgumentException(
                "A justificativa do cancelamento deve ter de 15 a 255 caracteres.", nameof(d));

        const int nSeq = 1; // cancelamento e unico por nota
        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var id = MontarIdEvento(TiposEvento.Cancelamento, d.ChaveAcesso.Trim(), nSeq);
        var dh = FormatarDataHora(d.DataEvento ?? DateTimeOffset.UtcNow);

        var infEvento = new XElement(ns + "infEvento",
            new XAttribute("Id", id),
            new XElement(ns + "cOrgao", d.CodigoOrgao),
            new XElement(ns + "tpAmb", (int)d.Ambiente),
            new XElement(ns + "CNPJ", SomenteDigitos(d.Cnpj)),
            new XElement(ns + "chNFe", d.ChaveAcesso.Trim()),
            new XElement(ns + "dhEvento", dh),
            new XElement(ns + "tpEvento", TiposEvento.Cancelamento),
            new XElement(ns + "nSeqEvento", nSeq),
            new XElement(ns + "verEvento", "1.00"),
            new XElement(ns + "detEvento",
                new XAttribute("versao", "1.00"),
                new XElement(ns + "descEvento", "Cancelamento"),
                new XElement(ns + "nProt", d.ProtocoloNFe.Trim()),
                new XElement(ns + "xJust", just)));

        var evento = new XElement(ns + "evento",
            new XAttribute("versao", "1.00"),
            infEvento);

        return (evento.ToString(SaveOptions.DisableFormatting), id);
    }

    /// <summary>
    /// Dados de uma carta de correcao (tpEvento 110110). Diferente do
    /// cancelamento, admite varias por nota — a sequencia (nSeqEvento, 1 a 20)
    /// identifica cada uma; a mais recente homologada substitui as anteriores.
    /// </summary>
    public sealed record DadosCartaCorrecao(
        string Uf,
        TipoAmbiente Ambiente,
        int CodigoOrgao,
        string Cnpj,
        string ChaveAcesso,
        string Correcao,
        int SequenciaEvento,
        DateTimeOffset? DataEvento = null);

    /// <summary>
    /// Texto fixo da condicao de uso da CC-e, exigido pela SEFAZ no campo
    /// xCondUso exatamente com este conteudo (Convenio S/N de 1970).
    /// </summary>
    public const string CondicaoUsoCartaCorrecao =
        "A Carta de Correcao e disciplinada pelo paragrafo 1o-A do art. 7o do " +
        "Convenio S/N, de 15 de dezembro de 1970 e pode ser utilizada para " +
        "regularizacao de erro ocorrido na emissao de documento fiscal, desde que o " +
        "erro nao esteja relacionado com: I - as variaveis que determinam o valor do " +
        "imposto tais como: base de calculo, aliquota, diferenca de preco, quantidade, " +
        "valor da operacao ou da prestacao; II - a correcao de dados cadastrais que " +
        "implique mudanca do remetente ou do destinatario; III - a data de emissao ou " +
        "de saida.";

    /// <summary>
    /// Monta o infEvento (SEM assinatura) de uma carta de correcao. O chamador
    /// assina o Id retornado e envia via <see cref="EnviarAsync"/>.
    /// </summary>
    public static (string Xml, string IdReferencia) MontarCartaCorrecao(DadosCartaCorrecao d)
    {
        if (string.IsNullOrWhiteSpace(d.ChaveAcesso) || d.ChaveAcesso.Trim().Length != 44)
            throw new ArgumentException("Chave de acesso deve ter 44 digitos.", nameof(d));
        if (d.SequenciaEvento is < 1 or > 20)
            throw new ArgumentException(
                "A sequencia da carta de correcao deve estar entre 1 e 20.", nameof(d));

        var correcao = (d.Correcao ?? string.Empty).Trim();
        if (correcao.Length is < 15 or > 1000)
            throw new ArgumentException(
                "A correcao (xCorrecao) deve ter de 15 a 1000 caracteres.", nameof(d));

        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var id = MontarIdEvento(TiposEvento.CartaCorrecao, d.ChaveAcesso.Trim(), d.SequenciaEvento);
        var dh = FormatarDataHora(d.DataEvento ?? DateTimeOffset.UtcNow);

        var infEvento = new XElement(ns + "infEvento",
            new XAttribute("Id", id),
            new XElement(ns + "cOrgao", d.CodigoOrgao),
            new XElement(ns + "tpAmb", (int)d.Ambiente),
            new XElement(ns + "CNPJ", SomenteDigitos(d.Cnpj)),
            new XElement(ns + "chNFe", d.ChaveAcesso.Trim()),
            new XElement(ns + "dhEvento", dh),
            new XElement(ns + "tpEvento", TiposEvento.CartaCorrecao),
            new XElement(ns + "nSeqEvento", d.SequenciaEvento),
            new XElement(ns + "verEvento", "1.00"),
            new XElement(ns + "detEvento",
                new XAttribute("versao", "1.00"),
                new XElement(ns + "descEvento", "Carta de Correcao"),
                new XElement(ns + "xCorrecao", correcao),
                new XElement(ns + "xCondUso", CondicaoUsoCartaCorrecao)));

        var evento = new XElement(ns + "evento",
            new XAttribute("versao", "1.00"),
            infEvento);

        return (evento.ToString(SaveOptions.DisableFormatting), id);
    }

    /// <summary>
    /// Envia um evento JA ASSINADO (elemento &lt;evento&gt; com Signature) ao
    /// servico RecepcaoEvento e interpreta o retorno.
    /// </summary>
    public async Task<EventoResultado> EnviarAsync(
        string uf,
        TipoAmbiente ambiente,
        string tpEvento,
        int nSeqEvento,
        string chaveAcesso,
        string xmlEventoAssinado,
        CertificadoSefaz certificado,
        string? idLote = null,
        string? autorizadorOverride = null,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xmlEventoAssinado))
            throw new ArgumentException("XML do evento vazio.", nameof(xmlEventoAssinado));

        var endpoint = _registry.Resolver(
            uf, ambiente, ServicoSefaz.RecepcaoEvento, autorizadorOverride, contingencia);

        var lote = string.IsNullOrWhiteSpace(idLote) ? GerarIdLote() : idLote;
        var envio = MontarEnvEvento(lote, xmlEventoAssinado);

        var resposta = await _soap.EnviarAsync(endpoint, envio, certificado, ct);

        var r = InterpretarRetorno(resposta.ConteudoRetorno, xmlEventoAssinado);

        return new EventoResultado(
            Uf: endpoint.Uf,
            Autorizador: endpoint.Autorizador,
            Ambiente: endpoint.Ambiente,
            TipoEvento: tpEvento,
            SequenciaEvento: nSeqEvento,
            ChaveAcesso: chaveAcesso,
            CodigoStatusLote: r.CodigoStatusLote,
            MotivoLote: r.MotivoLote,
            CodigoStatusEvento: r.CodigoStatusEvento,
            MotivoEvento: r.MotivoEvento,
            NumeroProtocolo: r.NumeroProtocolo,
            DataRegistro: r.DataRegistro,
            VersaoAplicacao: r.VersaoAplicacao,
            TempoRespostaMs: resposta.DuracaoMs,
            XmlEnviado: envio,
            XmlRecebido: resposta.EnvelopeRecebido)
        {
            XmlDistribuicao = r.XmlDistribuicao
        };
    }

    /// <summary>Monta o envEvento 1.00 embutindo o evento assinado.</summary>
    public static string MontarEnvEvento(string idLote, string xmlEventoAssinado)
    {
        ValidarIdLote(idLote);

        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var evento = ExtrairEvento(xmlEventoAssinado);

        var env = new XElement(ns + "envEvento",
            new XAttribute("versao", "1.00"),
            new XElement(ns + "idLote", idLote),
            evento);

        return env.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Monta o procEventoNFe (evento assinado + retEvento) para guarda.</summary>
    public static string MontarDistribuicao(string xmlEventoAssinado, string xmlRetEvento)
    {
        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var evento = ExtrairEvento(xmlEventoAssinado);
        var ret = XElement.Parse(xmlRetEvento, LoadOptions.PreserveWhitespace);

        var proc = new XElement(ns + "procEventoNFe",
            new XAttribute("versao", "1.00"),
            evento,
            ret);

        return proc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Campos interpretados do retEnvEvento, testaveis sem rede.</summary>
    public sealed record RetornoInterpretado(
        string CodigoStatusLote,
        string MotivoLote,
        string? VersaoAplicacao,
        string? CodigoStatusEvento,
        string? MotivoEvento,
        string? NumeroProtocolo,
        DateTime? DataRegistro)
    {
        public string? XmlDistribuicao { get; init; }
    }

    /// <summary>
    /// Interpreta o retEnvEvento. Publico e estatico para teste sem rede: o cStat
    /// de lote fica no nivel do retEnvEvento e o veredito real do evento fica no
    /// retEvento/infEvento.
    /// </summary>
    public static RetornoInterpretado InterpretarRetorno(
        XElement retEnvEvento, string xmlEventoAssinado)
    {
        string? DiretoOuDescendente(XElement escopo, string nome) =>
            escopo.Elements().FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim()
            ?? escopo.Descendants().FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();

        var cStatLote = retEnvEvento.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "cStat")?.Value?.Trim()
            ?? throw new SefazSoapException("Retorno de evento sem cStat de lote.");

        var xMotivoLote = retEnvEvento.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "xMotivo")?.Value?.Trim()
            ?? string.Empty;

        var verAplic = retEnvEvento.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "verAplic")?.Value?.Trim();

        var retEvento = retEnvEvento.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "retEvento");

        string? cStatEvento = null, xMotivoEvento = null, nProt = null;
        DateTime? dhReg = null;
        string? distribuicao = null;

        if (retEvento is not null)
        {
            var inf = retEvento.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "infEvento") ?? retEvento;

            cStatEvento = DiretoOuDescendente(inf, "cStat");
            xMotivoEvento = DiretoOuDescendente(inf, "xMotivo");
            nProt = DiretoOuDescendente(inf, "nProt");
            dhReg = StatusServicoClient.ParseDataHora(DiretoOuDescendente(inf, "dhRegEvento"));

            // Distribuicao so faz sentido quando o evento gerou protocolo.
            if (!string.IsNullOrEmpty(nProt))
                distribuicao = MontarDistribuicao(
                    xmlEventoAssinado, retEvento.ToString(SaveOptions.DisableFormatting));
        }

        return new RetornoInterpretado(
            CodigoStatusLote: cStatLote,
            MotivoLote: xMotivoLote,
            VersaoAplicacao: verAplic,
            CodigoStatusEvento: cStatEvento,
            MotivoEvento: xMotivoEvento,
            NumeroProtocolo: nProt,
            DataRegistro: dhReg)
        {
            XmlDistribuicao = distribuicao
        };
    }

    private static XElement ExtrairEvento(string xml)
    {
        var elem = XElement.Parse(RemoverDeclaracao(xml), LoadOptions.PreserveWhitespace);
        if (elem.Name.LocalName == "evento") return elem;

        return elem.Descendants().FirstOrDefault(e => e.Name.LocalName == "evento")
            ?? throw new ArgumentException(
                "XML fornecido nao contem um elemento <evento>.", nameof(xml));
    }

    private static string RemoverDeclaracao(string xml)
    {
        var texto = xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!texto.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return texto;
        var fim = texto.IndexOf("?>", StringComparison.Ordinal);
        return fim < 0 ? texto : texto[(fim + 2)..].TrimStart();
    }

    /// <summary>Data com offset de Brasilia (-03:00), como exigido em dhEvento.</summary>
    public static string FormatarDataHora(DateTimeOffset data) =>
        data.ToOffset(TimeSpan.FromHours(-3))
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string SomenteDigitos(string? valor) =>
        new string((valor ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string GerarIdLote()
    {
        var ticks = DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        var rnd = Random.Shared.Next(0, 1000).ToString("000", CultureInfo.InvariantCulture);
        return ticks + rnd;
    }

    private static void ValidarIdLote(string idLote)
    {
        if (string.IsNullOrWhiteSpace(idLote) || idLote.Length > 15 || !idLote.All(char.IsDigit))
            throw new ArgumentException(
                "idLote deve conter de 1 a 15 digitos numericos.", nameof(idLote));
    }
}
