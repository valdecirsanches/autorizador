using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NFe.Infrastructure.Configuracao;

namespace NFe.Infrastructure.Data;

/// <summary>
/// Fabrica usada apenas pelas ferramentas de linha de comando do EF Core para
/// gerar e aplicar migrations. Le a connection string de MYSQL_CONNECTION_STRING.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NFeDbContext>
{
    public NFeDbContext CreateDbContext(string[] args)
    {
        // O `dotnet ef` NAO executa o Program.cs, entao o .env.local precisa ser
        // carregado aqui tambem. Sem isso, `dotnet ef database update` falha com
        // "MYSQL_CONNECTION_STRING nao definida" mesmo com o .env.local pronto.
        DotEnv.Load(Directory.GetCurrentDirectory());

        var cs = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "MYSQL_CONNECTION_STRING nao definida. Exemplo: " +
                "Server=host;Port=3306;Database=nfe_autorizador;User Id=usuario;Password=senha;");

        var options = new DbContextOptionsBuilder<NFeDbContext>()
            .UseMySql(cs, ServerVersion.AutoDetect(cs), my =>
            {
                my.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
                my.MigrationsAssembly(typeof(NFeDbContext).Assembly.FullName);
            })
            .Options;

        return new NFeDbContext(options);
    }
}
