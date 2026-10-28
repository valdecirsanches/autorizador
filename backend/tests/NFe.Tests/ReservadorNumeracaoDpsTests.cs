using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Domain.Modelos;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.ServicosNfse;
using Xunit;

namespace NFe.Tests;

/// <summary>Testa numeração NFS-e no MySQL real, inclusive locks e unicidade.</summary>
public sealed class ReservadorNumeracaoDpsTests : IAsyncLifetime
{
    private readonly string _cnpj = Random.Shared
        .NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString();
    private string _connectionString = string.Empty;
    private bool _enabled;
    private Guid _empresaId;

    private NFeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NFeDbContext>()
            .UseMySql(_connectionString, ServerVersion.AutoDetect(_connectionString))
            .Options;
        return new NFeDbContext(options);
    }

    public async Task InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ?? string.Empty;
        _enabled = !string.IsNullOrWhiteSpace(_connectionString);
        if (!_enabled) return;

        await using var db = CreateContext();
        var empresa = new Empresa
        {
            Cnpj = _cnpj,
            RazaoSocial = "EMPRESA TESTE NUMERACAO NFSE",
            InscricaoEstadual = "1234567890",
            Logradouro = "RUA X",
            Numero = "1",
            Bairro = "CENTRO",
            CodigoMunicipio = "4314902",
            NomeMunicipio = "PORTO ALEGRE",
            Uf = "RS",
            Cep = "90000000",
            AmbientePadrao = TipoAmbiente.Homologacao
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        _empresaId = empresa.Id;
    }

    public async Task DisposeAsync()
    {
        if (!_enabled || _empresaId == Guid.Empty) return;
        await using var db = CreateContext();
        await db.Empresas.Where(e => e.Id == _empresaId).ExecuteDeleteAsync();
    }

    private async Task<long> ReserveAsync(string serie, TipoAmbiente ambiente, long? numeroInformado = null)
    {
        await using var db = CreateContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var serieNormalizada = ReservadorNumeracaoDps.NormalizarSerie(serie);
            var numero = await new ReservadorNumeracaoDps(db).ReservarProximoAsync(
                _empresaId, serieNormalizada, ambiente, numeroInformado: numeroInformado);
            db.NotasServico.Add(new NotaServico
            {
                EmpresaId = _empresaId,
                Ambiente = ambiente,
                Serie = serieNormalizada,
                NumeroDps = numero,
                DataEmissao = DateTime.UtcNow,
                DataCompetencia = DateTime.UtcNow.Date,
                CodigoTributacaoNacional = "010701",
                DescricaoServico = "Teste de reserva"
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return numero;
        });
    }

    private async Task<long> DefineProximoAsync(string serie, TipoAmbiente ambiente, long proximo)
    {
        await using var db = CreateContext();
        return await new ReservadorNumeracaoDps(db).DefinirProximoNumeroAsync(
            _empresaId, serie, ambiente, proximo);
    }

    [Fact]
    public void JsonDoApp_DesserializaNumeroDps()
    {
        var request = JsonSerializer.Deserialize<NfseRequest>(
            "{\"numeroDps\":425}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(425L, request?.NumeroDps);
    }

    [SkippableFact]
    public async Task NumeroInformado_UsaNumeroExatoEAtualizaContadorSeparadoPorAmbiente()
    {
        Skip.IfNot(_enabled, "MYSQL_CONNECTION_STRING nao definida.");

        Assert.Equal(120, await ReserveAsync("4", TipoAmbiente.Producao, 120));
        Assert.Equal(8, await ReserveAsync("4", TipoAmbiente.Homologacao, 8));
        Assert.Equal(121, await ReserveAsync("4", TipoAmbiente.Producao));
        Assert.Equal(9, await ReserveAsync("4", TipoAmbiente.Homologacao));
    }

    [SkippableFact]
    public async Task NumeroInformado_PermiteNumeroMenorQueContadorQuandoAindaNaoUsado()
    {
        Skip.IfNot(_enabled, "MYSQL_CONNECTION_STRING nao definida.");

        await DefineProximoAsync("6", TipoAmbiente.Homologacao, 501);

        Assert.Equal(42, await ReserveAsync("6", TipoAmbiente.Homologacao, 42));
        Assert.Equal(501, await ReserveAsync("6", TipoAmbiente.Homologacao));
    }

    [SkippableFact]
    public async Task NumeroInformado_RejeitaZero()
    {
        Skip.IfNot(_enabled, "MYSQL_CONNECTION_STRING nao definida.");

        await Assert.ThrowsAsync<NumeracaoDpsInvalidaException>(
            () => ReserveAsync("5", TipoAmbiente.Homologacao, 0));
    }

    [SkippableFact]
    public async Task NumeroInformado_RejeitaMaisDeQuinzeDigitos()
    {
        Skip.IfNot(_enabled, "MYSQL_CONNECTION_STRING nao definida.");

        await Assert.ThrowsAsync<NumeracaoDpsInvalidaException>(
            () => ReserveAsync("5", TipoAmbiente.Homologacao, 1_000_000_000_000_000));
    }

    [SkippableFact]
    public async Task NumeroInformado_RejeitaDuplicidadeNaMesmaSerieEAmbiente()
    {
        Skip.IfNot(_enabled, "MYSQL_CONNECTION_STRING nao definida.");

        Assert.Equal(99, await ReserveAsync("7", TipoAmbiente.Homologacao, 99));
        await Assert.ThrowsAsync<NumeracaoDpsInvalidaException>(
            () => ReserveAsync("7", TipoAmbiente.Homologacao, 99));

        // O mesmo número continua disponível na outra sequência fiscal.
        Assert.Equal(99, await ReserveAsync("7", TipoAmbiente.Producao, 99));
    }

    [SkippableFact]
    public async Task NumerosInformadosConcorrentes_NaoColidem()
    {
        Skip.IfNot(_enabled, "MYSQL_CONNECTION_STRING nao definida.");

        // Cria o contador antes da concorrência para que todas as requisições
        // disputem a mesma linha bloqueada por FOR UPDATE.
        await DefineProximoAsync("8", TipoAmbiente.Homologacao, 1);
        var pedidos = Enumerable.Range(1, 12)
            .Select(numero => ReserveAsync("8", TipoAmbiente.Homologacao, numero))
            .ToArray();

        var numeros = await Task.WhenAll(pedidos);
        Assert.Equal(Enumerable.Range(1, 12).Select(n => (long)n), numeros.OrderBy(n => n));
    }
}
