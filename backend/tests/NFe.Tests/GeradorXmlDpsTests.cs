using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Nfse.Contratos;
using NFe.Nfse.Nacional;
using Xunit;

namespace NFe.Tests;

/// <summary>
/// Cobre a resolucao do municipio emissor (cLocEmi) na DPS. E exatamente o alvo
/// do override enviado pelo app (configuracao.codigoMunicipioEmissao), que o
/// ServicoNfse aplica em cfg.CodigoMunicipioEmissao antes de gerar o XML.
/// </summary>
public class GeradorXmlDpsTests
{
    private static readonly XNamespace Ns = NfseNacionalEndpoints.NamespaceDps;

    [Fact]
    public void CLocEmi_UsaCodigoDaConfig_QuandoInformado()
    {
        // Config define o emissor (3550308 = Sao Paulo); o cadastro da empresa
        // tem outro municipio (3534401). A config deve vencer.
        var (xml, id) = Gerar(municipioEmpresa: "3534401", municipioConfig: "3550308");

        Assert.Equal("3550308", InfDps(xml).Element(Ns + "cLocEmi")!.Value);
        Assert.StartsWith("DPS3550308", id);
    }

    [Fact]
    public void CLocEmi_CaiNoMunicipioDaEmpresa_QuandoConfigVazia()
    {
        var (xml, id) = Gerar(municipioEmpresa: "3534401", municipioConfig: null);

        Assert.Equal("3534401", InfDps(xml).Element(Ns + "cLocEmi")!.Value);
        Assert.StartsWith("DPS3534401", id);
    }

    [Theory]
    [InlineData("1.01")]
    [InlineData("01.01")]
    [InlineData("")]
    [InlineData(null)]
    public void CTribMun_EhOmitido_QuandoNaoForCodigoMunicipalDeTresDigitos(string? valor)
    {
        var (xml, _) = Gerar("3534401", "3534401", valor);

        var cServ = InfDps(xml).Element(Ns + "serv")!.Element(Ns + "cServ")!;
        Assert.Null(cServ.Element(Ns + "cTribMun"));
    }

    [Theory]
    [InlineData("1", "001")]
    [InlineData("101", "101")]
    public void CTribMun_EhEnviado_QuandoForCodigoMunicipalValido(string valor, string esperado)
    {
        var (xml, _) = Gerar("3534401", "3534401", valor);

        var cServ = InfDps(xml).Element(Ns + "serv")!.Element(Ns + "cServ")!;
        Assert.Equal(esperado, cServ.Element(Ns + "cTribMun")!.Value);
    }

    // -------------------------------------------------------------- helpers

    private static (string Xml, string Id) Gerar(string municipioEmpresa, string? municipioConfig, string? cTribMun = null)
    {
        var empresa = new Empresa
        {
            Cnpj = "12345678000199",
            RazaoSocial = "Prestador Teste LTDA",
            InscricaoMunicipal = "123456",
            CodigoMunicipio = municipioEmpresa,
            Uf = "SP"
        };
        var cfg = new EmpresaNfseConfig
        {
            EmpresaId = empresa.Id,
            Padrao = PadraoNfse.Nacional,
            SerieDps = "00001",
            OpSimpNac = OpcaoSimplesNacional.OptanteMeEpp,
            RegApTribSN = 1,
            RegEspTrib = RegimeEspecialTributacao.Nenhum,
            CodigoMunicipioEmissao = municipioConfig,
            VersaoAplicativo = "Teste 1.0"
        };
        var req = new NfseRequest
        {
            Servico = new ServicoNfseRequest
            {
                CodigoTributacaoNacional = "010701",
                CodigoTributacaoMunicipal = cTribMun,
                Descricao = "Servico de teste"
            },
            Valores = new ValoresNfseRequest
            {
                ValorServico = 1000.00m,
                AliquotaIss = 2.00m,
                TributacaoIssqn = TributacaoIssqn.OperacaoTributavel,
                RetencaoIssqn = TipoRetencaoIssqn.NaoRetido
            }
        };

        var dados = new DpsDados(
            req, empresa, cfg, TipoAmbiente.Homologacao, "00001", 1,
            new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
            new DateTime(2026, 1, 15));

        return new GeradorXmlDps().Gerar(dados);
    }

    private static XElement InfDps(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Root!.Element(Ns + "infDPS")!;
    }
}
