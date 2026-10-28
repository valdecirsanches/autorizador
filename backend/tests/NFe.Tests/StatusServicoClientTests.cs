using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Servicos;

namespace NFe.Tests;

/// <summary>
/// Cobre a montagem de consStatServ e a normalizacao de data/hora do retorno.
/// A ordem dos elementos e ditada pelo XSD: fora de ordem, o autorizador rejeita
/// a mensagem inteira.
/// </summary>
public class StatusServicoClientTests
{
    private const string PortalFiscal = "http://www.portalfiscal.inf.br/nfe";

    private static XElement Consulta(int cUf, TipoAmbiente amb) =>
        XElement.Parse(StatusServicoClient.MontarConsulta(cUf, amb));

    [Fact]
    public void Consulta_TemEstruturaEsperada()
    {
        var c = Consulta(43, TipoAmbiente.Homologacao);

        Assert.Equal(XName.Get("consStatServ", PortalFiscal), c.Name);
        Assert.Equal("4.00", c.Attribute("versao")?.Value);
    }

    [Fact]
    public void Consulta_RespeitaAOrdemDoXsd()
    {
        // O XSD de consStatServ exige exatamente tpAmb, cUF, xServ nessa ordem.
        var c = Consulta(35, TipoAmbiente.Producao);

        Assert.Equal(
            new[] { "tpAmb", "cUF", "xServ" },
            c.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void Consulta_XServ_ESempreStatus()
    {
        var c = Consulta(43, TipoAmbiente.Homologacao);

        Assert.Equal("STATUS", c.Element(XName.Get("xServ", PortalFiscal))?.Value);
    }

    [Theory]
    [InlineData(TipoAmbiente.Producao, "1")]
    [InlineData(TipoAmbiente.Homologacao, "2")]
    public void Consulta_TpAmb_UsaCodigoNumerico(TipoAmbiente ambiente, string esperado)
    {
        // tpAmb 1 = producao, 2 = homologacao. Inverter emitiria em ambiente errado.
        var c = Consulta(43, ambiente);

        Assert.Equal(esperado, c.Element(XName.Get("tpAmb", PortalFiscal))?.Value);
    }

    [Theory]
    [InlineData(43)]
    [InlineData(35)]
    [InlineData(31)]
    public void Consulta_CUf_ELevadoAoXml(int cUf)
    {
        var c = Consulta(cUf, TipoAmbiente.Producao);

        Assert.Equal(cUf.ToString(), c.Element(XName.Get("cUF", PortalFiscal))?.Value);
    }

    [Fact]
    public void Consulta_NaoTemDeclaracaoXml()
    {
        // A declaracao seria duplicada ao embutir no envelope.
        Assert.DoesNotContain(
            "<?xml", StatusServicoClient.MontarConsulta(43, TipoAmbiente.Producao));
    }

    /// <summary>
    /// dhRecbto chega com offset de Brasilia. Guardar sem converter deslocaria
    /// todos os horarios em 3 horas no banco.
    /// </summary>
    [Fact]
    public void ParseDataHora_ComOffset_ConverteParaUtc()
    {
        var utc = StatusServicoClient.ParseDataHora("2026-07-28T14:30:00-03:00");

        Assert.NotNull(utc);
        Assert.Equal(new DateTime(2026, 7, 28, 17, 30, 0, DateTimeKind.Utc), utc!.Value);
    }

    /// <summary>
    /// Regressao: DateTimeOffset.TryParse aceita string sem offset e assume o
    /// fuso LOCAL DO SERVIDOR. Em container UTC isso gravava o horario 3 horas
    /// adiantado e tornava o fallback de Brasilia inalcancavel. O resultado deve
    /// ser o mesmo independente do fuso da maquina.
    /// </summary>
    [Fact]
    public void ParseDataHora_SemOffset_AssumeBrasiliaIndependenteDoFusoDoServidor()
    {
        var utc = StatusServicoClient.ParseDataHora("2026-07-28T14:30:00");

        Assert.NotNull(utc);
        Assert.Equal(new DateTime(2026, 7, 28, 17, 30, 0, DateTimeKind.Utc), utc!.Value);
        Assert.Equal(DateTimeKind.Utc, utc.Value.Kind);
    }

    [Theory]
    // Amazonas opera em UTC-4.
    [InlineData("2026-07-28T14:30:00-04:00", 18, 30)]
    // Forma compacta, sem dois-pontos no offset.
    [InlineData("2026-07-28T14:30:00-0300", 17, 30)]
    // Sufixo Z indica UTC e nao deve sofrer ajuste.
    [InlineData("2026-07-28T14:30:00Z", 14, 30)]
    public void ParseDataHora_OffsetExplicito_ERespeitado(
        string valor, int horaUtc, int minutoUtc)
    {
        var utc = StatusServicoClient.ParseDataHora(valor);

        Assert.NotNull(utc);
        Assert.Equal(new DateTime(2026, 7, 28, horaUtc, minutoUtc, 0, DateTimeKind.Utc), utc!.Value);
    }

    /// <summary>
    /// Garante que o detector de offset nao confunde o separador da data
    /// ("2026-07-28") com um offset de fuso.
    /// </summary>
    [Fact]
    public void ParseDataHora_SomenteData_NaoConfundeSeparadorComOffset()
    {
        var utc = StatusServicoClient.ParseDataHora("2026-07-28");

        Assert.NotNull(utc);
        // Meia-noite em Brasilia equivale a 03:00 UTC.
        Assert.Equal(new DateTime(2026, 7, 28, 3, 0, 0, DateTimeKind.Utc), utc!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data invalida")]
    public void ParseDataHora_ValorInvalido_RetornaNull(string? valor)
    {
        // dhRetorno so vem quando o servico esta fora: ausencia e normal.
        Assert.Null(StatusServicoClient.ParseDataHora(valor));
    }

    [Theory]
    [InlineData("107", true)]
    [InlineData("108", false)]
    [InlineData("109", false)]
    [InlineData("", false)]
    public void Online_SomenteQuandoCStat107(string cStat, bool esperado)
    {
        // 107 = em operacao, 108 = paralisado momentaneamente, 109 = paralisado.
        var r = new StatusServicoResultado(
            "RS", "SVRS", "Sefaz Virtual do RS", TipoAmbiente.Homologacao,
            cStat, "motivo", null, null, null, null, 0, "<a/>", "<b/>");

        Assert.Equal(esperado, r.Online);
    }
}
