using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Soap;

namespace NFe.Sefaz.Servicos;

/// <summary>Retorno interpretado de consSitNFe (consulta de protocolo).</summary>
public sealed record ConsultaProtocoloResultado(
    string ChaveAcesso,
    string Uf,
    string Autorizador,
    TipoAmbiente Ambiente,
    string CodigoStatus,
    string Motivo,
    string? VersaoAplicacao,
    string? NumeroProtocolo,
    DateTime? DataAutorizacao,
    DateTime? DataHoraSefaz,
    IReadOnlyList<EventoVinculado> Eventos,
    long TempoRespostaMs,
    string XmlEnviado,
    string XmlRecebido)
{
    /// <summary>100 = autorizado; 150 = autorizado fora de prazo.</summary>
    public bool Autorizada => CodigoStatus is "100" or "150";

    /// <summary>101 cancelado; 151 cancelado fora de prazo; 135/155 evento cancelamento homologado.</summary>
    public bool Cancelada => CodigoStatus is "101" or "151";

    /// <summary>110/301/302/303 sao denegacoes.</summary>
    public bool Denegada => CodigoStatus is "110" or "301" or "302" or "303";
}

/// <summary>Um evento (cancelamento, CCe, etc.) vinculado a NF-e no retorno.</summary>
public sealed record EventoVinculado(
    string TipoEvento,
    string Descricao,
    string? NumeroProtocolo,
    DateTime? DataEvento);

/// <summary>
/// Consulta a situacao de uma NF-e pela chave de acesso (consSitNFe, NFeConsulta4).
/// Retorna o status atual da nota e os eventos vinculados. Nao toca banco:
/// recebe o certificado ja carregado.
/// </summary>
public sealed class ConsultaProtocoloClient
{
    private readonly SefazEndpointRegistry _registry;
    private readonly SefazSoapClient _soap;

    public ConsultaProtocoloClient(SefazEndpointRegistry registry, SefazSoapClient soap)
    {
        _registry = registry;
        _soap = soap;
    }

    private static readonly Regex ChaveValida = new(@"^\d{44}$", RegexOptions.Compiled);

    public async Task<ConsultaProtocoloResultado> ConsultarAsync(
        string uf,
        TipoAmbiente ambiente,
        string chaveAcesso,
        CertificadoSefaz certificado,
        string? autorizadorOverride = null,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        var chave = (chaveAcesso ?? string.Empty).Trim().Replace("NFe", "", StringComparison.OrdinalIgnoreCase);
        if (!ChaveValida.IsMatch(chave))
            throw new ArgumentException(
                "Chave de acesso deve conter exatamente 44 digitos.", nameof(chaveAcesso));

        var endpoint = _registry.Resolver(
            uf, ambiente, ServicoSefaz.ConsultaProtocolo, autorizadorOverride, contingencia);

        var xml = MontarConsulta(ambiente, chave);
        var resposta = await _soap.EnviarAsync(endpoint, xml, certificado, ct);

        return Interpretar(endpoint, chave, resposta);
    }

    /// <summary>Monta o XML de consSitNFe 4.00.</summary>
    public static string MontarConsulta(TipoAmbiente ambiente, string chaveAcesso)
    {
        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);

        // Ordem dos elementos definida pelo XSD; nao alterar.
        var consulta = new XElement(ns + "consSitNFe",
            new XAttribute("versao", NamespacesSefaz.VersaoLayout),
            new XElement(ns + "tpAmb", ((int)ambiente).ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "xServ", "CONSULTAR"),
            new XElement(ns + "chNFe", chaveAcesso));

        return consulta.ToString(SaveOptions.DisableFormatting);
    }

    private static ConsultaProtocoloResultado Interpretar(
        EndpointResolvido endpoint, string chave, RespostaSoap resposta)
    {
        var ret = resposta.ConteudoRetorno;

        string? Valor(string nome) => ret
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();

        // cStat no nivel do retConsSitNFe (primeiro cStat que nao esteja dentro
        // de protNFe ou procEventoNFe).
        var cStat = ret.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "cStat")?.Value?.Trim()
            ?? Valor("cStat")
            ?? throw new SefazSoapException(
                "Retorno de consSitNFe sem cStat.",
                resposta.StatusHttp, resposta.EnvelopeRecebido);

        var xMotivo = ret.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "xMotivo")?.Value?.Trim()
            ?? Valor("xMotivo") ?? string.Empty;

        // Protocolo de autorizacao (protNFe/infProt), se houver.
        var infProt = ret.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "infProt");

        string? DoInf(string nome) => infProt?
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();

        // Eventos vinculados (procEventoNFe): cancelamento, CCe, etc.
        var eventos = ret.Descendants()
            .Where(e => e.Name.LocalName == "procEventoNFe")
            .Select(ev =>
            {
                string? Campo(string nome) => ev.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == nome)?.Value?.Trim();

                return new EventoVinculado(
                    TipoEvento: Campo("tpEvento") ?? string.Empty,
                    Descricao: Campo("xEvento") ?? Campo("descEvento") ?? string.Empty,
                    NumeroProtocolo: Campo("nProt"),
                    DataEvento: StatusServicoClient.ParseDataHora(Campo("dhRegEvento")));
            })
            .ToList();

        return new ConsultaProtocoloResultado(
            ChaveAcesso: DoInf("chNFe") ?? chave,
            Uf: endpoint.Uf,
            Autorizador: endpoint.Autorizador,
            Ambiente: endpoint.Ambiente,
            CodigoStatus: cStat,
            Motivo: xMotivo,
            VersaoAplicacao: Valor("verAplic"),
            NumeroProtocolo: DoInf("nProt"),
            DataAutorizacao: StatusServicoClient.ParseDataHora(DoInf("dhRecbto")),
            DataHoraSefaz: StatusServicoClient.ParseDataHora(
                ret.Elements().FirstOrDefault(e => e.Name.LocalName == "dhRecbto")?.Value?.Trim()),
            Eventos: eventos,
            TempoRespostaMs: resposta.DuracaoMs,
            XmlEnviado: resposta.EnvelopeEnviado,
            XmlRecebido: resposta.EnvelopeRecebido);
    }
}
