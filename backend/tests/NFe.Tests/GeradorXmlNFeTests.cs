using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Sefaz.Xml;

namespace NFe.Tests;

public class GeradorXmlNFeTests
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/nfe";

    private static Empresa Emitente(RegimeTributario regime = RegimeTributario.RegimeNormal) => new()
    {
        Cnpj = "12345678000195",
        RazaoSocial = "EMPRESA TESTE LTDA",
        NomeFantasia = "TESTE",
        InscricaoEstadual = "111111111",
        RegimeTributario = regime,
        Logradouro = "RUA A", Numero = "100", Bairro = "CENTRO",
        CodigoMunicipio = "3550308", NomeMunicipio = "SAO PAULO",
        Uf = "SP", Cep = "01001000",
    };

    private static NotaFiscalRequest Request() => new()
    {
        Serie = 1,
        Numero = 123,
        CodigoNumerico = "00000015",
        NaturezaOperacao = "VENDA DE MERCADORIA",
        DataEmissao = new DateTimeOffset(2024, 8, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
        Destinatario = new DestinatarioRequest
        {
            Documento = "98765432000199",
            Nome = "CLIENTE REAL LTDA",
            IndicadorIe = IndicadorIeDestinatario.NaoContribuinte,
            Logradouro = "AV B", Numero = "200", Bairro = "JARDIM",
            CodigoMunicipio = "3304557", NomeMunicipio = "RIO DE JANEIRO",
            Uf = "RJ", Cep = "20000000",
        },
        Itens =
        {
            new ItemNotaRequest
            {
                Codigo = "P001", Descricao = "PRODUTO TESTE", Ncm = "22030000",
                Cfop = "5102", UnidadeComercial = "UN",
                Quantidade = 2, ValorUnitario = 10.50m, ValorBruto = 21.00m,
                CstIcms = "00", AliquotaIcms = 18m, BaseCalculoIcms = 21.00m, ValorIcms = 3.78m,
                CstPis = "07", CstCofins = "07",
            }
        },
    };

    [Fact]
    public void DhSaiEnt_FicaEntreDhEmiETpNF()
    {
        var req = Request();
        req.DataSaidaEntrada = new DateTimeOffset(2024, 8, 16, 8, 0, 0, TimeSpan.FromHours(-3));

        var ide = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "ide")!;
        var nomes = ide.Elements().Select(e => e.Name.LocalName).ToList();

        var posDhEmi = nomes.IndexOf("dhEmi");
        Assert.Equal("dhSaiEnt", nomes[posDhEmi + 1]);
        Assert.Equal("tpNF", nomes[posDhEmi + 2]);
    }

    [Fact]
    public void IeDoDestinatario_SaiSoComDigitos()
    {
        var req = Request();
        req.Destinatario.IndicadorIe = IndicadorIeDestinatario.ContribuinteIcms;
        req.Destinatario.InscricaoEstadual = "535.250.513.115";

        var dest = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "dest")!;

        Assert.Equal("535250513115", dest.Element(Ns + "IE")!.Value);
    }

    [Fact]
    public void Cst20_ComFormatacao_GeraIcms20ComReducaoDerivada()
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstIcms = "20.0";
        item.BaseCalculoIcms = 10.50m; // 50% de 21,00
        item.AliquotaIcms = 18m;
        item.ValorIcms = 1.89m;

        var icms = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!.Element(Ns + "ICMS20")!;

        Assert.Equal("20", icms.Element(Ns + "CST")!.Value);
        Assert.Equal("50.0000", icms.Element(Ns + "pRedBC")!.Value);
        Assert.Equal("10.50", icms.Element(Ns + "vBC")!.Value);
        Assert.Equal("1.89", icms.Element(Ns + "vICMS")!.Value);
    }

    [Fact]
    public void Cst20_ComBaseMaiorQueOItem_RejeitaComMensagemClara()
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstIcms = "20";
        item.BaseCalculoIcms = 50m; // maior que o valor do item (21,00)

        var ex = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));
        Assert.Contains("baseCalculoIcms", ex.Message);
    }

    [Fact]
    public void Cst20_ComPercentualInformado_UsaOValorInformado()
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstIcms = "20";
        item.PercentualReducaoBcIcms = 33.3333m;
        item.BaseCalculoIcms = 14.00m;

        var icms = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!.Element(Ns + "ICMS20")!;

        Assert.Equal("33.3333", icms.Element(Ns + "pRedBC")!.Value);
    }

    [Fact]
    public void Cst20_ComReducaoDe100PorCento_GeraBaseZerada()
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstIcms = "20";
        item.PercentualReducaoBcIcms = 100m;
        item.BaseCalculoIcms = 0m;
        item.AliquotaIcms = 18m;
        item.ValorIcms = 0m;

        var icms = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!.Element(Ns + "ICMS20")!;

        Assert.Equal("100.0000", icms.Element(Ns + "pRedBC")!.Value);
        Assert.Equal("0.00", icms.Element(Ns + "vBC")!.Value);
        Assert.Equal("0.00", icms.Element(Ns + "vICMS")!.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100.01)]
    public void Cst20_ComPercentualForaDoIntervalo_Rejeita(double percentual)
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstIcms = "20";
        item.PercentualReducaoBcIcms = (decimal)percentual;

        var ex = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));
        Assert.Contains("percentualReducaoBcIcms", ex.Message);
    }

    [Fact]
    public void Cst60_SemValoresDeSt_GeraIcms60ApenasComOrigECst()
    {
        var req = Request();
        req.Itens[0].CstIcms = "60";

        var icms = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!.Element(Ns + "ICMS60")!;

        Assert.Equal("60", icms.Element(Ns + "CST")!.Value);
        Assert.Equal(new[] { "orig", "CST" }, icms.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Cst60_ComValoresDeSt_EmiteNaOrdemDoXsd()
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstIcms = "60.0";
        item.BaseCalculoIcmsStRetido = 21.00m;
        item.AliquotaSuportadaConsumidor = 18m;
        item.ValorIcmsSubstituto = 3.00m;
        item.ValorIcmsStRetido = 3.78m;

        var icms = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!.Element(Ns + "ICMS60")!;

        Assert.Equal(
            new[] { "orig", "CST", "vBCSTRet", "pST", "vICMSSubstituto", "vICMSSTRet" },
            icms.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("21.00", icms.Element(Ns + "vBCSTRet")!.Value);
        Assert.Equal("18.0000", icms.Element(Ns + "pST")!.Value);
        Assert.Equal("3.78", icms.Element(Ns + "vICMSSTRet")!.Value);
    }

    [Fact]
    public void CstIcmsNaoSuportado_RejeitaEmVezDeGerarXmlInvalido()
    {
        var req = Request();
        req.Itens[0].CstIcms = "70";

        var ex = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));
        Assert.Contains("cstIcms", ex.Message);
    }

    private static XElement InfNFe(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Root!.Element(Ns + "infNFe")!;
    }

    private static string ChaveValida(long numero = 99) =>
        ChaveAcesso.Compor(
            "SP", 2024, 7, "12345678000195", ModeloDocumento.NFe, 1, numero, TipoEmissao.Normal, "00000077");

    private static NotaFiscalRequest RequestDevolucao(params string[] chaves)
    {
        var req = Request();
        req.Finalidade = 4;
        req.TipoOperacao = TipoOperacao.Entrada;
        req.NaturezaOperacao = "DEVOLUCAO DE VENDA";
        req.Itens[0].Cfop = "1202";
        foreach (var chave in chaves)
            req.DocumentosReferenciados.Add(new DocumentoReferenciadoRequest { ChaveNFe = chave });
        return req;
    }

    [Fact]
    public void Gerar_Devolucao_EmiteFinNFe4ENFrefComRefNFeNoFimDoIde()
    {
        var chave = ChaveValida();

        var ide = InfNFe(new GeradorXmlNFe()
            .Gerar(Emitente(), RequestDevolucao(chave), TipoAmbiente.Homologacao).Xml).Element(Ns + "ide")!;

        Assert.Equal("4", ide.Element(Ns + "finNFe")!.Value);
        var nfref = ide.Elements(Ns + "NFref").Single();
        Assert.Equal(chave, nfref.Element(Ns + "refNFe")!.Value);
        Assert.Equal("NFref", ide.Elements().Last().Name.LocalName);
        Assert.Equal("verProc", ide.Elements().Reverse().Skip(1).First().Name.LocalName);
    }

    [Fact]
    public void Gerar_Devolucao_AceitaChaveComMascaraEMaisDeUmaNota()
    {
        var chave1 = ChaveValida(99);
        var chave2 = ChaveValida(100);
        var comMascara = string.Join(" ", Enumerable.Range(0, 11).Select(i => chave1.Substring(i * 4, 4)));

        var ide = InfNFe(new GeradorXmlNFe()
            .Gerar(Emitente(), RequestDevolucao(comMascara, chave2), TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "ide")!;

        Assert.Equal(
            new[] { chave1, chave2 },
            ide.Elements(Ns + "NFref").Select(n => n.Element(Ns + "refNFe")!.Value));
    }

    [Fact]
    public void Gerar_SemDocumentosReferenciados_NaoEmiteNFref()
    {
        var ide = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "ide")!;

        Assert.Equal("1", ide.Element(Ns + "finNFe")!.Value);
        Assert.Empty(ide.Elements(Ns + "NFref"));
    }

    [Fact]
    public void Gerar_NotaNormalComDocumentoReferenciado_EmiteNFref()
    {
        var req = Request();
        req.DocumentosReferenciados.Add(new DocumentoReferenciadoRequest { ChaveNFe = ChaveValida() });

        var ide = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "ide")!;

        Assert.Single(ide.Elements(Ns + "NFref"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Gerar_FinalidadeQueExigeReferencia_SemChave_Falha(int finalidade)
    {
        var req = Request();
        req.Finalidade = finalidade;

        var ex = Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("documentosReferenciados", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void Gerar_FinalidadeForaDoIntervalo_Falha(int finalidade)
    {
        var req = Request();
        req.Finalidade = finalidade;

        Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));
    }

    [Fact]
    public void Gerar_ChaveReferenciadaComDvErrado_Falha()
    {
        var chave = ChaveValida();
        var dvErrado = chave[..43] + ((chave[43] - '0' + 1) % 10);

        var ex = Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), RequestDevolucao(dvErrado), TipoAmbiente.Homologacao));

        Assert.Contains("chaveNFe invalida", ex.Message);
    }

    [Fact]
    public void Gerar_ChaveReferenciadaComTamanhoErrado_Falha()
    {
        Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), RequestDevolucao("123"), TipoAmbiente.Homologacao));
    }

    [Fact]
    public void Gerar_ChaveReferenciadaRepetida_Falha()
    {
        var chave = ChaveValida();

        var ex = Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), RequestDevolucao(chave, chave), TipoAmbiente.Homologacao));

        Assert.Contains("repetida", ex.Message);
    }

    [Fact]
    public void Gerar_ComCBenef_EmiteEntreNcmECfop()
    {
        var req = Request();
        req.Itens[0].CBenef = " SP000001 ";

        var prod = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "prod")!;
        var nomes = prod.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal("SP000001", prod.Element(Ns + "cBenef")!.Value);
        Assert.Equal(new[] { "NCM", "cBenef", "CFOP" }, nomes.Skip(nomes.IndexOf("NCM")).Take(3));
    }

    [Fact]
    public void Gerar_SemCBenef_NaoEmiteElemento()
    {
        var prod = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "det")!.Element(Ns + "prod")!;

        Assert.Null(prod.Element(Ns + "cBenef"));
    }

    [Fact]
    public void Gerar_ComVolumes_EmiteElementosNaOrdemDoSchema()
    {
        var req = Request();
        req.QuantidadeVolumes = 2;
        req.Especie = "CAIXA & PACOTE";
        req.Marca = "MARCA TESTE";

        var transp = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "transp")!;
        var vol = transp.Element(Ns + "vol")!;

        Assert.Equal(new[] { "modFrete", "vol" }, transp.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(new[] { "qVol", "esp", "marca" }, vol.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("2", vol.Element(Ns + "qVol")!.Value);
        Assert.Equal("CAIXA & PACOTE", vol.Element(Ns + "esp")!.Value);
        Assert.Equal("MARCA TESTE", vol.Element(Ns + "marca")!.Value);
    }

    [Fact]
    public void Gerar_ComPesos_EmitePesoLiquidoEBrutoNoGrupoVol()
    {
        var req = Request();
        req.PesoLiquido = 12.34m;
        req.PesoBruto = 15.678m;

        var transp = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "transp")!;
        var vol = transp.Element(Ns + "vol")!;

        Assert.Equal(new[] { "pesoL", "pesoB" }, vol.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("12.340", vol.Element(Ns + "pesoL")!.Value);
        Assert.Equal("15.678", vol.Element(Ns + "pesoB")!.Value);
    }

    [Fact]
    public void Gerar_PesoComMaisDeTresCasasDecimaisFalhaComMensagemDireta()
    {
        var req = Request();
        req.PesoBruto = 1.2345m;

        var erro = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("pesoBruto", erro.Message);
    }

    [Fact]
    public void Gerar_SemVolumes_NaoEmiteGrupoVol()
    {
        var transp = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "transp")!;

        Assert.Null(transp.Element(Ns + "vol"));
    }

    [Fact]
    public void Gerar_ComTransportadoraEVeiculo_EmiteGruposNaOrdemDoSchema()
    {
        var req = Request();
        req.QuantidadeVolumes = 1;
        req.Transportadora = new TransportadoraRequest
        {
            Cnpj = "11.222.333/0001-81",
            Nome = "TRANSPORTES RAPIDO LTDA",
            InscricaoEstadual = "123.456.789",
            Endereco = "RUA DAS FLORES, 100",
            Municipio = "SAO PAULO",
            Uf = "sp",
        };
        req.Veiculo = new VeiculoTransporteRequest { Placa = "ABC-1D23", Uf = "SP", Rntc = "12345" };

        var transp = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "transp")!;

        Assert.Equal(
            new[] { "modFrete", "transporta", "veicTransp", "vol" },
            transp.Elements().Select(e => e.Name.LocalName).ToArray());

        var transporta = transp.Element(Ns + "transporta")!;
        Assert.Equal("11222333000181", transporta.Element(Ns + "CNPJ")!.Value);
        Assert.Equal("TRANSPORTES RAPIDO LTDA", transporta.Element(Ns + "xNome")!.Value);
        Assert.Equal("123456789", transporta.Element(Ns + "IE")!.Value);
        Assert.Equal("SP", transporta.Element(Ns + "UF")!.Value);
        Assert.Equal(
            new[] { "CNPJ", "xNome", "IE", "xEnder", "xMun", "UF" },
            transporta.Elements().Select(e => e.Name.LocalName).ToArray());

        var veic = transp.Element(Ns + "veicTransp")!;
        Assert.Equal("ABC1D23", veic.Element(Ns + "placa")!.Value);
        Assert.Equal("12345", veic.Element(Ns + "RNTC")!.Value);
    }

    [Fact]
    public void Gerar_TransportadoraComCnpjECpfFalhaComMensagemDireta()
    {
        var req = Request();
        req.Transportadora = new TransportadoraRequest { Cnpj = "11222333000181", Cpf = "12345678901", Nome = "TRANSP" };

        var erro = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("transportadora", erro.Message);
    }

    [Fact]
    public void Gerar_QuantidadeDeVolumesInvalidaFalhaComMensagemDireta()
    {
        var req = Request();
        req.QuantidadeVolumes = 0;

        var erro = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("quantidadeVolumes", erro.Message);
    }

    [Fact]
    public void Gerar_EspecieSemQuantidadeDeVolumesFalhaComMensagemDireta()
    {
        var req = Request();
        req.Especie = "CAIXA";

        var erro = Assert.Throws<ArgumentException>(() =>
            new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("obrigatoria", erro.Message);
    }

    [Fact]
    public void Gerar_PagamentoMaiorQueTotal_EmiteVTroco()
    {
        // vNF = 21.00. Pagamento em dinheiro de 25.00 -> troco de 4.00.
        // Sem vTroco a SEFAZ rejeita com 866.
        var req = Request();
        req.Pagamentos.Add(new PagamentoRequest { Meio = "01", Valor = 25.00m });

        var pag = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "pag")!;

        Assert.Equal("25.00", pag.Element(Ns + "detPag")!.Element(Ns + "vPag")!.Value);
        Assert.Equal("4.00", pag.Element(Ns + "vTroco")!.Value);
    }

    [Fact]
    public void Gerar_PagamentoIgualAoTotal_NaoEmiteVTroco()
    {
        var req = Request();
        req.Pagamentos.Add(new PagamentoRequest { Meio = "01", Valor = 21.00m });

        var pag = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Element(Ns + "pag")!;

        Assert.Null(pag.Element(Ns + "vTroco"));
    }

    [Fact]
    public void Gerar_AcrescimoEmVOutroComPagamentoTotal_NaoEmiteVTroco()
    {
        // Acrescimo entra no total via outrasDespesas: vNF = 21 + 5 = 26.
        // O pagamento de 26 casa com o total, entao NAO ha troco (nem 866).
        var req = Request();
        req.OutrasDespesas = 5.00m;
        req.Pagamentos.Add(new PagamentoRequest { Meio = "01", Valor = 26.00m });

        var inf = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml);
        var pag = inf.Element(Ns + "pag")!;

        Assert.Equal("26.00", inf.Element(Ns + "total")!.Element(Ns + "ICMSTot")!.Element(Ns + "vNF")!.Value);
        Assert.Null(pag.Element(Ns + "vTroco"));
    }

    [Fact]
    public void Gerar_OutrasDespesas_RateiaEmVOutroEFechaOTotal()
    {
        // Dois itens de vProd 33.33 e 66.67 (total 100.00) e um acrescimo de
        // 10.00. O rateio proporcional daria 3.333 e 6.667 -> arredonda para
        // 3.33 no primeiro e a sobra (10.00 - 3.33 = 6.67) na ultima linha.
        var req = Request();
        req.Itens.Clear();
        req.Itens.Add(new ItemNotaRequest
        {
            Codigo = "A", Descricao = "ITEM A", Ncm = "22030000", Cfop = "5102",
            Quantidade = 1, ValorUnitario = 33.33m, ValorBruto = 33.33m,
            CstIcms = "00", AliquotaIcms = 0m, BaseCalculoIcms = 0m, ValorIcms = 0m,
        });
        req.Itens.Add(new ItemNotaRequest
        {
            Codigo = "B", Descricao = "ITEM B", Ncm = "22030000", Cfop = "5102",
            Quantidade = 1, ValorUnitario = 66.67m, ValorBruto = 66.67m,
            CstIcms = "00", AliquotaIcms = 0m, BaseCalculoIcms = 0m, ValorIcms = 0m,
        });
        req.OutrasDespesas = 10.00m;

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);
        var inf = InfNFe(r.Xml);

        var vOutrosItens = inf.Elements(Ns + "det")
            .Select(d => decimal.Parse(
                d.Element(Ns + "prod")!.Element(Ns + "vOutro")!.Value,
                CultureInfo.InvariantCulture))
            .ToList();

        // Precos unitarios permanecem intactos.
        var vProds = inf.Elements(Ns + "det")
            .Select(d => d.Element(Ns + "prod")!.Element(Ns + "vProd")!.Value)
            .ToList();
        Assert.Equal(new[] { "33.33", "66.67" }, vProds);

        // Rateio: 3.33 no primeiro, sobra de centavos (6.67) na ultima linha.
        Assert.Equal(new[] { 3.33m, 6.67m }, vOutrosItens);

        var tot = inf.Element(Ns + "total")!.Element(Ns + "ICMSTot")!;
        // vOutro do total = soma exata dos itens; vNF = vProd + vOutro.
        Assert.Equal("10.00", tot.Element(Ns + "vOutro")!.Value);
        Assert.Equal("110.00", tot.Element(Ns + "vNF")!.Value);
        Assert.Equal("100.00", tot.Element(Ns + "vProd")!.Value);
    }

    private static NotaFiscalRequest RequestDoisItensComFrete(decimal valorFrete)
    {
        var req = Request();
        req.ModalidadeFrete = ModalidadeFrete.ContratacaoPorContaDestinatario;
        req.Itens.Clear();
        req.Itens.Add(new ItemNotaRequest
        {
            Codigo = "A", Descricao = "ITEM A", Ncm = "22030000", Cfop = "5102",
            Quantidade = 1, ValorUnitario = 33.33m, ValorBruto = 33.33m,
            CstIcms = "00", AliquotaIcms = 0m, BaseCalculoIcms = 0m, ValorIcms = 0m,
        });
        req.Itens.Add(new ItemNotaRequest
        {
            Codigo = "B", Descricao = "ITEM B", Ncm = "22030000", Cfop = "5102",
            Quantidade = 1, ValorUnitario = 66.67m, ValorBruto = 66.67m,
            CstIcms = "00", AliquotaIcms = 0m, BaseCalculoIcms = 0m, ValorIcms = 0m,
        });
        req.ValorFrete = valorFrete;
        return req;
    }

    [Fact]
    public void Gerar_FreteDaNota_RateiaEmVFreteEFechaOTotal()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), RequestDoisItensComFrete(10.00m), TipoAmbiente.Homologacao);
        var inf = InfNFe(r.Xml);

        var vFretes = inf.Elements(Ns + "det")
            .Select(d => decimal.Parse(
                d.Element(Ns + "prod")!.Element(Ns + "vFrete")!.Value,
                CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(new[] { 3.33m, 6.67m }, vFretes);

        var tot = inf.Element(Ns + "total")!.Element(Ns + "ICMSTot")!;
        Assert.Equal("10.00", tot.Element(Ns + "vFrete")!.Value);
        Assert.Equal("110.00", tot.Element(Ns + "vNF")!.Value);
    }

    [Theory]
    [InlineData("01", "PISAliq", "COFINSAliq")]
    [InlineData("60", "PISOutr", "COFINSOutr")]
    [InlineData("99", "PISOutr", "COFINSOutr")]
    [InlineData("07", "PISNT", "COFINSNT")]
    public void Gerar_CstPisCofins_EscolheOGrupoCorreto(string cst, string grupoPis, string grupoCofins)
    {
        var req = Request();
        req.Itens[0].CstPis = cst;
        req.Itens[0].CstCofins = cst;

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);
        var imposto = InfNFe(r.Xml).Element(Ns + "det")!.Element(Ns + "imposto")!;

        Assert.NotNull(imposto.Element(Ns + "PIS")!.Element(Ns + grupoPis));
        Assert.NotNull(imposto.Element(Ns + "COFINS")!.Element(Ns + grupoCofins));
    }

    [Fact]
    public void Gerar_PisCofinsOutrasOperacoes_GravaValoresENoTotal()
    {
        var req = Request();
        var item = req.Itens[0];
        item.CstPis = "60";
        item.BaseCalculoPis = 100m;
        item.AliquotaPis = 1.65m;
        item.ValorPis = 1.65m;
        item.CstCofins = "60";
        item.BaseCalculoCofins = 100m;
        item.AliquotaCofins = 7.6m;
        item.ValorCofins = 7.60m;

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);
        var inf = InfNFe(r.Xml);
        var pis = inf.Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "PIS")!.Element(Ns + "PISOutr")!;

        Assert.Equal("60", pis.Element(Ns + "CST")!.Value);
        Assert.Equal("100.00", pis.Element(Ns + "vBC")!.Value);
        Assert.Equal("1.6500", pis.Element(Ns + "pPIS")!.Value);
        Assert.Equal("1.65", pis.Element(Ns + "vPIS")!.Value);

        var tot = inf.Element(Ns + "total")!.Element(Ns + "ICMSTot")!;
        Assert.Equal("1.65", tot.Element(Ns + "vPIS")!.Value);
        Assert.Equal("7.60", tot.Element(Ns + "vCOFINS")!.Value);
    }

    [Fact]
    public void Gerar_FreteDaNotaMaisFreteDoItem_SomaNoTotal()
    {
        var req = RequestDoisItensComFrete(10.00m);
        req.Itens[0].ValorFrete = 2.00m;

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);
        var tot = InfNFe(r.Xml).Element(Ns + "total")!.Element(Ns + "ICMSTot")!;

        Assert.Equal("12.00", tot.Element(Ns + "vFrete")!.Value);
        Assert.Equal("112.00", tot.Element(Ns + "vNF")!.Value);
    }

    [Fact]
    public void Gerar_FreteComModalidadeSemTransporte_FalhaComMensagemDireta()
    {
        var req = RequestDoisItensComFrete(10.00m);
        req.ModalidadeFrete = ModalidadeFrete.SemTransporte;

        var ex = Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));
        Assert.Contains("modalidadeFrete", ex.Message);
    }

    [Fact]
    public void Gerar_FreteNegativo_FalhaComMensagemDireta()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), RequestDoisItensComFrete(-1m), TipoAmbiente.Homologacao));
        Assert.Contains("valorFrete", ex.Message);
    }

    [Fact]
    public void Gerar_ChaveRetornada_ConfereComOsCamposDoIde()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);

        Assert.True(ChaveAcesso.Valida(r.ChaveAcesso));

        var ide = InfNFe(r.Xml).Element(Ns + "ide")!;
        Assert.Equal("35", ide.Element(Ns + "cUF")!.Value);           // SP
        Assert.Equal(r.CodigoNumerico, ide.Element(Ns + "cNF")!.Value);
        Assert.Equal(r.DigitoVerificador.ToString(), ide.Element(Ns + "cDV")!.Value);
        Assert.Equal("2", ide.Element(Ns + "tpAmb")!.Value);          // homologacao
    }

    [Fact]
    public void Gerar_IdDoInfNFe_UsaPrefixoNFeMaisChave()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);

        Assert.Equal("NFe" + r.ChaveAcesso, InfNFe(r.Xml).Attribute("Id")!.Value);
    }

    [Fact]
    public void Gerar_EmHomologacao_ForcaRazaoSocialPadraoNoDestinatario()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);

        var xNome = InfNFe(r.Xml).Element(Ns + "dest")!.Element(Ns + "xNome")!.Value;
        Assert.Equal("NF-E EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL", xNome);
    }

    [Fact]
    public void Gerar_EmProducao_PreservaNomeRealDoDestinatario()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Producao);

        var xNome = InfNFe(r.Xml).Element(Ns + "dest")!.Element(Ns + "xNome")!.Value;
        Assert.Equal("CLIENTE REAL LTDA", xNome);
    }

    [Fact]
    public void Gerar_RegimeNormal_UsaGrupoICMS00()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(RegimeTributario.RegimeNormal), Request(), TipoAmbiente.Homologacao);

        var icms = InfNFe(r.Xml).Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!;
        Assert.NotNull(icms.Element(Ns + "ICMS00"));
        Assert.Equal("3", emitCrt(r.Xml)); // CRT do regime normal
    }

    [Fact]
    public void Gerar_SimplesNacional_UsaGrupoICMSSN102()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(RegimeTributario.SimplesNacional), Request(), TipoAmbiente.Homologacao);

        var icms = InfNFe(r.Xml).Element(Ns + "det")!.Element(Ns + "imposto")!.Element(Ns + "ICMS")!;
        var sn = icms.Element(Ns + "ICMSSN102")!;
        Assert.NotNull(sn);
        Assert.Equal("102", sn.Element(Ns + "CSOSN")!.Value);
        Assert.Equal("1", emitCrt(r.Xml)); // CRT do Simples
    }

    private static string emitCrt(string xml) =>
        InfNFe(xml).Element(Ns + "emit")!.Element(Ns + "CRT")!.Value;

    [Fact]
    public void Gerar_OrdemDosElementosDeInfNFe_SegueOLayout()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);

        var ordem = InfNFe(r.Xml).Elements().Select(e => e.Name.LocalName).ToList();

        // A sequencia do XSD: ide, emit, dest, det..., total, transp, pag[, infAdic].
        Assert.Equal("ide", ordem[0]);
        Assert.Equal("emit", ordem[1]);
        Assert.Equal("dest", ordem[2]);
        Assert.Equal("det", ordem[3]);
        Assert.Equal("total", ordem[4]);
        Assert.Equal("transp", ordem[5]);
        Assert.Equal("pag", ordem[6]);
    }

    [Fact]
    public void Gerar_OrdemDoIde_ColocaDhSaiEntAposDhEmi()
    {
        var req = Request();
        req.DataSaidaEntrada = req.DataEmissao.AddHours(2);

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);
        var ide = InfNFe(r.Xml).Element(Ns + "ide")!.Elements().Select(e => e.Name.LocalName).ToList();

        var iDhEmi = ide.IndexOf("dhEmi");
        var iSai = ide.IndexOf("dhSaiEnt");
        Assert.Equal(iDhEmi + 1, iSai); // XSD 4.00: dhSaiEnt vem logo apos dhEmi, antes de tpNF
        Assert.True(iSai < ide.IndexOf("tpNF"));
    }

    [Fact]
    public void Gerar_ValoresDecimais_UsamPontoECasasFixas()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);
        var prod = InfNFe(r.Xml).Element(Ns + "det")!.Element(Ns + "prod")!;

        Assert.Equal("2.000", prod.Element(Ns + "qCom")!.Value);
        Assert.Equal("10.500", prod.Element(Ns + "vUnCom")!.Value);
        Assert.Equal("21.00", prod.Element(Ns + "vProd")!.Value);

        var vNF = InfNFe(r.Xml).Element(Ns + "total")!.Element(Ns + "ICMSTot")!.Element(Ns + "vNF")!.Value;
        Assert.Equal("21.00", vNF);
        // Garante cultura invariante: nunca virgula decimal.
        Assert.DoesNotContain(",", r.Xml);
    }

    [Fact]
    public void Gerar_cNFIgualAoNumero_ESubstituido()
    {
        var req = Request();
        req.Numero = 15;
        req.CodigoNumerico = "00000015"; // colide com nNF

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);

        Assert.NotEqual("00000015", r.CodigoNumerico);
    }

    [Fact]
    public void Gerar_SemItens_Lanca()
    {
        var req = Request();
        req.Itens.Clear();

        Assert.Throws<InvalidOperationException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));
    }

    [Fact]
    public void Gerar_dhEmi_TemOffsetDeBrasilia()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);
        var dhEmi = InfNFe(r.Xml).Element(Ns + "ide")!.Element(Ns + "dhEmi")!.Value;

        Assert.EndsWith("-03:00", dhEmi);
        Assert.StartsWith("2024-08-15T10:00:00", dhEmi);
    }

    [Fact]
    public void Gerar_DestinatarioComCpf_UsaTagCPF()
    {
        var req = Request();
        req.Destinatario.Documento = "12345678909"; // 11 digitos

        var r = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Producao);
        var dest = InfNFe(r.Xml).Element(Ns + "dest")!;

        Assert.NotNull(dest.Element(Ns + "CPF"));
        Assert.Null(dest.Element(Ns + "CNPJ"));
    }

    [Fact]
    public void Gerar_EntaoAssinar_ProduzAssinaturaValida()
    {
        var r = new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao);

        var pfx = CertificadoFalso.CriarPfx("senha");
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, "senha", X509KeyStorageFlags.Exportable);

        var assinador = new AssinadorXml();
        var assinado = assinador.Assinar(r.Xml, ChaveAcesso.ParaId(r.ChaveAcesso), cert, "NFe");

        Assert.True(assinador.Verificar(assinado));
    }

    [Fact]
    public void RegerarEmitente_AtualizaCrtMantendoCategoria()
    {
        var gerador = new GeradorXmlNFe();
        // Nota gerada como Simples (CRT=1).
        var r = gerador.Gerar(Emitente(RegimeTributario.SimplesNacional), Request(), TipoAmbiente.Homologacao);
        Assert.Equal("1", emitCrt(r.Xml));

        // Cadastro corrigido para MEI (CRT=4) — ainda categoria "Simples".
        var emitMei = Emitente(RegimeTributario.MicroempreendedorIndividual);
        var regenerado = gerador.RegerarEmitente(r.Xml, emitMei);

        Assert.Equal("4", emitCrt(regenerado));
        // A chave (Id do infNFe) nao muda: CRT nao entra na sua composicao.
        Assert.Equal(InfNFe(r.Xml).Attribute("Id")!.Value, InfNFe(regenerado).Attribute("Id")!.Value);
    }

    [Fact]
    public void RegerarEmitente_MudancaDeCategoria_Lanca()
    {
        var gerador = new GeradorXmlNFe();
        // Nota gerada como Simples (grupo ICMSSN).
        var r = gerador.Gerar(Emitente(RegimeTributario.SimplesNacional), Request(), TipoAmbiente.Homologacao);

        // Tentar regerar como Regime Normal exigiria refazer os grupos de ICMS.
        var ex = Assert.Throws<InvalidOperationException>(
            () => gerador.RegerarEmitente(r.Xml, Emitente(RegimeTributario.RegimeNormal)));
        Assert.Contains("categoria", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegerarEmitente_RemoveAssinaturaAntiga()
    {
        var gerador = new GeradorXmlNFe();
        var r = gerador.Gerar(Emitente(RegimeTributario.SimplesNacional), Request(), TipoAmbiente.Homologacao);

        var pfx = CertificadoFalso.CriarPfx("senha");
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, "senha", X509KeyStorageFlags.Exportable);
        var assinado = new AssinadorXml().Assinar(r.Xml, ChaveAcesso.ParaId(r.ChaveAcesso), cert, "NFe");

        var regenerado = gerador.RegerarEmitente(assinado, Emitente(RegimeTributario.MicroempreendedorIndividual));

        XNamespace dsig = "http://www.w3.org/2000/09/xmldsig#";
        Assert.Empty(XDocument.Parse(regenerado).Descendants(dsig + "Signature"));
    }

    [Fact]
    public void Gerar_SemIbsCbs_NaoEmiteGrupoNemTotal()
    {
        var infNFe = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao).Xml);

        Assert.Empty(infNFe.Descendants(Ns + "IBSCBS"));
        Assert.Empty(infNFe.Descendants(Ns + "IBSCBSTot"));
    }

    [Fact]
    public void Gerar_ComIbsCbs_EmiteGrupoNoItemETotalNaOrdemDoSchema()
    {
        var req = Request();
        req.Itens[0].IbsCbs = new IbsCbsItemRequest
        {
            Cst = "000",
            ClassificacaoTributaria = "000001",
            AliquotaIbsUf = 0.1m,
            AliquotaIbsMunicipal = 0m,
            AliquotaCbs = 0.9m,
        };

        var infNFe = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml);

        var imposto = infNFe.Element(Ns + "det")!.Element(Ns + "imposto")!;
        Assert.Equal(new[] { "ICMS", "PIS", "COFINS", "IBSCBS" },
            imposto.Elements().Select(e => e.Name.LocalName));

        var ibsCbs = imposto.Element(Ns + "IBSCBS")!;
        Assert.Equal(new[] { "CST", "cClassTrib", "gIBSCBS" }, ibsCbs.Elements().Select(e => e.Name.LocalName));
        var g = ibsCbs.Element(Ns + "gIBSCBS")!;
        Assert.Equal(new[] { "vBC", "gIBSUF", "gIBSMun", "vIBS", "gCBS" }, g.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("21.00", g.Element(Ns + "vBC")!.Value);
        Assert.Equal("0.02", g.Element(Ns + "gIBSUF")!.Element(Ns + "vIBSUF")!.Value);
        Assert.Equal("0.19", g.Element(Ns + "gCBS")!.Element(Ns + "vCBS")!.Value);

        var total = infNFe.Element(Ns + "total")!;
        Assert.Equal(new[] { "ICMSTot", "IBSCBSTot" }, total.Elements().Select(e => e.Name.LocalName));
        var tot = total.Element(Ns + "IBSCBSTot")!;
        Assert.Equal("21.00", tot.Element(Ns + "vBCIBSCBS")!.Value);
        Assert.Equal("0.02", tot.Element(Ns + "gIBS")!.Element(Ns + "vIBS")!.Value);
        Assert.Equal("0.19", tot.Element(Ns + "gCBS")!.Element(Ns + "vCBS")!.Value);
    }

    [Fact]
    public void Gerar_IbsCbsComValorInformado_UsaValorDoApp()
    {
        var req = Request();
        req.Itens[0].IbsCbs = new IbsCbsItemRequest
        {
            ClassificacaoTributaria = "000001",
            BaseCalculo = 20m,
            AliquotaCbs = 0.9m,
            ValorCbs = 0.25m,
        };

        var infNFe = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml);

        Assert.Equal("20.00", infNFe.Descendants(Ns + "gIBSCBS").First().Element(Ns + "vBC")!.Value);
        Assert.Equal("0.25", infNFe.Descendants(Ns + "vCBS").Last().Value);
    }

    [Fact]
    public void Gerar_IbsCbsComReducao_EmiteGRedNaOrdemECalculaSobreAliquotaEfetiva()
    {
        var req = Request();
        req.Itens[0].IbsCbs = new IbsCbsItemRequest
        {
            ClassificacaoTributaria = "000001",
            BaseCalculo = 100m,
            AliquotaIbsUf = 10m,
            ReducaoAliquotaIbsUf = 60m,
            AliquotaCbs = 10m,
            AliquotaEfetivaCbs = 4m,
        };

        var g = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Descendants(Ns + "gIBSCBS").First();

        var uf = g.Element(Ns + "gIBSUF")!;
        Assert.Equal(new[] { "pIBSUF", "gRed", "vIBSUF" }, uf.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("60.0000", uf.Element(Ns + "gRed")!.Element(Ns + "pRedAliq")!.Value);
        Assert.Equal("4.0000", uf.Element(Ns + "gRed")!.Element(Ns + "pAliqEfet")!.Value);
        Assert.Equal("4.00", uf.Element(Ns + "vIBSUF")!.Value);

        var cbs = g.Element(Ns + "gCBS")!;
        Assert.Equal("60.0000", cbs.Element(Ns + "gRed")!.Element(Ns + "pRedAliq")!.Value);
        Assert.Equal("4.00", cbs.Element(Ns + "vCBS")!.Value);

        var mun = g.Element(Ns + "gIBSMun")!;
        Assert.Equal(new[] { "pIBSMun", "gRed", "vIBSMun" }, mun.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("60.0000", mun.Element(Ns + "gRed")!.Element(Ns + "pRedAliq")!.Value);
    }

    [Fact]
    public void Gerar_IbsCbsReducaoTotalSoNaUf_MunicipioHerdaGRed()
    {
        var req = Request();
        req.Itens[0].IbsCbs = new IbsCbsItemRequest
        {
            Cst = "200",
            ClassificacaoTributaria = "200003",
            AliquotaIbsUf = 0.1m,
            AliquotaCbs = 0.9m,
            ReducaoAliquotaIbsUf = 100m,
            AliquotaEfetivaIbsUf = 0m,
            ReducaoAliquotaCbs = 100m,
            AliquotaEfetivaCbs = 0m,
        };

        var g = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml)
            .Descendants(Ns + "gIBSCBS").First();

        var red = g.Element(Ns + "gIBSMun")!.Element(Ns + "gRed");
        Assert.NotNull(red);
        Assert.Equal("100.0000", red!.Element(Ns + "pRedAliq")!.Value);
        Assert.Equal("0.0000", red.Element(Ns + "pAliqEfet")!.Value);
        Assert.Equal("0.00", g.Element(Ns + "vIBS")!.Value);
    }

    [Fact]
    public void Gerar_IbsCbsComReducaoInvalida_FalhaComMensagemDireta()
    {
        var req = Request();
        req.Itens[0].IbsCbs = new IbsCbsItemRequest
        {
            ClassificacaoTributaria = "000001",
            AliquotaCbs = 1m,
            ReducaoAliquotaCbs = 120m,
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("reducao", ex.Message);
    }

    [Fact]
    public void Gerar_IbsCbsComClassificacaoInvalida_FalhaComMensagemDireta()
    {
        var req = Request();
        req.Itens[0].IbsCbs = new IbsCbsItemRequest { ClassificacaoTributaria = "123" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao));

        Assert.Contains("cClassTrib", ex.Message);
    }

    [Theory]
    [InlineData("2151530921415354", null)]          // dois telefones colados (16 digitos)
    [InlineData("(11) 3687-7622", "1136877622")]
    [InlineData("12345", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Fone_segue_o_schema_ou_e_omitido(string? entrada, string? esperado)
    {
        var el = GeradorXmlNFe.Fone(entrada);

        Assert.Equal(esperado, el?.Value);
    }

    // ---- Fatura / duplicatas (grupo cobr) ---------------------------------

    [Fact]
    public void Cobranca_gera_fat_e_dup_numeradas_entre_transp_e_pag()
    {
        var req = Request(); // emissao 15/08/2024, vNF 21,00
        req.Duplicatas.Add(new DuplicataRequest { Vencimento = "2024-09-15", Valor = 10.50m });
        req.Duplicatas.Add(new DuplicataRequest { Vencimento = "15/10/2024", Valor = 10.50m });

        var inf = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao).Xml);
        var nomes = inf.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(nomes.IndexOf("transp") + 1, nomes.IndexOf("cobr"));
        Assert.Equal(nomes.IndexOf("cobr") + 1, nomes.IndexOf("pag"));

        var cobr = inf.Element(Ns + "cobr")!;
        var fat = cobr.Element(Ns + "fat")!;
        Assert.Equal("123", fat.Element(Ns + "nFat")!.Value);
        Assert.Equal("21.00", fat.Element(Ns + "vOrig")!.Value);
        Assert.Equal("0.00", fat.Element(Ns + "vDesc")!.Value);
        Assert.Equal("21.00", fat.Element(Ns + "vLiq")!.Value);

        var dups = cobr.Elements(Ns + "dup").ToList();
        Assert.Equal(2, dups.Count);
        Assert.Equal("001", dups[0].Element(Ns + "nDup")!.Value);
        Assert.Equal("2024-09-15", dups[0].Element(Ns + "dVenc")!.Value);
        Assert.Equal("002", dups[1].Element(Ns + "nDup")!.Value);
        Assert.Equal("2024-10-15", dups[1].Element(Ns + "dVenc")!.Value);
        Assert.Equal("10.50", dups[1].Element(Ns + "vDup")!.Value);
    }

    [Fact]
    public void Sem_fatura_nem_duplicatas_nao_gera_cobr()
    {
        var inf = InfNFe(new GeradorXmlNFe().Gerar(Emitente(), Request(), TipoAmbiente.Homologacao).Xml);
        Assert.Null(inf.Element(Ns + "cobr"));
    }

    [Fact]
    public void Cobranca_invalida_lista_os_problemas()
    {
        var req = Request();
        req.Fatura = new FaturaRequest { Numero = "71274", ValorOriginal = 30m };
        req.Duplicatas.Add(new DuplicataRequest { Numero = "1", Vencimento = "2024-08-01", Valor = 10m });
        req.Duplicatas.Add(new DuplicataRequest { Vencimento = "data-ruim", Valor = 0m });

        var erros = GeradorXmlNFe.ValidarCobranca(req);

        Assert.Contains(erros, e => e.Contains("numero '1'"));
        Assert.Contains(erros, e => e.Contains("anterior a emissao"));
        Assert.Contains(erros, e => e.Contains("'data-ruim'"));
        Assert.Contains(erros, e => e.Contains("maior que zero"));
        Assert.Contains(erros, e => e.Contains("soma das parcelas"));
    }
}
