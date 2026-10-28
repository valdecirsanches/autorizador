using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;
using NFe.Sefaz.Xml;

namespace NFe.Tests;

public class EventoClientTests
{
    private const string PortalFiscal = "http://www.portalfiscal.inf.br/nfe";
    private const string Chave = "43250412345678000195550010000000011000000015";

    private static EventoClient.DadosCancelamento Dados(
        string just = "Cancelamento por erro de digitacao no valor") => new(
        Uf: "RS",
        Ambiente: TipoAmbiente.Homologacao,
        CodigoOrgao: 43,
        Cnpj: "12.345.678/0001-95",
        ChaveAcesso: Chave,
        ProtocoloNFe: "143260000012345",
        Justificativa: just);

    private static XElement RetEnv(string corpoInterno)
        => XElement.Parse($"""<retEnvEvento xmlns="{PortalFiscal}" versao="1.00">{corpoInterno}</retEnvEvento>""");

    // ---- Montagem do infEvento de cancelamento --------------------------

    [Fact]
    public void MontarCancelamento_GeraInfEventoComCamposObrigatorios()
    {
        var (xml, id) = EventoClient.MontarCancelamento(Dados());
        var evento = XElement.Parse(xml);
        var inf = evento.Descendants().First(e => e.Name.LocalName == "infEvento");

        Assert.Equal("evento", evento.Name.LocalName);
        Assert.Equal("1.00", evento.Attribute("versao")!.Value);

        // Id = ID + tpEvento(6) + chave(44) + nSeqEvento(2).
        Assert.Equal($"ID110111{Chave}01", id);
        Assert.Equal(id, inf.Attribute("Id")!.Value);

        string V(string n) => inf.Elements().First(e => e.Name.LocalName == n).Value;
        Assert.Equal("43", V("cOrgao"));
        Assert.Equal("2", V("tpAmb")); // Homologacao
        Assert.Equal("12345678000195", V("CNPJ")); // somente digitos
        Assert.Equal(Chave, V("chNFe"));
        Assert.Equal("110111", V("tpEvento"));
        Assert.Equal("1", V("nSeqEvento"));
        Assert.Equal("1.00", V("verEvento"));

        var det = inf.Elements().First(e => e.Name.LocalName == "detEvento");
        Assert.Equal("Cancelamento", det.Elements().First(e => e.Name.LocalName == "descEvento").Value);
        Assert.Equal("143260000012345", det.Elements().First(e => e.Name.LocalName == "nProt").Value);
    }

