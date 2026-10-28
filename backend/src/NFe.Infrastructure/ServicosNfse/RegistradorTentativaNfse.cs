using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;

namespace NFe.Infrastructure.ServicosNfse;

/// <summary>
/// Persiste uma <see cref="TentativaEmissaoNfse"/> para CADA tentativa de emissao
/// de NFS-e, em escopo/DbContext proprio (mesma estrategia do
/// <c>RegistradorTentativa</c> da NF-e): auditoria limpa, independente do estado
/// do contexto que falhou, e que nunca derruba a emissao.
/// </summary>
public sealed class RegistradorTentativaNfse
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegistradorTentativaNfse>? _logger;

    public RegistradorTentativaNfse(
        IServiceScopeFactory scopeFactory,
        ILogger<RegistradorTentativaNfse>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RegistrarAsync(TentativaEmissaoNfse tentativa, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();

            tentativa.Mensagem = Limitar(tentativa.Mensagem, 1000);
            tentativa.Fase = Limitar(tentativa.Fase, 30);
            if (tentativa.CodigoStatus is not null)
                tentativa.CodigoStatus = Limitar(tentativa.CodigoStatus, 20);
            if (tentativa.TomadorNome is not null)
                tentativa.TomadorNome = Limitar(tentativa.TomadorNome, 300);

            db.TentativasEmissaoNfse.Add(tentativa);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Falha ao registrar tentativa de emissao de NFS-e (empresa {EmpresaId}).",
                tentativa.EmpresaId);
        }
    }

    private static string Limitar(string valor, int max) =>
        string.IsNullOrEmpty(valor) || valor.Length <= max ? valor : valor[..max];
}
