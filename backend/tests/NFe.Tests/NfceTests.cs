using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Nfce;
using NFe.Sefaz.Seguranca;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Xml;
using Xunit;

namespace NFe.Tests;

/// <summary>NFC-e (modelo 65): XML, QR Code v2/v3, contingencia off-line, catalogo e evento 110112.</summary>
public sealed class NfceTests : IDisposable
{
    private const string Csc = "0123456789ABCDEF0123456789ABCDEF";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly X509Certificate2 _cert;
    private readonly CertificadoLocal _certSefaz;
    private readonly SefazEndpointRegistry _registry = SefazEndpointRegistry.LoadDefault();

    public NfceTests()
    {
        var req = new CertificateRequest("CN=LOJA TESTE LTDA:12345678000195", _rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _certSefaz = new CertificadoLocal(_cert, donoDoCertificado: false);
    }

    public void Dispose()
    {
        _certSefaz.Dispose();
        _cert.Dispose();
        _rsa.Dispose();
    }

    private static Empresa Emitente() => new()
    {
        Cnpj = "12345678000195", RazaoSocial = "LOJA TESTE LTDA", InscricaoEstadual = "111111111111",
        RegimeTributario = RegimeTributario.SimplesNacional, Logradouro = "RUA A", Numero = "100",
        Bairro = "CENTRO", CodigoMunicipio = "3534401", NomeMunicipio = "OSASCO", Uf = "SP", Cep = "06226000",
    };

    private static NotaFiscalRequest Pedido()
    {
        var req = new NotaFiscalRequest
        {
            Configuracao = new ConfiguracaoNFeRequest { Ambiente = TipoAmbiente.Homologacao, Csc = Csc, IdCsc = "000001" },
            Emitente = new EmitenteNFeRequest
            {
                Cnpj = "12345678000195", RazaoSocial = "LOJA TESTE LTDA", InscricaoEstadual = "111111111111",
                RegimeTributario = RegimeTributario.SimplesNacional, Logradouro = "RUA A", Numero = "100",
                Bairro = "CENTRO", CodigoMunicipio = "3534401", NomeMunicipio = "OSASCO", Uf = "SP", Cep = "06226000",
            },
            Modelo = ModeloDocumento.NFCe,
            Serie = 1,
            Numero = 9,
            NaturezaOperacao = "VENDA AO CONSUMIDOR",
            DataEmissao = new DateTimeOffset(2026, 11, 2, 10, 15, 0, TimeSpan.FromHours(-3)),
            IndicadorPresenca = IndicadorPresenca.Presencial,
            ModalidadeFrete = ModalidadeFrete.SemTransporte,
            ConsumidorFinal = true,
            Destinatario = null!,
            ValorTotalTributos = 12.23m,
            Itens =
            {
                new ItemNotaRequest
                {
                    Codigo = "1001", Descricao = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL",
                    Gtin = "SEM GTIN", Ncm = "73181500", Cfop = "5102", UnidadeComercial = "UN",
                    Quantidade = 1, ValorUnitario = 38.90m, ValorBruto = 38.90m, CstIcms = "102",
                    CstPis = "49", CstCofins = "49", ValorTotalTributos = 12.23m,
                }
            },
            Pagamentos =
            {
                new PagamentoRequest
                {
                    Meio = "03", Valor = 38.90m, TipoIntegracao = 2, CnpjCredenciadora = "16501555000157",
                    Bandeira = "02", Autorizacao = "A1B2C3",
                }
            },
        };
        ValidadorNotaFiscal.Normalizar(req);
        return req;
    }

    private string Emitir(NotaFiscalRequest req, out string qr)
    {
        Assert.Empty(ValidadorNotaFiscal.Validar(req));
        var g = new GeradorXmlNFe().Gerar(Emitente(), req, TipoAmbiente.Homologacao);
        var assinado = new AssinadorXml().Assinar(g.Xml, "NFe" + g.ChaveAcesso, _certSefaz);
        var urls = _registry.ObterUrlsNfce("SP", TipoAmbiente.Homologacao);
        var cfg = req.Configuracao!;
        var (xml, q, _) = QrCodeNfce.Aplicar(assinado, urls, cfg.Csc, cfg.IdCscEfetivo(), cfg.VersaoQrCode, _certSefaz);
        qr = q;
        return xml;
    }

    private static string[] Partes(string qr) => qr[(qr.IndexOf("?p=", StringComparison.Ordinal) + 3)..].Split('|');

    [Fact]
    public void Xml_da_NFCe_tem_tpImp_4_vTotTrib_card_e_nao_tem_dest()
    {
        var xml = Emitir(Pedido(), out _);

        Assert.Contains("<mod>65</mod>", xml);
        Assert.Contains("<tpImp>4</tpImp>", xml);
        Assert.DoesNotContain("<dest>", xml);
        Assert.DoesNotContain("<dhSaiEnt>", xml);
        Assert.Contains("<imposto><vTotTrib>12.23</vTotTrib>", xml);
        Assert.Contains("<vNF>38.90</vNF><vTotTrib>12.23</vTotTrib>", xml);
        Assert.Contains("<card><tpIntegra>2</tpIntegra><CNPJ>16501555000157</CNPJ><tBand>02</tBand><cAut>A1B2C3</cAut></card>", xml);
        Assert.Contains("<urlChave>https://www.homologacao.nfce.fazenda.sp.gov.br/NFCeConsultaPublica</urlChave>", xml);
    }

    [Fact]
    public void QrCode_v2_online_tem_hash_SHA1_com_o_CSC()
    {
        Emitir(Pedido(), out var qr);
        var p = Partes(qr);

        Assert.StartsWith("https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode?p=", qr);
        Assert.Equal(5, p.Length);
        Assert.Equal(new[] { "2", "2", "1" }, p[1..4]);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{p[0]}|2|2|1{Csc}")));
        Assert.Equal(hash, p[4]);
    }

    [Fact]
    public void QrCode_v3_online_e_so_chave_versao_e_ambiente()
    {
        var req = Pedido();
        req.Configuracao!.VersaoQrCode = 3;

        Emitir(req, out var qr);

        Assert.Equal(3, Partes(qr).Length);
        Assert.EndsWith("|3|2", qr);
    }

    [Fact]
    public void Contingencia_offline_gera_tpEmis_9_dhCont_xJust_e_QrCode_v3_assinado()
    {
        var req = Pedido();
        req.Configuracao!.ContingenciaOffline = true;
        req.Destinatario = new DestinatarioRequest { Documento = "52998224725", Nome = "CONSUMIDOR" };
        ValidadorNotaFiscal.Normalizar(req);

        var xml = Emitir(req, out var qr);
        var p = Partes(qr);

        Assert.Contains("<tpEmis>9</tpEmis>", xml);
        Assert.Contains("<dhCont>", xml);
        Assert.Contains("<xJust>", xml);
        Assert.Equal('9', p[0][34]);
        Assert.Equal(8, p.Length);
        Assert.Equal(new[] { "3", "2", "02", "38.90", "2", "52998224725" }, p[1..7]);
        Assert.True(_rsa.VerifyData(Encoding.UTF8.GetBytes(string.Join("|", p[..7])),
            Convert.FromBase64String(p[7]), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        Assert.Contains("<indIEDest>9</indIEDest>", xml);
    }

    [Fact]
    public void Inserir_o_QrCode_nao_invalida_a_assinatura_do_XML()
    {
        var xml = Emitir(Pedido(), out _);
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        var sx = new SignedXml(doc);
        sx.LoadXml((XmlElement)doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#")[0]!);

        Assert.True(sx.CheckSignature(_cert, true));
        Assert.True(xml.IndexOf("<infNFeSupl>", StringComparison.Ordinal) < xml.IndexOf("<Signature", StringComparison.Ordinal));
    }

    [Fact]
    public void Validacao_da_NFCe()
    {
        var semCsc = Pedido();
        semCsc.Configuracao!.Csc = null;
        semCsc.Configuracao.VersaoQrCode = 2;
        Assert.Contains(ValidadorNotaFiscal.Validar(semCsc), e => e.Contains("QR Code versao 2 exige"));

        var docInvalido = Pedido();
        docInvalido.Destinatario = new DestinatarioRequest { Documento = "123" };
        Assert.Contains(ValidadorNotaFiscal.Validar(docInvalido), e => e.Contains("CPF (11 digitos) ou CNPJ"));

        var naoFinal = Pedido();
        naoFinal.ConsumidorFinal = false;
        Assert.Contains(ValidadorNotaFiscal.Validar(naoFinal), e => e.Contains("consumidorFinal"));

        var nfe = Pedido();
        nfe.Modelo = ModeloDocumento.NFe;
        nfe.Configuracao!.ContingenciaOffline = true;
        Assert.Contains(ValidadorNotaFiscal.Validar(nfe), e => e.Contains("so da NFC-e"));

        // Sem CSC e sem versao: cai no QR Code v3, que dispensa o CSC.
        var v3 = Pedido();
        v3.Configuracao!.Csc = null;
        Assert.Empty(ValidadorNotaFiscal.Validar(v3));
    }

    [Fact]
    public void Csc_na_raiz_do_JSON_e_aceito()
    {
        var req = Pedido();
        req.Configuracao!.Csc = null;
        req.Configuracao.IdCsc = null;
        req.Csc = Csc;
        req.IdCsc = "000002";

        ValidadorNotaFiscal.Normalizar(req);

        Assert.Equal(Csc, req.Configuracao.Csc);
        Assert.Equal("000002", req.Configuracao.IdCscEfetivo());
    }

    [Theory]
    [InlineData("AC")] [InlineData("BA")] [InlineData("CE")] [InlineData("GO")] [InlineData("MG")]
    [InlineData("MS")] [InlineData("PR")] [InlineData("RJ")] [InlineData("RS")] [InlineData("SC")] [InlineData("SP")]
    public void Catalogo_NFCe_resolve_servicos_QrCode_e_urlChave(string uf)
    {
        foreach (var amb in new[] { TipoAmbiente.Producao, TipoAmbiente.Homologacao })
        {
            foreach (var s in new[] { ServicoSefaz.Autorizacao, ServicoSefaz.ConsultaProtocolo,
                         ServicoSefaz.RecepcaoEvento, ServicoSefaz.Inutilizacao, ServicoSefaz.StatusServico })
                Assert.StartsWith("https://", _registry.Resolver(uf, amb, s, modelo: ModeloDocumento.NFCe).Url);
            var urls = _registry.ObterUrlsNfce(uf, amb);
            Assert.False(string.IsNullOrWhiteSpace(urls.QrCode));
            Assert.InRange(urls.UrlChave.Length, 21, 85);
        }
    }

    [Fact]
    public void NFCe_usa_endpoints_proprios_e_nao_tem_SVC()
    {
        Assert.Equal("https://nfce.fazenda.sp.gov.br/ws/NFeAutorizacao4.asmx",
            _registry.Resolver("SP", TipoAmbiente.Producao, ServicoSefaz.Autorizacao, modelo: ModeloDocumento.NFCe).Url);
        Assert.Equal("https://nfe.fazenda.sp.gov.br/ws/nfeautorizacao4.asmx",
            _registry.Resolver("SP", TipoAmbiente.Producao, ServicoSefaz.Autorizacao).Url.ToLowerInvariant());
        Assert.Throws<SefazEndpointException>(() =>
            _registry.Resolver("SP", TipoAmbiente.Producao, ServicoSefaz.Autorizacao, contingencia: true, modelo: ModeloDocumento.NFCe));
        Assert.Equal(ModeloDocumento.NFCe, SefazEndpointRegistry.ModeloDaChave("35261112345678000195650010000000091123456780"));
        Assert.Equal(ModeloDocumento.NFe, SefazEndpointRegistry.ModeloDaChave("35261112345678000195550010000000091123456780"));
    }

    [Fact]
    public void Evento_110112_tem_os_campos_na_ordem_do_leiaute()
    {
        var chave = "35261112345678000195650010000000091123456780";
        var subst = "35261112345678000195650010000000101123456781";

        var (xml, id) = EventoClient.MontarCancelamentoSubstituicao(new EventoClient.DadosCancelamentoSubstituicao(
            "SP", TipoAmbiente.Homologacao, 35, "12345678000195", chave, "135260000000001",
            "Venda refeita em outra NFC-e por erro no item", subst, "NFeAutorizador/1.0"));

        Assert.Equal("ID110112" + chave + "01", id);
        Assert.Contains("<tpEvento>110112</tpEvento>", xml);
        Assert.Contains("<detEvento versao=\"1.00\"><descEvento>Cancelamento por substituicao</descEvento>" +
                        "<cOrgaoAutor>35</cOrgaoAutor><tpAutor>1</tpAutor><verAplic>NFeAutorizador/1.0</verAplic>" +
                        "<nProt>135260000000001</nProt><xJust>Venda refeita em outra NFC-e por erro no item</xJust>" +
                        $"<chNFeRef>{subst}</chNFeRef></detEvento>", xml);
    }

    [Theory]
    [InlineData("35261112345678000195650010000000091123456780", "substituta deve ser de outra")]
    [InlineData("35261112345678000195550010000000101123456781", "deve ser de uma NFC-e")]
    [InlineData("35261199999999000199650010000000101123456781", "mesmo emitente")]
    public void Evento_110112_recusa_chave_substituta_invalida(string substituta, string mensagem)
    {
        var ex = Assert.Throws<ArgumentException>(() => EventoClient.MontarCancelamentoSubstituicao(
            new EventoClient.DadosCancelamentoSubstituicao(
                "SP", TipoAmbiente.Homologacao, 35, "12345678000195", "35261112345678000195650010000000091123456780",
                "135260000000001", "Venda refeita em outra NFC-e por erro no item", substituta, "x")));
        Assert.Contains(mensagem, ex.Message);
    }

    [Fact]
    public void CSOSN_500_usa_o_grupo_ICMSSN500()
    {
        var req = Pedido();
        req.Itens[0].CstIcms = "500";

        var xml = Emitir(req, out _);

        Assert.Contains("<ICMSSN500><orig>0</orig><CSOSN>500</CSOSN></ICMSSN500>", xml);
    }
}
