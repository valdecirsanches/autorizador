using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Servicos;

namespace NFe.Tests;

/// <summary>
/// Testes do espelhamento de XML em disco. Usam uma pasta temporaria isolada
/// por teste e a removem ao final. Verificam a organizacao "uma pasta por
/// empresa (CNPJ)" e que falhas de I/O nunca propagam.
/// </summary>
public class ArquivadorXmlTests : IDisposable
{
    private readonly string _baseDir =
        Path.Combine(Path.GetTempPath(), "nfe-xml-tests-" + Guid.NewGuid().ToString("N"));

    private const string Chave = "43250412345678000195550010000000011000000015";

    private static Empresa NovaEmpresa(string cnpj = "12.345.678/0001-95") => new()
    {
        Cnpj = cnpj,
        RazaoSocial = "EMPRESA TESTE LTDA",
        Uf = "SP",
    };

    private static DocumentoFiscal NovoDocumento(TipoAmbiente ambiente) => new()
    {
        ChaveAcesso = Chave,
        Ambiente = ambiente,
        Numero = 1,
        Serie = 1,
        XmlAssinado = "<NFe><assinado/></NFe>",
        XmlProtocolado = "<nfeProc><autorizado/></nfeProc>",
        XmlRetornoSefaz = "<retEnviNFe><cStat>217</cStat></retEnviNFe>",
    };

    [Fact]
    public void SalvarRetornoSefaz_GravaArquivoRetMesmoSemAutorizacao()
    {
        var arq = new ArquivadorXml(_baseDir);
        var empresa = NovaEmpresa();
        var doc = NovoDocumento(TipoAmbiente.Homologacao);
        // Simula rejeicao: sem nfeProc, mas com retorno da SEFAZ.
        doc.XmlProtocolado = null;

        arq.SalvarRetornoSefaz(empresa, doc);

        var esperado = Path.Combine(
            _baseDir, "12345678000195", "homologacao", $"{Chave}-ret.xml");

        Assert.True(File.Exists(esperado), $"arquivo de retorno esperado nao encontrado: {esperado}");
        Assert.Equal("<retEnviNFe><cStat>217</cStat></retEnviNFe>", File.ReadAllText(esperado));
    }

    [Fact]
    public void SalvarRetornoSefaz_SemChave_UsaIdDoDocumentoComoNome()
    {
        var arq = new ArquivadorXml(_baseDir);
        var empresa = NovaEmpresa();
        var doc = NovoDocumento(TipoAmbiente.Homologacao);
        // Erro antes de haver chave de acesso (ex.: rejeicao de lote / SOAP fault).
        doc.ChaveAcesso = null;

        arq.SalvarRetornoSefaz(empresa, doc);

        var esperado = Path.Combine(
            _baseDir, "12345678000195", "homologacao", $"{doc.Id}-ret.xml");

        Assert.True(File.Exists(esperado), $"retorno por Id esperado nao encontrado: {esperado}");
    }

    [Fact]
    public void SalvarAssinado_CriaPastaPorCnpjEArquivoNfe()
    {
        var arq = new ArquivadorXml(_baseDir);
        var empresa = NovaEmpresa();
        var doc = NovoDocumento(TipoAmbiente.Homologacao);

        arq.SalvarAssinado(empresa, doc);

        // {base}/12345678000195/homologacao/{chave}-nfe.xml
        var esperado = Path.Combine(
            _baseDir, "12345678000195", "homologacao", $"{Chave}-nfe.xml");

        Assert.True(File.Exists(esperado), $"arquivo esperado nao encontrado: {esperado}");
        Assert.Equal("<NFe><assinado/></NFe>", File.ReadAllText(esperado));
    }

    [Fact]
    public void SalvarAutorizado_GravaProcNFeEmPastaDeProducao()
    {
        var arq = new ArquivadorXml(_baseDir);
        var empresa = NovaEmpresa();
        var doc = NovoDocumento(TipoAmbiente.Producao);

        arq.SalvarAutorizado(empresa, doc);

        var esperado = Path.Combine(
            _baseDir, "12345678000195", "producao", $"{Chave}-procNFe.xml");

        Assert.True(File.Exists(esperado), $"arquivo esperado nao encontrado: {esperado}");
        Assert.Equal("<nfeProc><autorizado/></nfeProc>", File.ReadAllText(esperado));
    }

    [Fact]
    public void EmpresasDiferentes_FicamEmPastasSeparadas()
    {
        var arq = new ArquivadorXml(_baseDir);

        arq.SalvarAssinado(NovaEmpresa("11.111.111/0001-11"), NovoDocumento(TipoAmbiente.Producao));
        arq.SalvarAssinado(NovaEmpresa("22.222.222/0001-22"), NovoDocumento(TipoAmbiente.Producao));

        Assert.True(Directory.Exists(Path.Combine(_baseDir, "11111111000111")));
        Assert.True(Directory.Exists(Path.Combine(_baseDir, "22222222000122")));
    }

    [Fact]
    public void XmlVazio_NaoCriaArquivo()
    {
        var arq = new ArquivadorXml(_baseDir);
        var empresa = NovaEmpresa();
        var doc = NovoDocumento(TipoAmbiente.Homologacao);
        doc.XmlAssinado = null;

        arq.SalvarAssinado(empresa, doc);

        // Nada a salvar => nem a pasta da empresa deve ser criada.
        Assert.False(Directory.Exists(Path.Combine(_baseDir, "12345678000195")));
    }

    [Fact]
    public void CnpjVazio_UsaPastaSemCnpjSemLancar()
    {
        var arq = new ArquivadorXml(_baseDir);
        var empresa = NovaEmpresa(cnpj: "");
        var doc = NovoDocumento(TipoAmbiente.Homologacao);

        // Nao deve lancar; grava sob "sem-cnpj".
        arq.SalvarAssinado(empresa, doc);

        Assert.True(Directory.Exists(Path.Combine(_baseDir, "sem-cnpj")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
                Directory.Delete(_baseDir, recursive: true);
        }
        catch { /* limpeza best-effort */ }
    }
}
