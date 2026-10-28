using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using Xunit;

namespace NFe.Tests;

/// <summary>
/// Testes de integracao do ciclo de vida do certificado A1 contra MySQL real.
/// Sao ignorados automaticamente quando MYSQL_CONNECTION_STRING nao esta
/// definida, para nao quebrar a build em ambiente sem banco.
/// </summary>
public class ServicoCertificadoTests : IAsyncLifetime
{
    private NFeDbContext _db = null!;
    private ServicoCertificado _servico = null!;
    private Guid _empresaId;
    private bool _habilitado;

    private const string CnpjEmpresa = "12345678000195";

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");
        _habilitado = !string.IsNullOrWhiteSpace(cs);
        if (!_habilitado) return;

        var options = new DbContextOptionsBuilder<NFeDbContext>()
            .UseMySql(cs!, ServerVersion.AutoDetect(cs!))
            .Options;

        _db = new NFeDbContext(options);
        var cofre = CofreCriptografico.DeBase64(CofreCriptografico.GerarChaveMestraBase64());
        _servico = new ServicoCertificado(_db, cofre);

        var empresa = new Empresa
        {
            Cnpj = CnpjEmpresa,
            RazaoSocial = "EMPRESA TESTE LTDA",
            InscricaoEstadual = "1234567890",
            Logradouro = "RUA TESTE",
            Numero = "100",
            Bairro = "CENTRO",
            CodigoMunicipio = "4314902",
            NomeMunicipio = "PORTO ALEGRE",
            Uf = "RS",
            Cep = "90000000",
            AmbientePadrao = TipoAmbiente.Homologacao
        };

        // Remove residuo de execucao anterior.
        await _db.Empresas.Where(e => e.Cnpj == CnpjEmpresa).ExecuteDeleteAsync();

        _db.Empresas.Add(empresa);
        await _db.SaveChangesAsync();
        _empresaId = empresa.Id;
    }

    public async Task DisposeAsync()
    {
        if (!_habilitado) return;
        await _db.Empresas.Where(e => e.Id == _empresaId).ExecuteDeleteAsync();
        await _db.DisposeAsync();
    }

    [SkippableFact]
    public async Task Importar_CertificadoValido_PersistetCifrado()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "senha-teste-123";
        var pfx = CertificadoFalso.CriarPfx(senha, CnpjEmpresa);

        var cert = await _servico.ImportarAsync(_empresaId, pfx, senha, "A1 de teste");

        Assert.NotEqual(Guid.Empty, cert.Id);
        Assert.True(cert.Ativo);
        Assert.Equal(CnpjEmpresa, cert.CnpjCertificado);
        Assert.Contains("EMPRESA TESTE", cert.Subject);

        // O PFX no banco nao pode ser igual ao original.
        Assert.NotEqual(pfx, cert.PfxCifrado);
        Assert.Equal(12, cert.PfxNonce.Length);
        Assert.Equal(16, cert.PfxTag.Length);

        // A senha nao pode aparecer em claro em nenhum campo.
        var senhaBytes = System.Text.Encoding.UTF8.GetBytes(senha);
        Assert.DoesNotContain(
            Convert.ToBase64String(senhaBytes),
            Convert.ToBase64String(cert.SenhaCifrada));
    }

    [SkippableFact]
    public async Task CarregarAtivo_RecuperaChavePrivadaUtilizavel()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "outra-senha-456";
        var pfx = CertificadoFalso.CriarPfx(senha, CnpjEmpresa);
        await _servico.ImportarAsync(_empresaId, pfx, senha, "A1");

        using var x509 = await _servico.CarregarAtivoAsync(_empresaId);

        // O certificado precisa voltar com chave privada usavel, caso contrario
        // nao serve nem para TLS mutuo nem para assinar.
        Assert.True(x509.HasPrivateKey);

        using var rsa = x509.GetRSAPrivateKey();
        Assert.NotNull(rsa);

        var dados = System.Text.Encoding.UTF8.GetBytes("teste de assinatura");
        var assinatura = rsa!.SignData(
            dados, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var pub = x509.GetRSAPublicKey();
        Assert.True(pub!.VerifyData(
            dados, assinatura, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [SkippableFact]
    public async Task Importar_SenhaIncorreta_Falha()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        var pfx = CertificadoFalso.CriarPfx("senha-correta", CnpjEmpresa);

        var ex = await Assert.ThrowsAsync<CertificadoException>(() =>
            _servico.ImportarAsync(_empresaId, pfx, "senha-errada", "A1"));

        Assert.Contains("senha", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Importar_CertificadoVencido_Falha()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "s3nha";
        var pfx = CertificadoFalso.CriarPfx(
            senha, CnpjEmpresa,
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var ex = await Assert.ThrowsAsync<CertificadoException>(() =>
            _servico.ImportarAsync(_empresaId, pfx, senha, "A1 vencido"));

        Assert.Contains("vencido", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Importar_CnpjDivergente_Falha()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "s3nha";
        // Certificado de outro CNPJ: a SEFAZ rejeitaria a assinatura.
        var pfx = CertificadoFalso.CriarPfx(senha, "99999999000199");

        var ex = await Assert.ThrowsAsync<CertificadoException>(() =>
            _servico.ImportarAsync(_empresaId, pfx, senha, "A1 de outro CNPJ"));

        Assert.Contains("nao corresponde", ex.Message);
    }

    [SkippableFact]
    public async Task Importar_Duplicado_Falha()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "s3nha";
        var pfx = CertificadoFalso.CriarPfx(senha, CnpjEmpresa);

        await _servico.ImportarAsync(_empresaId, pfx, senha, "primeiro");

        var ex = await Assert.ThrowsAsync<CertificadoException>(() =>
            _servico.ImportarAsync(_empresaId, pfx, senha, "repetido"));

        Assert.Contains("ja esta cadastrado", ex.Message);
    }

    [SkippableFact]
    public async Task Importar_NovoCertificado_DesativaAnterior()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "s3nha";
        var primeiro = await _servico.ImportarAsync(
            _empresaId, CertificadoFalso.CriarPfx(senha, CnpjEmpresa), senha, "antigo");

        // Datas diferentes garantem thumbprint diferente.
        var segundo = await _servico.ImportarAsync(
            _empresaId,
            CertificadoFalso.CriarPfx(
                senha, CnpjEmpresa, notAfter: DateTimeOffset.UtcNow.AddYears(2)),
            senha, "novo");

        var ativos = await _db.Certificados
            .Where(c => c.EmpresaId == _empresaId && c.Ativo)
            .ToListAsync();

        Assert.Single(ativos);
        Assert.Equal(segundo.Id, ativos[0].Id);

        var antigo = await _db.Certificados.FindAsync(primeiro.Id);
        Assert.False(antigo!.Ativo);
    }

    [SkippableFact]
    public async Task Importar_SemExtensaoCnpj_NaoBloqueia()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const string senha = "s3nha";
        var pfx = CertificadoFalso.CriarPfx(senha, CnpjEmpresa, incluirCnpj: false);

        var cert = await _servico.ImportarAsync(_empresaId, pfx, senha, "sem OID");

        // Sem a extensao nao ha o que validar, e a importacao deve prosseguir.
        Assert.Null(cert.CnpjCertificado);
    }
}
