using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Infrastructure.Servicos;
using NFe.Sefaz.Servicos;

namespace NFe.Tests;

/// <summary>
/// Testes puros do mapeamento retorno-da-SEFAZ -> estado-do-documento. Nao
/// tocam banco: exercitam a traducao que decide se a nota foi autorizada,
/// denegada ou rejeitada e o que fica persistido.
/// </summary>
public class ServicoAutorizacaoTests
{
    private const string Chave = "43250412345678000195550010000000011000000015";

    private static DocumentoFiscal NovoDocumento() => new()
    {
        EmpresaId = Guid.NewGuid(),
        ChaveAcesso = Chave,
        Status = StatusDocumento.Assinado,
        Numero = 1,
        Serie = 1
    };

    private static AutorizacaoResultado Resultado(
        string cStatLote, ProtocoloNFe? protocolo, string? distribuicao = null) =>
        new(
            Uf: "RS",
            Autorizador: "SVRS",
            Ambiente: TipoAmbiente.Homologacao,
            IdLote: "1",
            CodigoStatusLote: cStatLote,
            MotivoLote: "Lote processado",
            VersaoAplicacao: "RS20250101",
            DataHoraSefaz: new DateTime(2026, 7, 28, 17, 30, 0, DateTimeKind.Utc),
            Protocolo: protocolo,
            TempoRespostaMs: 820,
            XmlEnviado: "<enviNFe/>",
            XmlRecebido: "<retEnviNFe/>")
        {
            XmlDistribuicao = distribuicao
        };

    private static ProtocoloNFe Protocolo(string cStat) => new(
        ChaveAcesso: Chave,
        CodigoStatus: cStat,
        Motivo: "motivo",
        NumeroProtocolo: "143260000012345",
        DigestValue: "abc=",
        DataAutorizacao: new DateTime(2026, 7, 28, 17, 30, 5, DateTimeKind.Utc),
        VersaoAplicacao: "RS20250101",
        XmlProtocolo: "<protNFe/>");

    [Fact]
    public void Validacao_ExigeAmbienteEEmitenteNoJson()
    {
        var problemas = ServicoAutorizacao.ValidarRequest(new NotaFiscalRequest
        {
            Itens = [new ItemNotaRequest
            {
                Codigo = "001",
                Descricao = "Produto",
                Ncm = "22030000",
                Cfop = "5102",
                Quantidade = 1,
                ValorUnitario = 10
            }]
        });

        Assert.Contains(problemas, p => p.Contains("Configuracao da emissao"));
        Assert.Contains(problemas, p => p.Contains("Cadastro do emitente"));
    }

    [Fact]
    public void Validacao_ExigeAmbienteMesmoQuandoConfiguracaoExiste()
    {
        var problemas = ServicoAutorizacao.ValidarRequest(new NotaFiscalRequest
        {
            Configuracao = new ConfiguracaoNFeRequest(),
            Itens = [new ItemNotaRequest
            {
                Codigo = "001",
                Descricao = "Produto",
                Ncm = "22030000",
                Cfop = "5102",
                Quantidade = 1,
                ValorUnitario = 10
            }]
        });

        Assert.Contains(problemas, p => p.Contains("Ambiente da emissao obrigatorio"));
    }

    [Fact]
    public void Autorizada_100_MarcaAutorizadoEPersisteProtocoloEDistribuicao()
    {
        var doc = NovoDocumento();
        var r = Resultado("104", Protocolo("100"), "<nfeProc/>");

        ServicoAutorizacao.AplicarResultado(doc, StatusDocumento.Assinado, r);

        Assert.Equal(StatusDocumento.Autorizado, doc.Status);
        Assert.Equal("143260000012345", doc.Protocolo);
        Assert.Equal("100", doc.CodigoStatusRetorno);
        Assert.Equal("abc=", doc.DigestValue);
        Assert.Equal(new DateTime(2026, 7, 28, 17, 30, 5, DateTimeKind.Utc), doc.DataAutorizacao);
        Assert.Equal("<nfeProc/>", doc.XmlProtocolado);

        // Toda transicao gera um evento de auditoria.
        var evento = Assert.Single(doc.Eventos);
        Assert.Equal(StatusDocumento.Assinado, evento.StatusAnterior);
        Assert.Equal(StatusDocumento.Autorizado, evento.StatusNovo);
    }

