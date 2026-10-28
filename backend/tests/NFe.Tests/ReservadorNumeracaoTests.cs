using Microsoft.EntityFrameworkCore;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Servicos;
using Xunit;

namespace NFe.Tests;

/// <summary>
/// Integracao do reservador de numeracao contra MySQL real. A numeracao fiscal
/// nao admite lacunas nem repeticoes nem sob concorrencia, e este e o ponto onde
/// o lock pessimista precisa provar que funciona. Ignorado sem
/// MYSQL_CONNECTION_STRING.
/// </summary>
public class ReservadorNumeracaoTests : IAsyncLifetime
{
    private string _cs = null!;
    private bool _habilitado;
    private Guid _empresaId;

    private const string CnpjEmpresa = "99888777000166";

    private NFeDbContext NovoContexto()
    {
        var options = new DbContextOptionsBuilder<NFeDbContext>()
            .UseMySql(_cs, ServerVersion.AutoDetect(_cs))
            .Options;
        return new NFeDbContext(options);
    }

    public async Task InitializeAsync()
    {
        _cs = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ?? "";
        _habilitado = !string.IsNullOrWhiteSpace(_cs);
        if (!_habilitado) return;

        await using var db = NovoContexto();
        await db.Empresas.Where(e => e.Cnpj == CnpjEmpresa).ExecuteDeleteAsync();

        var empresa = new Empresa
        {
            Cnpj = CnpjEmpresa,
            RazaoSocial = "EMPRESA NUMERACAO LTDA",
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
        if (!_habilitado) return;
        await using var db = NovoContexto();
        // Cascade remove as series junto com a empresa.
        await db.Empresas.Where(e => e.Id == _empresaId).ExecuteDeleteAsync();
    }

    private async Task<long> ReservarAsync(int serie, TipoAmbiente ambiente)
    {
        await using var db = NovoContexto();
        var estrategia = db.Database.CreateExecutionStrategy();
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var n = await new ReservadorNumeracao(db)
                .ReservarProximoAsync(_empresaId, ModeloDocumento.NFe, serie, ambiente);
            await tx.CommitAsync();
            return n;
        });
    }

    [SkippableFact]
    public async Task PrimeiraReserva_ComecaEmUm()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        var n = await ReservarAsync(serie: 1, TipoAmbiente.Homologacao);
        Assert.Equal(1, n);
    }

    [SkippableFact]
    public async Task ReservasSequenciais_SemLacunas()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        var numeros = new List<long>();
        for (var i = 0; i < 5; i++)
            numeros.Add(await ReservarAsync(serie: 7, TipoAmbiente.Homologacao));

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, numeros);
    }

    [SkippableFact]
    public async Task AmbientesDiferentes_TemContadoresIndependentes()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        var h1 = await ReservarAsync(serie: 9, TipoAmbiente.Homologacao);
        var p1 = await ReservarAsync(serie: 9, TipoAmbiente.Producao);
        var h2 = await ReservarAsync(serie: 9, TipoAmbiente.Homologacao);

        // Homologacao e producao nao compartilham sequencia.
        Assert.Equal(1, h1);
        Assert.Equal(1, p1);
        Assert.Equal(2, h2);
    }

    [SkippableFact]
    public async Task ReservasConcorrentes_NaoRepetemNumero()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        const int paralelas = 20;

        // Dispara varias reservas simultaneas na mesma serie. O lock pessimista
        // (FOR UPDATE) tem de serializar as reservas: 20 numeros distintos, sem
        // repeticao nem buraco.
        var tarefas = Enumerable.Range(0, paralelas)
            .Select(_ => ReservarAsync(serie: 42, TipoAmbiente.Homologacao))
            .ToArray();

        var numeros = await Task.WhenAll(tarefas);

        var distintos = numeros.Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(paralelas, distintos.Length);
        Assert.Equal(Enumerable.Range(1, paralelas).Select(i => (long)i), distintos);
    }

    // ---- Inicializacao/ajuste da numeracao (DefinirProximoNumeroAsync) ---

    private async Task<long> DefinirAsync(int serie, TipoAmbiente ambiente, long proximo)
    {
        await using var db = NovoContexto();
        return await new ReservadorNumeracao(db).DefinirProximoNumeroAsync(
            _empresaId, ModeloDocumento.NFe, serie, ambiente, proximo);
    }

    [SkippableFact]
    public async Task DefinirProximo_CriaSerie_E_ProximaReservaContinua()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        // Migracao: a numeracao ja avancou em outro sistema ate 1000.
        var ultimo = await DefinirAsync(serie: 3, TipoAmbiente.Homologacao, proximo: 1001);
        Assert.Equal(1000, ultimo); // ultimoNumero = proximo - 1

        // A proxima reserva deve emitir exatamente o proximo informado.
        var n = await ReservarAsync(serie: 3, TipoAmbiente.Homologacao);
        Assert.Equal(1001, n);
    }

    [SkippableFact]
    public async Task DefinirProximo_AjustaSerieExistente()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        await ReservarAsync(serie: 4, TipoAmbiente.Homologacao); // cria contador em 1
        await DefinirAsync(serie: 4, TipoAmbiente.Homologacao, proximo: 500);

        var n = await ReservarAsync(serie: 4, TipoAmbiente.Homologacao);
        Assert.Equal(500, n);
    }

    [SkippableFact]
    public async Task DefinirProximo_MenorOuIgualAoMaiorEmitido_Lanca()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");

        await using (var db = NovoContexto())
        {
            db.DocumentosFiscais.Add(new DocumentoFiscal
            {
                EmpresaId = _empresaId,
                Modelo = ModeloDocumento.NFe,
                Serie = 5,
                Numero = 10,
                Ambiente = TipoAmbiente.Homologacao,
                Status = StatusDocumento.Autorizado,
                CriadoEm = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Proximo <= maior emitido (10) repetiria uma NF-e: deve recusar.
        await Assert.ThrowsAsync<NumeracaoInvalidaException>(
            () => DefinirAsync(serie: 5, TipoAmbiente.Homologacao, proximo: 10));

        // Proximo acima do maior emitido e aceito.
        var ultimo = await DefinirAsync(serie: 5, TipoAmbiente.Homologacao, proximo: 11);
        Assert.Equal(10, ultimo);
    }

    [SkippableFact]
    public async Task DefinirProximo_MenorQueUm_Lanca()
    {
        Skip.IfNot(_habilitado, "MYSQL_CONNECTION_STRING nao definida.");
        await Assert.ThrowsAsync<NumeracaoInvalidaException>(
            () => DefinirAsync(serie: 6, TipoAmbiente.Homologacao, proximo: 0));
    }
}
