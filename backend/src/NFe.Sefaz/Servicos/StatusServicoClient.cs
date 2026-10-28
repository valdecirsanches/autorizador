using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Soap;

namespace NFe.Sefaz.Servicos;

/// <summary>Retorno interpretado de consStatServ.</summary>
public sealed record StatusServicoResultado(
    string Uf,
    string Autorizador,
    string NomeAutorizador,
    TipoAmbiente Ambiente,
    string CodigoStatus,
    string Motivo,
    string? VersaoAplicacao,
    int? TempoMedioSegundos,
    DateTime? DataHoraSefaz,
    DateTime? PrevisaoRetorno,
    long TempoRespostaMs,
    string XmlEnviado,
    string XmlRecebido)
{
    /// <summary>cStat 107 e o unico codigo que significa servico em operacao.</summary>
    public bool Online => CodigoStatus == "107";
}

/// <summary>
/// Consulta o status do servico de autorizacao (consStatServ, cStat 107 quando
/// em operacao). Nao depende de banco: recebe o certificado ja carregado.
/// </summary>
public sealed class StatusServicoClient
{
    private readonly SefazEndpointRegistry _registry;
    private readonly SefazSoapClient _soap;

    public StatusServicoClient(SefazEndpointRegistry registry, SefazSoapClient soap)
    {
        _registry = registry;
        _soap = soap;
    }

    public async Task<StatusServicoResultado> ConsultarAsync(
        string uf,
        TipoAmbiente ambiente,
        CertificadoSefaz certificado,
        string? autorizadorOverride = null,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        var endpoint = _registry.Resolver(
            uf, ambiente, ServicoSefaz.StatusServico, autorizadorOverride, contingencia);

        var xml = MontarConsulta(endpoint.CodigoUf, ambiente);
        var resposta = await _soap.EnviarAsync(endpoint, xml, certificado, ct);

        return Interpretar(endpoint, resposta);
    }

    /// <summary>Monta o XML de consStatServ 4.00.</summary>
    public static string MontarConsulta(int codigoUf, TipoAmbiente ambiente)
    {
        var ns = XNamespace.Get(NamespacesSefaz.PortalFiscal);

        // A ordem dos elementos e definida pelo XSD e nao pode ser alterada.
        var consulta = new XElement(ns + "consStatServ",
            new XAttribute("versao", NamespacesSefaz.VersaoLayout),
            new XElement(ns + "tpAmb", ((int)ambiente).ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "cUF", codigoUf.ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "xServ", "STATUS"));

        return consulta.ToString(SaveOptions.DisableFormatting);
    }

    private static StatusServicoResultado Interpretar(
        EndpointResolvido endpoint, RespostaSoap resposta)
    {
        var ret = resposta.ConteudoRetorno;

        string? Valor(string nome) => ret
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == nome)?.Value?.Trim();

        var cStat = Valor("cStat")
            ?? throw new SefazSoapException(
                "Retorno de consStatServ sem cStat.",
                resposta.StatusHttp, resposta.EnvelopeRecebido);

        return new StatusServicoResultado(
            Uf: endpoint.Uf,
            Autorizador: endpoint.Autorizador,
            NomeAutorizador: endpoint.NomeAutorizador,
            Ambiente: endpoint.Ambiente,
            CodigoStatus: cStat,
            Motivo: Valor("xMotivo") ?? string.Empty,
            VersaoAplicacao: Valor("verAplic"),
            TempoMedioSegundos: ParseInt(Valor("tMed")),
            DataHoraSefaz: ParseDataHora(Valor("dhRecbto")),
            PrevisaoRetorno: ParseDataHora(Valor("dhRetorno")),
            TempoRespostaMs: resposta.DuracaoMs,
            XmlEnviado: resposta.EnvelopeEnviado,
            XmlRecebido: resposta.EnvelopeRecebido);
    }

    private static int? ParseInt(string? valor) =>
        int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>
    /// Fuso oficial dos servicos da SEFAZ. O Brasil extinguiu o horario de verao
    /// em 2019, portanto um offset fixo e adequado.
    /// </summary>
    private static readonly TimeSpan FusoBrasilia = TimeSpan.FromHours(-3);

    /// <summary>
    /// Detecta offset explicito no fim da string: "Z", "-03:00" ou "-0300".
    /// Nao ha risco de casar com a parte de data ("2026-07-28"), pois o padrao
    /// exige dois grupos de digitos apos o sinal e esta ancorado no fim.
    /// </summary>
    private static readonly Regex OffsetExplicito =
        new(@"(?:Z|[+\-]\d{2}:?\d{2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// dhRecbto vem com offset (-03:00). Convertemos para UTC para manter o
    /// banco livre de ambiguidade de fuso e horario de verao.
    /// </summary>
    internal static DateTime? ParseDataHora(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;

        var texto = valor.Trim();

        // So confiamos no DateTimeOffset.TryParse quando o offset esta presente
        // na string. Sem offset, esse parse assume o fuso LOCAL DO SERVIDOR: em
        // container UTC, um horario de Brasilia seria gravado 3 horas adiantado,
        // e o ramo de fallback abaixo nunca seria alcancado.
        if (OffsetExplicito.IsMatch(texto) &&
            DateTimeOffset.TryParse(
                texto, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var offset))
            return offset.UtcDateTime;

        // Sem offset explicito, tratamos como horario de Brasilia (UTC-3),
        // que e o fuso oficial dos servicos da SEFAZ.
        if (DateTime.TryParse(
                texto, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return new DateTimeOffset(local, FusoBrasilia).UtcDateTime;

        return null;
    }
}
