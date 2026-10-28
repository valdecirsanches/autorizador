using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Soap;

namespace NFe.Sefaz.Servicos;

/// <summary>Retorno interpretado do servico de inutilizacao (retInutNFe).</summary>
public sealed record InutilizacaoResultado(
    string Uf,
    string Autorizador,
    TipoAmbiente Ambiente,
    int CodigoUf,
    int Ano,
    string Cnpj,
    ModeloDocumento Modelo,
    int Serie,
    long NumeroInicial,
    long NumeroFinal,
    string IdInutilizacao,
    string CodigoStatus,
    string Motivo,
    string? Protocolo,
    DateTime? DataRegistro,
    string? VersaoAplicacao,
    long TempoRespostaMs,
    string XmlEnviado,
    string XmlRecebido)
{
    /// <summary>cStat 102 = "Inutilizacao de numero homologado".</summary>
    public bool Homologado => CodigoStatus == "102";

    /// <summary>procInutNFe (pedido assinado + retorno), XML de guarda.</summary>
    public string? XmlDistribuicao { get; init; }
}

/// <summary>
/// Cliente do servico NFeInutilizacao4. Diferente dos eventos, a inutilizacao
/// nao passa por lote (envEvento): o proprio inutNFe assinado e enviado. Como o
/// <see cref="EventoClient"/>, esta classe nao toca banco nem assina — recebe o
/// certificado para o transporte e o pedido ja assinado para o envio.
/// </summary>
public sealed class InutilizacaoClient
{
    private readonly SefazEndpointRegistry _registry;
    private readonly SefazSoapClient _soap;

    public InutilizacaoClient(SefazEndpointRegistry registry, SefazSoapClient soap)
    {
        _registry = registry;
        _soap = soap;
    }

    /// <summary>Dados de um pedido de inutilizacao de faixa.</summary>
    public sealed record DadosInutilizacao(
        string Uf,
        TipoAmbiente Ambiente,
        int CodigoUf,
        int Ano,
        string Cnpj,
        ModeloDocumento Modelo,
        int Serie,
        long NumeroInicial,
        long NumeroFinal,
        string Justificativa);

    /// <summary>
    /// Id do infInut: "ID" + cUF(2) + ano(2) + CNPJ(14) + mod(2) + serie(3) +
    /// nNFIni(9) + nNFFin(9). Total de 43 caracteres (com o prefixo "ID").
    /// </summary>
    public static string MontarId(
        int cUf, int ano, string cnpj, ModeloDocumento modelo, int serie, long ini, long fin) =>
        "ID" +
        cUf.ToString("D2", CultureInfo.InvariantCulture) +
        (ano % 100).ToString("D2", CultureInfo.InvariantCulture) +
        SomenteDigitos(cnpj).PadLeft(14, '0') +
        ((int)modelo).ToString("D2", CultureInfo.InvariantCulture) +
        serie.ToString("D3", CultureInfo.InvariantCulture) +
        ini.ToString("D9", CultureInfo.InvariantCulture) +
        fin.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>
    /// Monta o inutNFe (SEM assinatura). O chamador assina o Id retornado (a
    /// Signature vai como irma do infInut, dentro do inutNFe) e envia via
    /// <see cref="EnviarAsync"/>.
    /// </summary>
    public static (string Xml, string IdReferencia) Montar(DadosInutilizacao d)
    {
        ValidarFaixa(d);

        var just = (d.Justificativa ?? string.Empty).Trim();
        if (just.Length is < 15 or > 255)
            throw new ArgumentException(
                "A justificativa da inutilizacao deve ter de 15 a 255 caracteres.", nameof(d));

        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var id = MontarId(d.CodigoUf, d.Ano, d.Cnpj, d.Modelo, d.Serie, d.NumeroInicial, d.NumeroFinal);

        var infInut = new XElement(ns + "infInut",
            new XAttribute("Id", id),
            new XElement(ns + "tpAmb", (int)d.Ambiente),
            new XElement(ns + "xServ", "INUTILIZAR"),
            new XElement(ns + "cUF", d.CodigoUf),
            new XElement(ns + "ano", (d.Ano % 100).ToString("D2", CultureInfo.InvariantCulture)),
            new XElement(ns + "CNPJ", SomenteDigitos(d.Cnpj)),
            new XElement(ns + "mod", (int)d.Modelo),
            new XElement(ns + "serie", d.Serie),
            new XElement(ns + "nNFIni", d.NumeroInicial),
            new XElement(ns + "nNFFin", d.NumeroFinal),
            new XElement(ns + "xJust", just));

        var inut = new XElement(ns + "inutNFe",
            new XAttribute("versao", NamespacesSefaz.VersaoLayout),
            infInut);

        return (inut.ToString(SaveOptions.DisableFormatting), id);
    }

    /// <summary>
    /// Envia um inutNFe JA ASSINADO ao servico de inutilizacao e interpreta o
    /// retInutNFe. A UF/ambiente/servico resolvem o endpoint; contingencia NAO
    /// se aplica (inutilizacao nao e oferecida pelos SVC).
    /// </summary>
    public async Task<InutilizacaoResultado> EnviarAsync(
        DadosInutilizacao dados,
        string xmlInutAssinado,
        CertificadoSefaz certificado,
        string? autorizadorOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(xmlInutAssinado))
            throw new ArgumentException("XML da inutilizacao vazio.", nameof(xmlInutAssinado));

