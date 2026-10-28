using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Persiste uma linha de <see cref="TentativaEmissao"/> para CADA tentativa de
/// emissao — inclusive as que falham antes de virar documento.
///
/// Grava sempre em um <see cref="NFeDbContext"/> proprio, obtido de um escopo
/// novo: quando a emissao falha, o contexto original pode estar com entidades
/// meio-rastreadas, e um SaveChanges ali arrastaria esse estado sujo. O escopo
/// isolado garante que a auditoria seja gravada de forma limpa e independente.
///
/// A gravacao NUNCA derruba a emissao: se o proprio registro falhar, o erro e
/// apenas logado.
/// </summary>
public sealed class RegistradorTentativa
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegistradorTentativa>? _logger;

    public RegistradorTentativa(
        IServiceScopeFactory scopeFactory,
        ILogger<RegistradorTentativa>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RegistrarAsync(TentativaEmissao tentativa, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NFeDbContext>();

            // Corta mensagens/detalhes ao tamanho da coluna para nunca estourar.
            tentativa.Mensagem = Limitar(tentativa.Mensagem, 1000);
            tentativa.Fase = Limitar(tentativa.Fase, 30);
            if (tentativa.CodigoStatus is not null)
                tentativa.CodigoStatus = Limitar(tentativa.CodigoStatus, 5);

            db.TentativasEmissao.Add(tentativa);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditoria e best-effort: nunca deve mascarar/derrubar a emissao.
            _logger?.LogError(ex,
                "Falha ao registrar tentativa de emissao (empresa {EmpresaId}).",
                tentativa.EmpresaId);
        }
    }

    private static string Limitar(string valor, int max) =>
        string.IsNullOrEmpty(valor) || valor.Length <= max ? valor : valor[..max];
}