    [Fact]
    public void MontarCancelamento_ProducaoUsaTpAmb1()
    {
        var (xml, _) = EventoClient.MontarCancelamento(Dados() with { Ambiente = TipoAmbiente.Producao });
        var inf = XElement.Parse(xml).Descendants().First(e => e.Name.LocalName == "infEvento");
        Assert.Equal("1", inf.Elements().First(e => e.Name.LocalName == "tpAmb").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("curta")] // < 15
    public void MontarCancelamento_JustificativaInvalida_Lanca(string just)
    {
        Assert.Throws<ArgumentException>(() => EventoClient.MontarCancelamento(Dados(just)));
    }

    [Fact]
    public void MontarCancelamento_ChaveInvalida_Lanca()
    {
        Assert.Throws<ArgumentException>(() =>
            EventoClient.MontarCancelamento(Dados() with { ChaveAcesso = "123" }));
    }

    // ---- Montagem do infEvento de carta de correcao ---------------------

    private static EventoClient.DadosCartaCorrecao DadosCC(
        string correcao = "No campo Informacoes Complementares leia-se pagamento a prazo em 30 dias.",
        int seq = 1) => new(
        Uf: "RS",
        Ambiente: TipoAmbiente.Homologacao,
        CodigoOrgao: 43,
        Cnpj: "12.345.678/0001-95",
        ChaveAcesso: Chave,
        Correcao: correcao,
        SequenciaEvento: seq);

    [Fact]
    public void MontarCartaCorrecao_GeraInfEventoComCamposObrigatorios()
    {
        var (xml, id) = EventoClient.MontarCartaCorrecao(DadosCC(seq: 2));
        var evento = XElement.Parse(xml);
        var inf = evento.Descendants().First(e => e.Name.LocalName == "infEvento");

        // Id = ID + tpEvento(110110) + chave(44) + nSeqEvento(2 digitos).
        Assert.Equal($"ID110110{Chave}02", id);
        Assert.Equal(id, inf.Attribute("Id")!.Value);

        string V(string n) => inf.Elements().First(e => e.Name.LocalName == n).Value;
        Assert.Equal("110110", V("tpEvento"));
        Assert.Equal("2", V("nSeqEvento"));

        var det = inf.Elements().First(e => e.Name.LocalName == "detEvento");
        Assert.Equal("Carta de Correcao", det.Elements().First(e => e.Name.LocalName == "descEvento").Value);
        Assert.Contains("Informacoes Complementares", det.Elements().First(e => e.Name.LocalName == "xCorrecao").Value);
        // xCondUso obrigatorio e com o texto legal exato.
        Assert.Equal(EventoClient.CondicaoUsoCartaCorrecao,
            det.Elements().First(e => e.Name.LocalName == "xCondUso").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("curta demais")] // < 15
    public void MontarCartaCorrecao_CorrecaoInvalida_Lanca(string correcao)
    {
        Assert.Throws<ArgumentException>(() => EventoClient.MontarCartaCorrecao(DadosCC(correcao)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void MontarCartaCorrecao_SequenciaForaDoIntervalo_Lanca(int seq)
    {
        Assert.Throws<ArgumentException>(() => EventoClient.MontarCartaCorrecao(DadosCC(seq: seq)));
    }

    [Fact]
    public void MontarCartaCorrecao_CorrecaoAcimaDe1000_Lanca()
    {
        Assert.Throws<ArgumentException>(() =>
            EventoClient.MontarCartaCorrecao(DadosCC(new string('a', 1001))));
    }

    // ---- Assinatura + envEvento (round-trip) ----------------------------

    [Fact]
    public void EventoAssinado_EmbutidoNoEnvEvento_PreservaSignature()
    {
        var (xml, id) = EventoClient.MontarCancelamento(Dados());

        var pfx = CertificadoFalso.CriarPfx("s3nha", cnpj: "12345678000195");
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, "s3nha");

        // A Signature do evento e irma de infEvento, filha de <evento>.
        var assinado = new AssinadorXml().Assinar(xml, id, cert, nomeTagAssinatura: "evento");
        Assert.True(new AssinadorXml().Verificar(assinado));

        var env = XElement.Parse(EventoClient.MontarEnvEvento("240728123", assinado));
        Assert.Equal("envEvento", env.Name.LocalName);
        Assert.Equal("240728123", env.Elements().First(e => e.Name.LocalName == "idLote").Value);

        var evento = env.Elements().First(e => e.Name.LocalName == "evento");
        Assert.Contains(evento.Elements(), e => e.Name.LocalName == "infEvento");
        // Signature deve ser filha direta de <evento>, irma de infEvento.
        Assert.Contains(evento.Elements(), e => e.Name.LocalName == "Signature");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456")] // 16 digitos
    public void MontarEnvEvento_IdLoteInvalido_Lanca(string idLote)
    {
        var (xml, _) = EventoClient.MontarCancelamento(Dados());
        Assert.Throws<ArgumentException>(() => EventoClient.MontarEnvEvento(idLote, xml));
    }

    // ---- Interpretacao do retEnvEvento ----------------------------------

    [Fact]
    public void Interpretar_CancelamentoHomologado_ExtraiProtocolo_E_MontaProcEvento()
    {
        var (evento, _) = EventoClient.MontarCancelamento(Dados());
        var ret = RetEnv(
            """
            <idLote>240728123</idLote>
            <tpAmb>2</tpAmb>
            <verAplic>RS20250101</verAplic>
            <cOrgao>43</cOrgao>
            <cStat>128</cStat>
            <xMotivo>Lote de Evento Processado</xMotivo>
            <retEvento versao="1.00">
              <infEvento>
                <tpAmb>2</tpAmb>
                <verAplic>RS20250101</verAplic>
                <cOrgao>43</cOrgao>
                <cStat>135</cStat>
                <xMotivo>Evento registrado e vinculado a NF-e</xMotivo>
                <chNFe>43250412345678000195550010000000011000000015</chNFe>
                <tpEvento>110111</tpEvento>
                <xEvento>Cancelamento registrado</xEvento>
                <nSeqEvento>1</nSeqEvento>
                <dhRegEvento>2026-07-28T14:30:05-03:00</dhRegEvento>
                <nProt>143260000098765</nProt>
              </infEvento>
            </retEvento>
            """);

        var r = EventoClient.InterpretarRetorno(ret, evento);

        Assert.Equal("128", r.CodigoStatusLote);
        Assert.Equal("135", r.CodigoStatusEvento);
        Assert.Equal("143260000098765", r.NumeroProtocolo);
        Assert.Equal(new DateTime(2026, 7, 28, 17, 30, 5, DateTimeKind.Utc), r.DataRegistro);

        // procEventoNFe montado com evento + retEvento, nessa ordem.
        Assert.NotNull(r.XmlDistribuicao);
        var proc = XElement.Parse(r.XmlDistribuicao!);
        Assert.Equal("procEventoNFe", proc.Name.LocalName);
        Assert.Equal(
            new[] { "evento", "retEvento" },
            proc.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void Interpretar_HomologadoForaDePrazo_155_TambemHomologa()
    {
        var (evento, _) = EventoClient.MontarCancelamento(Dados());
        var ret = RetEnv(
            """
            <cStat>128</cStat>
            <xMotivo>Lote de Evento Processado</xMotivo>
            <retEvento versao="1.00"><infEvento>
              <cStat>155</cStat>
              <xMotivo>Cancelamento homologado fora de prazo</xMotivo>
              <nProt>143260000011111</nProt>
            </infEvento></retEvento>
            """);

        var r = EventoClient.InterpretarRetorno(ret, evento);
        var resultado = ResultadoDe(r);
        Assert.True(resultado.Homologado);
    }

    [Fact]
    public void Interpretar_EventoRejeitado_NaoHomologa_E_SemProcEvento()
    {
        var (evento, _) = EventoClient.MontarCancelamento(Dados());
        var ret = RetEnv(
            """
            <cStat>128</cStat>
            <xMotivo>Lote de Evento Processado</xMotivo>
            <retEvento versao="1.00"><infEvento>
              <cStat>573</cStat>
              <xMotivo>Rejeicao: Duplicidade de evento</xMotivo>
            </infEvento></retEvento>
            """);

        var r = EventoClient.InterpretarRetorno(ret, evento);
        Assert.Equal("573", r.CodigoStatusEvento);
        Assert.Null(r.NumeroProtocolo);
        Assert.Null(r.XmlDistribuicao);
    }

    [Fact]
    public void Interpretar_SemCStatDeLote_Lanca()
    {
        Assert.Throws<SefazSoapException>(() =>
            EventoClient.InterpretarRetorno(RetEnv("<tpAmb>2</tpAmb>"), "<evento/>"));
    }

    // Helper: constroi um EventoResultado a partir do retorno para checar Homologado.
    private static EventoResultado ResultadoDe(EventoClient.RetornoInterpretado r) => new(
        "RS", "SVRS", TipoAmbiente.Homologacao, TiposEvento.Cancelamento, 1, Chave,
        r.CodigoStatusLote, r.MotivoLote, r.CodigoStatusEvento, r.MotivoEvento,
        r.NumeroProtocolo, r.DataRegistro, r.VersaoAplicacao, 0, "", "");
}