        var endpoint = _registry.Resolver(
            dados.Uf, dados.Ambiente, ServicoSefaz.Inutilizacao, autorizadorOverride);

        var resposta = await _soap.EnviarAsync(endpoint, xmlInutAssinado, certificado, ct);

        var r = InterpretarRetorno(resposta.ConteudoRetorno);

        var id = MontarId(
            endpoint.CodigoUf, dados.Ano, dados.Cnpj, dados.Modelo,
            dados.Serie, dados.NumeroInicial, dados.NumeroFinal);

        string? distribuicao = null;
        if (!string.IsNullOrEmpty(r.Protocolo) || r.CodigoStatus == "102")
            distribuicao = MontarDistribuicao(
                xmlInutAssinado, resposta.ConteudoRetorno.ToString(SaveOptions.DisableFormatting));

        return new InutilizacaoResultado(
            Uf: endpoint.Uf,
            Autorizador: endpoint.Autorizador,
            Ambiente: endpoint.Ambiente,
            CodigoUf: endpoint.CodigoUf,
            Ano: dados.Ano,
            Cnpj: SomenteDigitos(dados.Cnpj),
            Modelo: dados.Modelo,
            Serie: dados.Serie,
            NumeroInicial: dados.NumeroInicial,
            NumeroFinal: dados.NumeroFinal,
            IdInutilizacao: id,
            CodigoStatus: r.CodigoStatus,
            Motivo: r.Motivo,
            Protocolo: r.Protocolo,
            DataRegistro: r.DataRegistro,
            VersaoAplicacao: r.VersaoAplicacao,
            TempoRespostaMs: resposta.DuracaoMs,
            XmlEnviado: xmlInutAssinado,
            XmlRecebido: resposta.EnvelopeRecebido)
        {
            XmlDistribuicao = distribuicao
        };
    }

    /// <summary>Monta o procInutNFe (pedido assinado + retorno) para guarda.</summary>
    public static string MontarDistribuicao(string xmlInutAssinado, string xmlRetInut)
    {
        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);
        var inut = ExtrairInut(xmlInutAssinado);
        var ret = XElement.Parse(RemoverDeclaracao(xmlRetInut), LoadOptions.PreserveWhitespace);

        var proc = new XElement(ns + "ProcInutNFe",
            new XAttribute("versao", NamespacesSefaz.VersaoLayout),
            inut,
            ret);

        return proc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Campos interpretados do retInutNFe, testaveis sem rede.</summary>
    public sealed record RetornoInterpretado(
        string CodigoStatus,
        string Motivo,
        string? VersaoAplicacao,
        string? Protocolo,
        DateTime? DataRegistro);

    /// <summary>
    /// Interpreta o retInutNFe. O cStat/xMotivo e o veredito; nProt e dhRecbto so
    /// aparecem quando homologado (102). Publico e estatico para teste sem rede.
    /// </summary>
    public static RetornoInterpretado InterpretarRetorno(XElement retInutNFe)
    {
        var inf = retInutNFe.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "infInut") ?? retInutNFe;

        string? V(string nome) =>
            inf.Elements().FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();

        var cStat = V("cStat")
            ?? throw new SefazSoapException("Retorno de inutilizacao sem cStat.");

        return new RetornoInterpretado(
            CodigoStatus: cStat,
            Motivo: V("xMotivo") ?? string.Empty,
            VersaoAplicacao: V("verAplic"),
            Protocolo: V("nProt"),
            DataRegistro: StatusServicoClient.ParseDataHora(V("dhRecbto")));
    }

    private static void ValidarFaixa(DadosInutilizacao d)
    {
        if (d.Serie is < 0 or > 999)
            throw new ArgumentException("A serie deve estar entre 0 e 999.", nameof(d));
        if (d.NumeroInicial < 1 || d.NumeroFinal < 1)
            throw new ArgumentException("Os numeros da faixa devem ser maiores que zero.", nameof(d));
        if (d.NumeroInicial > d.NumeroFinal)
            throw new ArgumentException(
                "O numero inicial nao pode ser maior que o final.", nameof(d));
        if (d.NumeroFinal > 999_999_999)
            throw new ArgumentException("Os numeros nao podem exceder 9 digitos.", nameof(d));
        if (string.IsNullOrWhiteSpace(SomenteDigitos(d.Cnpj)) || SomenteDigitos(d.Cnpj).Length != 14)
            throw new ArgumentException("CNPJ do emitente invalido.", nameof(d));
    }

    private static XElement ExtrairInut(string xml)
    {
        var elem = XElement.Parse(RemoverDeclaracao(xml), LoadOptions.PreserveWhitespace);
        if (elem.Name.LocalName == "inutNFe") return elem;

        return elem.Descendants().FirstOrDefault(e => e.Name.LocalName == "inutNFe")
            ?? throw new ArgumentException(
                "XML fornecido nao contem um elemento <inutNFe>.", nameof(xml));
    }

    private static string RemoverDeclaracao(string xml)
    {
        var texto = xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!texto.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return texto;
        var fim = texto.IndexOf("?>", StringComparison.Ordinal);
        return fim < 0 ? texto : texto[(fim + 2)..].TrimStart();
    }

    private static string SomenteDigitos(string? valor) =>
        new string((valor ?? string.Empty).Where(char.IsDigit).ToArray());
}
