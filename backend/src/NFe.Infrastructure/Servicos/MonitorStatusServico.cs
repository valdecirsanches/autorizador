using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NFe.Domain;
using NFe.Domain.Entities;
using NFe.Infrastructure.Data;
using NFe.Infrastructure.Security;
using NFe.Sefaz.Endpoints;
using NFe.Sefaz.Servicos;
using NFe.Sefaz.Soap;

namespace NFe.Infrastructure.Servicos;

/// <summary>
/// Orquestra a consulta de status: carrega o certificado da empresa, chama a
/// SEFAZ e registra o resultado, inclusive as falhas de transporte, que sao o
/// sinal mais relevante de indisponibilidade.
/// </summary>
public sealed class MonitorStatusServico
{
    private readonly NFeDbContext _db;
    private readonly ServicoCertificado _certificados;
    private readonly StatusServicoClient _cliente;
    private readonly ILogger<MonitorStatusServico>? _log;

    public MonitorStatusServico(
        NFeDbContext db,
        ServicoCertificado certificados,
        StatusServicoClient cliente,
        ILogger<MonitorStatusServico>? log = null)
    {
        _db = db;
        _certificados = certificados;
        _cliente = cliente;
        _log = log;
    }

    /// <summary>
    /// Consulta o status usando o certificado da empresa e persiste o historico.
    /// Falhas de transporte nao propagam excecao: viram registro com
    /// ErroTransporte, para que o painel mostre a indisponibilidade.
    /// </summary>
    public async Task<ConsultaStatusServico> ConsultarAsync(
        Guid empresaId,
        string uf,
        TipoAmbiente ambiente,
        string? ufAutorizadorOverride = null,
        bool contingencia = false,
        CancellationToken ct = default)
    {
        if (!CodigoUf.SiglaValida(uf))
            throw new InvalidOperationException("UF invalida.");
        uf = uf.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(ufAutorizadorOverride) &&
            !CodigoUf.SiglaValida(ufAutorizadorOverride))
            throw new InvalidOperationException("UF do autorizador invalida.");
        var autorizador = string.IsNullOrWhiteSpace(ufAutorizadorOverride)
            ? null : ufAutorizadorOverride.Trim().ToUpperInvariant();

        var existe = await _db.Empresas.AsNoTracking().AnyAsync(e => e.Id == empresaId, ct);
        if (!existe)
            throw new InvalidOperationException($"Empresa {empresaId} nao encontrada.");

        var registro = new ConsultaStatusServico
        {
            EmpresaId = empresaId,
            Uf = uf,
            Ambiente = ambiente,
            Modelo = ModeloDocumento.NFe,
            ConsultadoEm = DateTime.UtcNow
        };

        try
        {
            using var certificado = await _certificados.CarregarAtivoSefazAsync(empresaId, ct);

            var resultado = await _cliente.ConsultarAsync(
                uf, ambiente, certificado,
                autorizador, contingencia, ct);

            registro.Autorizador = resultado.Autorizador;
            registro.CodigoStatus = resultado.CodigoStatus;
            registro.Motivo = resultado.Motivo;
            registro.TempoMedioResposta = resultado.TempoMedioSegundos;
            registro.DataHoraSefaz = resultado.DataHoraSefaz;
            registro.PrevisaoRetorno = resultado.PrevisaoRetorno;
            registro.TempoRespostaMs = resultado.TempoRespostaMs;
            registro.Online = resultado.Online;

            if (!resultado.Online)
                _log?.LogWarning(
                    "[SEFAZ] {Uf}/{Amb} fora de operacao: cStat {Stat} - {Motivo}",
                    uf, ambiente, resultado.CodigoStatus, resultado.Motivo);
        }
        catch (Exception ex) when (
            ex is SefazSoapException or CertificadoException or SefazEndpointException)
        {
            // Indisponibilidade e problema de certificado sao estado observavel,
            // nao excecao de programacao: registramos para o painel.
            registro.Online = false;
            registro.ErroTransporte = Truncar(ex.Message, 1000);

            if (string.IsNullOrEmpty(registro.Autorizador))
                registro.Autorizador = autorizador ?? string.Empty;

            _log?.LogError(ex,
                "[SEFAZ] Falha ao consultar status de {Uf}/{Amb}.", uf, ambiente);
        }

        _db.ConsultasStatusServico.Add(registro);
        await _db.SaveChangesAsync(ct);

        return registro;
    }

    /// <summary>
    /// Ultimo status conhecido por UF e ambiente, para o painel nao precisar
    /// bater na SEFAZ a cada carregamento de tela.
    /// </summary>
    public async Task<List<ConsultaStatusServico>> UltimosPorUfAsync(
        TipoAmbiente ambiente,
        CancellationToken ct = default)
    {
        // Agrupa por UF e pega a consulta mais recente de cada uma.
        return await _db.ConsultasStatusServico
            .Where(c => c.Ambiente == ambiente)
            .GroupBy(c => c.Uf)
            .Select(g => g.OrderByDescending(c => c.ConsultadoEm).First())
            .ToListAsync(ct);
    }

    private static string Truncar(string valor, int max) =>
        valor.Length <= max ? valor : valor[..max];
}
