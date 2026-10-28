using System.Text.Json;
using System.Xml.Linq;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Nfse;
using NFe.Nfse.Contratos;
using NFe.Nfse.Nacional;
using NFe.Infrastructure.ServicosNfse;

namespace NFe.Tests;

public sealed class ProvedorOsascoTests
{
    [Fact]
    public void MontarEmitirLegado_RemoveCamposRtcEUsaNotaFiscal()
    {
        XNamespace p = "http://schemas.datacontract.org/2004/07/Eissnfe.Dominio.DataTransferObject.Prestador";
        XNamespace m = "http://schemas.datacontract.org/2004/07/Eissnfe.Negocio.WebServices.Mensagem";
        var dados = new XElement(m + "NotaFiscal_RTC",
            new XElement(p + "Aliquota", "2.00"),
            new XElement(p + "Valor", "10.00"),
            new XElement(p + "CodigoNBS", "123"),
            new XElement(p + "ClassTrib", "000001"),
            new XElement(p + "IndOperacao", string.Empty));

        var body = ProvedorOsasco.MontarEmitirLegado(dados);

        Assert.Equal("Emitir", body.Name.LocalName);
        var nota = body.Descendants(m + "NotaFiscal").Single();
        Assert.Equal(new[] { "Aliquota", "Valor", "IndOperacao" }, nota.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(string.Empty, nota.Element(p + "IndOperacao")!.Value);
    }

    [Fact]
    public void InterpretarEmissao_LeEmitirResult()
    {
        var xml = XElement.Parse("<Envelope><Body><EmitirResponse><EmitirResult><Erro>false</Erro><NotaFiscalGerada><Numero>55</Numero><Autenticador>AB</Autenticador></NotaFiscalGerada></EmitirResult></EmitirResponse></Body></Envelope>");
        var r = ProvedorOsasco.InterpretarEmissao(xml, "", 1, TipoAmbiente.Producao);
        Assert.True(r.Sucesso);
        Assert.Equal("55", r.NumeroNfse);
    }

    [Fact]
    public void RespostaHomologacao_NaoETratadaComoAutorizacaoDeProducao()
    {
        var response = XElement.Parse("<Envelope><Body><EmitirResponse><EmitirResult><Erro>false</Erro><NotaFiscalGerada><Identificador>00001-2111</Identificador><Autenticador>HOMOLOGAÇÃO</Autenticador><Numero>5847553</Numero></NotaFiscalGerada></EmitirResult></EmitirResponse></Body></Envelope>");

        var producao = ProvedorOsasco.InterpretarEmissao(response, "retorno", 1, TipoAmbiente.Producao);
        var homologacao = ProvedorOsasco.InterpretarEmissao(response, "retorno", 1, TipoAmbiente.Homologacao);

        Assert.False(producao.Sucesso);
        Assert.Contains("HOMOLOGAÇÃO", producao.Motivo);
        Assert.True(homologacao.Sucesso);
        Assert.Equal("5847553", homologacao.NumeroNfse);
    }

    private static readonly XNamespace Prestador = "http://schemas.datacontract.org/2004/07/Eissnfe.Dominio.DataTransferObject.Prestador";
    private static readonly XNamespace Mensagem = "http://schemas.datacontract.org/2004/07/Eissnfe.Negocio.WebServices.Mensagem";
    private static readonly XNamespace Contribuinte = "http://schemas.datacontract.org/2004/07/Eissnfe.Dominio.DataTransferObject.Contribuinte";

    [Fact]
    public void CompletarCamposRtc_PreencheNbsClassTribELocalQuandoAusentes()
    {
        var dados = new XElement(Mensagem + "NotaFiscal_RTC",
            new XElement(Prestador + "Atividade", "1.01"),
            new XElement(Prestador + "CodigoCidadeIBGEPrestacaoServico", "3534401"),
            new XElement(Prestador + "Valor", "1403.95"),
            new XElement(Prestador + "CodigoCST", "000"));
        var resposta = XElement.Parse("""
            <r xmlns:b="urn:b"><b:Correlacao><b:CodigoClassTrib>410007</b:CodigoClassTrib><b:CodigoIndOperacao>100301</b:CodigoIndOperacao><b:CodigoNBS>1.1502.10.00</b:CodigoNBS></b:Correlacao>
            <b:Correlacao><b:CodigoClassTrib>000001</b:CodigoClassTrib><b:CodigoIndOperacao>100301</b:CodigoIndOperacao><b:CodigoNBS>1.1502.10.00</b:CodigoNBS></b:Correlacao></r>
            """);

        Assert.True(ProvedorOsasco.PrecisaCorrelacaoRtc(dados));
        var completo = ProvedorOsasco.CompletarCamposRtc(dados, ProvedorOsasco.InterpretarCorrelacoes(resposta));

        var nomes = completo.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(new[] { "Atividade", "CodigoCidadeIBGEPrestacaoServico", "Valor", "ClassTrib", "CodigoCST", "CodigoNBS", "IdLocalIncidencia", "IndOperacao" }, nomes);
        Assert.Equal("000001", completo.Element(Prestador + "ClassTrib")!.Value);
        Assert.Equal("1.1502.10.00", completo.Element(Prestador + "CodigoNBS")!.Value);
        Assert.Equal("0", completo.Element(Prestador + "IdLocalIncidencia")!.Value);
        Assert.Equal("100301", completo.Element(Prestador + "IndOperacao")!.Value);
    }

    [Fact]
    public void JsonDoApp_DesserializaEnderecoDePrestacaoRtc()
    {
        var request = JsonSerializer.Deserialize<NfseRequest>("""
            {"servico":{
              "codigoMunicipioPrestacao":"3534401",
              "codigoCidadeIBGEPrestacaoServico":"3534401",
              "enderecoPrestacaoServico":"Rua Paulo Aparecido Pereira",
              "numeroPrestacaoServico":"365",
              "bairroPrestacaoServico":"Bussocaba",
              "cidadePrestacaoServico":"Osasco",
              "estadoPrestacaoServico":"SP",
              "cepPrestacaoServico":"06056230",
              "paisPrestacaoServico":"BRASIL"
            }}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal("Rua Paulo Aparecido Pereira", request.Servico.EnderecoPrestacaoServico);
        Assert.Equal("365", request.Servico.NumeroPrestacaoServico);
        Assert.Equal("Bussocaba", request.Servico.BairroPrestacaoServico);
        Assert.Equal("Osasco", request.Servico.CidadePrestacaoServico);
        Assert.Equal("SP", request.Servico.EstadoPrestacaoServico);
        Assert.Equal("06056230", request.Servico.CepPrestacaoServico);
        Assert.Equal("3534401", request.Servico.CodigoCidadeIBGEPrestacaoServico);
    }

    [Fact]
    public void ConfiguracaoDoJson_NaoUsaCadastroEExigeCamposObrigatorios()
    {
        var request = JsonSerializer.Deserialize<NfseRequest>("""
            {"configuracao":{"padrao":3,"ambiente":2,"serie":"1","opSimpNac":1,"regEspTrib":0,
             "codigoMunicipioEmissao":"3534401","inscricaoMunicipal":"123",
             "osascoChaveAutenticacao":"chave-json"}}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var (prestador, cfg, erros) = ServicoNfse.MontarConfiguracao(
            new Empresa { CodigoMunicipio = "1111111", InscricaoMunicipal = "999" }, request.Configuracao);

        Assert.Empty(erros);
        Assert.Equal(PadraoNfse.Osasco, cfg.Padrao);
        Assert.Equal("1", cfg.SerieDps);
        Assert.Equal(OpcaoSimplesNacional.NaoOptante, cfg.OpSimpNac);
        Assert.Equal("chave-json", cfg.OsascoChaveAutenticacao);
        Assert.Equal("123", prestador.InscricaoMunicipal);
        Assert.Equal("3534401", prestador.CodigoMunicipio);
        Assert.Equal(TipoAmbiente.Homologacao, ServicoNfse.ResolverAmbiente(request));

        Assert.Throws<NfseNegocioException>(() => ServicoNfse.ResolverAmbiente(new NfseRequest()));
        var (_, _, errosVazio) = ServicoNfse.MontarConfiguracao(
            new Empresa(), new ConfiguracaoNfseRequest());
        Assert.True(errosVazio.Count >= 6);
    }

    [Fact]
    public void ConfiguracaoParaNota_ExigeCredencialNoJson()
    {
        var empresaId = Guid.NewGuid();
        Assert.Throws<NfseNegocioException>(() =>
            ServicoNfse.ConfiguracaoParaNota(empresaId, PadraoNfse.Osasco, "1", null));
        Assert.Throws<NfseNegocioException>(() =>
            ServicoNfse.ConfiguracaoParaNota(empresaId, PadraoNfse.Abrasf, "1", new ConfiguracaoNfseRequest()));

        var cfg = ServicoNfse.ConfiguracaoParaNota(empresaId, PadraoNfse.Osasco, "1",
            new ConfiguracaoNfseRequest { OsascoChaveAutenticacao = "chave-json" });
        Assert.Equal("chave-json", cfg.OsascoChaveAutenticacao);

        Assert.NotNull(ServicoNfse.ConfiguracaoParaNota(empresaId, PadraoNfse.Nacional, "1", null));
    }

    [Fact]
    public void GerarXml_ProduzPedidoRtcSemPersistirChave()
    {
        var provider = new ProvedorOsasco(new HttpMtlsClientFactory());
        var request = new NfseRequest
        {
            Tomador = new TomadorNfseRequest { Documento = "12345678000199", Nome = "Tomador Teste" },
            Servico = new ServicoNfseRequest
            {
                CodigoMunicipioPrestacao = "3534401",
                CodigoCidadeIBGEPrestacaoServico = "3534401",
                EnderecoPrestacaoServico = "Rua Dalia Formosa",
                NumeroPrestacaoServico = "112",
                BairroPrestacaoServico = "Santa Maria",
                CidadePrestacaoServico = "Osasco",
                EstadoPrestacaoServico = "SP",
                CepPrestacaoServico = "06150-495",
                PaisPrestacaoServico = "BRASIL",
                CodigoTributacaoNacional = "010701",
                CodigoTributacaoMunicipal = "107",
                CodigoNbs = "123456789",
                ClassTrib = "000001",
                CodigoCst = "000",
                IdLocalIncidencia = 12,
                IndOperacao = "1",
                Descricao = "Consultoria técnica"
            },
            Valores = new ValoresNfseRequest { ValorServico = 125.50m, AliquotaIss = 2m }
        };
        var config = new EmpresaNfseConfig
        {
            Padrao = PadraoNfse.Osasco,
            OsascoChaveAutenticacao = "chave-teste",
            OpSimpNac = OpcaoSimplesNacional.NaoOptante
        };
        var dados = new DpsDados(request, new Empresa { Cnpj = "12345678000199", CodigoMunicipio = "3534401" },
            config, TipoAmbiente.Homologacao, "A1", 42,
            new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.Zero), new DateTime(2026, 9, 30));

        var (xml, id) = provider.GerarXml(dados);
        var root = XDocument.Parse(xml).Root!;

        Assert.Equal("NotaFiscal_RTC", root.Name.LocalName);
        Assert.Equal(Mensagem.NamespaceName, root.Name.NamespaceName);
        Assert.Null(root.Element(Prestador + "ChaveAutenticacao"));
        Assert.DoesNotContain("chave-teste", xml);
        var pedidoDeRede = ProvedorOsasco.InserirChaveAutenticacao(root, "chave-teste");
        Assert.Equal("chave-teste", pedidoDeRede.Element(Prestador + "ChaveAutenticacao")!.Value);
        Assert.Equal("true", root.Element(Prestador + "Homologacao")!.Value);
        Assert.Equal("42.00", root.Element(Prestador + "NumeroRecibo")!.Value);
        Assert.Equal("112", root.Element(Prestador + "NumeroPrestacaoServico")!.Value);
        Assert.Equal("Rua Dalia Formosa", root.Element(Prestador + "EnderecoPrestacaoServico")!.Value);
        Assert.Equal("Santa Maria", root.Element(Prestador + "BairroPrestacaoServico")!.Value);
        Assert.Equal("Osasco", root.Element(Prestador + "CidadePrestacaoServico")!.Value);
        Assert.Equal("06150495", root.Element(Prestador + "CEPPrestacaoServico")!.Value);
        Assert.Equal("3534401", root.Element(Prestador + "CodigoCidadeIBGEPrestacaoServico")!.Value);
        Assert.Equal("2026-09-30T12:30:00", root.Element(Prestador + "DataRecibo")!.Value);
        Assert.Equal("125.50", root.Element(Prestador + "Valor")!.Value);
        Assert.Equal("107", root.Element(Prestador + "Atividade")!.Value);
        Assert.Equal("000001", root.Element(Prestador + "ClassTrib")!.Value);
        Assert.Equal("000", root.Element(Prestador + "CodigoCST")!.Value);
        Assert.Equal("123456789", root.Element(Prestador + "CodigoNBS")!.Value);
        Assert.Equal("12345678000199", root.Descendants().Single(e => e.Name.LocalName == "CNPJ").Value);
        Assert.Equal("A1-42", id);
    }

    [Fact]
    public void InterpretarEmissao_ExtraiNumeroEAutenticador()
    {
        var response = XElement.Parse("""
            <Envelope><Body><RTC_EmitirNFEResponse><RTC_EmitirNFEResult>
              <Erro>false</Erro><MensagemErro />
              <NotaFiscalGerada><Numero>321</Numero><Autenticador>ABCD1234</Autenticador><ChaveAcesso>OSASCO-KEY-321</ChaveAcesso></NotaFiscalGerada>
            </RTC_EmitirNFEResult></RTC_EmitirNFEResponse></Body></Envelope>
            """);

        var result = ProvedorOsasco.InterpretarEmissao(response, "retorno", 12, TipoAmbiente.Producao);

        Assert.True(result.Sucesso);
        Assert.Equal("321", result.NumeroNfse);
        Assert.Equal("OSASCO-KEY-321", result.ChaveAcesso);
        Assert.Equal("ABCD1234", result.CodigoVerificacao);
        Assert.Equal(12, result.DuracaoMs);
    }

    [Fact]
    public void InterpretarEmissao_TraduzRejeicaoMunicipal()
    {
        var response = XElement.Parse("<Envelope><RTC_EmitirNFEResult><Erro>1</Erro><MensagemErro>Chave inválida</MensagemErro></RTC_EmitirNFEResult></Envelope>");

        var result = ProvedorOsasco.InterpretarEmissao(response, "retorno", 4, TipoAmbiente.Producao);

        Assert.False(result.Sucesso);
        Assert.Equal("REJEITADA", result.Codigo);
        Assert.Equal("Chave inválida", result.Motivo);
    }

    [Fact]
    public void InterpretarConsulta_IdentificaNotaCancelada()
    {
        var response = XElement.Parse("<Envelope><RTC_ConsultarNFEResult><Erro>false</Erro><NotasGeradas><NotaFiscalConsultaDTO><Numero>321</Numero><DataCancelamento>2026-09-30T10:00:00</DataCancelamento><MotivoCancelamento>Serviço não prestado</MotivoCancelamento></NotaFiscalConsultaDTO></NotasGeradas></RTC_ConsultarNFEResult></Envelope>");

        var result = ProvedorOsasco.InterpretarConsulta(response, "321", "retorno");

        Assert.True(result.Encontrada);
        Assert.True(result.Cancelada);
        Assert.Equal("321", result.NumeroNfse);
        Assert.Equal("Serviço não prestado", result.Motivo);
    }

    [Fact]
    public void Url_SelecionaEndpointPeloAmbiente()
    {
        Assert.Equal("https://nfe.osasco.sp.gov.br/EissnfeWebServices/NotaFiscalEletronica.svc", ProvedorOsasco.UrlHomologacao);
        Assert.Equal(ProvedorOsasco.UrlProducao, ProvedorOsasco.Url(TipoAmbiente.Homologacao));
        Assert.Equal(ProvedorOsasco.UrlProducao, ProvedorOsasco.Url(TipoAmbiente.Producao));
    }

    private static DpsDados DadosDoLogReal(TomadorNfseRequest tomador)
    {
        var request = new NfseRequest
        {
            Tomador = tomador,
            Servico = new ServicoNfseRequest
            {
                CodigoMunicipioPrestacao = "3534401",
                CodigoCidadeIBGEPrestacaoServico = "3534401",
                CodigoTributacaoMunicipal = "1.01",
                EnderecoPrestacaoServico = "Rua Paulo Aparecido Pereira",
                NumeroPrestacaoServico = "365",
                BairroPrestacaoServico = "Bussocaba",
                CidadePrestacaoServico = "Osasco",
                EstadoPrestacaoServico = "SP",
                CepPrestacaoServico = "06056230",
                Descricao = "Suporte tecnico"
            },
            Valores = new ValoresNfseRequest { ValorServico = 1403.95m, AliquotaIss = 2.01m }
        };
        var config = new EmpresaNfseConfig { Padrao = PadraoNfse.Osasco, OsascoChaveAutenticacao = "k" };
        return new DpsDados(request, new Empresa { CodigoMunicipio = "3534401", NomeMunicipio = "Osasco" },
            config, TipoAmbiente.Homologacao, "1", 2053,
            new DateTimeOffset(2026, 10, 1, 14, 14, 39, TimeSpan.FromHours(-3)), new DateTime(2026, 10, 1));
    }

    [Fact]
    public void GerarXml_EnderecoDoTomadorSegueEnderecoDtoComCidadeEEstado()
    {
        var dados = DadosDoLogReal(new TomadorNfseRequest
        {
            Documento = "03707154000106", Nome = "LEVI PAO LTDA EPP", Email = "financeiro@levpao.com.br",
            Telefone = "(11) 3651-0000", Logradouro = "Avenida Cruzeiro do Sul", Numero = "219",
            Bairro = "Rochdale", CodigoMunicipio = "3534401", Cep = "06226000"
        });

        var root = XDocument.Parse(new ProvedorOsasco(new HttpMtlsClientFactory()).GerarXml(dados).Xml).Root!;
        var tomador = root.Element(Prestador + "Tomador")!;
        var endereco = tomador.Element(Contribuinte + "Endereco")!;

        Assert.Equal(new[] { "CNPJ", "DDD", "Email", "Endereco", "Nome", "Telefone" },
            tomador.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("11", tomador.Element(Contribuinte + "DDD")!.Value);
        Assert.Equal("36510000", tomador.Element(Contribuinte + "Telefone")!.Value);
        Assert.Equal(new[] { "Bairro", "CEP", "Cidade", "CodigoCidadeIBGE", "Estado", "Logradouro", "Numero", "Pais", "PaisSigla" },
            endereco.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("Osasco", endereco.Element(Contribuinte + "Cidade")!.Value);
        Assert.Equal("SP", endereco.Element(Contribuinte + "Estado")!.Value);
    }

    [Fact]
    public void GerarXml_TomadorDeOutroMunicipioSemCidadeExplicaOCampo()
    {
        var dados = DadosDoLogReal(new TomadorNfseRequest
        {
            Documento = "03707154000106", Nome = "X", Logradouro = "Rua A", Numero = "1",
            Bairro = "Centro", CodigoMunicipio = "3550308", Cep = "01001000"
        });

        var ex = Assert.Throws<NfseProvedorException>(() => new ProvedorOsasco(new HttpMtlsClientFactory()).GerarXml(dados));
        Assert.Contains("tomador.cidade", ex.Message);
        Assert.DoesNotContain("tomador.uf", ex.Message);
    }

    [Fact]
    public void GerarXml_ExigeChaveMunicipal()
    {
        var provider = new ProvedorOsasco(new HttpMtlsClientFactory());
        var request = new NfseRequest();
        var dados = new DpsDados(request, new Empresa(), new EmpresaNfseConfig(),
            TipoAmbiente.Homologacao, "1", 1, DateTimeOffset.UtcNow, DateTime.UtcNow.Date);

        var exception = Assert.Throws<NfseProvedorException>(() => provider.GerarXml(dados));
        Assert.Contains("Chave de Autenticação", exception.Message);
    }
}