    [Fact]
    public void AutorizadaForaDePrazo_150_TambemAutoriza()
    {
        var doc = NovoDocumento();

        ServicoAutorizacao.AplicarResultado(
            doc, StatusDocumento.Assinado, Resultado("104", Protocolo("150"), "<nfeProc/>"));

        Assert.Equal(StatusDocumento.Autorizado, doc.Status);
    }

    [Theory]
    [InlineData("110")]
    [InlineData("301")]
    [InlineData("302")]
    public void Denegada_MarcaDenegado(string cStat)
    {
        var doc = NovoDocumento();

        ServicoAutorizacao.AplicarResultado(
            doc, StatusDocumento.Assinado, Resultado("104", Protocolo(cStat), "<nfeProc/>"));

        Assert.Equal(StatusDocumento.Denegado, doc.Status);
        // Denegada tem protocolo e deve preservar a distribuicao para guarda legal.
        Assert.Equal("<nfeProc/>", doc.XmlProtocolado);
    }

    [Fact]
    public void RejeitadaComProtocolo_539_MarcaRejeitado()
    {
        var doc = NovoDocumento();

        ServicoAutorizacao.AplicarResultado(
            doc, StatusDocumento.Assinado, Resultado("104", Protocolo("539")));

        Assert.Equal(StatusDocumento.Rejeitado, doc.Status);
    }

    [Fact]
    public void LoteRejeitadoSemProtocolo_UsaCStatDoLote()
    {
        var doc = NovoDocumento();
        // cStat 225: falha de schema, sem protNFe.
        var r = Resultado("225", protocolo: null);

        ServicoAutorizacao.AplicarResultado(doc, StatusDocumento.Assinado, r);

        Assert.Equal(StatusDocumento.Rejeitado, doc.Status);
        // Sem protocolo, o veredito vem do cStat do lote.
        Assert.Equal("225", doc.CodigoStatusRetorno);
        Assert.Equal("Lote processado", doc.MotivoRetorno);
        Assert.Null(doc.Protocolo);
        Assert.Null(doc.XmlProtocolado);
        // A resposta BRUTA da SEFAZ e guardada mesmo sem protocolo: e o log
        // completo para diagnostico de rejeicao.
        Assert.Equal("<retEnviNFe/>", doc.XmlRetornoSefaz);
    }

    [Fact]
    public void LoteRejeitadoSemMotivo_NuncaFicaSemTexto()
    {
        var doc = NovoDocumento();
        // Retorno cru com motivo de lote vazio: o operador ainda precisa ver algo.
        var r = Resultado("999", protocolo: null) with { MotivoLote = "" };

        ServicoAutorizacao.AplicarResultado(doc, StatusDocumento.Assinado, r);

        Assert.False(string.IsNullOrWhiteSpace(doc.MotivoRetorno));
        Assert.Contains("999", doc.MotivoRetorno);
        Assert.Equal("<retEnviNFe/>", doc.XmlRetornoSefaz);
    }

    [Theory]
    [InlineData("10", true)]   // nao existe na tabela (caso real: app mandou 10)
    [InlineData("03", true)]   // existe, mas ainda nao suportado
    [InlineData("", true)]
    [InlineData("01", false)]
    [InlineData("07", false)]
    [InlineData("49", false)]
    [InlineData("60", false)]
    [InlineData("99", false)]
    public void Cst_de_PIS_COFINS_fora_da_tabela_vira_problema_de_validacao(string cst, bool problema)
    {
        Assert.Equal(problema, ServicoAutorizacao.ProblemaCstPisCofins(cst) is not null);
    }
}
